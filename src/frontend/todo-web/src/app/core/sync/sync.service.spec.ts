import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import Dexie from 'dexie';
import { SESSION_STORAGE_KEY } from '../auth/auth.service';
import type { TaskFields } from '../offline/local-models';
import { LocalTaskRepository } from '../offline/local-task-repository';
import { TodoLocalDb } from '../offline/todo-local-db';
import { newUuid } from '../offline/uuid';
import { SYNC_API } from './http-sync-api';
import type { SyncApi } from './sync-contract';
import {
  backoffDelay,
  DEFAULT_SYNC_TIMING,
  SYNC_TIMING,
  SyncService,
  type SyncTiming,
} from './sync.service';
import { FakeSyncServer } from './testing/fake-sync-server';

const fields: TaskFields = {
  title: 'Conferir medição',
  description: null,
  dueDate: null,
  priority: 'medium',
  status: 'pending',
  category: 'Campo',
};

describe('backoffDelay', () => {
  it('dobra a cada falha, com jitter de ±20% e teto', () => {
    const mid = () => 0.5;
    expect([1, 2, 3, 4].map((n) => backoffDelay(n, DEFAULT_SYNC_TIMING, mid))).toEqual([
      1_000, 2_000, 4_000, 8_000,
    ]);
    expect(backoffDelay(20, DEFAULT_SYNC_TIMING, mid)).toBe(60_000);
    expect(backoffDelay(1, DEFAULT_SYNC_TIMING, () => 0)).toBe(800);
    expect(backoffDelay(1, DEFAULT_SYNC_TIMING, () => 0.999)).toBeLessThanOrEqual(1_200);
    expect(backoffDelay(20, DEFAULT_SYNC_TIMING, () => 0.999)).toBe(60_000);
  });
});

