import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import type { StrategySummary, StrategyDetail } from '../../models/api.types';

@Injectable({ providedIn: 'root' })
export class StrategiesApiService {
  private http = inject(HttpClient);

  async getAll(): Promise<StrategySummary[]> {
    const r = await firstValueFrom(this.http.get<{ strategies: StrategySummary[] }>('/api/strategies'));
    return r.strategies;
  }

  getById(id: string): Promise<StrategyDetail> {
    return firstValueFrom(this.http.get<StrategyDetail>(`/api/strategies/${id}`));
  }

  toggleEnabled(id: string, enabled: boolean): Promise<unknown> {
    const ep = enabled ? 'enable' : 'disable';
    return firstValueFrom(this.http.put(`/api/strategies/${id}/${ep}`, {}));
  }

  save(id: string, body: StrategyDetail & Record<string, unknown>): Promise<StrategyDetail> {
    return firstValueFrom(this.http.put<StrategyDetail>(`/api/strategies/${id}/config`, body));
  }

  duplicate(id: string): Promise<{ id: string }> {
    return firstValueFrom(this.http.post<{ id: string }>(`/api/strategies/${id}/duplicate`, {}));
  }

  create(body: { id: string; displayName: string; symbols: string[]; timeframe: string; riskProfileId?: string }): Promise<{ id: string; displayName: string; saved: boolean }> {
    return firstValueFrom(this.http.post<{ id: string; displayName: string; saved: boolean }>('/api/strategies', body));
  }

  delete(id: string): Promise<unknown> {
    return firstValueFrom(this.http.delete(`/api/strategies/${id}`));
  }
}
