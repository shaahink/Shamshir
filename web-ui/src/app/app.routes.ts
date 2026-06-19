import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    loadComponent: () => import('./features/dashboard/dashboard.component').then((m) => m.DashboardComponent),
  },
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
    path: 'risk-profiles',
    loadChildren: () => import('./features/risk-profiles/risk-profiles.routes').then((m) => m.RISK_PROFILES_ROUTES),
  },
  {
    path: 'prop-firm-rules',
    loadChildren: () => import('./features/prop-firm-rules/prop-firm-rules.routes').then((m) => m.PROP_FIRM_ROUTES),
  },
  {
    path: 'governor-options',
    loadComponent: () => import('./features/governor/governor-edit.component').then((m) => m.GovernorEditComponent),
  },
  {
    path: 'events',
    loadComponent: () => import('./features/events/events.component').then((m) => m.EventsComponent),
  },
  {
    path: 'settings',
    loadComponent: () => import('./features/settings/settings.component').then((m) => m.SettingsComponent),
  },
  { path: '**', redirectTo: '' },
];
