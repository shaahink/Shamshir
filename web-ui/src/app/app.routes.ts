import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    loadComponent: () => import('./features/dashboard/dashboard.component').then((m) => m.DashboardComponent),
  },
  {
    path: 'runs',
    loadChildren: () => import('./features/runs/runs-hub.routes').then((m) => m.RUNS_HUB_ROUTES),
  },
  {
    path: 'strategies',
    loadChildren: () => import('./features/strategies/strategies.routes').then((m) => m.STRATEGIES_ROUTES),
  },
  {
    path: 'risk',
    loadChildren: () => import('./features/risk/risk-hub.routes').then((m) => m.RISK_HUB_ROUTES),
  },
  {
    path: 'data-manager',
    loadComponent: () => import('./features/data-manager/data-manager.component').then((m) => m.DataManagerComponent),
  },
  {
    path: 'settings',
    loadComponent: () => import('./features/settings/settings.component').then((m) => m.SettingsComponent),
  },

  // Legacy redirects — list paths
  { path: 'trades', redirectTo: '/runs/trades', pathMatch: 'full' },
  { path: 'ctrader-sessions', redirectTo: '/runs/ctrader', pathMatch: 'full' },
  { path: 'compare', redirectTo: '/runs/compare', pathMatch: 'full' },
  { path: 'risk-profiles', redirectTo: '/risk/profiles', pathMatch: 'full' },
  { path: 'prop-firm-rules', redirectTo: '/risk/ftmo', pathMatch: 'full' },
  { path: 'governor-options', redirectTo: '/risk/governor', pathMatch: 'full' },
  { path: 'addon-packs', redirectTo: '/risk/packs', pathMatch: 'full' },

  // Legacy detail routes — kept for existing component routerLinks
  {
    path: 'trades/:id',
    loadComponent: () => import('./features/trades/trade-detail/trade-detail.component').then((m) => m.TradeDetailComponent),
  },
  {
    path: 'risk-profiles/:id',
    loadComponent: () =>
      import('./features/risk-profiles/risk-profile-detail.component').then((m) => m.RiskProfileDetailComponent),
  },
  {
    path: 'prop-firm-rules/:id',
    loadComponent: () =>
      import('./features/prop-firm-rules/prop-firm-rule-detail.component').then((m) => m.PropFirmRuleDetailComponent),
  },
  {
    path: 'addon-packs/:id',
    loadComponent: () =>
      import('./features/addon-packs/addon-pack-detail.component').then((m) => m.AddOnPackDetailComponent),
  },

  { path: '**', redirectTo: '' },
];
