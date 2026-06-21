import { Routes } from '@angular/router';

export const PROP_FIRM_ROUTES: Routes = [
  { path: '', loadComponent: () => import('./prop-firm-rule-list.component').then((m) => m.PropFirmRuleListComponent) },
  {
    path: ':id',
    loadComponent: () => import('./prop-firm-rule-detail.component').then((m) => m.PropFirmRuleDetailComponent),
  },
];
