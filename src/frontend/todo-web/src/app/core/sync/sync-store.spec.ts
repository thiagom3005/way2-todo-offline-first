import Dexie from 'dexie';
import type { LocalMutation, LocalTask, ServerTask, TaskFields } from '../offline/local-models';
import { LocalTaskRepository } from '../offline/local-task-repository';
import { TodoLocalDb } from '../offline/todo-local-db';
import { newUuid } from '../offline/uuid';
import type { PushResult } from './sync-contract';
import { newest, SyncStore } from './sync-store';

const fields = (overrides: Partial<TaskFields> = {}): TaskFields => ({
  title: 'Conferir medição',
  description: null,
  dueDate: null,
  priority: 'medium',
  status: 'pending',
  category: 'Campo',
  ...overrides,
});

const serverTask = (
  id: string,
  version: number,
  overrides: Partial<ServerTask> = {},
): ServerTask => ({
  ...fields(),
  id,
  version,
  createdAt: '2026-10-28T12:00:00.000Z',
  updatedAt: `2026-10-29T12:00:0${version}.000Z`,
  deletedAt: null,
  ...overrides,
});

describe('SyncStore', () => {
  let db: TodoLocalDb;
  let repo: LocalTaskRepository;
  let store: SyncStore;

  const mutations = () => db.local_mutations.orderBy('seq').toArray();
  const applied = (mutation: Pick<LocalMutation, 'operationId'>, task: ServerTask): PushResult => ({
    operationId: mutation.operationId,
    status: 'applied',
    serverRevision: task.version,
    serverTask: task,
  });

  /** Tarefa já sincronizada na versão indicada. */
  async function syncedTask(
    version: number,
    overrides: Partial<TaskFields> = {},
  ): Promise<LocalTask> {
    const task = await repo.create(fields(overrides));
    await store.claimBatch(50);
    const [mutation] = await mutations();
    await store.applyResult(applied(mutation, serverTask(task.id, version, overrides)));
    return (await db.local_tasks.get(task.id))!;
  }

  beforeEach(() => {
    db = new TodoLocalDb(newUuid());
    repo = new LocalTaskRepository(db);
    store = new SyncStore(db);
  });

  afterEach(async () => {
    vi.restoreAllMocks();
    db.close();
    await Dexie.delete(db.name);
  });

  describe('claimBatch', () => {
    it('envia só a cabeça da fila de cada tarefa e a congela como inflight', async () => {
      const one = await syncedTask(1);
      const two = await repo.create(fields({ title: 'Outra' }));
      await repo.update(one.id, fields({ title: 'Edição 1' }));

      const first = await store.claimBatch(50);
      await repo.update(one.id, fields({ title: 'Edição 2' })); // chega durante o envio

      expect(first.map((op) => op.taskId).sort()).toEqual([one.id, two.id].sort());
      expect(first.find((op) => op.taskId === one.id)!.payload!.title).toBe('Edição 1');
      const queue = await mutations();
      expect(
        queue.filter((m) => m.taskId === one.id).map((m) => [m.status, m.payload!.title]),
      ).toEqual([
        ['inflight', 'Edição 1'],
        ['pending', 'Edição 2'],
      ]);
    });

    it('reenvia uma inflight exatamente igual, só incrementando retryCount', async () => {
      await repo.create(fields());
      const [sent] = await store.claimBatch(50);
      const [resent] = await store.claimBatch(50);

      expect(resent).toEqual(sent);
      expect(await mutations()).toMatchObject([{ status: 'inflight', retryCount: 1 }]);
    });

    it('ignora tarefas em conflito e exclusões ainda sem baseVersion, e respeita o limite', async () => {
      for (let i = 0; i < 3; i++) await repo.create(fields({ title: `T${i}` }));
      const [blocked] = await mutations();
      await db.local_mutations.update(blocked.seq!, { status: 'conflict' });
      await db.local_mutations.add({
        operationId: newUuid(),
        taskId: newUuid(),
        action: 'delete',
        baseVersion: null,
        payload: null,
        status: 'pending',
        queuedAt: new Date().toISOString(),
        retryCount: 0,
      });

      const batch = await store.claimBatch(1);

      expect(batch).toHaveLength(1);
      expect(batch[0].operationId).not.toBe(blocked.operationId);
      expect(batch[0].action).toBe('upsert');
    });
  });

  describe('applyResult', () => {
    it('é idempotente: resultado repetido ou de operação desconhecida não muda nada', async () => {
      const task = await repo.create(fields());
      await store.claimBatch(50);
      const [mutation] = await mutations();
      const result = applied(mutation, serverTask(task.id, 1));

      await store.applyResult(result);
      const afterFirst = await db.local_tasks.get(task.id);
      await store.applyResult(result);
      await store.applyResult({ ...result, operationId: newUuid() });

      expect(await db.local_tasks.get(task.id)).toEqual(afterFirst);
      expect(afterFirst).toMatchObject({ serverVersion: 1, syncState: 'synced' });
    });

    it('não aplica resultado de mutação ainda pending (nunca enviada)', async () => {
      const task = await repo.create(fields());
      const [mutation] = await mutations();

      await store.applyResult(applied(mutation, serverTask(task.id, 1)));

      expect(await mutations()).toMatchObject([{ status: 'pending' }]);
    });

    it('recusa resultado que aponta para outra tarefa', async () => {
      await repo.create(fields());
      await store.claimBatch(50);
      const [mutation] = await mutations();

      await expect(
        store.applyResult(applied(mutation, serverTask(newUuid(), 1))),
      ).rejects.toThrow();
      expect(await mutations()).toMatchObject([{ status: 'inflight' }]);
    });

    it('com a fila vazia aplica o snapshot mais novo (ack ou versão remota registrada no pull)', async () => {
      const task = await syncedTask(1);
      await repo.update(task.id, fields({ title: 'Minha edição' }));
      await store.claimBatch(50);
      // Pull durante o envio (outra aba, sem Web Locks) trouxe uma versão mais nova.
      await store.applyPullPage({
        changes: [
          { revision: 9, action: 'upsert', task: serverTask(task.id, 3, { title: 'Remota v3' }) },
        ],
        nextCursor: 9,
        hasMore: false,
      }, 0);
      const [mutation] = await mutations();

      await store.applyResult(applied(mutation, serverTask(task.id, 2, { title: 'Minha edição' })));

      expect(await db.local_tasks.get(task.id)).toMatchObject({
        title: 'Remota v3',
        serverVersion: 3,
        syncState: 'synced',
      });
    });

    it('nunca regride serverVersion e marca synced quando a fila esvazia', async () => {
      const task = await syncedTask(5);
      await repo.update(task.id, fields({ title: 'Edição' }));
      await store.claimBatch(50);
      const [mutation] = await mutations();
      await db.local_tasks.update(task.id, { serverVersion: 7 }); // versão mais nova já conhecida

      await store.applyResult(applied(mutation, serverTask(task.id, 6)));

      expect(await db.local_tasks.get(task.id)).toMatchObject({
        serverVersion: 7,
        syncState: 'synced',
      });
    });

    it('exclusão local contra tarefa inexistente ou já excluída no servidor se resolve sozinha', async () => {
      const gone = await syncedTask(2);
      const deletedThere = await syncedTask(2, { title: 'Excluída lá' });
      await repo.delete(gone.id);
      await repo.delete(deletedThere.id);
      await store.claimBatch(50);
      const queue = await mutations();
      const opOf = (taskId: string) => queue.find((m) => m.taskId === taskId)!;

      await store.applyResult({
        operationId: opOf(gone.id).operationId,
        status: 'conflict',
        reason: 'not_found',
        serverTask: null,
      });
      await store.applyResult({
        operationId: opOf(deletedThere.id).operationId,
        status: 'conflict',
        reason: 'deleted',
        serverTask: serverTask(deletedThere.id, 3, { deletedAt: '2026-10-29T13:00:00.000Z' }),
      });

      expect(await mutations()).toEqual([]);
      expect(await db.conflicts.count()).toBe(0);
      expect(await db.local_tasks.get(deletedThere.id)).toMatchObject({
        serverVersion: 3,
        syncState: 'synced',
      });
      expect((await db.local_tasks.get(gone.id))!.deletedAt).not.toBeNull();
    });
  });

  describe('applyPullPage', () => {
    it('não sobrescreve tarefa com mutação local, mas registra a versão remota mais nova', async () => {
      const task = await syncedTask(1, { title: 'Base' });
      await repo.update(task.id, fields({ title: 'Edição local pendente' }));

      await store.applyPullPage({
        changes: [
          { revision: 2, action: 'upsert', task: serverTask(task.id, 3, { title: 'Remota v3' }) },
          { revision: 3, action: 'upsert', task: serverTask(task.id, 2, { title: 'Remota v2' }) },
        ],
        nextCursor: 3,
        hasMore: false,
      }, 0);

      const local = await db.local_tasks.get(task.id);
      expect(local).toMatchObject({
        title: 'Edição local pendente',
        serverVersion: 1,
        syncState: 'pending',
      });
      expect(local!.remoteSnapshot).toMatchObject({ title: 'Remota v3', version: 3 });
      expect(await store.cursor()).toBe(3);
    });

    it('aplica página e cursor juntos: falha no meio não grava nenhum dos dois', async () => {
      const id = newUuid();
      vi.spyOn(db.sync_metadata, 'put').mockRejectedValueOnce(new Error('falha simulada'));

      await expect(
        store.applyPullPage({
          changes: [{ revision: 1, action: 'upsert', task: serverTask(id, 1) }],
          nextCursor: 1,
          hasMore: false,
        }, 0),
      ).rejects.toThrow();

      expect(await db.local_tasks.get(id)).toBeUndefined();
      expect(await store.cursor()).toBe(0);
    });

    it('não desfaz o cursor rebaixado por um "Descartar" durante o pull', async () => {
      const task = await syncedTask(2);
      await store.applyPullPage({ changes: [], nextCursor: 4, hasMore: false }, 0);
      await repo.update(task.id, fields({ title: 'Recusada' }));
      await store.claimBatch(50);
      const [mutation] = await mutations();
      await store.applyResult({
        operationId: mutation.operationId,
        status: 'validation_error',
        serverTask: null,
        errors: { title: ['inválido'] },
      });

      const requested = await store.cursor(); // pull sai com o cursor 4...
      const updatedAt = (await db.local_tasks.get(task.id))!.updatedAt;
      await store.useServerVersion(task.id, mutation.operationId, updatedAt); // ...e o cursor volta a 0
      const other = newUuid();
      await store.applyPullPage(
        {
          changes: [{ revision: 5, action: 'upsert', task: serverTask(other, 1) }],
          nextCursor: 5,
          hasMore: false,
        },
        requested,
      );

      expect(await store.cursor()).toBe(0); // o próximo pull reobtém a tarefa descartada
      expect(await db.local_tasks.get(other)).toMatchObject({ serverVersion: 1 });
    });
  });

  it('deviceId é gerado uma vez e reaproveitado', async () => {
    const first = await store.deviceId();
    expect(await new SyncStore(db).deviceId()).toBe(first);
  });

  it('newest escolhe o snapshot de maior versão', () => {
    const v1 = serverTask('t', 1);
    const v2 = serverTask('t', 2);
    expect(newest(v1, v2)).toBe(v2);
    expect(newest(v2, v1)).toBe(v2);
    expect(newest(null, v1)).toBe(v1);
    expect(newest(undefined, undefined)).toBeUndefined();
  });
});
