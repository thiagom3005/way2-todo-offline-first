import { computed, inject, Injectable, InjectionToken, signal } from '@angular/core';
import { AuthService } from '../auth/auth.service';
import type { TodoLocalDb } from '../offline/todo-local-db';
import { SYNC_API } from './http-sync-api';
import { SyncEngine, type SyncRunResult } from './sync-engine';
import { type ConflictView, type ResolutionOutcome, SyncStore } from './sync-store';

/** Estados do indicador (docs/SYNC_CONTRACT.md §4). */
export type SyncStatus =
  'offline' | 'pending' | 'syncing' | 'synced' | 'conflict' | 'error' | 'auth_required';

export interface SyncTiming {
  /** Espera após uma escrita local, para agrupar edições seguidas num único envio. */
  debounceMs: number;
  /** Sincronização periódica enquanto a aba está visível (traz mudanças de outros dispositivos). */
  periodicMs: number;
  /** Outra aba está sincronizando a mesma fila: tenta de novo depois. */
  busyRetryMs: number;
  backoffBaseMs: number;
  backoffMaxMs: number;
}

export const DEFAULT_SYNC_TIMING: SyncTiming = {
  debounceMs: 1_000,
  periodicMs: 30_000,
  busyRetryMs: 5_000,
  backoffBaseMs: 1_000,
  backoffMaxMs: 60_000,
};

export const SYNC_TIMING = new InjectionToken<SyncTiming>('SyncTiming', {
  providedIn: 'root',
  factory: () => DEFAULT_SYNC_TIMING,
});

/** Backoff exponencial com jitter de ±20%, limitado a `backoffMaxMs`. */
export function backoffDelay(
  failures: number,
  timing: SyncTiming = DEFAULT_SYNC_TIMING,
  random: () => number = Math.random,
): number {
  const exponential = timing.backoffBaseMs * 2 ** Math.max(0, failures - 1);
  const capped = Math.min(exponential, timing.backoffMaxMs);
  return Math.min(Math.round(capped * (0.8 + random() * 0.4)), timing.backoffMaxMs);
}

type Outcome = 'none' | 'ok' | 'offline' | 'error' | 'auth';

/**
 * Orquestra o `SyncEngine` do usuário da sessão: gatilhos (início, `online`, foco, visibilidade,
 * escrita local, periódico), backoff e exclusividade — uma execução por vez na aba e, entre abas,
 * via Web Locks. Só sincroniza com o app aberto; não depende de Background Sync.
 */
@Injectable({ providedIn: 'root' })
export class SyncService {
  private readonly auth = inject(AuthService);
  private readonly api = inject(SYNC_API);
  private readonly timing = inject(SYNC_TIMING);

  readonly pendingCount = signal(0);
  readonly conflictCount = signal(0);
  readonly conflicts = signal<ConflictView[]>([]);
  readonly lastSyncedAt = signal<Date | null>(null);
  readonly nextRetryAt = signal<Date | null>(null);
  private readonly running = signal(false);
  private readonly outcome = signal<Outcome>('none');

  readonly status = computed<SyncStatus>(() => {
    if (this.outcome() === 'none') return 'syncing';
    // Pull periódico com tudo em dia não pisca o indicador.
    if (this.running() && (this.pendingCount() > 0 || this.outcome() !== 'ok')) return 'syncing';
    if (this.outcome() === 'auth') return 'auth_required';
    if (this.conflictCount() > 0) return 'conflict';
    if (this.outcome() === 'offline') return 'offline';
    if (this.outcome() === 'error') return 'error';
    return this.pendingCount() > 0 ? 'pending' : 'synced';
  });

  private session: {
    userId: string;
    store: SyncStore;
    engine: SyncEngine;
    abort: AbortController;
    cleanup: (() => void)[];
  } | null = null;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private timerAt = 0;
  private failures = 0;
  private rerun = false;

  /** Inicia a sincronização do banco do usuário da sessão (substitui qualquer sessão anterior). */
  start(userId: string, db: TodoLocalDb): void {
    this.stop();
    const store = new SyncStore(db);
    const abort = new AbortController();
    const cleanup: (() => void)[] = [];
    this.session = { userId, store, engine: new SyncEngine(store, this.api), abort, cleanup };

    const pending = store.pendingCount$().subscribe((count) => this.pendingCount.set(count));
    const conflicts = store.conflictCount$().subscribe((count) => this.conflictCount.set(count));
    const views = store.conflicts$().subscribe((list) => this.conflicts.set(list));
    cleanup.push(
      () => pending.unsubscribe(),
      () => conflicts.unsubscribe(),
      () => views.unsubscribe(),
    );

    const now = () => void this.syncNow();
    const onVisible = () => document.visibilityState === 'visible' && this.requestSync(0);
    window.addEventListener('online', now);
    window.addEventListener('focus', onVisible);
    document.addEventListener('visibilitychange', onVisible);
    const periodic = setInterval(onVisible, this.timing.periodicMs);
    cleanup.push(
      () => window.removeEventListener('online', now),
      () => window.removeEventListener('focus', onVisible),
      () => document.removeEventListener('visibilitychange', onVisible),
      () => clearInterval(periodic),
    );

    void this.syncNow();
  }

