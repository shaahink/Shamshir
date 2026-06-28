import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import type {
  RunSummary,
  RunDetail,
  TradeSummary,
  TradeListResponse,
  JournalEntry,
  EquityPoint,
  DailyPnl,
  RunAnalytics,
  StartRunRequest,
  StartRunResponse,
  StrategyPerformance,
} from '../../models/api.types';

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
    const payload = {
      start: req.start,
      end: req.end,
      balance: req.balance,
      commissionPerMillion: req.commissionPerMillion,
      spreadPips: req.spreadPips,
      symbols: req.symbols,
      periods: req.periods,
      strategyIds: req.strategyIds,
      riskProfileId: req.riskProfileId ?? '',
      venue: req.venue ?? '',
      strategyOverrides: req.strategyOverrides ?? {},
      usePackId: req.usePackId ?? '',
      disableRegime: req.disableRegime ?? false,
      perStrategyPackIds: req.perStrategyPackIds,
    };
    return firstValueFrom(this.http.post<StartRunResponse>('/api/runs', payload));
  }

  cancelRun(runId: string): Promise<{ cancelled: boolean }> {
    return firstValueFrom(this.http.delete<{ cancelled: boolean }>(`/api/runs/${runId}`));
  }

  async getRunTrades(runId: string): Promise<TradeSummary[]> {
    const r = await firstValueFrom(this.http.get<TradeListResponse>(`/api/runs/${runId}/trades`));
    return r.trades;
  }

  getRunJournal(runId: string, kind?: string, afterSeq?: number, limit = 50): Promise<JournalEntry[]> {
    let url = `/api/runs/${runId}/journal?limit=${limit}`;
    if (kind) url += `&kind=${kind}`;
    if (afterSeq != null) url += `&afterSeq=${afterSeq}`;
    return firstValueFrom(this.http.get<JournalEntry[]>(url));
  }

  // T4: bar-decisions endpoint — server-paged BarClosed StepRecords with per-strategy verdicts.
  getBarDecisions(runId: string, afterSeq?: number, limit = 200): Promise<JournalEntry[]> {
    let url = `/api/runs/${runId}/bar-decisions?limit=${limit}`;
    if (afterSeq != null) url += `&afterSeq=${afterSeq}`;
    return firstValueFrom(this.http.get<JournalEntry[]>(url));
  }

  getRunEquity(runId: string): Promise<EquityPoint[]> {
    return firstValueFrom(this.http.get<EquityPoint[]>(`/api/runs/${runId}/equity`));
  }

  getRunDailyPnl(runId: string): Promise<DailyPnl[]> {
    return firstValueFrom(this.http.get<DailyPnl[]>(`/api/runs/${runId}/daily-pnl`));
  }

  getRunAnalytics(runId: string): Promise<RunAnalytics> {
    return firstValueFrom(this.http.get<RunAnalytics>(`/api/runs/${runId}/analytics`));
  }

  // iter-37 F2 — per-strategy decision funnel (signals fired / top no-signal reasons), off the journal.
  getStrategyBreakdown(runId: string): Promise<StrategyPerformance[]> {
    return firstValueFrom(this.http.get<StrategyPerformance[]>(`/api/runs/${runId}/analytics/strategies`));
  }

  // iter-36 K6 / iter-37 F3 — "duplicate with changes": re-run the source over the SAME dataset with an
  // optionally-changed strategy set / risk profile / overrides. Returns the new run id (parent linked).
  duplicateRun(
    sourceRunId: string,
    body?: {
      strategyIds?: string[];
      riskProfileId?: string;
      venue?: string;
      strategyOverrides?: Record<string, Record<string, unknown>>;
      usePackId?: string;
      disableRegime?: boolean;
    },
  ): Promise<StartRunResponse> {
    return firstValueFrom(this.http.post<StartRunResponse>(`/api/runs/${sourceRunId}/duplicate`, body ?? {}));
  }

  // iter-37 F3 — NDJSON download of the lossless StepRecord journal.
  journalExportUrl(runId: string): string {
    return `/api/runs/${runId}/journal/export`;
  }

  // iter-37 F8 — CSV export of the run's trades (M20).
  tradesCsvUrl(runId: string): string {
    return `/api/export/trades.csv?runId=${runId}`;
  }
}
