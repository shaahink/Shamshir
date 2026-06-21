import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import type { TradeSummary, BarData } from '../../models/api.types';

@Injectable({ providedIn: 'root' })
export class TradesApiService {
  private http = inject(HttpClient);

  getAll(): Promise<TradeSummary[]> {
    return firstValueFrom(this.http.get<TradeSummary[]>('/api/trades'));
  }

  getById(id: string): Promise<TradeSummary> {
    return firstValueFrom(this.http.get<TradeSummary>(`/api/trades/${id}`));
  }

  getBars(symbol: string, timeframe: string, from: string, to: string): Promise<BarData[]> {
    return firstValueFrom(
      this.http.get<BarData[]>(`/api/bars?symbol=${symbol}&timeframe=${timeframe}&from=${from}&to=${to}`),
    );
  }
}
