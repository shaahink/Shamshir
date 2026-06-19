import { Routes } from '@angular/router';

export const RISK_PROFILES_ROUTES: Routes = [
  { path: '', loadComponent: () => import('./risk-profile-list.component').then(m => m.RiskProfileListComponent) },
  { path: ':id', loadComponent: () => import('./risk-profile-detail.component').then(m => m.RiskProfileDetailComponent) },
];
