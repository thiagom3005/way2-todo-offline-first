import type { LocalTask } from '../../core/offline/local-models';
import { buildTaskSections, filterTasks, listCategories, NO_FILTERS, sortTasks } from './task-view';

let seq = 0;
const task = (overrides: Partial<LocalTask> = {}): LocalTask => {
  seq++;
  return {
    id: `00000000-0000-4000-8000-${String(seq).padStart(12, '0')}`,
    title: `Tarefa ${seq}`,
    description: null,
    dueDate: null,
    priority: 'medium',
    status: 'pending',
    category: 'Geral',
    createdAt: new Date(Date.UTC(2026, 9, 28, 12) + seq * 1000).toISOString(),
    updatedAt: '2026-10-28T12:00:00.000Z',
    deletedAt: null,
    serverVersion: null,
    syncState: 'pending',
    ...overrides,
  };
};
const titles = (tasks: LocalTask[]) => tasks.map((t) => t.title);

describe('sortTasks', () => {
  it('urgente primeiro, independentemente do vencimento', () => {
    const early = task({ title: 'Vence antes', dueDate: '2026-10-01' });
    const urgent = task({ title: 'Ligar urgente', dueDate: '2026-12-31' });
    const urgentInDescription = task({ title: 'Relatório', description: 'URGENTE', dueDate: null });

    expect(titles(sortTasks([early, urgentInDescription, urgent]))).toEqual([
      'Ligar urgente',
      'Relatório',
      'Vence antes',
    ]);
  });

  it('vencimento crescente com sem vencimento por último, depois criação e id', () => {
    const noDue = task({ title: 'Sem vencimento', createdAt: '2026-10-01T00:00:00.000Z' });
    const later = task({ title: 'Dia 30', dueDate: '2026-10-30' });
    const sooner = task({ title: 'Dia 29', dueDate: '2026-10-29' });
    const sameDayOlder = task({
      title: 'Dia 30 antiga',
      dueDate: '2026-10-30',
      createdAt: '2026-10-27T08:00:00Z',
    });

    expect(titles(sortTasks([noDue, later, sooner, sameDayOlder]))).toEqual([
      'Dia 29',
      'Dia 30 antiga',
      'Dia 30',
      'Sem vencimento',
    ]);
  });

  it('compara createdAt como instante, mesmo em formatos ISO diferentes', () => {
    const withMillis = task({ title: 'Depois', createdAt: '2026-10-28T12:02:00.500Z' });
    const withoutMillis = task({ title: 'Antes', createdAt: '2026-10-28T12:02:00Z' });

    expect(titles(sortTasks([withMillis, withoutMillis]))).toEqual(['Antes', 'Depois']);
  });

  it('desempata por id', () => {
    const createdAt = '2026-10-28T12:00:00.000Z';
    const b = task({ id: 'b', title: 'B', createdAt });
    const a = task({ id: 'a', title: 'A', createdAt });

    expect(titles(sortTasks([b, a]))).toEqual(['A', 'B']);
  });
});

describe('filterTasks', () => {
  const tasks = [
    task({ title: 'Conferir medição', category: 'Campo', priority: 'high' }),
    task({
      title: 'Comprar café',
      description: 'Padaria',
      category: 'casa',
      status: 'completed',
      priority: 'low',
    }),
    task({ title: 'Excluída', category: 'Campo', deletedAt: '2026-10-28T13:00:00Z' }),
  ];

  it('busca no título e na descrição sem diferenciar caixa e acentos', () => {
    expect(titles(filterTasks(tasks, { ...NO_FILTERS, text: 'MEDICAO' }))).toEqual([
      'Conferir medição',
    ]);
    expect(titles(filterTasks(tasks, { ...NO_FILTERS, text: 'padaria' }))).toEqual([
      'Comprar café',
    ]);
  });

  it('filtra por status, prioridade e categoria (chave sem caixa)', () => {
    expect(titles(filterTasks(tasks, { ...NO_FILTERS, status: 'completed' }))).toEqual([
      'Comprar café',
    ]);
    expect(titles(filterTasks(tasks, { ...NO_FILTERS, priority: 'high' }))).toEqual([
      'Conferir medição',
    ]);
    expect(titles(filterTasks(tasks, { ...NO_FILTERS, category: 'casa' }))).toEqual([
      'Comprar café',
    ]);
  });

  it('nunca exibe tombstones', () => {
    expect(titles(filterTasks(tasks, NO_FILTERS))).not.toContain('Excluída');
  });
});

describe('buildTaskSections', () => {
  const tasks = [
    task({ title: 'Regar plantas', category: 'Casa' }),
    task({ title: 'Conferir medição', category: 'Campo', dueDate: '2026-10-30' }),
    task({ title: 'Pagar conta urgente', category: 'casa', dueDate: '2026-11-15' }),
    task({
      title: 'Relatório',
      description: 'Urgente para diretoria',
      category: 'Trabalho',
      dueDate: '2026-11-01',
    }),
    task({ title: 'Lavar carro', category: ' CASA ' }),
  ];

  it('agrupado: seção Urgentes no topo e categorias em ordem alfabética, sem duplicar', () => {
    const sections = buildTaskSections(tasks, NO_FILTERS, true);

    expect(sections.map((s) => s.title)).toEqual(['Urgentes', 'Campo', 'Casa']);
    expect(titles(sections[0].tasks)).toEqual(['Relatório', 'Pagar conta urgente']);
    expect(titles(sections[2].tasks)).toEqual(['Regar plantas', 'Lavar carro']);
    const all = sections.flatMap((s) => s.tasks);
    expect(new Set(all.map((t) => t.id)).size).toBe(all.length);
  });

  it('lista plana: uma seção com urgentes primeiro', () => {
    const [section, ...rest] = buildTaskSections(tasks, NO_FILTERS, false);

    expect(rest).toHaveLength(0);
    expect(titles(section.tasks).slice(0, 2)).toEqual(['Relatório', 'Pagar conta urgente']);
  });

  it('aplica filtros antes de ordenar e agrupar', () => {
    const sections = buildTaskSections(tasks, { ...NO_FILTERS, category: 'casa' }, true);

    expect(sections.map((s) => s.title)).toEqual(['Urgentes', 'Casa']);
    expect(titles(sections[0].tasks)).toEqual(['Pagar conta urgente']);
  });

  it('remover "urgente" na edição devolve a tarefa ao grupo da categoria', () => {
    const edited = tasks.map((t) =>
      t.title === 'Pagar conta urgente' ? { ...t, title: 'Pagar conta' } : t,
    );

    const sections = buildTaskSections(edited, NO_FILTERS, true);

    expect(titles(sections[0].tasks)).toEqual(['Relatório']);
    expect(titles(sections.find((s) => s.title === 'Casa')!.tasks)).toContain('Pagar conta');
  });

  it('sem tarefas visíveis não gera seções', () => {
    expect(buildTaskSections([], NO_FILTERS, true)).toEqual([]);
    expect(buildTaskSections([], NO_FILTERS, false)).toEqual([]);
  });
});

describe('listCategories', () => {
  it('lista categorias únicas sem diferenciar caixa', () => {
    const options = listCategories([
      task({ category: 'Casa' }),
      task({ category: 'casa' }),
      task({ category: 'Campo' }),
      task({ category: 'Oculta', deletedAt: '2026-10-28T13:00:00Z' }),
    ]);

    expect(options).toEqual([
      { key: 'campo', label: 'Campo' },
      { key: 'casa', label: 'Casa' },
    ]);
  });
});
