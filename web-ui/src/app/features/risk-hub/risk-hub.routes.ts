import { Routes } from '@angular/router';

export const RISK_HUB_ROUTES: Routes = [
  {
    path: '',
    loadComponent: () => import('./risk-hub.component').then((m) => m.RiskHubComponent),
    children: [
      { path: '', redirectTo: 'profiles', pathMatch: 'full' },
      {
        path: 'profiles',
        loadChildren: () =>
          import('../risk-profiles/risk-profiles.routes').then((m) => m.RISK_PROFILES_ROUTES),
      },
      {
        path: 'ftmo',
        loadChildren: () =>
          import('../prop-firm-rules/prop-firm-rules.routes').then((m) => m.PROP_FIRM_ROUTES),
      },
      {
        path: 'governor',
        loadComponent: () =>
          import('../governor/governor-edit.component').then((m) => m.GovernorEditComponent),
      },
      {
        path: 'packs',
        loadChildren: () =>
          import('../addon-packs/addon-packs.routes').then((m) => m.addonPacksRoutes),
      },
    ],
  },
];
