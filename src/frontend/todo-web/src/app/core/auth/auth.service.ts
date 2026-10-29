import { HttpClient } from '@angular/common/http';
import { computed, inject, Injectable, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export interface AuthUser {
  id: string;
  email: string;
}

/** Resposta de `POST /api/auth/login` (docs/SYNC_CONTRACT.md). */
export interface AuthSession {
  accessToken: string;
  expiresAt: string;
  user: AuthUser;
}

export const SESSION_STORAGE_KEY = 'way2.todo.session';

/**
 * Sessão do usuário previamente autenticado. O primeiro login exige rede; depois disso a sessão
 * persistida permite abrir os dados locais offline. Token vencido ou recusado pelo servidor não bloqueia
 * o uso local, apenas a sincronização (estado `auth_required`); só um novo login substitui a sessão.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly session = signal<AuthSession | null>(readStoredSession());
  private readonly rejected = signal(false);

  readonly user = computed(() => this.session()?.user ?? null);

  accessToken(): string | null {
    return this.session()?.accessToken ?? null;
  }

  isTokenExpired(now: Date = new Date()): boolean {
    const session = this.session();
    return !session || Date.parse(session.expiresAt) <= now.getTime();
  }

  /** O servidor recusou o token (401) antes do prazo local: chave trocada, relógio adiantado etc. */
  markTokenRejected(): void {
    this.rejected.set(true);
  }

  /** Sincronizar exige novo login: token ausente, vencido ou recusado pelo servidor. */
  needsSignIn(now: Date = new Date()): boolean {
    return this.rejected() || this.isTokenExpired(now);
  }

  async login(email: string, password: string): Promise<AuthUser> {
    const session = await firstValueFrom(
      this.http.post<AuthSession>('/api/auth/login', { email, password }),
    );
    localStorage.setItem(SESSION_STORAGE_KEY, JSON.stringify(session));
    this.session.set(session);
    this.rejected.set(false);
    return session.user;
  }

  /** Remove apenas token/sessão. Os dados locais do usuário permanecem no banco dele. */
  logout(): void {
    localStorage.removeItem(SESSION_STORAGE_KEY);
    this.session.set(null);
    this.rejected.set(false);
  }
}

function readStoredSession(): AuthSession | null {
  try {
    const raw = localStorage.getItem(SESSION_STORAGE_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as Partial<AuthSession>;
    const valid =
      typeof parsed.accessToken === 'string' &&
      typeof parsed.expiresAt === 'string' &&
      typeof parsed.user?.id === 'string' &&
      typeof parsed.user?.email === 'string';
    return valid ? (parsed as AuthSession) : null;
  } catch {
    return null;
  }
}
