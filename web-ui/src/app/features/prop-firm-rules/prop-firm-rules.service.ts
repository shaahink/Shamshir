import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import type { PropFirmRule } from '../../models/api.types';

@Injectable({ providedIn: 'root' })
export class PropFirmRulesApiService {
  private http = inject(HttpClient);

  getAll(): Promise<PropFirmRule[]> {
    return firstValueFrom(this.http.get<PropFirmRule[]>('/api/prop-firm-rules'));
  }

  getById(id: string): Promise<PropFirmRule> {
    return firstValueFrom(this.http.get<PropFirmRule>(`/api/prop-firm-rules/${id}`));
  }

  save(id: string, body: Record<string, unknown>): Promise<PropFirmRule> {
    return firstValueFrom(this.http.put<PropFirmRule>(`/api/prop-firm-rules/${id}`, body));
  }

  create(body: Record<string, unknown>): Promise<PropFirmRule> {
    return firstValueFrom(this.http.post<PropFirmRule>('/api/prop-firm-rules', body));
  }

  duplicate(id: string): Promise<{ id: string }> {
    return firstValueFrom(this.http.post<{ id: string }>(`/api/prop-firm-rules/${id}/duplicate`, {}));
  }

  delete(id: string): Promise<void> {
    return firstValueFrom(this.http.delete(`/api/prop-firm-rules/${id}`)) as unknown as Promise<void>;
  }
}
