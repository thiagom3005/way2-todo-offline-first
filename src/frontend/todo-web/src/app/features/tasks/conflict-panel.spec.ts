import { TestBed } from '@angular/core/testing';
import type { LocalTask, ServerTask } from '../../core/offline/local-models';
import type { ConflictView } from '../../core/sync/sync-store';
import { type ConflictDecision, ConflictPanel } from './conflict-panel';

const local: LocalTask = {
  id: 't1',
  title: 'Versão do B',
  description: null,
  dueDate: '2026-10-30',
  priority: 'high',
  status: 'pending',
  category: 'Campo',
  createdAt: '2026-10-28T12:00:00.000Z',
  updatedAt: '2026-10-29T12:00:00.000Z',
  deletedAt: null,
  serverVersion: 1,
  syncState: 'conflict',
};

const server: ServerTask = {
  id: 't1',
  title: 'Versão do A',
  description: 'Alterada no outro dispositivo',
  dueDate: null,
  priority: 'low',
  status: 'completed',
  category: 'Campo',
  version: 2,
  createdAt: local.createdAt,
  updatedAt: '2026-10-29T12:05:00.000Z',
  deletedAt: null,
};

const view = (overrides: Partial<ConflictView['conflict']> = {}, srv: ServerTask | null = server) =>
  ({
    conflict: {
      taskId: 't1',
      operationId: 'op-1',
      reason: 'version_mismatch',
      serverTask: srv,
      errors: null,
      detectedAt: '2026-10-29T12:06:00.000Z',
      ...overrides,
    },
    local,
    server: srv,
  }) satisfies ConflictView;

async function render(views: ConflictView[]) {
  const fixture = TestBed.createComponent(ConflictPanel);
  fixture.componentRef.setInput('conflicts', views);
  await fixture.whenStable();
  const decisions: ConflictDecision[] = [];
  fixture.componentInstance.decide.subscribe((d) => decisions.push(d));
  return { el: fixture.nativeElement as HTMLElement, decisions };
}

describe('ConflictPanel', () => {
  it('mostra as duas versões lado a lado e emite a decisão escolhida', async () => {
    const { el, decisions } = await render([view()]);

    expect(el.querySelector('[data-testid="conflict-reason"]')!.textContent).toContain(
      'Alterada em outro dispositivo',
    );
    expect(el.querySelector('[data-testid="local-version"]')!.textContent).toContain('Versão do B');
    const serverText = el.querySelector('[data-testid="server-version"]')!.textContent!;
    expect(serverText).toContain('Versão do A');
    expect(serverText).toContain('Concluída');

    const buttons = Array.from(el.querySelectorAll('button'), (b) => b.textContent!.trim());
    expect(buttons).toEqual(['Usar versão do servidor', 'Manter minha alteração']);
    el.querySelectorAll('button')[1].click();
    expect(decisions.map((d) => d.choice)).toEqual(['local']);
  });

  it('exclusão no servidor oferece aceitar ou recriar como nova tarefa', async () => {
    const deleted = { ...server, deletedAt: '2026-10-29T12:05:00.000Z' };
    const { el } = await render([view({ reason: 'deleted', serverTask: deleted }, deleted)]);

    expect(el.textContent).toContain('Excluída em outro dispositivo');
    expect(Array.from(el.querySelectorAll('button'), (b) => b.textContent!.trim())).toEqual([
      'Aceitar exclusão',
      'Recriar como nova tarefa',
    ]);
  });

  it('não expõe dados quando o servidor não devolve a tarefa', async () => {
    const { el } = await render([view({ reason: 'already_exists', serverTask: null }, null)]);

    expect(el.querySelector('[data-testid="server-version"]')!.textContent).toContain(
      'Sem versão disponível',
    );
    expect(el.textContent).toContain('Recriar como nova tarefa');
  });
});
