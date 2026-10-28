import { Injectable } from '@angular/core';
import { TodoLocalDb } from './todo-local-db';

/** Mantém aberto somente o banco local do usuário da sessão ativa. */
@Injectable({ providedIn: 'root' })
export class LocalDatabase {
  private db: TodoLocalDb | null = null;

  forUser(userId: string): TodoLocalDb {
    if (this.db?.userId !== userId) {
      this.db?.close();
      this.db = new TodoLocalDb(userId);
    }
    return this.db;
  }

  /** Fecha a conexão sem apagar dados: pendências do usuário sobrevivem ao logout. */
  close(): void {
    this.db?.close();
    this.db = null;
  }
}
