import { liveQuery, type Observable } from 'dexie';
import type { LocalConflict, LocalMutation, LocalTask, ServerTask } from '../offline/local-models';
import { buildMutation } from '../offline/local-task-repository';
import type { TodoLocalDb } from '../offline/todo-local-db';
import { newUuid } from '../offline/uuid';
import type { PullResponse, PushOperation, PushResult } from './sync-contract';

const CURSOR_KEY = 'pullCursor';
const DEVICE_KEY = 'deviceId';

/** Conflito aberto com as duas versões a comparar (cópia de trabalho atual × servidor mais recente). */
export interface ConflictView {
  conflict: LocalConflict;
  local: LocalTask | undefined;
  server: ServerTask | null;
}

/**
 * `resolved`: decisão aplicada. `stale`: o conflito já foi resolvido (outra aba/clique repetido).
 * `changed`: a tarefa mudou depois que a tela foi exibida; nada foi feito, o usuário revisa de novo.
 */
export type ResolutionOutcome = 'resolved' | 'stale' | 'changed';

/**
 * Lado IndexedDB da sincronização. Cada método roda numa única transação `rw`: ou tudo persiste,
 * ou nada — e reaplicar o mesmo resultado/página é seguro (idempotente).
 */
export class SyncStore {
  constructor(
    private readonly db: TodoLocalDb,
    private readonly now: () => Date = () => new Date(),
    private readonly newId: () => string = newUuid,
  ) {}

  /**
   * Seleciona a cabeça da fila (menor `seq`) de cada tarefa e a congela antes do envio:
   * `pending → inflight`. Uma `inflight` é reenviada exatamente igual (mesmo `operationId`, `baseVersion`
   * e `payload`); só `retryCount` muda. Tarefas em conflito ficam de fora até a decisão do usuário.
   */
  claimBatch(limit: number): Promise<PushOperation[]> {
    return this.db.transaction('rw', this.db.local_mutations, async () => {
      const heads = new Map<string, LocalMutation>();
      for (const mutation of await this.db.local_mutations.orderBy('seq').toArray()) {
        if (!heads.has(mutation.taskId)) heads.set(mutation.taskId, mutation);
      }

      const batch: LocalMutation[] = [];
      for (const head of heads.values()) {
        if (batch.length >= limit) break;
        if (head.status === 'conflict') continue;
        // Exclusão de uma criação ainda sem ack: espera o rebase para a versão criada.
        if (head.action === 'delete' && head.baseVersion === null) continue;
        const claimed: LocalMutation =
          head.status === 'inflight'
            ? { ...head, retryCount: head.retryCount + 1 }
            : { ...head, status: 'inflight' };
        await this.db.local_mutations.put(claimed);
        batch.push(claimed);
      }
      return batch.map(toOperation);
    });
  }

  /** Aplica o resultado de uma operação enviada. Resultado repetido ou de mutação desconhecida é ignorado. */
  applyResult(result: PushResult): Promise<void> {
    return this.db.transaction(
      'rw',
      this.db.local_tasks,
      this.db.local_mutations,
      this.db.conflicts,
      async () => {
        const mutation = await this.db.local_mutations
          .where('operationId')
          .equals(result.operationId)
          .first();
        if (mutation?.status !== 'inflight') return;
        if (result.serverTask && result.serverTask.id !== mutation.taskId) {
          throw new Error(`Resultado da operação ${result.operationId} aponta para outra tarefa.`);
        }
        const task = await this.db.local_tasks.get(mutation.taskId);

        if (result.status === 'applied' || result.status === 'duplicate') {
          if (!result.serverTask) throw new Error(`Ack sem serverTask (${result.operationId}).`);
          await this.acknowledge(mutation, task, result.serverTask);
        } else {
          await this.recordConflict(mutation, task, result);
        }
      },
    );
  }

  /**
   * Aplica uma página do pull e avança o cursor na mesma transação. Tarefa com mutação local não tem a
   * cópia de trabalho sobrescrita (só registra a versão remota); versão já conhecida é ignorada, o que
   * torna o replay seguro e impede ressuscitar tarefas excluídas.
   */
  applyPullPage(page: PullResponse): Promise<void> {
    return this.db.transaction(
      'rw',
      this.db.local_tasks,
      this.db.local_mutations,
      this.db.sync_metadata,
      async () => {
        for (const { task: remote } of page.changes) {
          const local = await this.db.local_tasks.get(remote.id);
          const dirty =
            (await this.db.local_mutations.where('taskId').equals(remote.id).count()) > 0;
          if (dirty) {
            if (local) {
              await this.db.local_tasks.put({
                ...local,
                remoteSnapshot: newest(remote, local.remoteSnapshot),
              });
            }
            continue;
          }
          if (local?.serverVersion != null && remote.version <= local.serverVersion) continue;
          await this.db.local_tasks.put(fromServer(remote));
        }
        await this.db.sync_metadata.put({ key: CURSOR_KEY, value: page.nextCursor });
      },
    );
  }

