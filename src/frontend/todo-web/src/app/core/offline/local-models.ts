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
 * `synced`/`conflict` serão atribuídos pelo SyncEngine (Fase B).
 */
export type LocalSyncState = 'pending' | 'synced' | 'conflict';

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
}

export type MutationAction = 'upsert' | 'delete';

/**
 * `pending`: nunca enviada — pode ser coalescida.
 * `inflight`: já enviada (ou em envio) — `operationId` e `payload` congelados; nunca volta a `pending`.
 */
export type MutationStatus = 'pending' | 'inflight';

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

/** Chave/valor do sincronizador (cursor de pull, último sucesso) — usado a partir da Fase B. */
export interface SyncMetadataEntry {
  key: string;
  value: unknown;
}

/** Conflito aguardando decisão do usuário — preenchido pelo SyncEngine na Fase B. */
export interface LocalConflict {
  taskId: string;
  operationId: string;
  serverTask: unknown;
  detectedAt: string;
}
