import Dexie from 'dexie';
import type { LocalConflict } from '../offline/local-models';
import { newUuid } from '../offline/uuid';
import { FakeSyncServer } from './testing/fake-sync-server';
import { SyncDevice, taskFields as fields } from './testing/sync-device';

describe('Resolução de conflitos', () => {
  let server: FakeSyncServer;
  let devices: SyncDevice[];
  let a: SyncDevice;
  let b: SyncDevice;

  const device = () => {
    const created = new SyncDevice(`device-${newUuid()}`, server);
    devices.push(created);
    return created;
  };

  /** Tarefa sincronizada nos dois dispositivos e alterada nos dois a partir da mesma versão. */
  async function concurrentEdit(titleA = 'Versão do A', titleB = 'Versão do B') {
    const task = await a.repo.create(fields({ title: 'Original' }));
    await a.sync();
    await b.sync();
    await a.repo.update(task.id, fields({ title: titleA }));
    await b.repo.update(task.id, fields({ title: titleB }));
    await a.sync();
    await b.sync();
    const conflict = (await b.db.conflicts.get(task.id))!;
    expect(conflict.reason).toBe('version_mismatch');
    return { task, conflict };
  }

  const updatedAt = async (device: SyncDevice, id: string) => (await device.task(id))!.updatedAt;
  const keepMine = async (device: SyncDevice, conflict: LocalConflict) =>
    device.store.keepLocalVersion(
      conflict.taskId,
      conflict.operationId,
      await updatedAt(device, conflict.taskId),
    );
  const useServer = async (device: SyncDevice, conflict: LocalConflict) =>
    device.store.useServerVersion(
      conflict.taskId,
      conflict.operationId,
      await updatedAt(device, conflict.taskId),
    );

  beforeEach(() => {
    server = new FakeSyncServer();
    devices = [];
    a = device();
    b = device();
  });

  afterEach(async () => {
    for (const d of devices) {
      d.db.close();
      await Dexie.delete(d.db.name);
    }
  });

  it('"Manter minha alteração" gera novo operationId com baseVersion = versão atual do servidor', async () => {
    const { task, conflict } = await concurrentEdit();

    expect(await keepMine(b, conflict)).toBe('resolved');
    const [mutation] = await b.mutations();
    expect(mutation.operationId).not.toBe(conflict.operationId);
    expect(mutation).toMatchObject({
      status: 'pending',
      baseVersion: 2,
      payload: { title: 'Versão do B' },
    });
    expect(await b.db.conflicts.count()).toBe(0);

    await b.sync();
    expect(server.pushRequests.at(-1)!.operations[0]).toMatchObject({
      operationId: mutation.operationId,
      baseVersion: 2,
    });
    expect(server.tasks.get(task.id)).toMatchObject({ title: 'Versão do B', version: 3 });
    expect(await b.task(task.id)).toMatchObject({ serverVersion: 3, syncState: 'synced' });

    await a.sync();
    expect(await a.task(task.id)).toMatchObject({ title: 'Versão do B', serverVersion: 3 });
  });

  it('"Usar versão do servidor" descarta a intenção local e aplica o servidor, sem enviar nada', async () => {
    const { task, conflict } = await concurrentEdit();
    const requests = server.pushRequests.length;

    expect(await useServer(b, conflict)).toBe('resolved');
    await b.sync();

    expect(await b.task(task.id)).toMatchObject({
      title: 'Versão do A',
      serverVersion: 2,
      syncState: 'synced',
    });
    expect(await b.mutations()).toEqual([]);
    expect(await b.db.conflicts.count()).toBe(0);
    expect(server.pushRequests).toHaveLength(requests);
  });

  it('edições feitas com o conflito aberto mantêm o estado e seguem em "Manter minha alteração"', async () => {
    const { task, conflict } = await concurrentEdit();

    await b.repo.update(task.id, fields({ title: 'B revisado depois do conflito' }));
    expect(await b.task(task.id)).toMatchObject({ syncState: 'conflict' });
    expect((await b.mutations()).map((m) => m.status)).toEqual(['conflict', 'pending']);

    await keepMine(b, conflict);
    expect(await b.mutations()).toMatchObject([
      { status: 'pending', baseVersion: 2, payload: { title: 'B revisado depois do conflito' } },
    ]);
    await b.sync();
    expect(server.tasks.get(task.id)).toMatchObject({ title: 'B revisado depois do conflito' });
  });

  it('a resolução usa o servidor mais recente registrado no pull, não o do momento da recusa', async () => {
    const { task, conflict } = await concurrentEdit();
    await a.repo.update(task.id, fields({ title: 'A de novo (v3)' }));
    await a.sync();
    await b.sync(); // pull registra v3 sem tocar a cópia de B

    expect(await b.task(task.id)).toMatchObject({ title: 'Versão do B', syncState: 'conflict' });
    await keepMine(b, conflict);
    expect(await b.mutations()).toMatchObject([{ baseVersion: 3 }]);
    await b.sync();
    expect(server.tasks.get(task.id)).toMatchObject({ title: 'Versão do B', version: 4 });
  });

  it('edição contra exclusão remota: aceitar a exclusão ou recriar como nova tarefa', async () => {
    const original = await a.repo.create(fields({ title: 'Disputada' }));
    await a.sync();
    await b.sync();
    await a.repo.delete(original.id);
    await b.repo.update(original.id, fields({ title: 'Editada em B' }));
    await a.sync();
    await b.sync();
    const conflict = (await b.db.conflicts.get(original.id))!;
    expect(conflict.reason).toBe('deleted');

    expect(await keepMine(b, conflict)).toBe('resolved');
    await b.sync();

    const recreated = [...server.tasks.values()].find((t) => t.title === 'Editada em B')!;
    expect(recreated.id).not.toBe(original.id);
    expect(server.tasks.get(original.id)!.deletedAt).not.toBeNull();
    expect((await b.task(original.id))!.deletedAt).not.toBeNull();
    expect(await b.visibleTitles()).toEqual(['Editada em B']);
    await a.sync();
    expect(await a.visibleTitles()).toEqual(['Editada em B']);
  });

  it('edição contra exclusão remota: "Aceitar exclusão" remove a tarefa', async () => {
    const original = await a.repo.create(fields({ title: 'Disputada' }));
    await a.sync();
    await b.sync();
    await a.repo.delete(original.id);
    await b.repo.update(original.id, fields({ title: 'Editada em B' }));
    await a.sync();
    await b.sync();

    await useServer(b, (await b.db.conflicts.get(original.id))!);

    expect(await b.visibleTitles()).toEqual([]);
    expect(await b.mutations()).toEqual([]);
  });

  it('exclusão local contra edição remota: manter exclui com a versão nova; usar servidor restaura', async () => {
    const kept = await a.repo.create(fields({ title: 'Excluir mesmo assim' }));
    const restored = await a.repo.create(fields({ title: 'Restaurar' }));
    await a.sync();
    await b.sync();
    for (const id of [kept.id, restored.id]) {
      await a.repo.update(
        id,
        fields({ title: `${id === kept.id ? 'Excluir mesmo assim' : 'Restaurar'} (A)` }),
      );
      await b.repo.delete(id);
    }
    await a.sync();
    await b.sync();
    expect(await b.db.conflicts.count()).toBe(2);

    await keepMine(b, (await b.db.conflicts.get(kept.id))!);
    await useServer(b, (await b.db.conflicts.get(restored.id))!);
    await b.sync();

    expect(server.tasks.get(kept.id)).toMatchObject({ version: 3 });
    expect(server.tasks.get(kept.id)!.deletedAt).not.toBeNull();
    expect(await b.visibleTitles()).toEqual(['Restaurar (A)']);
    await a.sync();
    expect(await a.visibleTitles()).toEqual(['Restaurar (A)']);
  });

  it('id já usado por outra conta: "Manter" recria com novo id', async () => {
    const task = await b.repo.create(fields({ title: 'Minha' }));
    server.foreignTaskIds.add(task.id);
    await b.sync();
    const conflict = (await b.db.conflicts.get(task.id))!;
    expect(conflict).toMatchObject({ reason: 'already_exists', serverTask: null });

    await keepMine(b, conflict);
    await b.sync();

    const [created] = [...server.tasks.values()];
    expect(created).toMatchObject({ title: 'Minha' });
    expect(created.id).not.toBe(task.id);
    expect(await b.task(task.id)).toBeUndefined();
  });

  it('excluir uma tarefa cuja criação está em conflito não a apaga localmente', async () => {
    const task = await a.repo.create(fields({ title: 'Do A' }));
    await a.sync();
    // B tem uma criação local com o mesmo id (ex.: dado restaurado), recusada como already_exists.
    await b.db.local_tasks.add({
      ...(await a.task(task.id))!,
      serverVersion: null,
      syncState: 'pending',
    });
    await b.db.local_mutations.add({
      operationId: newUuid(),
      taskId: task.id,
      action: 'upsert',
      baseVersion: null,
      payload: { ...fields({ title: 'Do B' }), createdAt: task.createdAt },
      status: 'pending',
      queuedAt: task.createdAt,
      retryCount: 0,
    });
    await b.sync();
    expect(await b.db.conflicts.get(task.id)).toMatchObject({ reason: 'already_exists' });

    await b.repo.delete(task.id);

    expect(await b.task(task.id)).toMatchObject({ syncState: 'conflict' });
    expect((await b.task(task.id))!.deletedAt).not.toBeNull();
    expect(await b.db.conflicts.count()).toBe(1);
  });

  it('validation_error: corrigir e "Enviar novamente" usa novo operationId', async () => {
    server.rejectTitle = 'Título recusado';
    const task = await b.repo.create(fields({ title: 'Título recusado' }));
    await b.sync();
    const conflict = (await b.db.conflicts.get(task.id))!;
    expect(conflict.reason).toBe('validation_error');

    await b.repo.update(task.id, fields({ title: 'Título corrigido' }));
    await keepMine(b, conflict);
    await b.sync();

    expect([...server.tasks.values()].map((t) => t.title)).toEqual(['Título corrigido']);
    expect(server.pushRequests.at(-1)!.operations[0].operationId).not.toBe(conflict.operationId);
    expect(await b.task(task.id)).toMatchObject({ serverVersion: 1, syncState: 'synced' });
  });

  it('formulário aberto durante um pull não sobrescreve a mudança do outro dispositivo', async () => {
    const task = await a.repo.create(fields({ title: 'Original' }));
    await a.sync();
    await b.sync();
    const openedForm = (await a.task(task.id))!; // A abre "Editar" (v1)

    await b.repo.update(task.id, fields({ title: 'Mudança do B' }));
    await b.sync(); // servidor em v2
    await a.sync(); // pull traz v2 para a cópia de A (sem pendências)
    expect(await a.task(task.id)).toMatchObject({ title: 'Mudança do B', serverVersion: 2 });

    await a.repo.update(task.id, fields({ title: 'Edição do A' }), openedForm);
    expect(await a.mutations()).toMatchObject([{ baseVersion: 1 }]);
    await a.sync();

    expect(server.tasks.get(task.id)).toMatchObject({ title: 'Mudança do B', version: 2 });
    expect(await a.db.conflicts.get(task.id)).toMatchObject({
      reason: 'version_mismatch',
      serverTask: { title: 'Mudança do B' },
    });
    expect(await a.task(task.id)).toMatchObject({ title: 'Edição do A', syncState: 'conflict' });
  });

  it('formulário aberto durante o ack da própria edição não gera conflito falso', async () => {
    const task = await a.repo.create(fields({ title: 'v1' }));
    await a.sync();
    await a.repo.update(task.id, fields({ title: 'v2 local' }));
    const openedForm = (await a.task(task.id))!; // pendente, serverVersion 1
    await a.sync(); // ack: v2 no servidor, mesmo conteúdo

    await a.repo.update(task.id, fields({ title: 'v3 pelo formulário' }), openedForm);
    await a.sync();

    expect(await a.db.conflicts.count()).toBe(0);
    expect(server.tasks.get(task.id)).toMatchObject({ title: 'v3 pelo formulário', version: 3 });
  });

  it('concluir com o formulário aberto não é desfeito ao salvar', async () => {
    const task = await a.repo.create(fields({ title: 'Tarefa' }));
    await a.sync();
    const openedForm = (await a.task(task.id))!;
    await a.repo.setStatus(task.id, 'completed');

    // O formulário ainda tem status "pending" do momento em que abriu.
    await a.repo.update(
      task.id,
      fields({ title: 'Tarefa renomeada', status: 'pending' }),
      openedForm,
    );
    await a.sync();

    expect(await a.task(task.id)).toMatchObject({ title: 'Tarefa renomeada', status: 'completed' });
    expect(server.tasks.get(task.id)).toMatchObject({ status: 'completed' });
  });

  it('é idempotente e não age sobre decisões desatualizadas', async () => {
    const { task, conflict } = await concurrentEdit();
    const seen = await updatedAt(b, task.id);

    await b.repo.update(task.id, fields({ title: 'Mudou enquanto o usuário decidia' }));
    expect(await b.store.useServerVersion(task.id, conflict.operationId, seen)).toBe('changed');
    expect(await b.db.conflicts.count()).toBe(1);
    expect(await b.store.keepLocalVersion(task.id, newUuid(), await updatedAt(b, task.id))).toBe(
      'stale',
    );

    expect(await keepMine(b, conflict)).toBe('resolved');
    expect(await keepMine(b, conflict)).toBe('stale');
    expect(await b.mutations()).toHaveLength(1);
  });

  it('nunca descarta uma mutação inflight', async () => {
    const { task, conflict } = await concurrentEdit();
    const [head] = await b.mutations();
    await b.db.local_mutations.update(head.seq!, { status: 'inflight' });

    expect(await useServer(b, conflict)).toBe('stale');
    expect(await b.mutations()).toMatchObject([{ status: 'inflight' }]);
    expect(await b.task(task.id)).toMatchObject({ title: 'Versão do B' });
  });
});
