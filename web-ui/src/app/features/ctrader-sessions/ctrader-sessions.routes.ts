import { Routes } from '@angular/router';
export const CTRADER_ROUTES: Routes = [
  { path: '', loadComponent: () => import('./ctrader-sessions.component').then(m => m.CtraderSessionsComponent) },
];
