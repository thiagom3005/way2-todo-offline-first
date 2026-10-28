import type { Routes } from '@angular/router';
import { LoginPage } from './core/auth/login.page';

export const routes: Routes = [
  { path: 'login', component: LoginPage },
  { path: '**', redirectTo: 'login' },
];
