import type { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { AuthService } from './auth.service';

/** Anexa o Bearer apenas a chamadas relativas à própria API (nunca a outros hosts nem ao login). */
export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const token = inject(AuthService).accessToken();
  const isOwnApi = request.url.startsWith('/api/') && request.url !== '/api/auth/login';
  if (!token || !isOwnApi) {
    return next(request);
  }
  return next(request.clone({ setHeaders: { Authorization: `Bearer ${token}` } }));
};
