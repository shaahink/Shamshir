import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import type {
  RunSummary, RunDetail, TradeSummary, JournalEntry, EquityPoint,
  DailyPnl, RunAnalytics, StartRunRequest, StartRunResponse,
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
      symbol: req.symbol,
      period: req.period,
      start: req.start,
      end: req.end,
      balance: req.balance,
      commissionPerMillion: req.commissionPerMillion,
      spreadPips: req.spreadPips,
      symbols: req.symbols.join(','),
      periods: req.periods.join(','),
      strategyIds: req.strategyIds.join(','),
      riskProfileId: req.riskProfileId ?? '',
      venue: req.venue ?? '',
    };
    return firstValueFrom(this.http.post<StartRunResponse>('/api/runs', payload));
  }

  cancelRun(runId: string): Promise<{ cancelled: boolean }> {
    return firstValueFrom(this.http.delete<{ cancelled: boolean }>(`/api/runs/${runId}`));
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

  getRunAnalytics(runId: string): Promise<RunAnalytics> {
    return firstValueFrom(this.http.get<RunAnalytics>(`/api/runs/${runId}/analytics`));
  }
}
