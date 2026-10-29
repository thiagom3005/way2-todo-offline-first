import { Component, computed, DestroyRef, effect, inject, signal, viewChild } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { from } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { LocalDatabase } from '../../core/offline/local-database';
import type { LocalTask, TaskFields } from '../../core/offline/local-models';
import { LocalTaskRepository } from '../../core/offline/local-task-repository';
import { SyncService } from '../../core/sync/sync.service';
import { ConflictPanel, type ConflictDecision } from './conflict-panel';
import { todayLocalIsoDate } from './local-date';
import { TaskForm } from './task-form';
import { TaskItem } from './task-item';
import { buildTaskSections, listCategories, NO_FILTERS, type TaskFilters } from './task-view';

@Component({
  selector: 'app-tasks-page',
  imports: [ConflictPanel, TaskForm, TaskItem],
  templateUrl: './tasks.page.html',
})
export class TasksPage {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly localDatabase = inject(LocalDatabase);
  protected readonly sync = inject(SyncService);
  // authGuard garante usuário previamente autenticado; o banco aberto é sempre o dele.
  private readonly db = this.localDatabase.forUser(this.auth.user()!.id);
  private readonly repository = new LocalTaskRepository(this.db);

  protected readonly user = this.auth.user;
  protected readonly today = signal(todayLocalIsoDate());

  /** A UI sempre lê do IndexedDB (liveQuery), inclusive online. */
  protected readonly tasks = toSignal(from(this.repository.activeTasks$()), {
    initialValue: [] as LocalTask[],
  });

  protected readonly filters = signal<TaskFilters>(NO_FILTERS);
  protected readonly groupByCategory = signal(true);
  protected readonly editing = signal<LocalTask | null>(null);
  protected readonly error = signal<string | null>(null);
  private readonly taskForm = viewChild.required(TaskForm);

  protected readonly sections = computed(() =>
    buildTaskSections(this.tasks(), this.filters(), this.groupByCategory()),
  );
  protected readonly categories = computed(() => listCategories(this.tasks()));
  protected readonly categoryLabels = computed(() => this.categories().map((c) => c.label));

  constructor() {
    this.sync.start(this.auth.user()!.id, this.db);
    inject(DestroyRef).onDestroy(() => this.sync.stop());

    // Categoria filtrada que deixou de existir (última tarefa excluída ou movida) volta para
    // "todas"; senão o seletor exibiria "Todas as categorias" com a lista vazia por um filtro oculto.
    effect(() => {
      const { category } = this.filters();
      if (category && !this.categories().some((option) => option.key === category)) {
        this.setFilter('category', '');
      }
    });
  }

  protected async save(fields: TaskFields): Promise<void> {
    const editing = this.editing();
    const saved = await this.run(async () => {
      if (editing) {
        // A cópia exibida ao abrir o formulário: mudança remota nesse meio-tempo vira conflito.
        await this.repository.update(editing.id, fields, editing);
        this.editing.set(null);
      } else {
        await this.repository.create(fields);
      }
    });
    // Só limpa depois de gravado: se o IndexedDB falhar (cota), o texto digitado não se perde.
    if (saved && !editing) this.taskForm().clearForNext();
  }

  protected toggle(task: LocalTask): Promise<boolean> {
    return this.run(() =>
      this.repository.setStatus(task.id, task.status === 'completed' ? 'pending' : 'completed'),
    );
  }

  protected async remove(task: LocalTask): Promise<void> {
    if (!confirm(`Excluir "${task.title}"?`)) return;
    await this.run(() => this.repository.delete(task.id));
    if (this.editing()?.id === task.id) this.editing.set(null);
  }

  protected async resolveConflict({ view, choice }: ConflictDecision): Promise<void> {
    this.error.set(null);
    try {
      const outcome = await this.sync.resolve(view, choice);
      if (outcome === 'changed') {
        this.error.set(
          'A tarefa mudou enquanto você decidia. Revise as versões e escolha de novo.',
        );
      }
    } catch {
      this.error.set('Não foi possível aplicar a decisão neste dispositivo. Tente novamente.');
    }
  }

  protected setFilter<K extends keyof TaskFilters>(key: K, value: string): void {
    this.filters.update((current) => ({ ...current, [key]: value as TaskFilters[K] }));
  }

  /** Login de novo sem sair: offline, a sessão atual continua dando acesso às tarefas locais. */
  protected reauthenticate(): void {
    void this.router.navigateByUrl('/login');
  }

  /** Sai preservando tarefas e pendências locais no banco do usuário. */
  protected logout(): void {
    this.sync.stop();
    this.auth.logout();
    this.localDatabase.close();
    void this.router.navigateByUrl('/login');
  }

  private async run(action: () => Promise<unknown>): Promise<boolean> {
    this.error.set(null);
    try {
      await action();
      this.sync.requestSync();
      return true;
    } catch {
      this.error.set('Não foi possível salvar neste dispositivo. Tente novamente.');
      return false;
    }
  }
}
