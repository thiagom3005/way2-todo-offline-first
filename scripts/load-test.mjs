// Teste de carga local: N usuários virtuais reproduzindo o ciclo real do cliente de sincronização.
//
// Cada usuário tem conta própria (JWT emitido com a chave de desenvolvimento do ambiente de teste), cursor de
// pull próprio e tarefas próprias. O comportamento segue o SyncService do frontend:
//   - ciclo periódico a cada 30s: GET /api/health + pull paginado;
//   - escrita local em média 1 por minuto: debounce de 1s, depois health + push (uma operação por tarefa) + pull;
//   - uma execução por vez por usuário (nova escrita durante a sincronização agenda outra rodada).
// Fases: rampa, carga nominal, estresse (3x a taxa) e tempestade de reconexão (todos voltam de uma queda
// com 3 alterações pendentes, em uma janela de 2s). Uma operação já enviada fica congelada: o retry reenvia
// o mesmo operationId, payload e baseVersion, como o cliente real.
//
// Uso: node scripts/load-test.mjs <usuarios.json> <baseUrl> <chaveJwt> [saida.json]
//   usuarios.json: [{ "id": "<uuid>", "email": "..." }], usuários já existentes no banco de teste.
//   chaveJwt: Jwt:SigningKey do ambiente de teste (os tokens são emitidos localmente, sem login).
// Variáveis opcionais: RAMP_S, NOMINAL_S, STRESS_S, STRESS_FACTOR, PERIOD_S, WRITE_MEAN_S, STORM (1/0).
// Roteiro completo (stack isolada, criação dos usuários e conferência de integridade) no README, seção
// "Dimensionamento". Sem dependências: só Node.js 24.
import crypto from 'node:crypto';
import fs from 'node:fs';

const [usersFile, BASE, KEY, outFile] = process.argv.slice(2);
if (!usersFile || !BASE || !KEY) throw new Error('uso: node load-test.mjs <usuarios.json> <baseUrl> <chaveJwt> [saida.json]');
const env = (name, fallback) => Number(process.env[name] ?? fallback);
const CFG = {
  rampS: env('RAMP_S', 60),
  nominalS: env('NOMINAL_S', 300),
  stressS: env('STRESS_S', 120),
  stressFactor: env('STRESS_FACTOR', 3),
  periodS: env('PERIOD_S', 30),
  writeMeanS: env('WRITE_MEAN_S', 60),
  storm: env('STORM', 1) === 1,
};
const users = JSON.parse(fs.readFileSync(usersFile, 'utf8'));
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const now = () => performance.now();

// --- JWT HS256 igual ao emitido pela API (sub, email, iss, aud, iat, nbf, exp) ---
const b64u = (buf) => Buffer.from(buf).toString('base64url');
function token(user) {
  const t = Math.floor(Date.now() / 1000);
  const head = b64u(JSON.stringify({ alg: 'HS256', typ: 'JWT' }));
  const body = b64u(JSON.stringify({ sub: user.id, email: user.email, iss: 'way2-todo-api', aud: 'way2-todo-web', iat: t, nbf: t, exp: t + 8 * 3600 }));
  const sig = b64u(crypto.createHmac('sha256', KEY).update(`${head}.${body}`).digest());
  return `${head}.${body}.${sig}`;
}

// --- Métricas por fase e por endpoint ---
let phase = 'rampa';
const metrics = {};
const timeline = []; // [segundo, fase, requisições concluídas no segundo]
let doneThisSecond = 0;
const pushStatus = {};
let appliedOps = 0;
let createdTasks = 0;
function record(endpoint, ms, ok, status) {
  const key = `${phase}|${endpoint}`;
  const m = (metrics[key] ??= { count: 0, errors: 0, statuses: {}, lat: [] });
  m.count++;
  if (!ok) m.errors++;
  m.statuses[status] = (m.statuses[status] ?? 0) + 1;
  m.lat.push(ms);
  doneThisSecond++;
}
async function call(endpoint, url, init) {
  const start = now();
  try {
    const res = await fetch(url, init);
    const body = res.headers.get('content-type')?.includes('json') ? await res.json() : await res.text();
    record(endpoint, now() - start, res.ok, res.status);
    return { ok: res.ok, status: res.status, body };
  } catch (error) {
    record(endpoint, now() - start, false, `rede:${error.cause?.code ?? error.name}`);
    return { ok: false, status: 0, body: null };
  }
}

