import { Routes } from '@angular/router';
import { RiskHubComponent } from './risk-hub.component';

export const RISK_HUB_ROUTES: Routes = [
  {
    path: '',
    component: RiskHubComponent,
    children: [
      { path: '', redirectTo: 'profiles', pathMatch: 'full' },
      {
        path: 'profiles',
        loadComponent: () =>
          import('../risk-profiles/risk-profile-list.component').then((m) => m.RiskProfileListComponent),
      },
      {
        path: 'ftmo',
        loadComponent: () =>
          import('../prop-firm-rules/prop-firm-rule-list.component').then((m) => m.PropFirmRuleListComponent),
      },
      {
        path: 'governor',
        loadComponent: () =>
          import('../governor/governor-edit.component').then((m) => m.GovernorEditComponent),
      },
      {
        path: 'packs',
        loadComponent: () =>
          import('../addon-packs/addon-pack-list.component').then((m) => m.AddOnPackListComponent),
      },
    ],
  },
];
