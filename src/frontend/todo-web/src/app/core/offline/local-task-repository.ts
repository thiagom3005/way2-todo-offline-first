import { liveQuery, type Observable } from 'dexie';
import {
  DEFAULT_CATEGORY,
  type LocalMutation,
  type LocalTask,
  type TaskFields,
  type UpsertPayload,
} from './local-models';
import type { TodoLocalDb } from './todo-local-db';
import { newUuid } from './uuid';

/**
 * Escritas locais: cada alteração grava a cópia de trabalho (`local_tasks`) e a intenção pendente
 * (`local_mutations`) na MESMA transação IndexedDB — ou as duas persistem, ou nenhuma.
 *
 * Coalescência só atua em mutações `pending` (nunca enviadas). Mutações `inflight` e `conflict` jamais
 * são alteradas aqui: novas edições viram novas mutações, cuja `baseVersion` o `SyncStore` ajusta no ack.
 */
export class LocalTaskRepository {
  constructor(
    private readonly db: TodoLocalDb,
    private readonly now: () => Date = () => new Date(),
    private readonly newId: () => string = newUuid,
  ) {}

  /** Tarefas visíveis (sem tombstone), reativas a qualquer escrita no banco. */
  activeTasks$(): Observable<LocalTask[]> {
    return liveQuery(() => this.db.local_tasks.filter((t) => t.deletedAt === null).toArray());
  }

  /** Mutações aguardando envio ou confirmação (as que estão em conflito são contadas à parte). */
  pendingMutationCount$(): Observable<number> {
    return liveQuery(() =>
      this.db.local_mutations.where('status').anyOf('pending', 'inflight').count(),
    );
  }

  async create(fields: TaskFields): Promise<LocalTask> {
    const now = this.timestamp();
    const task: LocalTask = {
      ...normalizeFields(fields),
      id: this.newId(),
      createdAt: now,
      updatedAt: now,
      deletedAt: null,
      serverVersion: null,
      syncState: 'pending',
    };
    await this.db.transaction('rw', this.db.local_tasks, this.db.local_mutations, async () => {
      await this.db.local_tasks.add(task);
      await this.db.local_mutations.add(buildMutation(task, 'upsert', now, this.newId));
    });
    return task;
  }

  /**
   * Edição de campos. Com `origin` (a cópia que o formulário exibiu ao abrir): se o conteúdo mudou por
   * baixo do formulário — pull de outro dispositivo, outra aba —, a intenção se baseia na versão de
   * origem e o servidor responde conflito, em vez de sobrescrever em silêncio. Se só a versão avançou
   * (ack da própria edição), não há conflito. O status vem da cópia atual: o formulário não o edita, e
   * um "concluir" feito com o formulário aberto não é desfeito.
   */
  update(taskId: string, fields: TaskFields, origin?: LocalTask): Promise<LocalTask> {
    if (!origin) return this.change(taskId, () => fields);
    return this.change(
      taskId,
      (current) => ({ ...fields, status: current.status }),
      (current) => (contentChanged(origin, current) ? origin.serverVersion : current.serverVersion),
    );
  }

  setStatus(taskId: string, status: TaskFields['status']): Promise<LocalTask> {
    return this.change(taskId, (current) => ({ ...current, status }));
  }

  async delete(taskId: string): Promise<void> {
    const now = this.timestamp();
    await this.db.transaction('rw', this.db.local_tasks, this.db.local_mutations, async () => {
      const current = await this.db.local_tasks.get(taskId);
      if (!current || current.deletedAt !== null) {
        return; // exclusão idempotente
      }
      const mutations = await this.mutationsOf(taskId);
      // Mutação já enviada (inflight) ou recusada (conflict): o servidor pode conhecer a tarefa.
      const serverMayKnow =
        current.serverVersion !== null || mutations.some((m) => m.status !== 'pending');

      if (!serverMayKnow) {
        // O servidor nunca soube desta tarefa: criação + exclusão se anulam localmente.
        await this.db.local_mutations.bulkDelete(mutations.map((m) => m.seq!));
        await this.db.local_tasks.delete(taskId);
        return;
      }

      await this.db.local_tasks.put({
        ...current,
        deletedAt: now,
        updatedAt: now,
        syncState: nextSyncState(current),
      });
      const last = mutations.at(-1);
      if (last?.status === 'pending' && last.action === 'upsert') {
        // Edição ainda não enviada vira exclusão, mantendo a versão de origem.
        await this.db.local_mutations.update(last.seq!, {
          action: 'delete',
          payload: null,
          queuedAt: now,
        });
      } else {
        await this.db.local_mutations.add(buildMutation(current, 'delete', now, this.newId));
      }
    });
  }

