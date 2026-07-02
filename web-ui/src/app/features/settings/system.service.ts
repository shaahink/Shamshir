import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

export interface SystemInfo {
  version: string;
  branch: string;
  buildDate: string;
  dataPaths: { tradingDb: string };
  activeRuns: number;
  runningRuns: number;
}

export type ResetScope = 'runs' | 'config' | 'all';

@Injectable({ providedIn: 'root' })
export class SystemApiService {
  private http = inject(HttpClient);

  getInfo(): Promise<SystemInfo> {
    return firstValueFrom(this.http.get<SystemInfo>('/api/system/info'));
  }

  // M1.3 — the API requires this exact confirm token for every scope, and 409s while a run is active.
  reset(scope: ResetScope): Promise<{ scope: string; status: string }> {
    return firstValueFrom(
      this.http.post<{ scope: string; status: string }>('/api/system/reset', { scope, confirm: 'delete-everything' }),
    );
  }
}