  async cursor(): Promise<number> {
    const entry = await this.db.sync_metadata.get(CURSOR_KEY);
    return typeof entry?.value === 'number' ? entry.value : 0;
  }

  /** Identificador estável deste dispositivo/navegador (informativo, enviado no push). */
  deviceId(): Promise<string> {
    return this.db.transaction('rw', this.db.sync_metadata, async () => {
      const entry = await this.db.sync_metadata.get(DEVICE_KEY);
      if (typeof entry?.value === 'string') return entry.value;
      const id = this.newId();
      await this.db.sync_metadata.put({ key: DEVICE_KEY, value: id });
      return id;
    });
  }

  async hasInflight(): Promise<boolean> {
    return (await this.db.local_mutations.where('status').equals('inflight').count()) > 0;
  }

  /** Envio aguardando ou em voo (conflitos são contados à parte). */
  pendingCount$(): Observable<number> {
    return liveQuery(() =>
      this.db.local_mutations.where('status').anyOf('pending', 'inflight').count(),
    );
  }

  conflictCount$(): Observable<number> {
    return liveQuery(() => this.db.conflicts.count());
  }

  /** Conflitos abertos, inclusive de tarefas excluídas localmente (que não aparecem na lista). */
  conflicts$(): Observable<ConflictView[]> {
    return liveQuery(async () => {
      const conflicts = await this.db.conflicts.orderBy('taskId').toArray();
      const tasks = await this.db.local_tasks.bulkGet(conflicts.map((c) => c.taskId));
      return conflicts
        .map((conflict, i) => ({
          conflict,
          local: tasks[i],
          server: newest(conflict.serverTask, tasks[i]?.remoteSnapshot) ?? null,
        }))
        .sort((a, b) => a.conflict.detectedAt.localeCompare(b.conflict.detectedAt));
    });
  }

  /**
   * "Usar versão do servidor": descarta as intenções locais da tarefa e aplica o snapshot mais novo
   * do servidor (sem snapshot, a cópia local sai da lista).
   */
  useServerVersion(
    taskId: string,
    operationId: string,
    expectedUpdatedAt: string | null,
  ): Promise<ResolutionOutcome> {
    return this.resolve(
      taskId,
      operationId,
      expectedUpdatedAt,
      async ({ conflict, task, server }) => {
        if (server) {
          await this.db.local_tasks.put(fromServer(server));
          return;
        }
        await this.db.local_tasks.delete(taskId);
        if (conflict.reason === 'validation_error' && task?.serverVersion != null) {
          // O servidor tem a tarefa, mas não veio o snapshot: rebaixa o cursor para reobtê-la no pull.
          await this.db.sync_metadata.put({ key: CURSOR_KEY, value: 0 });
        }
      },
    );
  }

  /**
   * "Manter minha alteração": substitui as intenções da tarefa por UMA nova operação, com novo
   * `operationId` e `baseVersion` = versão atual do servidor, levando a cópia de trabalho atual
   * (inclusive edições feitas com o conflito aberto). Se o servidor não tem a tarefa ou ela foi
   * excluída lá, o conteúdo é recriado como uma NOVA tarefa — o registro original não ressuscita.
   */
  keepLocalVersion(
    taskId: string,
    operationId: string,
    expectedUpdatedAt: string | null,
  ): Promise<ResolutionOutcome> {
    return this.resolve(
      taskId,
      operationId,
      expectedUpdatedAt,
      async ({ conflict, task, server }) => {
        if (!task) return;
        const now = this.now().toISOString();
        const recreate =
          conflict.reason !== 'validation_error' && (server === null || server.deletedAt !== null);

        if (recreate) {
          if (server) await this.db.local_tasks.put(fromServer(server));
          else await this.db.local_tasks.delete(taskId);
          const copy: LocalTask = {
            ...task,
            id: this.newId(),
            updatedAt: now,
            deletedAt: null,
            serverVersion: null,
            syncState: 'pending',
            remoteSnapshot: undefined,
          };
          await this.db.local_tasks.add(copy);
          await this.db.local_mutations.add(buildMutation(copy, 'upsert', now, this.newId));
          return;
        }

        const rebased: LocalTask = {
          ...task,
          serverVersion: server ? server.version : task.serverVersion,
          syncState: 'pending',
          remoteSnapshot: undefined,
        };
        await this.db.local_tasks.put(rebased);
        const action = task.deletedAt === null ? 'upsert' : 'delete';
        await this.db.local_mutations.add(buildMutation(rebased, action, now, this.newId));
      },
    );
  }

