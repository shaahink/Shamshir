import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

export interface WalkForwardJob {
  id: string;
  status: string;
  totalWindows: number;
  completedWindows: number;
  createdAtUtc: string;
  completedAtUtc: string | null;
  errorMessage: string | null;
  windows?: WalkForwardWindow[];
}

export interface WalkForwardWindow {
  windowIndex: number;
  trainFrom: string;
  trainTo: string;
  testFrom: string;
  testTo: string;
  strategyId: string;
  symbol: string;
  timeframe: string;
  chosenParamsJson: string;
  testRunId: string | null;
  testNetProfit: number;
  testTotalTrades: number;
  testWinRatePct: number;
  trialsCount: number;
}

export interface WalkForwardRequest {
  folds?: number;
  trainFraction?: number;
  strategies: string[];
  symbols: string[];
  timeframes: string[];
  from: string;
  to: string;
  paramGrid: Record<string, number[]>;
  balance?: number;
}

@Injectable({ providedIn: 'root' })
export class WalkForwardApiService {
  private http = inject(HttpClient);

  start(req: WalkForwardRequest): Promise<{ jobId: string; status: string }> {
    return firstValueFrom(this.http.post<{ jobId: string; status: string }>('/api/walk-forward/start', req));
  }

  getJobs(): Promise<WalkForwardJob[]> {
    return firstValueFrom(this.http.get<WalkForwardJob[]>('/api/walk-forward/jobs'));
  }

  getJob(jobId: string): Promise<WalkForwardJob> {
    return firstValueFrom(this.http.get<WalkForwardJob>(`/api/walk-forward/jobs/${jobId}`));
  }
}
