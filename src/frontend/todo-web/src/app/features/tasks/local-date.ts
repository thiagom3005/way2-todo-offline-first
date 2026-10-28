import type { LocalTask } from '../../core/offline/local-models';

/**
 * Data civil local (`YYYY-MM-DD`). Não usar `toISOString()`: ele converte para UTC e, à noite no
 * Brasil, já devolveria o dia seguinte.
 */
export function todayLocalIsoDate(now: Date = new Date()): string {
  const month = String(now.getMonth() + 1).padStart(2, '0');
  const day = String(now.getDate()).padStart(2, '0');
  return `${now.getFullYear()}-${month}-${day}`;
}

/** Datas `YYYY-MM-DD` são comparadas como texto: nunca viram `Date` (evita deslocamento de fuso). */
export function isOverdue(task: Pick<LocalTask, 'dueDate' | 'status'>, today: string): boolean {
  return task.status === 'pending' && task.dueDate !== null && task.dueDate < today;
}

export function formatDueDate(isoDate: string): string {
  const [year, month, day] = isoDate.split('-');
  return `${day}/${month}/${year}`;
}
