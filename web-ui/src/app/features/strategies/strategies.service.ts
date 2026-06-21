import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import type { StrategySummary, StrategyDetail } from '../../models/api.types';

@Injectable({ providedIn: 'root' })
export class StrategiesApiService {
  private http = inject(HttpClient);

  getAll(): Promise<StrategySummary[]> {
    return firstValueFrom(this.http.get<StrategySummary[]>('/api/strategies'));
  }

  getById(id: string): Promise<StrategyDetail> {
    return firstValueFrom(this.http.get<StrategyDetail>(`/api/strategies/${id}`));
  }

  toggleEnabled(id: string, enabled: boolean): Promise<unknown> {
    const ep = enabled ? 'enable' : 'disable';
    return firstValueFrom(this.http.put(`/api/strategies/${id}/${ep}`, {}));
  }

  // iter-38 NG-R2: the backend PUT returns the updated strategy (not JSON-stringified).
  save(id: string, body: StrategyDetail & Record<string, unknown>): Promise<StrategyDetail> {
    return firstValueFrom(this.http.put<StrategyDetail>(`/api/strategies/${id}/config`, body));
  }

  duplicate(id: string): Promise<{ id: string }> {
    return firstValueFrom(this.http.post<{ id: string }>(`/api/strategies/${id}/duplicate`, {}));
  }
}
