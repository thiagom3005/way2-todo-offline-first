import type { LocalMutation, LocalTask, TaskFields } from '../../offline/local-models';
import { LocalTaskRepository } from '../../offline/local-task-repository';
import { TodoLocalDb } from '../../offline/todo-local-db';
import { SyncEngine, type SyncEngineOptions } from '../sync-engine';
import { SyncStore } from '../sync-store';
import type { FakeSyncServer } from './fake-sync-server';

/** SOMENTE para testes: um navegador/dispositivo com banco e fila próprios, falando com o mesmo servidor. */
export class SyncDevice {
  readonly db: TodoLocalDb;
  readonly repo: LocalTaskRepository;
  readonly store: SyncStore;
  readonly engine: SyncEngine;

  constructor(
    readonly name: string,
    readonly server: FakeSyncServer,
    options: Partial<SyncEngineOptions> = {},
  ) {
    this.db = new TodoLocalDb(name);
    this.repo = new LocalTaskRepository(this.db);
    this.store = new SyncStore(this.db);
    this.engine = new SyncEngine(this.store, server.api(), options);
  }

  sync() {
    return this.engine.run();
  }

  task(id: string): Promise<LocalTask | undefined> {
    return this.db.local_tasks.get(id);
  }

  async visibleTitles(): Promise<string[]> {
    const tasks = await this.db.local_tasks.filter((t) => t.deletedAt === null).toArray();
    return tasks.map((t) => t.title).sort();
  }

  mutations(): Promise<LocalMutation[]> {
    return this.db.local_mutations.orderBy('seq').toArray();
  }
}

export const taskFields = (overrides: Partial<TaskFields> = {}): TaskFields => ({
  title: 'Conferir medição',
  description: null,
  dueDate: '2026-10-30',
  priority: 'high',
  status: 'pending',
  category: 'Campo',
  ...overrides,
});
