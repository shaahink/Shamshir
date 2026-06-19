import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', redirectTo: 'runs', pathMatch: 'full' },
  {
    path: 'runs',
    loadChildren: () => import('./features/runs/runs.routes').then((m) => m.RUNS_ROUTES),
  },
  {
    path: 'trades',
    loadChildren: () => import('./features/trades/trades.routes').then((m) => m.TRADES_ROUTES),
  },
  {
    path: 'strategies',
    loadChildren: () => import('./features/strategies/strategies.routes').then((m) => m.STRATEGIES_ROUTES),
  },
  {
    path: 'compliance',
    loadComponent: () => import('./features/compliance/compliance.component').then((m) => m.ComplianceComponent),
  },
  {
    path: 'events',
    loadComponent: () => import('./features/events/events.component').then((m) => m.EventsComponent),
  },
  { path: '**', redirectTo: 'runs' },
];
