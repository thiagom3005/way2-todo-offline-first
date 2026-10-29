import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { inject, Injectable, InjectionToken } from '@angular/core';
import { firstValueFrom, type Observable, timeout, TimeoutError } from 'rxjs';
import {
  type PullResponse,
  type PushRequest,
  type PushResponse,
  type SyncApi,
  SyncHttpError,
} from './sync-contract';

/** Toda chamada tem prazo: uma requisição pendurada não pode segurar o lock de sincronização. */
export const SYNC_TIMEOUTS_MS = { health: 5_000, push: 15_000, pull: 15_000 } as const;

@Injectable({ providedIn: 'root' })
export class HttpSyncApi implements SyncApi {
  private readonly http = inject(HttpClient);

  async health(): Promise<void> {
    await this.call(this.http.get('/api/health'), SYNC_TIMEOUTS_MS.health);
  }

  push(request: PushRequest): Promise<PushResponse> {
    return this.call(
      this.http.post<PushResponse>('/api/sync/push', request),
      SYNC_TIMEOUTS_MS.push,
    );
  }

  pull(cursor: number, limit: number): Promise<PullResponse> {
    return this.call(
      this.http.get<PullResponse>('/api/sync/pull', { params: { cursor, limit } }),
      SYNC_TIMEOUTS_MS.pull,
    );
  }

  private async call<T>(request: Observable<T>, timeoutMs: number): Promise<T> {
    try {
      return await firstValueFrom(request.pipe(timeout(timeoutMs)));
    } catch (error) {
      throw toSyncError(error);
    }
  }
}

/** API usada pelo SyncService; os testes substituem por um servidor falso. */
export const SYNC_API = new InjectionToken<SyncApi>('SyncApi', {
  providedIn: 'root',
  factory: () => inject(HttpSyncApi),
});

export function toSyncError(error: unknown): SyncHttpError {
  if (error instanceof SyncHttpError) return error;
  if (error instanceof TimeoutError) {
    // A requisição pode ter sido aplicada no servidor: o retry reenvia o mesmo operationId.
    return new SyncHttpError('unreachable', 'Tempo de resposta esgotado.');
  }
  if (error instanceof HttpErrorResponse) {
    if (error.status === 0 || [502, 503, 504].includes(error.status)) {
      return new SyncHttpError('unreachable', 'Servidor indisponível.');
    }
    if (error.status === 401) return new SyncHttpError('auth', 'Sessão expirada.');
    if (error.status >= 500)
      return new SyncHttpError('server', `Erro do servidor (${error.status}).`);
    return new SyncHttpError('client', `Requisição recusada (${error.status}).`);
  }
  return new SyncHttpError('server', 'Resposta inesperada do servidor.');
}