// --- Usuário virtual ---
const CATEGORIES = ['Campo', 'Casa', 'Trabalho', 'Financeiro', 'Pessoal'];
class VirtualUser {
  constructor(user, index) {
    this.index = index;
    this.auth = { Authorization: `Bearer ${token(user)}` };
    this.deviceId = crypto.randomUUID();
    this.cursor = 0;
    this.tasks = new Map(); // id -> { version, createdAt, title }
    this.pending = []; // operações aguardando envio (no máximo uma por tarefa, coalescidas)
    this.running = false;
    this.rerun = false;
    this.timers = new Set();
    this.stopped = false;
    this.seq = 0;
  }

  schedule(fn, ms) {
    const t = setTimeout(() => { this.timers.delete(t); if (!this.stopped) fn(); }, ms);
    this.timers.add(t);
  }

  stop() {
    this.stopped = true;
    for (const t of this.timers) clearTimeout(t);
    this.timers.clear();
  }

  start(rate) {
    this.stopped = false;
    const period = (CFG.periodS / rate) * 1000;
    const periodic = () => { this.sync(); this.schedule(periodic, period); };
    this.schedule(periodic, Math.random() * period);
    const write = () => {
      this.write();
      this.schedule(write, -Math.log(1 - Math.random()) * (CFG.writeMeanS / rate) * 1000);
    };
    this.schedule(write, -Math.log(1 - Math.random()) * (CFG.writeMeanS / rate) * 1000);
  }

  // Escrita local: cria tarefa (até 5 por usuário) ou edita uma existente; coalesce com pendente da mesma tarefa.
  write({ sync = true } = {}) {
    const createNew = this.tasks.size < 5 && (this.tasks.size === 0 || Math.random() < 0.4);
    let taskId;
    if (createNew) {
      taskId = crypto.randomUUID();
      this.tasks.set(taskId, { version: null, createdAt: new Date().toISOString() });
    } else {
      const ids = [...this.tasks.keys()];
      taskId = ids[Math.floor(Math.random() * ids.length)];
    }
    const task = this.tasks.get(taskId);
    const n = ++this.seq;
    const payload = {
      title: `Tarefa ${this.index}-${n}${n % 7 === 0 ? ' URGENTE' : ''}`,
      description: n % 3 === 0 ? `Detalhe ${n}` : null,
      dueDate: n % 2 === 0 ? '2026-11-15' : null,
      priority: ['low', 'medium', 'high'][n % 3],
      status: n % 5 === 0 ? 'completed' : 'pending',
      category: CATEGORIES[n % CATEGORIES.length],
      createdAt: task.createdAt,
    };
    const existing = this.pending.find((op) => op.taskId === taskId && !op.sent); // enviada = congelada
    if (existing) existing.payload = payload; // coalescência de operação ainda não enviada
    else this.pending.push({ operationId: crypto.randomUUID(), taskId, payload, sent: false, baseVersion: undefined });
    if (sync) this.schedule(() => this.sync(), 1000); // debounce de 1s
  }

  async sync() {
    if (this.running) { this.rerun = true; return; }
    this.running = true;
    try {
      do {
        this.rerun = false;
        await this.runOnce();
      } while (this.rerun);
    } finally {
      this.running = false;
    }
  }

  async runOnce() {
    const health = await call('health', `${BASE}/api/health`);
    if (!health.ok) return;
    // Push: uma operação por tarefa por rodada; a seguinte da mesma tarefa espera o ack.
    for (let round = 0; round < 10; round++) {
      const batch = [];
      const seen = new Set();
      for (const op of this.pending) {
        if (seen.has(op.taskId)) continue;
        seen.add(op.taskId);
        if (!op.sent) { op.sent = true; op.baseVersion = this.tasks.get(op.taskId).version; } // congela no 1º envio
        batch.push(op);
      }
      if (batch.length === 0) break;
      const operations = batch.map((op) => ({
        operationId: op.operationId,
        taskId: op.taskId,
        action: 'upsert',
        baseVersion: op.baseVersion,
        payload: op.payload,
      }));
      const res = await call('push', `${BASE}/api/sync/push`, {
        method: 'POST',
        headers: { ...this.auth, 'content-type': 'application/json' },
        body: JSON.stringify({ deviceId: this.deviceId, operations }),
      });
      if (!res.ok) return; // continua congelada: o retry reenvia exatamente a mesma operação
      for (const result of res.body.results) {
        pushStatus[result.status] = (pushStatus[result.status] ?? 0) + 1;
        const op = batch.find((o) => o.operationId === result.operationId);
        this.pending.splice(this.pending.indexOf(op), 1);
        if (result.status === 'applied' || result.status === 'duplicate') {
          const task = this.tasks.get(op.taskId);
          if (result.status === 'applied') {
            appliedOps++;
            if (task.version === null) createdTasks++;
          }
          task.version = result.serverTask.version;
        }
      }
    }
    // Pull paginado até o fim (o cliente real pede 200 por página).
    for (let page = 0; page < 50; page++) {
      const res = await call('pull', `${BASE}/api/sync/pull?cursor=${this.cursor}&limit=200`, { headers: this.auth });
      if (!res.ok) return;
      this.cursor = res.body.nextCursor;
      if (!res.body.hasMore) break;
    }
  }
}

