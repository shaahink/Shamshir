import type { Routes } from '@angular/router';

export const addonPacksRoutes: Routes = [
  {
    path: '',
    loadComponent: () => import('./addon-pack-list.component').then((m) => m.AddOnPackListComponent),
  },
  {
    path: ':id',
    loadComponent: () => import('./addon-pack-detail.component').then((m) => m.AddOnPackDetailComponent),
  },
];
