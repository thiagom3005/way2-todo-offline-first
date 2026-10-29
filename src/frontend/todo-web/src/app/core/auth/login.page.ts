import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AuthService } from './auth.service';

@Component({
  selector: 'app-login-page',
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <main class="login">
      <h1>Way2 Todo</h1>
      @if (returning) {
        <p class="muted">
          Sua sessão expirou. Entre novamente para voltar a sincronizar; as alterações feitas neste
          dispositivo continuam salvas.
        </p>
        <a routerLink="/tasks" data-testid="back-to-tasks">Voltar às tarefas</a>
      } @else {
        <p class="muted">
          O primeiro acesso exige conexão. Depois, suas tarefas ficam disponíveis neste dispositivo.
        </p>
      }

      <form [formGroup]="form" (ngSubmit)="submit()" class="card stack">
        <label>
          E-mail
          <input type="email" formControlName="email" autocomplete="username" />
        </label>
        <label>
          Senha
          <input type="password" formControlName="password" autocomplete="current-password" />
        </label>
        @if (error()) {
          <p class="error" role="alert">{{ error() }}</p>
        }
        <button type="submit" class="primary" [disabled]="form.invalid || busy()">
          {{ busy() ? 'Entrando…' : 'Entrar' }}
        </button>
      </form>
    </main>
  `,
})
export class LoginPage {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  /** Reautenticação: a sessão anterior continua valendo para o uso local até um login bem-sucedido. */
  protected readonly returning = this.auth.user() !== null;
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly form = inject(NonNullableFormBuilder).group({
    email: [this.auth.user()?.email ?? '', [Validators.required, Validators.email]],
    password: ['', Validators.required],
  });

  protected async submit(): Promise<void> {
    if (this.form.invalid || this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const { email, password } = this.form.getRawValue();
      await this.auth.login(email, password);
      await this.router.navigateByUrl('/tasks');
    } catch (err) {
      this.error.set(loginErrorMessage(err));
    } finally {
      this.busy.set(false);
    }
  }
}

export function loginErrorMessage(err: unknown): string {
  if (!(err instanceof HttpErrorResponse)) return 'Não foi possível entrar. Tente novamente.';
  switch (err.status) {
    case 0:
    case 502:
    case 503:
    case 504:
      return 'Servidor indisponível. O primeiro acesso exige conexão com a internet.';
    case 400:
    case 401:
      return 'E-mail ou senha inválidos.';
    case 429:
      return 'Muitas tentativas. Aguarde um minuto e tente novamente.';
    default:
      return 'Não foi possível entrar. Tente novamente.';
  }
}