// --- Execução ---
const percentile = (sorted, p) => (sorted.length ? sorted[Math.min(sorted.length - 1, Math.floor((p / 100) * sorted.length))] : null);
const t0 = now();
const secondsTicker = setInterval(() => {
  timeline.push([Math.round((now() - t0) / 1000), phase, doneThisSecond]);
  doneThisSecond = 0;
}, 1000);
const progress = setInterval(() => {
  const last10 = timeline.slice(-10).reduce((s, [, , c]) => s + c, 0) / 10;
  const errors = Object.values(metrics).reduce((s, m) => s + m.errors, 0);
  console.log(JSON.stringify({ t: Math.round((now() - t0) / 1000), phase, rps10s: Math.round(last10), applied: appliedOps, errors }));
}, 10000);

const vus = users.map((u, i) => new VirtualUser(u, i));
const phaseWindows = {};
const mark = (name) => { phase = name; phaseWindows[name] = { start: (now() - t0) / 1000 }; };
const close = (name) => { phaseWindows[name].end = (now() - t0) / 1000; };

mark('rampa');
for (let i = 0; i < vus.length; i++) {
  vus[i].start(1);
  await sleep((CFG.rampS * 1000) / vus.length);
}
close('rampa');

mark('nominal');
await sleep(CFG.nominalS * 1000);
close('nominal');

if (CFG.stressS > 0) {
  for (const vu of vus) { vu.stop(); vu.start(CFG.stressFactor); }
  mark('estresse');
  await sleep(CFG.stressS * 1000);
  close('estresse');
}

for (const vu of vus) vu.stop();
while (vus.some((vu) => vu.running)) await sleep(200);

if (CFG.storm) {
  // Queda do servidor: cada usuário acumula 3 alterações offline; na volta, todos sincronizam em até 2s.
  for (const vu of vus) for (let i = 0; i < 3; i++) vu.write({ sync: false });
  mark('reconexao');
  const stormStart = now();
  await Promise.all(vus.map(async (vu) => { await sleep(Math.random() * 2000); await vu.sync(); }));
  phaseWindows.reconexao.allSyncedAfterS = (now() - stormStart) / 1000;
  close('reconexao');
}

clearInterval(progress);
clearInterval(secondsTicker);
timeline.push([Math.round((now() - t0) / 1000), phase, doneThisSecond]);

// --- Resumo ---
const summary = { config: CFG, users: vus.length, phases: {}, pushStatus, appliedOps, createdTasks, pendingLeft: vus.reduce((s, v) => s + v.pending.length, 0) };
for (const [name, w] of Object.entries(phaseWindows)) {
  const seconds = w.end - w.start;
  const endpoints = {};
  let total = 0;
  let errors = 0;
  for (const [key, m] of Object.entries(metrics)) {
    const [p, endpoint] = key.split('|');
    if (p !== name) continue;
    const sorted = m.lat.sort((a, b) => a - b);
    total += m.count;
    errors += m.errors;
    endpoints[endpoint] = {
      count: m.count,
      errors: m.errors,
      statuses: m.statuses,
      p50: Math.round(percentile(sorted, 50)),
      p95: Math.round(percentile(sorted, 95)),
      p99: Math.round(percentile(sorted, 99)),
      max: Math.round(sorted.at(-1)),
    };
  }
  const steady = timeline.filter(([s, p]) => p === name).map(([, , c]) => c);
  summary.phases[name] = {
    seconds: Math.round(seconds),
    requests: total,
    errors,
    errorRate: total ? +(errors / total * 100).toFixed(3) : 0,
    rpsAvg: +(total / seconds).toFixed(1),
    rpsPeak1s: steady.length ? Math.max(...steady) : 0,
    ...(w.allSyncedAfterS !== undefined ? { allSyncedAfterS: +w.allSyncedAfterS.toFixed(2) } : {}),
    endpoints,
  };
}
console.log(JSON.stringify(summary, null, 1));
if (outFile) fs.writeFileSync(outFile, JSON.stringify({ summary, timeline }, null, 1));
process.exit(0);
