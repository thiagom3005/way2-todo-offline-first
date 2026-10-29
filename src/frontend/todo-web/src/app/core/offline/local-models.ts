export type TaskPriority = 'low' | 'medium' | 'high';
export type TaskStatus = 'pending' | 'completed';

/** Campos editáveis de uma tarefa (payload de upsert do contrato de sync). */
export interface TaskFields {
  title: string;
  description: string | null;
  /** Data civil `YYYY-MM-DD` (date-only, sem fuso). */
  dueDate: string | null;
  priority: TaskPriority;
  status: TaskStatus;
  category: string;
}

export const TASK_LIMITS = { title: 200, description: 2000, category: 50 } as const;
export const DEFAULT_CATEGORY = 'Geral';

/**
 * `pending`: há mutação local ainda não confirmada pelo servidor.
 * `synced`: cópia igual à última versão confirmada. `conflict`: aguarda decisão do usuário.
 */
export type LocalSyncState = 'pending' | 'synced' | 'conflict';

/** Snapshot de tarefa como o servidor a devolve (docs/SYNC_CONTRACT.md, "Modelo de tarefa"). */
export interface ServerTask extends TaskFields {
  id: string;
  version: number;
  createdAt: string;
  updatedAt: string;
  deletedAt: string | null;
}

/** Cópia de trabalho exibida pela UI (fonte de leitura, mesmo online). */
export interface LocalTask extends TaskFields {
  id: string;
  /** Instante UTC ISO 8601 da criação (pode ter ocorrido offline). */
  createdAt: string;
  /** Instante UTC ISO 8601 da última edição local. */
  updatedAt: string;
  /** Tombstone local: tarefa excluída aguardando envio; oculta da lista. */
  deletedAt: string | null;
  /** Última versão confirmada pelo servidor; `null` = servidor ainda não conhece a tarefa. */
  serverVersion: number | null;
  syncState: LocalSyncState;
  /**
   * Versão remota mais nova recebida no pull enquanto havia mutação local: não sobrescreve a cópia de
   * trabalho, mas fica registrada para o ack e a resolução de conflito usarem o estado mais recente.
   */
  remoteSnapshot?: ServerTask;
}

export type MutationAction = 'upsert' | 'delete';

/**
 * `pending`: nunca enviada — pode ser coalescida.
 * `inflight`: já enviada (ou em envio) — `operationId` e `payload` congelados; nunca volta a `pending`.
 * `conflict`: recusada pelo servidor; bloqueia a fila da tarefa até a decisão do usuário.
 */
export type MutationStatus = 'pending' | 'inflight' | 'conflict';

export interface UpsertPayload extends TaskFields {
  createdAt: string;
}

/** Intenção durável de alteração (fila local de operações). */
export interface LocalMutation {
  /** Auto-incremento: ordem FIFO global de enfileiramento. */
  seq?: number;
  /** UUID estável usado pelo servidor para idempotência. */
  operationId: string;
  taskId: string;
  action: MutationAction;
  /** Versão do servidor em que a intenção se baseia; `null` só para criação. */
  baseVersion: number | null;
  payload: UpsertPayload | null;
  status: MutationStatus;
  queuedAt: string;
  retryCount: number;
}

/** Chave/valor do sincronizador (cursor de pull, identificador do dispositivo). */
export interface SyncMetadataEntry {
  key: string;
  value: unknown;
}

export type ConflictReason =
  'version_mismatch' | 'deleted' | 'already_exists' | 'not_found' | 'validation_error';

/** Operação recusada pelo servidor, aguardando decisão do usuário (uma por tarefa). */
export interface LocalConflict {
  taskId: string;
  /** Mutação `conflict` que originou o conflito; a resolução só vale para ela. */
  operationId: string;
  reason: ConflictReason;
  /** Estado do servidor no momento da recusa; `null` quando não existe ou não pertence ao usuário. */
  serverTask: ServerTask | null;
  errors: Record<string, string[]> | null;
  detectedAt: string;
}