describe('SyncService', () => {
  let server: FakeSyncServer;
  let db: TodoLocalDb;
  let repo: LocalTaskRepository;
  let service: SyncService;
  let calls: { health: number; push: number; maxConcurrentPush: number };
  const userId = newUuid();

  function setup(timing: Partial<SyncTiming> = {}, tokenExpiresInMs = 3_600_000) {
    localStorage.setItem(
      SESSION_STORAGE_KEY,
      JSON.stringify({
        accessToken: 'header.payload.signature',
        expiresAt: new Date(Date.now() + tokenExpiresInMs).toISOString(),
        user: { id: userId, email: 'demo@way2.local' },
      }),
    );
    const base = server.api();
    let concurrent = 0;
    const api: SyncApi = {
      health: () => {
        calls.health++;
        return base.health();
      },
      push: async (request) => {
        calls.push++;
        calls.maxConcurrentPush = Math.max(calls.maxConcurrentPush, ++concurrent);
        try {
          await new Promise((resolve) => setTimeout(resolve, 15));
          return await base.push(request);
        } finally {
          concurrent--;
        }
      },
      pull: (cursor, limit) => base.pull(cursor, limit),
    };
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: SYNC_API, useValue: api },
        {
          provide: SYNC_TIMING,
          useValue: {
            debounceMs: 20,
            periodicMs: 60_000,
            busyRetryMs: 30,
            backoffBaseMs: 30,
            backoffMaxMs: 200,
            ...timing,
          },
        },
      ],
    });
    service = TestBed.inject(SyncService);
  }

  beforeEach(() => {
    server = new FakeSyncServer();
    calls = { health: 0, push: 0, maxConcurrentPush: 0 };
    db = new TodoLocalDb(userId);
    repo = new LocalTaskRepository(db);
  });

  afterEach(async () => {
    service?.stop();
    Reflect.deleteProperty(navigator, 'locks');
    localStorage.clear();
    db.close();
    await Dexie.delete(db.name);
  });

  it('sincroniza ao iniciar e mostra "synced" com a fila vazia', async () => {
    setup();
    await repo.create(fields);

    service.start(userId, db);

    await vi.waitFor(() => expect(service.status()).toBe('synced'));
    expect(service.pendingCount()).toBe(0);
    expect(server.log).toHaveLength(1);
    expect(service.lastSyncedAt()).not.toBeNull();
  });

  it('offline: agenda retry com backoff e sincroniza sozinho quando a API volta', async () => {
    setup();
    server.reachable = false;
    await repo.create(fields);

    service.start(userId, db);
    await vi.waitFor(() => expect(service.status()).toBe('offline'));
    expect(service.pendingCount()).toBe(1);
    expect(service.nextRetryAt()).not.toBeNull();
    await vi.waitFor(() => expect(calls.health).toBeGreaterThanOrEqual(2));

    server.reachable = true;
    await vi.waitFor(() => expect(service.status()).toBe('synced'));
    expect(server.log).toHaveLength(1);
  });

  it.each([
    ['online', window],
    ['focus', window],
  ] as const)(
    'o evento "%s" dispara sincronização sem esperar o backoff',
    async (event, target) => {
      setup({ backoffBaseMs: 60_000, backoffMaxMs: 60_000 });
      server.reachable = false;
      await repo.create(fields);
      service.start(userId, db);
      await vi.waitFor(() => expect(service.status()).toBe('offline'));

      server.reachable = true;
      target.dispatchEvent(new Event(event));

      await vi.waitFor(() => expect(service.status()).toBe('synced'));
    },
  );

  it('requestSync agrupa escritas seguidas num único envio', async () => {
    setup();
    service.start(userId, db);
    await vi.waitFor(() => expect(service.status()).toBe('synced'));

    for (const title of ['A', 'B', 'C']) {
      await repo.create({ ...fields, title });
      service.requestSync();
    }

    await vi.waitFor(() => expect(server.log).toHaveLength(3));
    expect(server.pushRequests).toHaveLength(1);
    expect(server.pushRequests[0].operations).toHaveLength(3);
  });

  it('execuções simultâneas não processam a mesma fila em paralelo', async () => {
    setup();
    await repo.create(fields);
    service.start(userId, db);

    await Promise.all([service.syncNow(), service.syncNow(), service.syncNow()]);
    await vi.waitFor(() => expect(service.status()).toBe('synced'));

    expect(calls.maxConcurrentPush).toBe(1);
    expect(server.log).toHaveLength(1);
  });

  it('outra aba com o Web Lock: não envia agora e tenta de novo depois', async () => {
    let held = true;
    Object.defineProperty(navigator, 'locks', {
      configurable: true,
      value: {
        request: (_name: string, _options: unknown, callback: (lock: unknown) => unknown) =>
          Promise.resolve(callback(held ? null : {})),
      },
    });
    setup();
    await repo.create(fields);

    service.start(userId, db);
    await new Promise((resolve) => setTimeout(resolve, 10));
    expect(calls.health).toBe(0);

    held = false;
    await vi.waitFor(() => expect(service.status()).toBe('synced'));
    expect(server.log).toHaveLength(1);
  });

  it('token expirado: auth_required sem nenhuma requisição, com a edição local preservada', async () => {
    setup({}, -1_000);
    await repo.create(fields);

    service.start(userId, db);

    await vi.waitFor(() => expect(service.status()).toBe('auth_required'));
    expect(calls.health).toBe(0);
    expect(service.pendingCount()).toBe(1);
  });

  it('401 do servidor também leva a auth_required', async () => {
    setup();
    server.authExpired = true;

    service.start(userId, db);

    await vi.waitFor(() => expect(service.status()).toBe('auth_required'));
  });

  it('conflito tem prioridade no indicador', async () => {
    setup();
    server.rejectTitle = 'Recusada';
    await repo.create({ ...fields, title: 'Recusada' });

    service.start(userId, db);

    await vi.waitFor(() => expect(service.status()).toBe('conflict'));
    expect(service.conflictCount()).toBe(1);
  });

  it('stop remove gatilhos e timers sem apagar a fila', async () => {
    setup();
    server.reachable = false;
    await repo.create(fields);
    service.start(userId, db);
    await vi.waitFor(() => expect(service.status()).toBe('offline'));

    service.stop();
    const healthCalls = calls.health;
    server.reachable = true;
    window.dispatchEvent(new Event('online'));
    await new Promise((resolve) => setTimeout(resolve, 120));

    expect(calls.health).toBe(healthCalls);
    expect(await db.local_mutations.count()).toBe(1);
  });
});
