import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import type { TradeSummary, TradeDetail, BarData, TradeListResponse, TradeChartResponse, TradeExcursionResponse } from '../../models/api.types';

@Injectable({ providedIn: 'root' })
export class TradesApiService {
  private http = inject(HttpClient);

  async getAll(): Promise<TradeSummary[]> {
    const r = await firstValueFrom(this.http.get<TradeListResponse>('/api/trades'));
    return r.trades;
  }

  getById(id: string): Promise<TradeDetail> {
    return firstValueFrom(this.http.get<TradeDetail>(`/api/trades/${id}`));
  }

  // X3: padBars = context bars before entry / after exit (server default 20).
  getChart(id: string, padBars?: number): Promise<TradeChartResponse> {
    const q = padBars ? `?padBars=${padBars}` : '';
    return firstValueFrom(this.http.get<TradeChartResponse>(`/api/trades/${id}/chart${q}`));
  }

  // P3.5: per-bar MAE/MFE excursion path for one trade.
  getExcursions(id: string): Promise<TradeExcursionResponse> {
    return firstValueFrom(this.http.get<TradeExcursionResponse>(`/api/trades/${id}/excursions`));
  }

  getBars(symbol: string, timeframe: string, from: string, to: string): Promise<BarData[]> {
    return firstValueFrom(
      this.http.get<BarData[]>(`/api/bars?symbol=${symbol}&timeframe=${timeframe}&from=${from}&to=${to}`),
    );
  }
}
