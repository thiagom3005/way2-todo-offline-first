import { inject } from '@angular/core';
import { type CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth.service';

/** Exige um usuário previamente autenticado neste dispositivo (token expirado ainda permite uso local). */
export const authGuard: CanActivateFn = () =>
  inject(AuthService).user() ? true : inject(Router).createUrlTree(['/login']);

/** Evita a tela de login quando a sessão ainda serve; vencida ou recusada, o login fica acessível. */
export const guestGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  return auth.user() && !auth.needsSignIn() ? inject(Router).createUrlTree(['/tasks']) : true;
};
