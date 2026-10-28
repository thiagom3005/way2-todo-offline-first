import { TestBed } from '@angular/core/testing';
import type { LocalTask, TaskFields } from '../../core/offline/local-models';
import { TaskForm } from './task-form';

async function render(task: LocalTask | null = null) {
  const fixture = TestBed.createComponent(TaskForm);
  fixture.componentRef.setInput('task', task);
  fixture.componentRef.setInput('categories', ['Campo', 'Casa']);
  await fixture.whenStable();
  const saved: TaskFields[] = [];
  fixture.componentInstance.saved.subscribe((fields) => saved.push(fields));
  const el = fixture.nativeElement as HTMLElement;
  const field = <T extends HTMLElement>(id: string) =>
    el.querySelector<T>(`[data-testid="${id}"]`)!;
  const type = async (id: string, value: string) => {
    const input = field<HTMLInputElement>(id);
    input.value = value;
    input.dispatchEvent(new Event(input.tagName === 'SELECT' ? 'change' : 'input'));
    await fixture.whenStable();
  };
  const submit = async () => {
    el.querySelector('form')!.dispatchEvent(new Event('submit'));
    await fixture.whenStable();
  };
  return { fixture, el, saved, field, type, submit };
}

describe('TaskForm', () => {
  it('mostra a prévia "Urgente" enquanto o usuário digita', async () => {
    const { el, type } = await render();
    const badge = () => el.querySelector('[data-testid="urgent-preview"]');

    await type('title', 'Ligar para o cliente');
    expect(badge()).toBeNull();

    await type('description', 'Pedido URGENTE');
    expect(badge()).not.toBeNull();

    await type('description', 'Resolver urgentemente');
    expect(badge()).toBeNull();
  });

  it('não envia com título em branco', async () => {
    const { el, saved, type, submit } = await render();

    await type('title', '   ');
    await submit();

    expect(saved).toHaveLength(0);
    expect(el.textContent).toContain('Informe um título.');
  });

  it('envia campos normalizados com vencimento date-only e limpa o título', async () => {
    const { saved, field, type, submit } = await render();

    await type('title', '  Conferir medição  ');
    await type('description', '   ');
    await type('due-date', '2026-10-30');
    await type('priority', 'high');
    await type('category', ' Campo ');
    await submit();

    expect(saved).toEqual([
      {
        title: 'Conferir medição',
        description: null,
        dueDate: '2026-10-30',
        priority: 'high',
        status: 'pending',
        category: 'Campo',
      },
    ]);
    expect(field<HTMLInputElement>('title').value).toBe('');
    expect(field<HTMLInputElement>('category').value).toBe(' Campo ');
  });

  it('em edição carrega a tarefa e preserva o status', async () => {
    const task: LocalTask = {
      id: 't1',
      title: 'Revisar contrato',
      description: null,
      dueDate: null,
      priority: 'low',
      status: 'completed',
      category: 'Trabalho',
      createdAt: '2026-10-28T12:00:00.000Z',
      updatedAt: '2026-10-28T12:00:00.000Z',
      deletedAt: null,
      serverVersion: 2,
      syncState: 'synced',
    };
    const { saved, field, type, submit } = await render(task);
    expect(field<HTMLInputElement>('title').value).toBe('Revisar contrato');

    await type('title', 'Revisar contrato assinado');
    await submit();

    expect(saved[0]).toMatchObject({
      title: 'Revisar contrato assinado',
      status: 'completed',
      dueDate: null,
    });
  });
});
