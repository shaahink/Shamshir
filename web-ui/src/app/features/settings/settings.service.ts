import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import type { SystemInfo, ResetRequest, ResetResponse } from '../../models/api.types';

@Injectable({ providedIn: 'root' })
export class SettingsService {
  private http = inject(HttpClient);

  getSystemInfo(): Promise<SystemInfo> {
    return firstValueFrom(this.http.get<SystemInfo>('/api/system/info'));
  }

  reset(req: ResetRequest): Promise<ResetResponse> {
    return firstValueFrom(this.http.post<ResetResponse>('/api/system/reset', req));
  }
}
