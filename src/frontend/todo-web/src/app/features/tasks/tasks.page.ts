import { Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { from } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { LocalDatabase } from '../../core/offline/local-database';
import type { LocalTask, TaskFields } from '../../core/offline/local-models';
import { LocalTaskRepository } from '../../core/offline/local-task-repository';
import { todayLocalIsoDate } from './local-date';
import { TaskForm } from './task-form';
import { TaskItem } from './task-item';
import { buildTaskSections, listCategories, NO_FILTERS, type TaskFilters } from './task-view';

@Component({
  selector: 'app-tasks-page',
  imports: [TaskForm, TaskItem],
  templateUrl: './tasks.page.html',
})
export class TasksPage {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly localDatabase = inject(LocalDatabase);
  // authGuard garante usuário previamente autenticado; o banco aberto é sempre o dele.
  private readonly repository = new LocalTaskRepository(
    this.localDatabase.forUser(this.auth.user()!.id),
  );

  protected readonly user = this.auth.user;
  protected readonly sessionExpired = signal(this.auth.isTokenExpired());
  protected readonly today = signal(todayLocalIsoDate());

  /** A UI sempre lê do IndexedDB (liveQuery), inclusive online. */
  protected readonly tasks = toSignal(from(this.repository.activeTasks$()), {
    initialValue: [] as LocalTask[],
  });
  protected readonly pendingCount = toSignal(from(this.repository.pendingMutationCount$()), {
    initialValue: 0,
  });

  protected readonly filters = signal<TaskFilters>(NO_FILTERS);
  protected readonly groupByCategory = signal(true);
  protected readonly editing = signal<LocalTask | null>(null);
  protected readonly error = signal<string | null>(null);

  protected readonly sections = computed(() =>
    buildTaskSections(this.tasks(), this.filters(), this.groupByCategory()),
  );
  protected readonly categories = computed(() => listCategories(this.tasks()));
  protected readonly categoryLabels = computed(() => this.categories().map((c) => c.label));

  protected async save(fields: TaskFields): Promise<void> {
    const editing = this.editing();
    await this.run(async () => {
      if (editing) {
        await this.repository.update(editing.id, fields);
        this.editing.set(null);
      } else {
        await this.repository.create(fields);
      }
    });
  }

  protected toggle(task: LocalTask): Promise<void> {
    return this.run(() =>
      this.repository.setStatus(task.id, task.status === 'completed' ? 'pending' : 'completed'),
    );
  }

  protected async remove(task: LocalTask): Promise<void> {
    if (!confirm(`Excluir "${task.title}"?`)) return;
    await this.run(() => this.repository.delete(task.id));
    if (this.editing()?.id === task.id) this.editing.set(null);
  }

  protected setFilter<K extends keyof TaskFilters>(key: K, value: string): void {
    this.filters.update((current) => ({ ...current, [key]: value as TaskFilters[K] }));
  }

  protected logout(): void {
    this.auth.logout();
    this.localDatabase.close();
    void this.router.navigateByUrl('/login');
  }

  private async run(action: () => Promise<unknown>): Promise<void> {
    this.error.set(null);
    try {
      await action();
    } catch {
      this.error.set('Não foi possível salvar neste dispositivo. Tente novamente.');
    }
  }
}
