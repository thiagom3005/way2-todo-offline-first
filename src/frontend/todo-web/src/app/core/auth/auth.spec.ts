import {
  HttpClient,
  HttpErrorResponse,
  provideHttpClient,
  withInterceptors,
} from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import {
  provideRouter,
  Router,
  UrlTree,
  type ActivatedRouteSnapshot,
  type RouterStateSnapshot,
} from '@angular/router';
import { authGuard, guestGuard } from './auth.guards';
import { authInterceptor } from './auth.interceptor';
import { AuthService, SESSION_STORAGE_KEY, type AuthSession } from './auth.service';
import { loginErrorMessage } from './login.page';

const session = (expiresAt: string): AuthSession => ({
  accessToken: 'header.payload.signature',
  expiresAt,
  user: { id: '44a9729e-53c0-46f6-96db-36ae8033bd88', email: 'demo@way2.local' },
});
const future = () => new Date(Date.now() + 3_600_000).toISOString();
const past = () => new Date(Date.now() - 3_600_000).toISOString();

function setup(stored?: AuthSession | string) {
  localStorage.clear();
  if (stored) {
    localStorage.setItem(
      SESSION_STORAGE_KEY,
      typeof stored === 'string' ? stored : JSON.stringify(stored),
    );
  }
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(withInterceptors([authInterceptor])),
      provideHttpClientTesting(),
      provideRouter([]),
    ],
  });
  return {
    auth: TestBed.inject(AuthService),
    http: TestBed.inject(HttpClient),
    backend: TestBed.inject(HttpTestingController),
  };
}

const runGuard = (guard: typeof authGuard) =>
  TestBed.runInInjectionContext(() =>
    guard({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot),
  );

describe('AuthService', () => {
  afterEach(() => localStorage.clear());

  it('login persiste a sessão retornada pela API', async () => {
    const { auth, backend } = setup();
    const response = session(future());

    const login = auth.login('demo@way2.local', 'senha');
    const request = backend.expectOne('/api/auth/login');
    expect(request.request.body).toEqual({ email: 'demo@way2.local', password: 'senha' });
    expect(request.request.headers.has('Authorization')).toBe(false);
    request.flush(response);
    await login;

    expect(auth.user()).toEqual(response.user);
    expect(auth.isTokenExpired()).toBe(false);
    expect(JSON.parse(localStorage.getItem(SESSION_STORAGE_KEY)!)).toEqual(response);
  });

  it('restaura sessão anterior (uso offline) e detecta token expirado', () => {
    const { auth } = setup(session(past()));

    expect(auth.user()?.email).toBe('demo@way2.local');
    expect(auth.isTokenExpired()).toBe(true);
  });

  it('ignora sessão corrompida', () => {
    const { auth } = setup('{not json');

    expect(auth.user()).toBeNull();
  });

  it('logout remove apenas a sessão', () => {
    const { auth } = setup(session(future()));
    localStorage.setItem('outra-chave', 'mantida');

    auth.logout();

    expect(auth.user()).toBeNull();
    expect(localStorage.getItem(SESSION_STORAGE_KEY)).toBeNull();
    expect(localStorage.getItem('outra-chave')).toBe('mantida');
  });
});

describe('authInterceptor', () => {
  afterEach(() => localStorage.clear());

  it('envia Bearer somente para a própria API', () => {
    const { http, backend } = setup(session(future()));

    http.get('/api/tasks').subscribe();
    http.get('https://example.com/api/tasks').subscribe();

    expect(backend.expectOne('/api/tasks').request.headers.get('Authorization')).toBe(
      'Bearer header.payload.signature',
    );
    expect(
      backend.expectOne('https://example.com/api/tasks').request.headers.has('Authorization'),
    ).toBe(false);
  });
});

describe('guards', () => {
  afterEach(() => localStorage.clear());

  it('authGuard permite usuário previamente autenticado mesmo com token expirado', () => {
    setup(session(past()));
    expect(runGuard(authGuard)).toBe(true);
  });

  it('authGuard redireciona para login sem sessão', () => {
    setup();
    const result = runGuard(authGuard);
    expect(result).toBeInstanceOf(UrlTree);
    expect(TestBed.inject(Router).serializeUrl(result as UrlTree)).toBe('/login');
  });

  it('guestGuard só pula o login com token válido', () => {
    setup(session(future()));
    expect(runGuard(guestGuard)).toBeInstanceOf(UrlTree);

    TestBed.resetTestingModule();
    setup(session(past()));
    expect(runGuard(guestGuard)).toBe(true);
  });
});

describe('loginErrorMessage', () => {
  it.each([
    [0, 'Servidor indisponível'],
    [401, 'E-mail ou senha inválidos'],
    [429, 'Muitas tentativas'],
    [500, 'Não foi possível entrar'],
  ])('status %i', (status, expected) => {
    expect(loginErrorMessage(new HttpErrorResponse({ status }))).toContain(expected);
  });
});
