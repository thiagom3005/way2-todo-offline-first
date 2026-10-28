import { formatDueDate, isOverdue, todayLocalIsoDate } from './local-date';

describe('datas date-only', () => {
  it('usa a data civil local, não a data UTC', () => {
    // 23:30 do dia 28 no horário local — em UTC-3 isso já é dia 29.
    expect(todayLocalIsoDate(new Date(2026, 9, 28, 23, 30))).toBe('2026-10-28');
    expect(todayLocalIsoDate(new Date(2026, 0, 5, 0, 5))).toBe('2026-01-05');
  });

  it('vencida somente se pendente e com vencimento antes de hoje', () => {
    const today = '2026-10-28';

    expect(isOverdue({ dueDate: '2026-10-27', status: 'pending' }, today)).toBe(true);
    expect(isOverdue({ dueDate: '2026-10-28', status: 'pending' }, today)).toBe(false);
    expect(isOverdue({ dueDate: '2026-10-27', status: 'completed' }, today)).toBe(false);
    expect(isOverdue({ dueDate: null, status: 'pending' }, today)).toBe(false);
  });

  it('formata sem passar por Date', () => {
    expect(formatDueDate('2026-10-30')).toBe('30/10/2026');
  });
});
