import { newUuid } from './uuid';

const UUID_V4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

describe('newUuid', () => {
  it('gera UUID v4', () => {
    expect(newUuid()).toMatch(UUID_V4);
  });

  it('funciona sem crypto.randomUUID (contexto não seguro)', () => {
    Object.defineProperty(crypto, 'randomUUID', { value: undefined, configurable: true });
    try {
      const ids = new Set(Array.from({ length: 50 }, newUuid));

      expect(ids.size).toBe(50);
      for (const id of ids) expect(id).toMatch(UUID_V4);
    } finally {
      // Remove a sombra e volta a usar a implementação do protótipo.
      delete (crypto as { randomUUID?: unknown }).randomUUID;
    }
  });
});
