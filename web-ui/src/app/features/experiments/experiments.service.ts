import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import type {
  ExperimentSummary,
  ExperimentDetail,
  ExperimentCreateResult,
  ExperimentSpecInput,
} from '../../models/api.types';

@Injectable({ providedIn: 'root' })
export class ExperimentsApiService {
  private http = inject(HttpClient);

  getAll(): Promise<ExperimentSummary[]> {
    return firstValueFrom(this.http.get<ExperimentSummary[]>('/api/experiments'));
  }

  getById(id: string): Promise<ExperimentDetail> {
    return firstValueFrom(this.http.get<ExperimentDetail>(`/api/experiments/${id}`));
  }

  create(spec: ExperimentSpecInput): Promise<ExperimentCreateResult> {
    return firstValueFrom(this.http.post<ExperimentCreateResult>('/api/experiments', spec));
  }

  getReport(id: string): Promise<string> {
    return firstValueFrom(this.http.get(`/api/experiments/${id}/report`, { responseType: 'text' }));
  }
}
