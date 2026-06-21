import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import type { GovernorOptions } from '../../models/api.types';

@Injectable({ providedIn: 'root' })
export class GovernorApiService {
  private http = inject(HttpClient);

  get(): Promise<GovernorOptions> {
    return firstValueFrom(this.http.get<GovernorOptions>('/api/governor-options'));
  }

  save(body: Record<string, unknown>): Promise<GovernorOptions> {
    return firstValueFrom(this.http.put<GovernorOptions>('/api/governor-options', body));
  }
}
