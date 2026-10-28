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
 * Coalescência só atua em mutações `pending` (nunca enviadas). Mutações `inflight` jamais são
 * alteradas: novas edições viram novas mutações, cuja `baseVersion` o SyncEngine ajusta no ack (Fase B).
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

  pendingMutationCount$(): Observable<number> {
    return liveQuery(() => this.db.local_mutations.count());
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
      await this.db.local_mutations.add(this.newMutation(task, 'upsert', now));
    });
    return task;
  }

  update(taskId: string, fields: TaskFields): Promise<LocalTask> {
    return this.change(taskId, () => fields);
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
      const hasInflight = mutations.some((m) => m.status === 'inflight');

      if (current.serverVersion === null && !hasInflight) {
        // O servidor nunca soube desta tarefa: criação + exclusão se anulam localmente.
        await this.db.local_mutations.bulkDelete(mutations.map((m) => m.seq!));
        await this.db.local_tasks.delete(taskId);
        return;
      }

      await this.db.local_tasks.put({
        ...current,
        deletedAt: now,
        updatedAt: now,
        syncState: 'pending',
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
        await this.db.local_mutations.add(this.newMutation(current, 'delete', now));
      }
    });
  }

  private async change(
    taskId: string,
    next: (current: LocalTask) => TaskFields,
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
        syncState: 'pending',
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
        await this.db.local_mutations.add(this.newMutation(updated, 'upsert', now));
      }
      return updated;
    });
  }

  private mutationsOf(taskId: string): Promise<LocalMutation[]> {
    return this.db.local_mutations.where('taskId').equals(taskId).sortBy('seq');
  }

  private newMutation(
    task: LocalTask,
    action: LocalMutation['action'],
    now: string,
  ): LocalMutation {
    return {
      operationId: this.newId(),
      taskId: task.id,
      action,
      baseVersion: task.serverVersion,
      payload: action === 'upsert' ? toPayload(task) : null,
      status: 'pending',
      queuedAt: now,
      retryCount: 0,
    };
  }

  private timestamp(): string {
    return this.now().toISOString();
  }
}

export function normalizeFields(fields: TaskFields): TaskFields {
  const description = fields.description?.trim() ?? '';
  const title = fields.title.trim();
  if (!title) {
    throw new Error('Título é obrigatório.');
  }
  return {
    title,
    description: description === '' ? null : description,
    dueDate: fields.dueDate ? fields.dueDate : null,
    priority: fields.priority,
    status: fields.status,
    category: fields.category.trim() || DEFAULT_CATEGORY,
  };
}

function toPayload(task: LocalTask): UpsertPayload {
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
