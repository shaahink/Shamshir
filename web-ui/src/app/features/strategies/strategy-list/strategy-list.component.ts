import { Component, inject, OnInit, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { RouterLink } from '@angular/router';
import { NgClass } from '@angular/common';
import { firstValueFrom } from 'rxjs';
import type { StrategySummary } from '../../../models/api.types';

@Component({
  selector: 'app-strategy-list',
  standalone: true,
  imports: [RouterLink, NgClass],
  template: `
    <div class="space-y-6">
      <h1 class="text-xl font-semibold">Strategies</h1>
      <div class="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
        @for (s of strategies(); track s.id) {
          <a [routerLink]="['/strategies', s.id]"
             class="rounded-lg border border-gray-800 bg-gray-900/50 p-4 transition hover:border-gray-600 block">
            <div class="flex items-center justify-between mb-2">
              <span class="text-sm font-medium text-gray-100 hover:text-emerald-400">{{ s.displayName }}</span>
              <span class="inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium"
                [ngClass]="s.isEnabled ? 'bg-emerald-900/50 text-emerald-400' : 'bg-gray-800 text-gray-500'">
                {{ s.isEnabled ? 'Active' : 'Disabled' }}
              </span>
            </div>
            <p class="font-mono text-xs text-gray-500 mb-2">{{ s.id }}</p>
            @if (s.stats) {
              <div class="grid grid-cols-3 gap-2 text-xs text-gray-400">
                <div><span class="text-gray-500">Trades</span><br>{{ s.stats.totalTrades }}</div>
                <div><span class="text-gray-500">Win Rate</span><br>{{ (s.stats.winRate * 100).toFixed(0) }}%</div>
                <div><span class="text-gray-500">P/L</span><br><span [class.text-emerald-400]="s.stats.totalPnL>0" [class.text-red-400]="s.stats.totalPnL<0">{{ s.stats.totalPnL.toFixed(0) }}</span></div>
              </div>
            }
          </a>
        }
      </div>
      @if (strategies().length === 0) {
        <div class="py-12 text-center text-sm text-gray-500">No strategies loaded.</div>
      }
    </div>
  `,
})
export class StrategyListComponent implements OnInit {
  private http = inject(HttpClient);
  strategies = signal<StrategySummary[]>([]);

  async ngOnInit(): Promise<void> {
    try {
      const data = await firstValueFrom(this.http.get<StrategySummary[]>('/api/strategies'));
      this.strategies.set(data);
    } catch { /* API may not be available */ }
  }
}
