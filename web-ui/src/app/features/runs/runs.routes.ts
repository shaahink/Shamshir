import { Routes } from '@angular/router';

export const RUNS_ROUTES: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./runs-layout.component').then((m) => m.RunsLayoutComponent),
    children: [
      {
        path: '',
        loadComponent: () =>
          import('./run-list/run-list.component').then((m) => m.RunListComponent),
      },
      {
        path: 'new',
        loadComponent: () =>
          import('./new-backtest/new-backtest.component').then((m) => m.NewBacktestComponent),
      },
      {
        path: 'compare',
        loadComponent: () =>
          import('../compare/compare.component').then((m) => m.CompareComponent),
      },
      {
        path: 'trades',
        loadChildren: () =>
          import('../trades/trades.routes').then((m) => m.TRADES_ROUTES),
      },
      {
        path: 'ctrader',
        loadChildren: () =>
          import('../ctrader-sessions/ctrader-sessions.routes').then((m) => m.CTRADER_ROUTES),
      },
      {
        path: ':runId',
        loadComponent: () =>
          import('./run-report/run-report.component').then((m) => m.RunReportComponent),
      },
      {
        path: ':runId/monitor',
        loadComponent: () =>
          import('./run-monitor/run-monitor.component').then((m) => m.RunMonitorComponent),
      },
      {
        path: ':runId/analyzer',
        loadComponent: () =>
          import('./run-analyzer/run-analyzer.component').then((m) => m.RunAnalyzerComponent),
      },
      {
        path: ':runId/gallery',
        loadComponent: () =>
          import('./trade-gallery/trade-gallery.component').then((m) => m.TradeGalleryComponent),
      },
    ],
  },
];
