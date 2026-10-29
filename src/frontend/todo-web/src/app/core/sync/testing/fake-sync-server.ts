import type { ServerTask, TaskFields } from '../../offline/local-models';
import {
  type PullChange,
  type PullResponse,
  type PushOperation,
  type PushRequest,
  type PushResponse,
  type PushResult,
  type SyncApi,
  SyncHttpError,
} from '../sync-contract';

/**
 * Servidor de sincronização em memória, SOMENTE para testes: implementa as regras de
 * docs/SYNC_CONTRACT.md para um único usuário (idempotência por operationId + conteúdo, baseVersion,
 * tombstones, change log paginado) e permite simular falhas de rede no momento exato.
 */
export class FakeSyncServer {
  readonly tasks = new Map<string, ServerTask>();
  readonly log: PullChange[] = [];
  readonly pushRequests: PushRequest[] = [];
  readonly pullCursors: number[] = [];

  /** API inalcançável (rede/servidor fora do ar). */
  reachable = true;
  authExpired = false;
  /** Aplica o próximo push e perde a resposta (timeout depois do commit). */
  dropNextPushResponse = false;
  /** Executado depois de aplicar o push e antes de responder (ex.: o usuário edita durante o envio). */
  beforePushResponse: (() => Promise<void>) | null = null;
  /** Ids de tarefas de outra conta (criação devolve `already_exists` sem dados). */
  readonly foreignTaskIds = new Set<string>();
  /** Título recusado como `validation_error`. */
  rejectTitle: string | null = null;

  private readonly processed = new Map<string, { content: string; result: PushResult }>();
  private tick = 0;

  api(): SyncApi {
    return {
      health: async () => this.guard(),
      push: (request) => this.push(request),
      pull: async (cursor, limit) => this.pull(cursor, limit),
    };
  }

  private guard(): void {
    if (!this.reachable) throw new SyncHttpError('unreachable', 'API inalcançável (simulado).');
    if (this.authExpired) throw new SyncHttpError('auth', '401 (simulado).');
  }

  private async push(request: PushRequest): Promise<PushResponse> {
    this.guard();
    this.pushRequests.push(structuredClone(request));
    const results = request.operations.map((operation) => this.apply(operation));
    if (this.beforePushResponse) {
      const hook = this.beforePushResponse;
      this.beforePushResponse = null;
      await hook();
    }
    if (this.dropNextPushResponse) {
      this.dropNextPushResponse = false;
      throw new SyncHttpError('unreachable', 'Timeout depois do commit (simulado).');
    }
    return { results };
  }

  private pull(cursor: number, limit: number): PullResponse {
    this.guard();
    this.pullCursors.push(cursor);
    const after = this.log.filter((change) => change.revision > cursor);
    const changes = structuredClone(after.slice(0, limit));
    return {
      changes,
      nextCursor: changes.at(-1)?.revision ?? cursor,
      hasMore: after.length > limit,
    };
  }

  private apply(operation: PushOperation): PushResult {
    const { operationId, taskId, action, baseVersion, payload } = operation;
    const content = JSON.stringify([
      taskId,
      action,
      baseVersion,
      action === 'upsert' ? payload : null,
    ]);
    const seen = this.processed.get(operationId);
    if (seen) {
      return seen.content === content
        ? { ...structuredClone(seen.result), status: 'duplicate' }
        : { operationId, status: 'validation_error', errors: { operationId: ['reutilizado'] } };
    }
    if (action === 'delete' && baseVersion === null) {
      return { operationId, status: 'validation_error', errors: { baseVersion: ['obrigatório'] } };
    }
    if (payload && payload.title === this.rejectTitle) {
      return { operationId, status: 'validation_error', errors: { title: ['recusado'] } };
    }

    const current = this.tasks.get(taskId);
    const conflict = (reason: PushResult['reason'], serverTask: ServerTask | null): PushResult => ({
      operationId,
      status: 'conflict',
      reason,
      serverTask: structuredClone(serverTask),
    });

    if (action === 'upsert' && baseVersion === null) {
      if (this.foreignTaskIds.has(taskId)) return conflict('already_exists', null);
      if (current) return conflict('already_exists', current);
      const now = this.now();
      return this.commit(operationId, content, {
        ...fieldsOf(payload!),
        id: taskId,
        version: 1,
        createdAt: payload!.createdAt,
        updatedAt: now,
        deletedAt: null,
      });
    }
    if (!current) return conflict('not_found', null);
    if (current.deletedAt) return conflict('deleted', current);
    if (current.version !== baseVersion) return conflict('version_mismatch', current);

    const now = this.now();
    const next: ServerTask =
      action === 'delete'
        ? { ...current, version: current.version + 1, updatedAt: now, deletedAt: now }
        : { ...current, ...fieldsOf(payload!), version: current.version + 1, updatedAt: now };
    return this.commit(operationId, content, next);
  }

  private commit(operationId: string, content: string, task: ServerTask): PushResult {
    this.tasks.set(task.id, task);
    const revision = this.log.length + 1;
    this.log.push({
      revision,
      action: task.deletedAt ? 'delete' : 'upsert',
      task: structuredClone(task),
    });
    const result: PushResult = {
      operationId,
      status: 'applied',
      serverRevision: revision,
      serverTask: structuredClone(task),
    };
    this.processed.set(operationId, { content, result });
    return structuredClone(result);
  }

  private now(): string {
    return new Date(Date.UTC(2026, 9, 29, 12, 0, this.tick++)).toISOString();
  }
}

function fieldsOf(fields: TaskFields): TaskFields {
  const { title, description, dueDate, priority, status, category } = fields;
  return { title, description, dueDate, priority, status, category };
}
