import { Component, computed, input, output } from '@angular/core';
import type { LocalTask } from '../../core/offline/local-models';
import { formatDueDate, isOverdue } from './local-date';
import { isUrgent } from './urgency';

const PRIORITY_LABELS = { low: 'Baixa', medium: 'Média', high: 'Alta' } as const;

@Component({
  selector: 'app-task-item',
  template: `
    <article
      class="task"
      [class.completed]="completed()"
      [class.overdue]="overdue()"
      [attr.data-task-id]="task().id"
    >
      <input
        type="checkbox"
        [checked]="completed()"
        (change)="toggled.emit()"
        [attr.aria-label]="(completed() ? 'Reabrir ' : 'Concluir ') + task().title"
      />
      <div class="task-body">
        <p class="task-title">
          @if (urgent()) {
            <span class="badge urgent">Urgente</span>
          }
          <span data-testid="task-title">{{ task().title }}</span>
        </p>
        @if (task().description) {
          <p class="task-description">{{ task().description }}</p>
        }
        <p class="task-meta">
          <span class="chip">{{ task().category }}</span>
          <span class="priority priority-{{ task().priority }}">{{ priorityLabel() }}</span>
          @if (task().dueDate; as dueDate) {
            <span class="due"
              >{{ overdue() ? 'Venceu' : 'Vence' }} {{ formatDueDate(dueDate) }}</span
            >
          }
          @if (task().syncState === 'pending') {
            <span class="local" title="Salvo neste dispositivo; ainda não confirmado pelo servidor">
              não sincronizada
            </span>
          } @else if (task().syncState === 'conflict') {
            <span class="local conflict" title="Alterada em outro dispositivo; escolha a versão">
              conflito
            </span>
          }
        </p>
      </div>
      <div class="task-actions">
        <button type="button" (click)="edit.emit()">Editar</button>
        <button type="button" class="danger" (click)="remove.emit()">Excluir</button>
      </div>
    </article>
  `,
})
export class TaskItem {
  readonly task = input.required<LocalTask>();
  /** Data civil local (`YYYY-MM-DD`) usada para destacar vencidas. */
  readonly today = input.required<string>();
  readonly toggled = output<void>();
  readonly edit = output<void>();
  readonly remove = output<void>();

  protected readonly formatDueDate = formatDueDate;
  protected readonly completed = computed(() => this.task().status === 'completed');
  protected readonly urgent = computed(() => isUrgent(this.task()));
  protected readonly overdue = computed(() => isOverdue(this.task(), this.today()));
  protected readonly priorityLabel = computed(() => PRIORITY_LABELS[this.task().priority]);
}
