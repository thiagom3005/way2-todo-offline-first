import { NgTemplateOutlet } from '@angular/common';
import { Component, input, output } from '@angular/core';
import type { TaskFields } from '../../core/offline/local-models';
import type { ConflictView } from '../../core/sync/sync-store';
import { formatDueDate } from './local-date';

export interface ConflictDecision {
  view: ConflictView;
  choice: 'server' | 'local';
}

const PRIORITY = { low: 'Baixa', medium: 'Média', high: 'Alta' } as const;
const STATUS = { pending: 'Pendente', completed: 'Concluída' } as const;

/** Rótulos por motivo: a decisão é sempre explícita e o texto diz o que cada botão faz. */
export function conflictCopy(view: ConflictView): {
  reason: string;
  server: string;
  local: string;
} {
  const { reason, errors } = view.conflict;
  switch (reason) {
    case 'deleted':
      return {
        reason: 'Excluída em outro dispositivo.',
        server: 'Aceitar exclusão',
        local: 'Recriar como nova tarefa',
      };
    case 'not_found':
      return {
        reason: 'Não existe mais no servidor.',
        server: 'Descartar minha versão',
        local: 'Recriar como nova tarefa',
      };
    case 'already_exists':
      return view.server
        ? {
            reason: 'Já existe no servidor com outra versão.',
            server: 'Usar versão do servidor',
            local: 'Manter minha alteração',
          }
        : {
            reason: 'O identificador já pertence a outro registro.',
            server: 'Descartar minha versão',
            local: 'Recriar como nova tarefa',
          };
    case 'validation_error': {
      const details = Object.values(errors ?? {})
        .flat()
        .join(' ');
      return {
        reason: `Recusada pelo servidor. ${details}`.trim(),
        server: 'Descartar minha alteração',
        local: 'Enviar novamente',
      };
    }
    default:
      return {
        reason: 'Alterada em outro dispositivo depois da sua última sincronização.',
        server: 'Usar versão do servidor',
        local: 'Manter minha alteração',
      };
  }
}

@Component({
  selector: 'app-conflict-panel',
  imports: [NgTemplateOutlet],
  template: `
    <section class="card conflicts" aria-label="Conflitos de sincronização" data-testid="conflicts">
      <h2>Conflitos de sincronização</h2>
      <p class="muted">Escolha qual versão manter. Nada é sobrescrito sem a sua decisão.</p>
      @for (view of conflicts(); track view.conflict.taskId) {
        @let copy = conflictCopy(view);
        <article class="conflict" [attr.data-task-id]="view.conflict.taskId">
          <h3>{{ view.local?.title ?? view.server?.title }}</h3>
          <p class="conflict-reason" data-testid="conflict-reason">{{ copy.reason }}</p>
          <div class="conflict-versions">
            <div data-testid="local-version">
              <h4>Sua versão</h4>
              @if (!view.local || view.local.deletedAt) {
                <p>Você excluiu esta tarefa.</p>
              } @else {
                <ng-container *ngTemplateOutlet="summary; context: { $implicit: view.local }" />
              }
            </div>
            <div data-testid="server-version">
              <h4>Servidor</h4>
              @if (!view.server) {
                <p>Sem versão disponível para você.</p>
              } @else if (view.server.deletedAt) {
                <p>Excluída.</p>
              } @else {
                <ng-container *ngTemplateOutlet="summary; context: { $implicit: view.server }" />
              }
            </div>
          </div>
          <div class="actions">
            <button type="button" (click)="decide.emit({ view, choice: 'server' })">
              {{ copy.server }}
            </button>
            <button type="button" class="primary" (click)="decide.emit({ view, choice: 'local' })">
              {{ copy.local }}
            </button>
          </div>
        </article>
      }
    </section>

    <ng-template #summary let-task>
      <dl>
        <dt>Título</dt>
        <dd>{{ task.title }}</dd>
        @if (task.description) {
          <dt>Descrição</dt>
          <dd>{{ task.description }}</dd>
        }
        <dt>Categoria</dt>
        <dd>{{ task.category }}</dd>
        <dt>Prioridade</dt>
        <dd>{{ priority(task) }}</dd>
        <dt>Status</dt>
        <dd>{{ status(task) }}</dd>
        <dt>Vencimento</dt>
        <dd>{{ task.dueDate ? formatDueDate(task.dueDate) : '—' }}</dd>
      </dl>
    </ng-template>
  `,
})
export class ConflictPanel {
  readonly conflicts = input.required<ConflictView[]>();
  readonly decide = output<ConflictDecision>();

  protected readonly conflictCopy = conflictCopy;
  protected readonly formatDueDate = formatDueDate;
  protected priority = (task: TaskFields) => PRIORITY[task.priority];
  protected status = (task: TaskFields) => STATUS[task.status];
}
