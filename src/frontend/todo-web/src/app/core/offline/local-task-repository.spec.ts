import Dexie from 'dexie';
import { firstValueFrom, from } from 'rxjs';
import type { LocalMutation, LocalTask, TaskFields } from './local-models';
import { LocalTaskRepository } from './local-task-repository';
import { TodoLocalDb } from './todo-local-db';
import { newUuid } from './uuid';

const fields = (overrides: Partial<TaskFields> = {}): TaskFields => ({
  title: 'Conferir medição',
  description: 'Unidade A',
  dueDate: '2026-10-30',
  priority: 'high',
  status: 'pending',
  category: 'Campo',
  ...overrides,
});

const syncedTask = (overrides: Partial<LocalTask> = {}): LocalTask => ({
  ...fields(),
  id: newUuid(),
  createdAt: '2026-10-27T10:00:00.000Z',
  updatedAt: '2026-10-27T10:00:00.000Z',
  deletedAt: null,
  serverVersion: 3,
  syncState: 'synced',
  ...overrides,
});

describe('LocalTaskRepository', () => {
  let db: TodoLocalDb;
  let repo: LocalTaskRepository;
  let tick: number;
  let failNextId: boolean;

  const clock = () => new Date(Date.UTC(2026, 9, 28, 12, 0, tick++));
  const ids = () => {
    if (failNextId) throw new Error('falha simulada');
    return newUuid();
  };
  const mutations = () => db.local_mutations.orderBy('seq').toArray();

  beforeEach(() => {
    tick = 0;
    failNextId = false;
    db = new TodoLocalDb(newUuid());
    repo = new LocalTaskRepository(db, clock, ids);
  });

  afterEach(async () => {
    db.close();
    await Dexie.delete(db.name);
  });

  it('remove o caractere nulo, que o servidor não aceita', async () => {
    const task = await repo.create(
      fields({ title: 'Con\u0000ferir', description: '\u0000', category: 'Ca\u0000mpo' }),
    );

    expect(task).toMatchObject({ title: 'Conferir', description: null, category: 'Campo' });
    expect((await mutations())[0].payload).toMatchObject({ title: 'Conferir', category: 'Campo' });
  });

  it('grava a tarefa e a mutação de criação juntas', async () => {
    const task = await repo.create(fields({ title: '  Nova  ', description: '  ', category: ' ' }));

    expect(await db.local_tasks.get(task.id)).toEqual(task);
    expect(task).toMatchObject({
      title: 'Nova',
      description: null,
      category: 'Geral',
      serverVersion: null,
      syncState: 'pending',
      deletedAt: null,
      createdAt: '2026-10-28T12:00:00.000Z',
    });
    const [mutation] = await mutations();
    expect(mutation).toMatchObject({
      taskId: task.id,
      action: 'upsert',
      baseVersion: null,
      status: 'pending',
      retryCount: 0,
      payload: { title: 'Nova', createdAt: task.createdAt },
    });
  });

  it('não grava nada quando a mutação falha no meio da transação', async () => {
    let calls = 0;
    repo = new LocalTaskRepository(db, clock, () => {
      if (++calls === 2) throw new Error('falha ao gerar operationId');
      return newUuid();
    });

    await expect(repo.create(fields())).rejects.toThrow('falha ao gerar operationId');

    expect(await db.local_tasks.count()).toBe(0);
    expect(await db.local_mutations.count()).toBe(0);
  });

  it('rejeita título vazio sem gravar', async () => {
    await expect(repo.create(fields({ title: '   ' }))).rejects.toThrow();
    expect(await db.local_tasks.count()).toBe(0);
  });

  it('coalesce edições de uma criação ainda não enviada', async () => {
    const task = await repo.create(fields());
    const [created] = await mutations();

    await repo.update(task.id, fields({ title: 'Editada' }));
    await repo.setStatus(task.id, 'completed');

    const queue = await mutations();
    expect(queue).toHaveLength(1);
    expect(queue[0].operationId).toBe(created.operationId);
    expect(queue[0].baseVersion).toBeNull();
    expect(queue[0].payload).toMatchObject({
      title: 'Editada',
      status: 'completed',
      createdAt: task.createdAt,
    });
    expect(await db.local_tasks.get(task.id)).toMatchObject({
      title: 'Editada',
      status: 'completed',
    });
  });

  it('descarta criação e exclusão que o servidor nunca viu', async () => {
    const task = await repo.create(fields());

    await repo.delete(task.id);

    expect(await db.local_tasks.get(task.id)).toBeUndefined();
    expect(await db.local_mutations.count()).toBe(0);
  });

  it('exclusão de tarefa sincronizada vira tombstone e mutação delete com a versão do servidor', async () => {
    const task = syncedTask();
    await db.local_tasks.add(task);

    await repo.delete(task.id);

    const stored = await db.local_tasks.get(task.id);
    expect(stored?.deletedAt).not.toBeNull();
    expect(stored?.syncState).toBe('pending');
    const [mutation] = await mutations();
    expect(mutation).toMatchObject({
      action: 'delete',
      baseVersion: 3,
      payload: null,
      status: 'pending',
    });
  });

  it('edição pendente de tarefa sincronizada seguida de exclusão vira um único delete', async () => {
    const task = syncedTask();
    await db.local_tasks.add(task);
    await repo.update(task.id, fields({ title: 'Editada' }));
    const [edit] = await mutations();

    await repo.delete(task.id);

    const queue = await mutations();
    expect(queue).toHaveLength(1);
    expect(queue[0]).toMatchObject({
      operationId: edit.operationId,
      action: 'delete',
      baseVersion: 3,
      payload: null,
    });
  });

  it('nunca altera mutação in-flight: nova edição vira nova mutação pendente', async () => {
    const task = syncedTask();
    const inflight: LocalMutation = {
      operationId: newUuid(),
      taskId: task.id,
      action: 'upsert',
      baseVersion: 3,
      payload: { ...fields(), createdAt: task.createdAt },
      status: 'inflight',
      queuedAt: '2026-10-28T11:00:00.000Z',
      retryCount: 1,
    };
    await db.local_tasks.add(task);
    await db.local_mutations.add(inflight);

    await repo.update(task.id, fields({ title: 'Durante o envio' }));
    await repo.update(task.id, fields({ title: 'Ainda durante o envio' }));

    const [frozen, next, ...rest] = await mutations();
    expect(frozen).toEqual({ ...inflight, seq: frozen.seq });
    expect(next).toMatchObject({
      status: 'pending',
      baseVersion: 3,
      payload: { title: 'Ainda durante o envio' },
    });
    expect(next.operationId).not.toBe(inflight.operationId);
    expect(rest).toHaveLength(0);
  });

  it('exclusão durante criação in-flight mantém tombstone e enfileira delete', async () => {
    const task = await repo.create(fields());
    const [create] = await mutations();
    await db.local_mutations.update(create.seq!, { status: 'inflight' });

    await repo.delete(task.id);

    expect((await db.local_tasks.get(task.id))?.deletedAt).not.toBeNull();
    const [frozen, deletion] = await mutations();
    expect(frozen.status).toBe('inflight');
    expect(deletion).toMatchObject({ action: 'delete', status: 'pending' });
  });

  it('desfaz a edição local quando a nova mutação não pode ser gravada', async () => {
    const task = syncedTask();
    await db.local_tasks.add(task);
    await db.local_mutations.add({
      operationId: newUuid(),
      taskId: task.id,
      action: 'upsert',
      baseVersion: 3,
      payload: { ...fields(), createdAt: task.createdAt },
      status: 'inflight',
      queuedAt: '2026-10-28T11:00:00.000Z',
      retryCount: 0,
    });
    failNextId = true;

    await expect(repo.update(task.id, fields({ title: 'Não deve persistir' }))).rejects.toThrow(
      'falha simulada',
    );

    expect(await db.local_tasks.get(task.id)).toEqual(task);
    expect(await db.local_mutations.count()).toBe(1);
  });

  it('lista reativa oculta tombstones e conta pendências', async () => {
    const visible = await repo.create(fields({ title: 'Visível' }));
    const removed = syncedTask({ title: 'Removida' });
    await db.local_tasks.add(removed);
    await repo.delete(removed.id);

    const active = await firstValueFrom(from(repo.activeTasks$()));
    const pending = await firstValueFrom(from(repo.pendingMutationCount$()));

    expect(active.map((t) => t.id)).toEqual([visible.id]);
    expect(pending).toBe(2);
  });

  it('mantém dados por usuário e preserva pendências ao reabrir o banco', async () => {
    const task = await repo.create(fields());
    const other = new TodoLocalDb(newUuid());
    db.close();

    const reopened = new TodoLocalDb(db.userId);
    try {
      expect(await other.local_tasks.count()).toBe(0);
      expect(await reopened.local_tasks.get(task.id)).toEqual(task);
      expect(await reopened.local_mutations.count()).toBe(1);
    } finally {
      other.close();
      reopened.close();
      await Dexie.delete(other.name);
    }
  });
});
