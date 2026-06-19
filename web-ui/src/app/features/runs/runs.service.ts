import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

export interface RunSummary {
  runId: string;
  status: string;
  symbol: string;
  period: string;
  startedAtUtc: string;
  completedAtUtc: string | null;
  netProfit: number;
  maxDrawdownPct: number;
  totalTrades: number;
  winningTrades: number;
  winRatePct: number;
  errorMessage: string | null;
}

export interface RunDetail {
  runId: string;
  status: string;
  symbol: string;
  period: string;
  startedAtUtc: string;
  completedAtUtc: string | null;
  backtestFrom: string;
  backtestTo: string;
  initialBalance: number;
  netProfit: number;
  maxDrawdownPct: number;
  totalTrades: number;
  winningTrades: number;
  winRatePct: number;
  errorMessage: string | null;
  exitCode: number;
  effectiveConfigJson: string | null;
  reportJsonPath: string | null;
}

export interface TradeSummary {
  id: string;
  positionId: string;
  orderId: string;
  symbol: string;
  direction: string;
  lots: number;
  entryPrice: number;
  exitPrice: number;
  openedAtUtc: string;
  closedAtUtc: string;
  grossPnLAmount: number;
  commissionAmount: number;
  swapAmount: number;
  netPnLAmount: number;
  pnLPips: number;
  rMultiple: number;
  maxAdverseExcursion: number;
  maxFavorableExcursion: number;
  exitReason: string;
  strategyId: string;
  durationSeconds: number;
}

export interface JournalEntry {
  seq: number;
  simTimeUtc: string;
  kind: string;
  symbol: string | null;
  strategyId: string | null;
  reason: string | null;
  detail: string | null;
}

export interface EquityPoint {
  timestampUtc: string;
  equity: number;
  balance: number;
}

export interface DailyPnl {
  date: string;
  pnl: number;
}

export interface StartRunRequest {
  symbol: string;
  period: string;
  start: string;
  end: string;
  balance: number;
  commissionPerMillion: number;
  spreadPips: number;
  symbols?: string;
  periods?: string;
  strategyIds?: string;
}

export interface StartRunResponse {
  runId: string;
  status: string;
}

@Injectable({ providedIn: 'root' })
export class RunsApiService {
  private http = inject(HttpClient);

  getRuns(): Promise<RunSummary[]> {
    return firstValueFrom(this.http.get<RunSummary[]>('/api/runs'));
  }

  getRun(runId: string): Promise<RunDetail> {
    return firstValueFrom(this.http.get<RunDetail>(`/api/runs/${runId}`));
  }

  startRun(req: StartRunRequest): Promise<StartRunResponse> {
    return firstValueFrom(this.http.post<StartRunResponse>('/api/runs', req));
  }

  cancelRun(runId: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`/api/runs/${runId}`));
  }

  getRunTrades(runId: string): Promise<TradeSummary[]> {
    return firstValueFrom(this.http.get<TradeSummary[]>(`/api/runs/${runId}/trades`));
  }

  getRunJournal(runId: string, kind?: string, afterSeq?: number, limit = 50): Promise<JournalEntry[]> {
    let url = `/api/runs/${runId}/journal?limit=${limit}`;
    if (kind) url += `&kind=${kind}`;
    if (afterSeq != null) url += `&afterSeq=${afterSeq}`;
    return firstValueFrom(this.http.get<JournalEntry[]>(url));
  }

  getRunEquity(runId: string): Promise<EquityPoint[]> {
    return firstValueFrom(this.http.get<EquityPoint[]>(`/api/runs/${runId}/equity`));
  }

  getRunDailyPnl(runId: string): Promise<DailyPnl[]> {
    return firstValueFrom(this.http.get<DailyPnl[]>(`/api/runs/${runId}/daily-pnl`));
  }
}
