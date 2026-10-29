import Dexie, { type Table } from 'dexie';
import type { LocalConflict, LocalMutation, LocalTask, SyncMetadataEntry } from './local-models';

/**
 * Banco IndexedDB de um único usuário (`way2-todo-<userId>`): a aplicação só abre o banco do
 * usuário da sessão ativa, então dados de contas diferentes nunca se misturam.
 *
 * Durabilidade `strict`: o Chromium só confirma a transação depois de gravá-la em disco, para que o
 * congelamento `pending → inflight` persista antes do envio. Reduz a janela de perda, mas não é garantia
 * absoluta (suporte varia entre navegadores); a recuperação continua em retries e idempotência.
 * O schema segue na versão 1: os campos adicionados na Fase B são opcionais e não exigem índice.
 */
export class TodoLocalDb extends Dexie {
  local_tasks!: Table<LocalTask, string>;
  local_mutations!: Table<LocalMutation, number>;
  sync_metadata!: Table<SyncMetadataEntry, string>;
  conflicts!: Table<LocalConflict, string>;

  constructor(readonly userId: string) {
    super(TodoLocalDb.nameFor(userId), { chromeTransactionDurability: 'strict' });
    this.version(1).stores({
      local_tasks: 'id, syncState',
      local_mutations: '++seq, &operationId, taskId, status',
      sync_metadata: 'key',
      conflicts: 'taskId',
    });
  }

  static nameFor(userId: string): string {
    return `way2-todo-${userId}`;
  }
}
