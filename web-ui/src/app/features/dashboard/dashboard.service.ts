import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import type { GovernorState } from '../../models/api.types';

export interface EquitySnapshot {
  timestampUtc: string;
  equity: number;
}

@Injectable({ providedIn: 'root' })
export class DashboardApiService {
  private http = inject(HttpClient);

  getGovernorState(): Promise<GovernorState> {
    return firstValueFrom(this.http.get<GovernorState>('/api/governor/state'));
  }

  getEquity(): Promise<EquitySnapshot[]> {
    return firstValueFrom(this.http.get<EquitySnapshot[]>('/api/equity'));
  }
}
