import type { Routes } from '@angular/router';
import { authGuard, guestGuard } from './core/auth/auth.guards';
import { LoginPage } from './core/auth/login.page';
import { TasksPage } from './features/tasks/tasks.page';

export const routes: Routes = [
  { path: 'login', component: LoginPage, canActivate: [guestGuard] },
  { path: 'tasks', component: TasksPage, canActivate: [authGuard] },
  { path: '**', redirectTo: 'tasks' },
];
