import { Routes } from '@angular/router';

export const TRADES_ROUTES: Routes = [
  {
    path: '',
    loadComponent: () => import('./trade-list/trade-list.component').then((m) => m.TradeListComponent),
  },
  {
    path: ':id',
    loadComponent: () => import('./trade-detail/trade-detail.component').then((m) => m.TradeDetailComponent),
  },
];