  private resolve(
    taskId: string,
    operationId: string,
    expectedUpdatedAt: string | null,
    apply: (state: {
      conflict: LocalConflict;
      task: LocalTask | undefined;
      server: ServerTask | null;
    }) => Promise<void>,
  ): Promise<ResolutionOutcome> {
    return this.db.transaction(
      'rw',
      [this.db.local_tasks, this.db.local_mutations, this.db.conflicts, this.db.sync_metadata],
      async () => {
        const conflict = await this.db.conflicts.get(taskId);
        if (conflict?.operationId !== operationId) return 'stale';
        const task = await this.db.local_tasks.get(taskId);
        if ((task?.updatedAt ?? null) !== expectedUpdatedAt) return 'changed';
        const mutations = await this.mutationsOf(taskId);
        // Nunca descarta algo já enviado: só resolve com a fila da tarefa parada no conflito.
        if (mutations.some((m) => m.status === 'inflight')) return 'stale';

        await this.db.local_mutations.bulkDelete(mutations.map((m) => m.seq!));
        await this.db.conflicts.delete(taskId);
        const server = newest(conflict.serverTask, task?.remoteSnapshot) ?? null;
        await apply({ conflict, task, server });
        return 'resolved' as const;
      },
    );
  }

  private async acknowledge(
    mutation: LocalMutation,
    task: LocalTask | undefined,
    serverTask: ServerTask,
  ): Promise<void> {
    await this.db.local_mutations.delete(mutation.seq!);
    const remaining = await this.mutationsOf(mutation.taskId);
    // A próxima intenção da tarefa passa a se basear na versão recém-confirmada.
    for (const next of remaining) {
      if (next.status === 'pending') {
        await this.db.local_mutations.update(next.seq!, { baseVersion: serverTask.version });
      }
    }
    if (!task) return;

    if (remaining.length > 0) {
      // Há edição local mais nova: o ack só atualiza a versão, nunca os valores exibidos.
      await this.db.local_tasks.put({
        ...task,
        serverVersion: Math.max(task.serverVersion ?? 0, serverTask.version),
      });
      return;
    }
    const latest = newest(serverTask, task.remoteSnapshot)!;
    await this.db.local_tasks.put(
      latest.version >= (task.serverVersion ?? 0)
        ? fromServer(latest)
        : { ...task, syncState: 'synced', remoteSnapshot: undefined },
    );
  }

  private async recordConflict(
    mutation: LocalMutation,
    task: LocalTask | undefined,
    result: PushResult,
  ): Promise<void> {
    const serverTask = newest(result.serverTask ?? null, task?.remoteSnapshot) ?? null;

    // Exclusão local contra tarefa inexistente ou já excluída no servidor: as duas pontas querem a
    // tarefa fora da lista — não há decisão a tomar.
    if (
      result.status === 'conflict' &&
      task?.deletedAt != null &&
      (serverTask === null || serverTask.deletedAt !== null)
    ) {
      const mutations = await this.mutationsOf(mutation.taskId);
      await this.db.local_mutations.bulkDelete(mutations.map((m) => m.seq!));
      await this.db.local_tasks.put(
        serverTask
          ? fromServer(serverTask)
          : { ...task, syncState: 'synced', remoteSnapshot: undefined },
      );
      return;
    }

    await this.db.local_mutations.update(mutation.seq!, { status: 'conflict' });
    const conflict: LocalConflict = {
      taskId: mutation.taskId,
      operationId: mutation.operationId,
      reason:
        result.status === 'validation_error'
          ? 'validation_error'
          : (result.reason ?? 'version_mismatch'),
      serverTask,
      errors: result.errors ?? null,
      detectedAt: this.now().toISOString(),
    };
    await this.db.conflicts.put(conflict);
    if (task) await this.db.local_tasks.put({ ...task, syncState: 'conflict' });
  }

  private mutationsOf(taskId: string): Promise<LocalMutation[]> {
    return this.db.local_mutations.where('taskId').equals(taskId).sortBy('seq');
  }
}

/** Cópia local confirmada a partir de um snapshot do servidor (tombstone continua como linha oculta). */
export function fromServer(server: ServerTask): LocalTask {
  return {
    id: server.id,
    title: server.title,
    description: server.description,
    dueDate: server.dueDate,
    priority: server.priority,
    status: server.status,
    category: server.category,
    createdAt: server.createdAt,
    updatedAt: server.updatedAt,
    deletedAt: server.deletedAt,
    serverVersion: server.version,
    syncState: 'synced',
  };
}

/** O snapshot de maior versão (versões do servidor só crescem). */
export function newest(
  a: ServerTask | null | undefined,
  b: ServerTask | null | undefined,
): ServerTask | undefined {
  if (!a) return b ?? undefined;
  if (!b) return a;
  return b.version > a.version ? b : a;
}

function toOperation(mutation: LocalMutation): PushOperation {
  return {
    operationId: mutation.operationId,
    taskId: mutation.taskId,
    action: mutation.action,
    baseVersion: mutation.baseVersion,
    payload: mutation.payload,
  };
}
