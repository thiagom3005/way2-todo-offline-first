import { isUrgent } from './urgency';

const task = (title: string, description: string | null = null) => ({ title, description });

describe('isUrgent', () => {
  it.each(['urgente', 'URGENTE', 'Urgente', 'UrGeNtE'])(
    'reconhece "%s" no título, sem diferenciar caixa',
    (word) => {
      expect(isUrgent(task(`Conferir medição ${word}`))).toBe(true);
    },
  );

  it('reconhece a palavra na descrição', () => {
    expect(isUrgent(task('Conferir medição', 'Cliente pediu: URGENTE'))).toBe(true);
  });

  it.each([
    'urgente',
    'urgente!',
    '(urgente)',
    '#urgente',
    'urgente.',
    'tarefa:urgente',
    '"urgente"',
  ])('aceita pontuação como separador: %s', (text) => {
    expect(isUrgent(task(text))).toBe(true);
  });

  it.each(['urgentemente', 'Urgentes', 'inurgente', 'urgente2', 'urgente_', 'urgent', 'urgênte'])(
    'exige palavra inteira: "%s" não conta',
    (text) => {
      expect(isUrgent(task(text, text))).toBe(false);
    },
  );

  it('não considera letra acentuada colada como fim de palavra', () => {
    expect(isUrgent(task('urgenteé'))).toBe(false);
    expect(isUrgent(task('urgenté'))).toBe(false); // e + acento combinante
  });

  it('premissa: a regra é literal, sem interpretar negação', () => {
    expect(isUrgent(task('não urgente'))).toBe(true);
    expect(isUrgent(task('não-urgente'))).toBe(true);
  });

  it('sem a palavra ou sem descrição não é urgente', () => {
    expect(isUrgent(task('Comprar café'))).toBe(false);
    expect(isUrgent(task('Comprar café', ''))).toBe(false);
  });

  it('editar removendo a palavra remove a urgência', () => {
    const before = task('Pagar conta urgente');
    const after = { ...before, title: 'Pagar conta' };

    expect(isUrgent(before)).toBe(true);
    expect(isUrgent(after)).toBe(false);
  });
});
