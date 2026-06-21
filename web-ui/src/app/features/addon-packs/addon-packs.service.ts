import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import type { AddOnPack, AutoTunePreview } from '../../models/api.types';

@Injectable({ providedIn: 'root' })
export class AddOnPacksApiService {
  private http = inject(HttpClient);

  getAll(): Promise<AddOnPack[]> {
    return firstValueFrom(this.http.get<AddOnPack[]>('/api/addons/packs'));
  }

  getById(id: string): Promise<AddOnPack> {
    return firstValueFrom(this.http.get<AddOnPack>(`/api/addons/packs/${id}`));
  }

  save(id: string, body: AddOnPack): Promise<{ id: string }> {
    return firstValueFrom(this.http.put<{ id: string }>(`/api/addons/packs/${id}`, body));
  }

  delete(id: string): Promise<unknown> {
    return firstValueFrom(this.http.delete(`/api/addons/packs/${id}`));
  }

  preview(tf: string, atrPips?: number, spreadPips?: number): Promise<AutoTunePreview> {
    let url = `/api/addons/preview?tf=${tf}`;
    if (atrPips != null) url += `&atrPips=${atrPips}`;
    if (spreadPips != null) url += `&spreadPips=${spreadPips}`;
    return firstValueFrom(this.http.get<AutoTunePreview>(url));
  }
}
