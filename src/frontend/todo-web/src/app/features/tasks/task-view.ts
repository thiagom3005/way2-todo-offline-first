import type { LocalTask, TaskPriority, TaskStatus } from '../../core/offline/local-models';
import { isUrgent } from './urgency';

export interface TaskFilters {
  text: string;
  /** Chave normalizada da categoria (`categoryKey`); vazio = todas. */
  category: string;
  status: TaskStatus | 'all';
  priority: TaskPriority | 'all';
}

export const NO_FILTERS: TaskFilters = { text: '', category: '', status: 'all', priority: 'all' };

export interface TaskSection {
  key: string;
  title: string;
  kind: 'urgent' | 'category' | 'all';
  tasks: LocalTask[];
}

export interface CategoryOption {
  key: string;
  label: string;
}

const LOCALE = 'pt-BR';

/** "Campo", "campo" e " CAMPO " são a mesma categoria. */
export function categoryKey(category: string): string {
  return category.trim().toLocaleLowerCase(LOCALE);
}

/** Busca sem diferenciar caixa nem acentos ("medicao" encontra "Medição"). */
function searchable(text: string | null): string {
  return (text ?? '').normalize('NFD').replace(/\p{M}/gu, '').toLocaleLowerCase(LOCALE);
}

export function filterTasks(tasks: readonly LocalTask[], filters: TaskFilters): LocalTask[] {
  const query = searchable(filters.text.trim());
  return tasks.filter(
    (task) =>
      task.deletedAt === null &&
      (filters.status === 'all' || task.status === filters.status) &&
      (filters.priority === 'all' || task.priority === filters.priority) &&
      (!filters.category || categoryKey(task.category) === filters.category) &&
      (!query ||
        searchable(task.title).includes(query) ||
        searchable(task.description).includes(query)),
  );
}

/**
 * Ordem da lista: urgente primeiro; vencimento crescente, sem vencimento
 * por último; criação crescente; id como desempate determinístico.
 */
export function sortTasks(tasks: readonly LocalTask[]): LocalTask[] {
  return tasks
    .map((task) => ({ task, urgent: isUrgent(task), created: Date.parse(task.createdAt) }))
    .sort(
      (a, b) =>
        Number(b.urgent) - Number(a.urgent) ||
        compareDueDates(a.task.dueDate, b.task.dueDate) ||
        a.created - b.created ||
        compareText(a.task.id, b.task.id),
    )
    .map((entry) => entry.task);
}

/**
 * Filtros primeiro, depois ordenação. Agrupado: seção "Urgentes" no topo com todas as urgentes
 * (de qualquer categoria) e, abaixo, as demais por categoria em ordem alfabética — sem duplicar.
 */
export function buildTaskSections(
  tasks: readonly LocalTask[],
  filters: TaskFilters,
  groupByCategory: boolean,
): TaskSection[] {
  const sorted = sortTasks(filterTasks(tasks, filters));
  if (!groupByCategory) {
    return sorted.length ? [{ key: 'all', title: 'Tarefas', kind: 'all', tasks: sorted }] : [];
  }

  const sections: TaskSection[] = [];
  const urgent = sorted.filter(isUrgent);
  if (urgent.length) {
    sections.push({ key: 'urgent', title: 'Urgentes', kind: 'urgent', tasks: urgent });
  }

  const labels = categoryLabels(sorted);
  const byCategory = new Map<string, TaskSection>();
  for (const task of sorted) {
    if (isUrgent(task)) continue;
    const key = categoryKey(task.category);
    const section = byCategory.get(key) ?? {
      key: `category:${key}`,
      title: labels.get(key)!,
      kind: 'category',
      tasks: [],
    };
    section.tasks.push(task);
    byCategory.set(key, section);
  }
  return sections.concat([...byCategory.values()].sort((a, b) => compareLabels(a.title, b.title)));
}

export function listCategories(tasks: readonly LocalTask[]): CategoryOption[] {
  const labels = categoryLabels(tasks.filter((task) => task.deletedAt === null));
  return [...labels]
    .map(([key, label]) => ({ key, label }))
    .sort((a, b) => compareLabels(a.label, b.label));
}

/**
 * Rótulo exibido para cada categoria: a grafia da tarefa mais antiga daquela categoria. Assim o
 * título do grupo não muda quando outra tarefa é editada ou reordenada.
 */
function categoryLabels(tasks: readonly LocalTask[]): Map<string, string> {
  const oldest = new Map<string, LocalTask>();
  for (const task of tasks) {
    const key = categoryKey(task.category);
    const current = oldest.get(key);
    if (!current || compareCreation(task, current) < 0) oldest.set(key, task);
  }
  return new Map([...oldest].map(([key, task]) => [key, task.category.trim()]));
}

function compareCreation(a: LocalTask, b: LocalTask): number {
  return Date.parse(a.createdAt) - Date.parse(b.createdAt) || compareText(a.id, b.id);
}

function compareLabels(a: string, b: string): number {
  return a.localeCompare(b, LOCALE, { sensitivity: 'base' });
}

function compareDueDates(a: string | null, b: string | null): number {
  if (a === b) return 0;
  if (a === null) return 1;
  if (b === null) return -1;
  return compareText(a, b);
}

function compareText(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}
