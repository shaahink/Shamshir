import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import type { RiskProfile } from '../../models/api.types';

@Injectable({ providedIn: 'root' })
export class RiskProfilesApiService {
  private http = inject(HttpClient);

  getAll(): Promise<RiskProfile[]> {
    return firstValueFrom(this.http.get<RiskProfile[]>('/api/risk-profiles'));
  }

  getById(id: string): Promise<RiskProfile> {
    return firstValueFrom(this.http.get<RiskProfile>(`/api/risk-profiles/${id}`));
  }

  save(id: string, body: Record<string, unknown>): Promise<RiskProfile> {
    return firstValueFrom(this.http.put<RiskProfile>(`/api/risk-profiles/${id}`, body));
  }

  create(body: Record<string, unknown>): Promise<void> {
    return firstValueFrom(this.http.post('/api/risk-profiles', body)) as unknown as Promise<void>;
  }

  duplicate(id: string): Promise<{ id: string }> {
    return firstValueFrom(this.http.post<{ id: string }>(`/api/risk-profiles/${id}/duplicate`, {}));
  }

  delete(id: string): Promise<void> {
    return firstValueFrom(this.http.delete(`/api/risk-profiles/${id}`)) as unknown as Promise<void>;
  }
}
