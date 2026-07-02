import { Routes } from '@angular/router';
import { RunsHubComponent } from './runs-hub.component';

export const RUNS_HUB_ROUTES: Routes = [
  {
    path: '',
    component: RunsHubComponent,
    children: [
      { path: '', redirectTo: 'all', pathMatch: 'full' },
      {
        path: 'all',
        loadComponent: () => import('./run-list/run-list.component').then((m) => m.RunListComponent),
      },
      {
        path: 'compare',
        loadComponent: () => import('../compare/compare.component').then((m) => m.CompareComponent),
      },
      {
        path: 'trades',
        loadChildren: () => import('../trades/trades.routes').then((m) => m.TRADES_ROUTES),
      },
      {
        path: 'ctrader',
        loadComponent: () =>
          import('../ctrader-sessions/ctrader-sessions.component').then((m) => m.CtraderSessionsComponent),
      },
      {
        path: 'new',
        loadComponent: () => import('./new-backtest/new-backtest.component').then((m) => m.NewBacktestComponent),
      },
      {
        path: ':runId',
        loadComponent: () => import('./run-report/run-report.component').then((m) => m.RunReportComponent),
      },
      {
        path: ':runId/monitor',
        loadComponent: () => import('./run-monitor/run-monitor.component').then((m) => m.RunMonitorComponent),
      },
      {
        path: ':runId/analyzer',
        loadComponent: () => import('./run-analyzer/run-analyzer.component').then((m) => m.RunAnalyzerComponent),
      },
      {
        path: ':runId/gallery',
        loadComponent: () => import('./trade-gallery/trade-gallery.component').then((m) => m.TradeGalleryComponent),
      },
    ],
  },
];
