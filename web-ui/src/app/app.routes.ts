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
    path: 'addon-packs',
    loadChildren: () => import('./features/addon-packs/addon-packs.routes').then((m) => m.addonPacksRoutes),
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
    path: 'ctrader-sessions',
    loadChildren: () => import('./features/ctrader-sessions/ctrader-sessions.routes').then((m) => m.CTRADER_ROUTES),
  },
  {
    path: 'data-manager',
    loadComponent: () => import('./features/data-manager/data-manager.component').then((m) => m.DataManagerComponent),
  },
  {
    path: 'settings',
    loadComponent: () => import('./features/settings/settings.component').then((m) => m.SettingsComponent),
  },
  { path: '**', redirectTo: '' },
];
