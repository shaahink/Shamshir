import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

@Injectable({ providedIn: 'root' })
export class GovernorApiService {
  private http = inject(HttpClient);

  get(): Promise<any> {
    return firstValueFrom(this.http.get<any>('/api/governor-options'));
  }

  save(body: Record<string, any>): Promise<any> {
    return firstValueFrom(this.http.put<any>('/api/governor-options', body));
  }
}