  private async change(
    taskId: string,
    next: (current: LocalTask) => TaskFields,
    baseVersionOf: (current: LocalTask) => number | null = (current) => current.serverVersion,
  ): Promise<LocalTask> {
    const now = this.timestamp();
    return this.db.transaction('rw', this.db.local_tasks, this.db.local_mutations, async () => {
      const current = await this.db.local_tasks.get(taskId);
      if (!current || current.deletedAt !== null) {
        throw new Error(`Tarefa ${taskId} não encontrada.`);
      }
      const updated: LocalTask = {
        ...current,
        ...normalizeFields(next(current)),
        updatedAt: now,
        syncState: nextSyncState(current),
      };
      await this.db.local_tasks.put(updated);

      const last = (await this.mutationsOf(taskId)).at(-1);
      if (last?.status === 'pending' && last.action === 'upsert') {
        // Coalescência: substitui o snapshot da intenção ainda não enviada (mesma baseVersion e operationId).
        await this.db.local_mutations.update(last.seq!, {
          payload: toPayload(updated),
          queuedAt: now,
        });
      } else {
        const base = { ...updated, serverVersion: baseVersionOf(current) };
        await this.db.local_mutations.add(buildMutation(base, 'upsert', now, this.newId));
      }
      return updated;
    });
  }

  private mutationsOf(taskId: string): Promise<LocalMutation[]> {
    return this.db.local_mutations.where('taskId').equals(taskId).sortBy('seq');
  }

  private timestamp(): string {
    return this.now().toISOString();
  }
}

/** Campos editáveis pelo formulário (o status tem ação própria) diferentes entre duas cópias. */
function contentChanged(origin: LocalTask, current: LocalTask): boolean {
  return (
    origin.title !== current.title ||
    origin.description !== current.description ||
    origin.dueDate !== current.dueDate ||
    origin.priority !== current.priority ||
    origin.category !== current.category
  );
}

/** Conflito aberto continua visível até a decisão do usuário, mesmo com novas edições locais. */
function nextSyncState(current: LocalTask): LocalTask['syncState'] {
  return current.syncState === 'conflict' ? 'conflict' : 'pending';
}

/** Nova intenção `pending` com `operationId` próprio, baseada na versão de servidor conhecida. */
export function buildMutation(
  task: LocalTask,
  action: LocalMutation['action'],
  now: string,
  newId: () => string,
): LocalMutation {
  return {
    operationId: newId(),
    taskId: task.id,
    action,
    baseVersion: task.serverVersion,
    payload: action === 'upsert' ? toPayload(task) : null,
    status: 'pending',
    queuedAt: now,
    retryCount: 0,
  };
}

export function normalizeFields(fields: TaskFields): TaskFields {
  // U+0000 (colado de outra fonte) é recusado pelo servidor: nunca chega à fila.
  const clean = (text: string) => text.replaceAll('\u0000', '').trim();
  const description = clean(fields.description ?? '');
  const title = clean(fields.title);
  if (!title) {
    throw new Error('Título é obrigatório.');
  }
  return {
    title,
    description: description === '' ? null : description,
    dueDate: fields.dueDate ? fields.dueDate : null,
    priority: fields.priority,
    status: fields.status,
    category: clean(fields.category) || DEFAULT_CATEGORY,
  };
}

export function toPayload(task: TaskFields & { createdAt: string }): UpsertPayload {
  return {
    title: task.title,
    description: task.description,
    dueDate: task.dueDate,
    priority: task.priority,
    status: task.status,
    category: task.category,
    createdAt: task.createdAt,
  };
}
