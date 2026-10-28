import type { TaskFields } from '../../core/offline/local-models';

// Caractere de palavra Unicode: letras (inclui acentuadas), marcas combinantes, dígitos e "_".
const WORD_CHAR = String.raw`[\p{L}\p{M}\p{N}_]`;

/** Regex que casa `word` como palavra inteira, ignorando caixa. */
export function standaloneWordPattern(word: string): RegExp {
  const escaped = word.normalize('NFC').replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  return new RegExp(`(?<!${WORD_CHAR})${escaped}(?!${WORD_CHAR})`, 'iu');
}

const URGENT_WORD = standaloneWordPattern('urgente');

export function hasStandaloneWord(text: string | null | undefined, pattern: RegExp): boolean {
  return !!text && pattern.test(text.normalize('NFC'));
}

/**
 * Regra de prioridade visual: "urgente" como palavra inteira no título OU na descrição.
 * Derivada só dos dados locais — funciona offline e não depende do servidor.
 */
export function isUrgent(task: Pick<TaskFields, 'title' | 'description'>): boolean {
  return (
    hasStandaloneWord(task.title, URGENT_WORD) || hasStandaloneWord(task.description, URGENT_WORD)
  );
}