  /** Para gatilhos e aborta a execução em andamento (logout). Não apaga nada do IndexedDB. */
  stop(): void {
    if (!this.session) return;
    this.session.abort.abort();
    this.session.cleanup.forEach((dispose) => dispose());
    this.session = null;
    this.clearTimer();
    this.failures = 0;
    this.rerun = false;
    this.outcome.set('none');
    this.pendingCount.set(0);
    this.conflictCount.set(0);
    this.conflicts.set([]);
    this.nextRetryAt.set(null);
  }

  /**
   * Decisão explícita do usuário para um conflito. Vale só para o conflito exibido e só se a tarefa
   * não mudou desde então; a nova operação (se houver) é enviada em seguida.
   */
  async resolve(view: ConflictView, choice: 'server' | 'local'): Promise<ResolutionOutcome> {
    const store = this.session?.store;
    if (!store) return 'stale';
    const { taskId, operationId } = view.conflict;
    const expectedUpdatedAt = view.local?.updatedAt ?? null;
    const outcome =
      choice === 'server'
        ? await store.useServerVersion(taskId, operationId, expectedUpdatedAt)
        : await store.keepLocalVersion(taskId, operationId, expectedUpdatedAt);
    if (outcome === 'resolved') this.requestSync(0);
    return outcome;
  }

  /** Agenda uma execução em breve (debounce), p.ex. depois de uma escrita local. */
  requestSync(delayMs: number = this.timing.debounceMs): void {
    if (!this.session) return;
    const at = Date.now() + delayMs;
    if (this.timer && this.timerAt <= at) return; // já há execução marcada antes disso
    this.schedule(delayMs);
  }

  /** Executa agora; se já houver uma execução, roda outra logo em seguida. */
  async syncNow(): Promise<void> {
    if (!this.session) return;
    if (this.running()) {
      this.rerun = true;
      return;
    }
    this.clearTimer();
    this.running.set(true);
    try {
      do {
        this.rerun = false;
        await this.runOnce();
      } while (this.rerun && this.session);
    } finally {
      this.running.set(false);
    }
  }

  private async runOnce(): Promise<void> {
    const session = this.session;
    if (!session) return;
    if (this.auth.needsSignIn()) {
      // Edição local continua; nenhuma requisição até um novo login.
      this.outcome.set('auth');
      return;
    }

    const result = await withSyncLock(`way2-sync-${session.userId}`, () =>
      session.engine.run(session.abort.signal),
    );
    if (session !== this.session || result === 'busy') {
      if (result === 'busy') this.schedule(this.timing.busyRetryMs);
      return;
    }
    this.handle(result);
  }

  private handle(result: SyncRunResult): void {
    switch (result.kind) {
      case 'ok':
        this.failures = 0;
        this.outcome.set('ok');
        this.lastSyncedAt.set(new Date());
        this.nextRetryAt.set(null);
        break;
      case 'offline':
      case 'error':
        this.failures++;
        this.outcome.set(result.kind);
        this.schedule(backoffDelay(this.failures, this.timing));
        break;
      case 'auth':
        // 401: o token não serve mais, mesmo dentro do prazo local. Libera a tela de login.
        this.auth.markTokenRejected();
        this.outcome.set('auth');
        this.nextRetryAt.set(null);
        break;
      case 'aborted':
        break;
    }
  }

  private schedule(delayMs: number): void {
    this.clearTimer();
    this.timerAt = Date.now() + delayMs;
    this.nextRetryAt.set(new Date(this.timerAt));
    this.timer = setTimeout(() => {
      this.timer = null;
      void this.syncNow();
    }, delayMs);
  }

  private clearTimer(): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
  }
}

/**
 * Exclusividade entre abas do mesmo usuário com Web Locks (`ifAvailable`: se outra aba já sincroniza,
 * devolve `busy` em vez de esperar). Sem Web Locks (contexto não seguro), vale só o mutex da aba; a
 * fila continua consistente porque acks repetidos são ignorados e o servidor deduplica por operationId.
 */
async function withSyncLock<T>(name: string, run: () => Promise<T>): Promise<T | 'busy'> {
  const locks = typeof navigator === 'undefined' ? undefined : navigator.locks;
  if (!locks) return run();
  return locks.request(name, { ifAvailable: true }, (lock) => (lock ? run() : 'busy'));
}
