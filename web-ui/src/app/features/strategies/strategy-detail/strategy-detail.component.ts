import { JsonPipe } from '@angular/common';
import { Component, inject, OnInit, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { ActivatedRoute } from '@angular/router';
import { firstValueFrom } from 'rxjs';

@Component({
  selector: 'app-strategy-detail',
  standalone: true,
  imports: [JsonPipe],
  template: `
    <div class="space-y-6">
      <h1 class="text-xl font-semibold">{{ strategy()?.displayName || 'Strategy' }}</h1>

      @if (strategy(); as s) {
        <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
          <pre class="overflow-auto text-xs text-gray-400">{{ s | json }}</pre>
        </div>
      } @else {
        <div class="py-12 text-center text-sm text-gray-500">Loading...</div>
      }
    </div>
  `,
})
export class StrategyDetailComponent implements OnInit {
  private route = inject(ActivatedRoute);
  private http = inject(HttpClient);
  strategy = signal<any>(null);

  async ngOnInit(): Promise<void> {
    const id = this.route.snapshot.paramMap.get('id');
    if (!id) return;
    const data = await firstValueFrom(this.http.get<any>(`/api/strategies/${id}`));
    this.strategy.set(data);
  }
}
