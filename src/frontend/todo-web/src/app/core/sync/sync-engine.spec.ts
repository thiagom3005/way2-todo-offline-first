import Dexie from 'dexie';
import type { TaskFields } from '../offline/local-models';
import { TodoLocalDb } from '../offline/todo-local-db';
import { newUuid } from '../offline/uuid';
import type { SyncEngineOptions } from './sync-engine';
import { FakeSyncServer } from './testing/fake-sync-server';
import { SyncDevice as Device, taskFields } from './testing/sync-device';

const fields = taskFields;

describe('SyncEngine (dois dispositivos, mesmo servidor)', () => {
  let server: FakeSyncServer;
  let devices: Device[];

  const device = (options: Partial<SyncEngineOptions> = {}) => {
    const created = new Device(`device-${newUuid()}`, server, options);
    devices.push(created);
    return created;
  };
  const sentOperationIds = () =>
    server.pushRequests.flatMap((request) => request.operations.map((op) => op.operationId));

  beforeEach(() => {
    server = new FakeSyncServer();
    devices = [];
  });

  afterEach(async () => {
    vi.restoreAllMocks();
    for (const d of devices) {
      d.db.close();
      await Dexie.delete(d.db.name);
    }
  });

  it('sincroniza criação, edição e exclusão entre dispositivos', async () => {
    const a = device();
    const b = device();
    const task = await a.repo.create(fields({ title: 'Ligar para cliente' }));

    expect(await a.sync()).toMatchObject({ kind: 'ok', pushed: 1 });
    expect(await a.task(task.id)).toMatchObject({ serverVersion: 1, syncState: 'synced' });
    expect(await a.mutations()).toEqual([]);

    await b.sync();
    expect(await b.task(task.id)).toMatchObject({ title: 'Ligar para cliente', serverVersion: 1 });

    await a.repo.update(task.id, fields({ title: 'Ligar para cliente URGENTE' }));
    await a.sync();
    await b.sync();
    expect(await b.task(task.id)).toMatchObject({
      title: 'Ligar para cliente URGENTE',
      serverVersion: 2,
    });

    await a.repo.delete(task.id);
    await a.sync();
    await b.sync();
    expect(await b.visibleTitles()).toEqual([]);
    expect(await b.task(task.id)).toMatchObject({ serverVersion: 3, syncState: 'synced' });
    expect((await b.task(task.id))!.deletedAt).not.toBeNull();
  });

  it('operação reenviada após timeout é confirmada uma única vez (duplicate)', async () => {
    const a = device();
    const task = await a.repo.create(fields());
    server.dropNextPushResponse = true;

    expect(await a.sync()).toMatchObject({ kind: 'offline' });
    const [inflight] = await a.mutations();
    expect(inflight).toMatchObject({ status: 'inflight', retryCount: 0 });

    expect(await a.sync()).toMatchObject({ kind: 'ok' });
    expect(server.log).toHaveLength(1);
    expect(sentOperationIds()).toEqual([inflight.operationId, inflight.operationId]);
    expect(server.pushRequests[1].operations[0]).toEqual(server.pushRequests[0].operations[0]);
    expect(await a.task(task.id)).toMatchObject({ serverVersion: 1, syncState: 'synced' });
    expect(await a.mutations()).toEqual([]);
  });

  it('timeout após o commit, edição local e retry: rebase sem perder a edição', async () => {
    const a = device();
    const task = await a.repo.create(fields({ title: 'v1' }));
    await a.sync();

    await a.repo.update(task.id, fields({ title: 'v2' }));
    server.dropNextPushResponse = true;
    await a.sync(); // servidor aplicou v2, resposta perdida
    await a.repo.update(task.id, fields({ title: 'v3 local' }));

    const [inflight, pending] = await a.mutations();
    expect(inflight).toMatchObject({ status: 'inflight', baseVersion: 1 });
    expect(inflight.payload!.title).toBe('v2');
    expect(pending).toMatchObject({ status: 'pending', baseVersion: 1 });

    expect(await a.sync()).toMatchObject({ kind: 'ok', pushed: 2 });
    expect(server.pushRequests.at(-1)!.operations[0]).toMatchObject({
      operationId: pending.operationId,
      baseVersion: 2,
    });
    expect(server.tasks.get(task.id)).toMatchObject({ title: 'v3 local', version: 3 });
    expect(await a.task(task.id)).toMatchObject({
      title: 'v3 local',
      serverVersion: 3,
      syncState: 'synced',
    });
  });

  it('edição durante o envio não é sobrescrita pelo ack e segue na próxima rodada', async () => {
    const a = device();
    const task = await a.repo.create(fields({ title: 'Antes' }));
    server.beforePushResponse = async () => {
      await a.repo.update(task.id, fields({ title: 'Editada durante o push' }));
      expect(await a.task(task.id)).toMatchObject({ title: 'Editada durante o push' });
    };

    await a.sync();

    const sent = server.pushRequests.map((r) => r.operations[0]);
    expect(sent.map((op) => [op.payload?.title, op.baseVersion])).toEqual([
      ['Antes', null],
      ['Editada durante o push', 1],
    ]);
    expect(await a.task(task.id)).toMatchObject({
      title: 'Editada durante o push',
      serverVersion: 2,
      syncState: 'synced',
    });
  });

  it('falha ao gravar o ack no IndexedDB mantém a operação inflight e a próxima execução recupera', async () => {
    const a = device();
    const task = await a.repo.create(fields());
    vi.spyOn(a.db.local_tasks, 'put').mockRejectedValueOnce(new Error('falha de disco simulada'));

    expect(await a.sync()).toMatchObject({ kind: 'error' });
    // Transação do ack abortada por inteiro: mutação não foi removida e tarefa não mudou.
    expect(await a.mutations()).toMatchObject([{ status: 'inflight' }]);
    expect(await a.task(task.id)).toMatchObject({ serverVersion: null, syncState: 'pending' });

    expect(await a.sync()).toMatchObject({ kind: 'ok' });
    expect(server.log).toHaveLength(1);
    expect(await a.task(task.id)).toMatchObject({ serverVersion: 1, syncState: 'synced' });
  });

  it('duas alterações concorrentes na mesma tarefa: uma aplica, a outra vira conflito', async () => {
    const a = device();
    const b = device();
    const task = await a.repo.create(fields({ title: 'Original' }));
    await a.sync();
    await b.sync();

    await a.repo.update(task.id, fields({ title: 'Versão do A' }));
    await b.repo.update(task.id, fields({ title: 'Versão do B' }));
    await a.sync();
    expect(await b.sync()).toMatchObject({ kind: 'ok' });

    expect(server.tasks.get(task.id)).toMatchObject({ title: 'Versão do A', version: 2 });
    expect(await b.task(task.id)).toMatchObject({ title: 'Versão do B', syncState: 'conflict' });
    const conflict = await b.db.conflicts.get(task.id);
    expect(conflict).toMatchObject({
      reason: 'version_mismatch',
      serverTask: { title: 'Versão do A', version: 2 },
    });
    expect(await b.mutations()).toMatchObject([{ status: 'conflict', baseVersion: 1 }]);

    // Conflito bloqueia só a própria tarefa; nada é reenviado às cegas.
    const requests = server.pushRequests.length;
    await b.sync();
    expect(server.pushRequests).toHaveLength(requests);
  });

  it('exclusão offline de tarefa sincronizada chega ao outro dispositivo e replay antigo não ressuscita', async () => {
    const a = device();
    const b = device();
    const task = await a.repo.create(fields({ title: 'Vai sumir' }));
    await a.sync();
    await b.sync();
    const oldPage = { changes: structuredClone(server.log), nextCursor: 1, hasMore: false };

    server.reachable = false;
    await a.repo.delete(task.id);
    expect(await a.sync()).toMatchObject({ kind: 'offline' });
    expect(await a.mutations()).toMatchObject([
      { action: 'delete', status: 'pending', baseVersion: 1 },
    ]);

    server.reachable = true;
    await a.sync();
    await b.sync();
    expect(await b.visibleTitles()).toEqual([]);

    await b.store.applyPullPage(oldPage);
    expect(await b.visibleTitles()).toEqual([]);
    expect(await b.task(task.id)).toMatchObject({ serverVersion: 2 });
  });

  it('criar e excluir antes da primeira sincronização não envia nada', async () => {
    const a = device();
    const task = await a.repo.create(fields());
    await a.repo.delete(task.id);

    expect(await a.sync()).toMatchObject({ kind: 'ok', pushed: 0 });
    expect(server.pushRequests).toEqual([]);
    expect(server.log).toEqual([]);
  });

  it('recupera a fila após interrupção, com nova instância do banco e do engine', async () => {
    const a = device();
    await a.repo.create(fields({ title: 'Um' }));
    await a.repo.create(fields({ title: 'Dois' }));
    server.dropNextPushResponse = true;
    await a.sync();
    const frozen = await a.mutations();
    expect(frozen.map((m) => m.status)).toEqual(['inflight', 'inflight']);
    a.db.close(); // "fechou o navegador"

    const reopened = new Device(a.name, server);
    devices.push(reopened);
    expect(await reopened.sync()).toMatchObject({ kind: 'ok' });

    expect(server.log).toHaveLength(2);
    expect(sentOperationIds()).toEqual([...frozen, ...frozen].map((m) => m.operationId));
    expect(await reopened.mutations()).toEqual([]);
    expect(await reopened.visibleTitles()).toEqual(['Dois', 'Um']);
  });

  it('página de pull interrompida não avança o cursor e a próxima execução continua', async () => {
    const a = device();
    for (const title of ['T1', 'T2', 'T3', 'T4', 'T5']) await a.repo.create(fields({ title }));
    await a.sync();

    const b = device({ pullLimit: 2 });
    const apply = b.store.applyPullPage.bind(b.store);
    let pages = 0;
    vi.spyOn(b.store, 'applyPullPage').mockImplementation(async (page) => {
      if (++pages === 2) throw new Error('aba fechada no meio do pull (simulado)');
      return apply(page);
    });

    expect(await b.sync()).toMatchObject({ kind: 'error' });
    expect(await b.store.cursor()).toBe(2);
    expect(await b.visibleTitles()).toEqual(['T1', 'T2']);

    expect(await b.sync()).toMatchObject({ kind: 'ok' });
    expect(await b.store.cursor()).toBe(5);
    expect(await b.visibleTitles()).toEqual(['T1', 'T2', 'T3', 'T4', 'T5']);
  });

  it('pagina todas as mudanças sem perda', async () => {
    const a = device();
    for (let i = 1; i <= 7; i++) await a.repo.create(fields({ title: `Tarefa ${i}` }));
    await a.sync();

    const b = device({ pullLimit: 3 });
    server.pullCursors.length = 0;
    expect(await b.sync()).toMatchObject({ kind: 'ok', pulled: 7 });

    expect(server.pullCursors).toEqual([0, 3, 6]);
    expect(await b.visibleTitles()).toHaveLength(7);
    expect(await b.store.cursor()).toBe(7);
  });

  it('cada usuário sincroniza apenas o próprio banco', async () => {
    const aliceServer = new FakeSyncServer();
    const bobServer = new FakeSyncServer();
    const alice = new Device(`user-alice-${newUuid()}`, aliceServer);
    const bob = new Device(`user-bob-${newUuid()}`, bobServer);
    devices.push(alice, bob);
    await alice.repo.create(fields({ title: 'Da Alice' }));
    await bob.repo.create(fields({ title: 'Do Bob' }));

    await alice.sync();
    await bob.sync();

    expect(await alice.visibleTitles()).toEqual(['Da Alice']);
    expect(await bob.visibleTitles()).toEqual(['Do Bob']);
    expect([...aliceServer.tasks.values()].map((t) => t.title)).toEqual(['Da Alice']);
  });

  it('banco gravado na Fase A, com cinco criações pendentes, sincroniza sem migração', async () => {
    const userId = `phase-a-${newUuid()}`;
    // Mesmo nome e mesmo schema (versão 1) que a Fase A gravava, sem a opção de durabilidade.
    const legacy = new Dexie(TodoLocalDb.nameFor(userId));
    legacy.version(1).stores({
      local_tasks: 'id, syncState',
      local_mutations: '++seq, &operationId, taskId, status',
      sync_metadata: 'key',
      conflicts: 'taskId',
    });
    const titles = [
      'Comprar café',
      'Ligar para cliente',
      'Pagar boleto',
      'Revisar contrato',
      'Relatório mensal',
    ];
    const operationIds: string[] = [];
    for (const [index, title] of titles.entries()) {
      const createdAt = new Date(Date.UTC(2026, 9, 28, 23, index)).toISOString();
      const task = {
        ...fields({ title, status: index === 4 ? 'completed' : 'pending' }),
        id: newUuid(),
        createdAt,
        updatedAt: createdAt,
        deletedAt: null,
        serverVersion: null,
        syncState: 'pending',
      };
      const operationId = newUuid();
      operationIds.push(operationId);
      await legacy.table('local_tasks').add(task);
      await legacy.table('local_mutations').add({
        operationId,
        taskId: task.id,
        action: 'upsert',
        baseVersion: null,
        payload: { ...fields({ title, status: task.status as TaskFields['status'] }), createdAt },
        status: 'pending',
        queuedAt: createdAt,
        retryCount: 0,
      });
    }
    legacy.close();

    const upgraded = new Device(userId, server);
    devices.push(upgraded);
    expect(await upgraded.sync()).toMatchObject({ kind: 'ok', pushed: 5 });

    expect(sentOperationIds().sort()).toEqual([...operationIds].sort());
    expect([...server.tasks.values()].map((t) => [t.title, t.version]).sort()).toEqual(
      titles.map((t) => [t, 1]).sort(),
    );
    expect(await upgraded.mutations()).toEqual([]);
    const local = await upgraded.db.local_tasks.toArray();
    expect(local.every((t) => t.serverVersion === 1 && t.syncState === 'synced')).toBe(true);
  });

  it('validation_error não trava a fila e o pull continua', async () => {
    const a = device();
    server.rejectTitle = 'Recusada';
    const rejected = await a.repo.create(fields({ title: 'Recusada' }));
    await a.repo.create(fields({ title: 'Aceita' }));

    expect(await a.sync()).toMatchObject({ kind: 'ok' });

    expect([...server.tasks.values()].map((t) => t.title)).toEqual(['Aceita']);
    expect(await a.db.conflicts.get(rejected.id)).toMatchObject({
      reason: 'validation_error',
      errors: { title: ['recusado'] },
    });
    expect(await a.store.cursor()).toBe(1);
    expect(await a.store.hasInflight()).toBe(false);
  });

  it('exclusão enquanto a criação está em voo: delete é rebaseado para v1 e aplicado', async () => {
    const a = device();
    const task = await a.repo.create(fields());
    server.beforePushResponse = () => a.repo.delete(task.id);

    await a.sync();

    expect(
      server.pushRequests.map((r) => [r.operations[0].action, r.operations[0].baseVersion]),
    ).toEqual([
      ['upsert', null],
      ['delete', 1],
    ]);
    expect(server.tasks.get(task.id)!.deletedAt).not.toBeNull();
    expect(await a.mutations()).toEqual([]);
    expect(await a.task(task.id)).toMatchObject({ serverVersion: 2, syncState: 'synced' });
  });

  it('criação em voo recusada (id de outra conta) com exclusão local não deixa conflito órfão', async () => {
    const a = device();
    const task = await a.repo.create(fields());
    server.foreignTaskIds.add(task.id);
    server.beforePushResponse = () => a.repo.delete(task.id);

    expect(await a.sync()).toMatchObject({ kind: 'ok' });

    expect(await a.mutations()).toEqual([]);
    expect(await a.db.conflicts.count()).toBe(0);
    expect(await a.visibleTitles()).toEqual([]);
  });

  it('dois engines no mesmo banco (sem Web Locks) não aplicam nada duas vezes', async () => {
    const a = device();
    for (const title of ['A', 'B', 'C']) await a.repo.create(fields({ title }));
    const secondTab = new Device(a.name, server);
    devices.push(secondTab);

    const results = await Promise.all([a.sync(), secondTab.sync()]);

    expect(results.map((r) => r.kind)).toEqual(['ok', 'ok']);
    expect(server.log).toHaveLength(3);
    expect(await a.mutations()).toEqual([]);
    const local = await a.db.local_tasks.toArray();
    expect(local.map((t) => [t.serverVersion, t.syncState])).toEqual([
      [1, 'synced'],
      [1, 'synced'],
      [1, 'synced'],
    ]);
  });

  it('API fora do ar não congela pendências: edições continuam coalescendo', async () => {
    const a = device();
    const task = await a.repo.create(fields({ title: 'v1' }));
    server.reachable = false;

    expect(await a.sync()).toMatchObject({ kind: 'offline' });
    await a.repo.update(task.id, fields({ title: 'v2' }));

    expect(await a.mutations()).toMatchObject([{ status: 'pending', payload: { title: 'v2' } }]);
    expect(server.pushRequests).toEqual([]);
  });

  it('sessão expirada no servidor resulta em auth sem alterar a fila', async () => {
    const a = device();
    await a.repo.create(fields());
    server.authExpired = true;

    expect(await a.sync()).toMatchObject({ kind: 'auth' });
    expect(await a.mutations()).toMatchObject([{ status: 'pending' }]);
  });
});
