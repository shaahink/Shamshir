import { Routes } from '@angular/router';

export const STRATEGIES_ROUTES: Routes = [
  {
    path: '',
    loadComponent: () => import('./strategy-list/strategy-list.component').then((m) => m.StrategyListComponent),
  },
  {
    path: ':id',
    loadComponent: () => import('./strategy-detail/strategy-detail.component').then((m) => m.StrategyDetailComponent),
  },
];
