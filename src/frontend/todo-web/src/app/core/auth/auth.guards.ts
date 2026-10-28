import { inject } from '@angular/core';
import { type CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth.service';

/** Exige um usuário previamente autenticado neste dispositivo (token expirado ainda permite uso local). */
export const authGuard: CanActivateFn = () =>
  inject(AuthService).user() ? true : inject(Router).createUrlTree(['/login']);

/** Evita a tela de login quando já existe sessão válida. */
export const guestGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  return auth.user() && !auth.isTokenExpired() ? inject(Router).createUrlTree(['/tasks']) : true;
};
