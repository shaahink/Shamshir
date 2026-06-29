import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import type { TradeSummary, TradeDetail, BarData, TradeListResponse, TradeChartResponse } from '../../models/api.types';

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

  // iter-redesign P6.2: bars + entry/exit/SL/TP markers around the trade window.
  getChart(id: string): Promise<TradeChartResponse> {
    return firstValueFrom(this.http.get<TradeChartResponse>(`/api/trades/${id}/chart`));
  }

  getBars(symbol: string, timeframe: string, from: string, to: string): Promise<BarData[]> {
    return firstValueFrom(
      this.http.get<BarData[]>(`/api/bars?symbol=${symbol}&timeframe=${timeframe}&from=${from}&to=${to}`),
    );
  }
}
