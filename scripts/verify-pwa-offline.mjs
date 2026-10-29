// Verificação real do PWA offline no build de produção (serviço "web" do docker compose).
//
// Abre um Chromium (Edge ou Chrome) headless com perfil temporário, via CDP e sem dependências,
// faz login, espera o Service Worker ficar ativo e controlando a página e então PARA de verdade os
// containers web e api (origem inalcançável). Recarrega offline, cria/edita/conclui/exclui, recarrega
// de novo e religa os containers, esperando a sincronização automática.
//
// Uso (na raiz do repositório, com `docker compose up -d` rodando):
//   DEMO_PASSWORD=<senha de demonstração> node scripts/verify-pwa-offline.mjs
// Variáveis opcionais: BROWSER (caminho do executável), DEMO_EMAIL (padrão demo2@way2.local), WEB_ORIGIN.
// ATENÇÃO: executa `docker compose stop web api` e `docker compose start api web` durante o teste.
import { execSync, spawn } from 'node:child_process';
import { existsSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const BROWSER =
  process.env.BROWSER ??
  [
    'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
    'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
    '/usr/bin/google-chrome',
    '/usr/bin/chromium',
    '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
  ].find((path) => existsSync(path));
const ORIGIN = process.env.WEB_ORIGIN ?? 'http://localhost:4280';
const EMAIL = process.env.DEMO_EMAIL ?? 'demo2@way2.local';
const PASSWORD = process.env.DEMO_PASSWORD;
const PORT = 9333;
// Títulos únicos por execução: o roteiro pode rodar várias vezes com o mesmo usuário.
const RUN = new Date().toISOString().slice(11, 19);
const T = {
  urgent: `Ligar para fornecedor URGENTE ${RUN}`,
  coffee: `Comprar café ${RUN}`,
  contract: `Revisar contrato ${RUN}`,
};

const log = (step, data) => console.log(JSON.stringify({ step, ...data }));
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const compose = (args) => execSync(`docker compose ${args}`, { stdio: 'pipe' }).toString();

if (!BROWSER) throw new Error('Nenhum Chromium encontrado: defina BROWSER com o caminho do executável.');
if (!PASSWORD) throw new Error('Defina DEMO_PASSWORD (senha de demonstração do ambiente local).');

const profile = mkdtempSync(join(tmpdir(), 'way2-pwa-check-'));
const browser = spawn(
  BROWSER,
  [
    '--headless=new',
    `--remote-debugging-port=${PORT}`,
    `--user-data-dir=${profile}`,
    '--no-first-run',
    '--no-default-browser-check',
    'about:blank',
  ],
  { stdio: 'ignore' },
);

let ws;
let sessionId;
let nextId = 0;
const pending = new Map();

function send(method, params = {}, session = sessionId) {
  const id = ++nextId;
  ws.send(JSON.stringify({ id, method, params, ...(session ? { sessionId: session } : {}) }));
  return new Promise((resolve, reject) => pending.set(id, { resolve, reject }));
}

async function evaluate(expression) {
  const { result, exceptionDetails } = await send('Runtime.evaluate', {
    expression,
    awaitPromise: true,
    returnByValue: true,
  });
  if (exceptionDetails) throw new Error(exceptionDetails.exception?.description ?? exceptionDetails.text);
  return result.value;
}

async function waitFor(label, expression, timeoutMs = 20000) {
  const start = Date.now();
  let last;
  while (Date.now() - start < timeoutMs) {
    try {
      last = await evaluate(expression);
      if (last) return last;
    } catch (error) {
      last = String(error);
    }
    await sleep(500);
  }
  throw new Error(`Tempo esgotado esperando ${label}: ${JSON.stringify(last)}`);
}

const STATE = `(() => { const s = document.querySelector('[data-testid=sync-status]'); return s && { state: s.dataset.state, text: s.textContent.trim().replace(/\\s+/g, ' ') }; })()`;
const VIEW = `(() => ({
  href: location.href,
  sections: [...document.querySelectorAll('[data-section]')].map(s => s.querySelector('h2').textContent.trim().replace(/\\s+/g,' ') + ' => ' + [...s.querySelectorAll('[data-testid=task-title]')].map(t => t.textContent).join(' | ')),
  status: ${STATE},
  onLine: navigator.onLine,
}))()`;
const QUEUE = `(async () => {
  const user = JSON.parse(localStorage.getItem('way2.todo.session')).user;
  const db = await new Promise((res, rej) => { const r = indexedDB.open('way2-todo-' + user.id); r.onsuccess = () => res(r.result); r.onerror = () => rej(r.error); });
  const all = (s) => new Promise((res) => { const q = db.transaction(s).objectStore(s).getAll(); q.onsuccess = () => res(q.result); });
  const mutations = await all('local_mutations');
  db.close();
  return mutations.map(m => m.action + ':' + m.status + ':base=' + m.baseVersion + ':' + (m.payload?.title ?? m.taskId.slice(0, 8)));
})()`;
const fill = (testId, value) =>
  `(() => { const el = document.querySelector('app-task-form [data-testid="${testId}"]'); el.value = ${JSON.stringify(value)}; el.dispatchEvent(new Event('input')); el.dispatchEvent(new Event('change')); return true; })()`;
const card = (title) =>
  `[...document.querySelectorAll('app-task-item')].find(i => i.querySelector('[data-testid=task-title]')?.textContent === ${JSON.stringify(title)})`;

async function addTask(title, category) {
  await evaluate(fill('title', title));
  await evaluate(fill('category', category));
  await evaluate(`document.querySelector('app-task-form form').requestSubmit(), true`);
  await waitFor(
    `tarefa "${title}"`,
    `[...document.querySelectorAll('[data-testid=task-title]')].some(t => t.textContent === ${JSON.stringify(title)})`,
  );
}

try {
  let version;
  for (let i = 0; i < 40 && !version; i++) {
    try {
      version = await (await fetch(`http://127.0.0.1:${PORT}/json/version`)).json();
    } catch {
      await sleep(250);
    }
  }
  log('navegador', { product: version.Browser });
  ws = new WebSocket(version.webSocketDebuggerUrl);
  await new Promise((resolve) => ws.addEventListener('open', resolve, { once: true }));
  ws.addEventListener('message', (event) => {
    const message = JSON.parse(event.data);
    if (message.id && pending.has(message.id)) {
      const { resolve, reject } = pending.get(message.id);
      pending.delete(message.id);
      message.error ? reject(new Error(message.error.message)) : resolve(message.result);
    }
  });
  const { targetId } = await send('Target.createTarget', { url: `${ORIGIN}/login` }, null);
  ({ sessionId } = await send('Target.attachToTarget', { targetId, flatten: true }, null));
  await send('Page.enable');
  await send('Runtime.enable');

  // 1) Login online e primeira sincronização.
  await waitFor('tela de login', `!!document.querySelector('input[type=email]')`);
  await evaluate(
    `(() => { for (const [sel, v] of [['input[type=email]', ${JSON.stringify(EMAIL)}], ['input[type=password]', ${JSON.stringify(PASSWORD)}]]) { const el = document.querySelector(sel); el.value = v; el.dispatchEvent(new Event('input')); } document.querySelector('form').requestSubmit(); return true; })()`,
  );
  await waitFor('sincronizado após login', `${STATE}?.state === 'synced'`);
  await addTask(T.urgent, 'Trabalho');
  await waitFor('tarefa enviada', `${STATE}?.state === 'synced' && !document.querySelector('.local')`);
  log('online', await evaluate(VIEW));

  // 2) Service Worker instalado, ativado e controlando a página (pré-condição do teste offline).
  const sw = await waitFor(
    'Service Worker ativado',
    `navigator.serviceWorker.getRegistration().then(r => r?.active?.state === 'activated' && { scope: r.scope, script: r.active.scriptURL })`,
    45000,
  );
  await send('Page.reload');
  await waitFor(
    'página recarregada controlada pelo Service Worker',
    `document.readyState === 'complete' && !!navigator.serviceWorker.controller`,
  );
  const ngswState = await evaluate(
    `fetch('/ngsw/state').then(r => r.text()).then(t => t.split('\\n').filter(l => /Driver state|Latest manifest hash/.test(l)))`,
  );
  log('service-worker', { ...sw, controlled: await evaluate(`!!navigator.serviceWorker.controller`), ngswState });
  await waitFor('sincronizado após recarga', `${STATE}?.state === 'synced'`);

  // 3) Origem inalcançável: para web (nginx) e api.
  compose('stop web api');
  const probe = await evaluate(
    `fetch('/index.html?probe=' + Date.now(), { cache: 'no-store' }).then(r => 'respondeu ' + r.status, e => 'falhou: ' + e.message)`,
  );
  log('servidores parados', { fetchSemCache: probe });

  await send('Page.reload');
  await waitFor(
    'app aberto offline pelo Service Worker',
    `document.readyState === 'complete' && !!document.querySelector('app-root [data-testid=sync-status]')`,
  );
  await waitFor('indicador offline', `${STATE}?.state === 'offline'`);
  log('recarga offline', await evaluate(VIEW));

  // 4) Criar, editar (urgência imediata), concluir e excluir offline.
  await addTask(T.coffee, 'Casa');
  await addTask(T.contract, 'Trabalho');
  await evaluate(`${card(T.contract)}.querySelector('button').click(), true`); // Editar
  await sleep(300);
  await evaluate(fill('description', 'Cliente pediu: urgente!'));
  await evaluate(`document.querySelector('app-task-form form').requestSubmit(), true`);
  await waitFor(
    'urgente sobe ao topo',
    `[...document.querySelectorAll('[data-section=urgent] [data-testid=task-title]')].some(t => t.textContent === ${JSON.stringify(T.contract)})`,
  );
  await evaluate(`${card(T.coffee)}.querySelector('input[type=checkbox]').click(), true`);
  await waitFor('concluída', `${card(T.coffee)}.querySelector('.task').classList.contains('completed')`);
  await evaluate(
    `window.confirm = () => true; ${card(T.urgent)}.querySelector('button.danger').click(), true`,
  );
  await waitFor('excluída', `!${card(T.urgent)}`);
  log('crud offline', await evaluate(VIEW));

  await send('Page.reload');
  await waitFor('recarga offline com alterações', `document.readyState === 'complete' && ${STATE}?.state === 'offline'`);
  log('após nova recarga offline', { ...(await evaluate(VIEW)), queue: await evaluate(QUEUE) });

  // 5) Conexão de volta: sincroniza sozinho com o app aberto (backoff/periódico, sem intervenção).
  compose('start api web');
  const startedAt = Date.now();
  await waitFor('sincronização automática', `${STATE}?.state === 'synced'`, 90000);
  const serverTasks = await evaluate(
    `fetch('/api/tasks', { headers: { Authorization: 'Bearer ' + JSON.parse(localStorage.getItem('way2.todo.session')).accessToken } }).then(r => r.json()).then(ts => ts.map(t => t.title + ' v' + t.version + ' ' + t.status))`,
  );
  log('reconexão', {
    segundosAteSincronizar: Math.round((Date.now() - startedAt) / 1000),
    view: await evaluate(VIEW),
    serverTasks,
  });
  log('ok', {});
} catch (error) {
  log('FALHA', { error: String(error?.stack ?? error) });
  process.exitCode = 1;
} finally {
  try {
    compose('start api web');
  } catch {}
  try {
    ws?.close();
  } catch {}
  browser.kill();
  await sleep(1000);
  try {
    rmSync(profile, { recursive: true, force: true });
  } catch {}
}
