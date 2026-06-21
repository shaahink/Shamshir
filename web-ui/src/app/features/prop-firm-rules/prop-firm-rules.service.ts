import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

@Injectable({ providedIn: 'root' })
export class PropFirmRulesApiService {
  private http = inject(HttpClient);

  getAll(): Promise<any[]> {
    return firstValueFrom(this.http.get<any[]>('/api/prop-firm-rules'));
  }

  getById(id: string): Promise<any> {
    return firstValueFrom(this.http.get<any>(`/api/prop-firm-rules/${id}`));
  }

  save(id: string, body: Record<string, any>): Promise<any> {
    return firstValueFrom(this.http.put<any>(`/api/prop-firm-rules/${id}`, body));
  }

  create(body: Record<string, any>): Promise<any> {
    return firstValueFrom(this.http.post<any>('/api/prop-firm-rules', body));
  }

  duplicate(id: string): Promise<{ id: string }> {
    return firstValueFrom(this.http.post<{ id: string }>(`/api/prop-firm-rules/${id}/duplicate`, {}));
  }

  delete(id: string): Promise<any> {
    return firstValueFrom(this.http.delete<any>(`/api/prop-firm-rules/${id}`));
  }
}
