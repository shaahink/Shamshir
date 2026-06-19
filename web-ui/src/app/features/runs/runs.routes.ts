import { Routes } from '@angular/router';

export const RUNS_ROUTES: Routes = [
  {
    path: '',
    loadComponent: () => import('./run-list/run-list.component').then((m) => m.RunListComponent),
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
];
