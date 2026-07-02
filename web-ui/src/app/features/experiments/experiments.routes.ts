import type { Routes } from '@angular/router';

export const experimentsRoutes: Routes = [
  {
    path: '',
    loadComponent: () => import('./experiment-list.component').then((m) => m.ExperimentListComponent),
  },
  {
    path: 'new',
    loadComponent: () => import('./experiment-new.component').then((m) => m.ExperimentNewComponent),
  },
  {
    path: ':id',
    loadComponent: () => import('./experiment-detail.component').then((m) => m.ExperimentDetailComponent),
  },
];
