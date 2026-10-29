import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { TimeoutError } from 'rxjs';
import { HttpSyncApi, toSyncError } from './http-sync-api';
import { SyncHttpError } from './sync-contract';

describe('HttpSyncApi', () => {
  it('usa as rotas e parâmetros do contrato', async () => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    const api = TestBed.inject(HttpSyncApi);
    const http = TestBed.inject(HttpTestingController);

    const pull = api.pull(20, 100);
    const request = http.expectOne((r) => r.url === '/api/sync/pull');
    expect(request.request.params.get('cursor')).toBe('20');
    expect(request.request.params.get('limit')).toBe('100');
    request.flush({ changes: [], nextCursor: 20, hasMore: false });
    expect(await pull).toEqual({ changes: [], nextCursor: 20, hasMore: false });

    const push = api.push({ deviceId: 'd', operations: [] });
    const pushRequest = http.expectOne('/api/sync/push');
    expect(pushRequest.request.method).toBe('POST');
    pushRequest.flush({ results: [] });
    await push;

    const health = api.health();
    http.expectOne('/api/health').flush(null, { status: 503, statusText: 'Unavailable' });
    await expect(health).rejects.toMatchObject({ kind: 'unreachable' });
    http.verify();
  });

  it.each([
    [0, 'unreachable'],
    [502, 'unreachable'],
    [503, 'unreachable'],
    [504, 'unreachable'],
    [401, 'auth'],
    [500, 'server'],
    [400, 'client'],
    [404, 'client'],
  ] as const)('HTTP %i vira falha "%s"', (status, kind) => {
    expect(toSyncError(new HttpErrorResponse({ status })).kind).toBe(kind);
  });

  it('timeout é tratado como servidor inalcançável (retry com o mesmo operationId)', () => {
    expect(toSyncError(new TimeoutError()).kind).toBe('unreachable');
    const original = new SyncHttpError('auth', 'x');
    expect(toSyncError(original)).toBe(original);
  });
});
