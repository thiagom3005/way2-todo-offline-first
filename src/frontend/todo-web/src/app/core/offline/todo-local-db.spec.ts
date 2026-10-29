import Dexie from 'dexie';
import { TodoLocalDb } from './todo-local-db';
import { newUuid } from './uuid';

describe('TodoLocalDb', () => {
  it('abre as transações com durabilidade strict', async () => {
    const transaction = vi.spyOn(IDBDatabase.prototype, 'transaction');
    const db = new TodoLocalDb(newUuid());
    try {
      await db.sync_metadata.put({ key: 'probe', value: 1 });

      const options = transaction.mock.calls.map((call) => call[2]);
      expect(options).toContainEqual({ durability: 'strict' });
    } finally {
      transaction.mockRestore();
      db.close();
      await Dexie.delete(db.name);
    }
  });
});
