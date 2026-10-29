import type {
  ConflictReason,
  MutationAction,
  ServerTask,
  UpsertPayload,
} from '../offline/local-models';

// Contratos HTTP de docs/SYNC_CONTRACT.md (§1 push, §2 pull).

export interface PushOperation {
  operationId: string;
  taskId: string;
  action: MutationAction;
  baseVersion: number | null;
  payload: UpsertPayload | null;
}

export interface PushRequest {
  deviceId: string;
  operations: PushOperation[];
}

export type PushStatus = 'applied' | 'duplicate' | 'conflict' | 'validation_error';

export interface PushResult {
  operationId: string;
  status: PushStatus;
  serverRevision?: number;
  serverTask?: ServerTask | null;
  reason?: Exclude<ConflictReason, 'validation_error'>;
  errors?: Record<string, string[]>;
}

export interface PushResponse {
  results: PushResult[];
}

export interface PullChange {
  revision: number;
  action: MutationAction;
  task: ServerTask;
}

export interface PullResponse {
  changes: PullChange[];
  nextCursor: number;
  hasMore: boolean;
}

/**
 * `unreachable`: sem resposta útil da API (rede, timeout, 502/503/504) — estado `offline`.
 * `auth`: 401 — estado `auth_required`. `server`/`client`: falha inesperada — estado `error`.
 */
export type SyncFailureKind = 'unreachable' | 'auth' | 'server' | 'client';

export class SyncHttpError extends Error {
  constructor(
    readonly kind: SyncFailureKind,
    message: string,
  ) {
    super(message);
    this.name = 'SyncHttpError';
  }
}

/** Porta do SyncEngine para a API (implementada via HttpClient e, nos testes, por um servidor falso). */
export interface SyncApi {
  /** Sonda real de conectividade: `navigator.onLine` não prova que a API responde. */
  health(): Promise<void>;
  push(request: PushRequest): Promise<PushResponse>;
  pull(cursor: number, limit: number): Promise<PullResponse>;
}
