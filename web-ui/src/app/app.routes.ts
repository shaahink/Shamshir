import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./features/dashboard/dashboard.component').then((m) => m.DashboardComponent),
  },
  {
    path: 'runs',
    loadChildren: () => import('./features/runs/runs.routes').then((m) => m.RUNS_ROUTES),
  },
  {
    path: 'strategies',
    loadChildren: () =>
      import('./features/strategies/strategies.routes').then((m) => m.STRATEGIES_ROUTES),
  },
  {
    path: 'risk',
    loadChildren: () =>
      import('./features/risk-hub/risk-hub.routes').then((m) => m.RISK_HUB_ROUTES),
  },
  {
    path: 'data-manager',
    loadComponent: () =>
      import('./features/data-manager/data-manager.component').then((m) => m.DataManagerComponent),
  },
  {
    path: 'settings',
    loadComponent: () =>
      import('./features/settings/settings.component').then((m) => m.SettingsComponent),
  },
  {
    path: 'exit-lab',
    loadComponent: () =>
      import('./features/exit-lab/exit-lab.component').then((m) => m.ExitLabComponent),
  },
  {
    path: 'walk-forward',
    loadComponent: () =>
      import('./features/walk-forward/walk-forward.component').then((m) => m.WalkForwardComponent),
  },
  {
    path: 'scoreboard',
    loadComponent: () =>
      import('./features/scoreboard/scoreboard.component').then((m) => m.ScoreboardComponent),
  },
  {
    path: 'phase-tracker',
    loadComponent: () =>
      import('./features/phase-tracker/phase-tracker.component').then((m) => m.PhaseTrackerComponent),
  },
  { path: 'risk-profiles', redirectTo: '/risk/profiles', pathMatch: 'full' },
  { path: 'risk-profiles/:id', redirectTo: '/risk/profiles/:id' },
  { path: 'prop-firm-rules', redirectTo: '/risk/ftmo', pathMatch: 'full' },
  { path: 'prop-firm-rules/:id', redirectTo: '/risk/ftmo/:id' },
  { path: 'governor-options', redirectTo: '/risk/governor' },
  { path: 'addon-packs', redirectTo: '/risk/packs', pathMatch: 'full' },
  { path: 'addon-packs/:id', redirectTo: '/risk/packs/:id' },
  { path: 'trades', redirectTo: '/runs/trades', pathMatch: 'full' },
  { path: 'trades/:id', redirectTo: '/runs/trades/:id' },
  { path: 'compare', redirectTo: '/runs/compare' },
  { path: 'ctrader-sessions', redirectTo: '/runs/ctrader' },
  { path: '**', redirectTo: '' },
];
