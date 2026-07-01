import { Component, inject, OnInit, signal, ChangeDetectionStrategy } from '@angular/core';
import { RouterLink, Router } from '@angular/router';
import { DatePipe } from '@angular/common';
import type { StrategySummary } from '../../../models/api.types';
import { StrategiesApiService } from '../strategies.service';

@Component({
  selector: 'app-strategy-list',
  standalone: true,
  imports: [RouterLink, DatePipe],
  template: `
    <div class="space-y-6">
      <div class="flex items-center justify-between">
        <h1 class="text-xl font-semibold">Strategies</h1>
        <a
          routerLink="/strategies/new"
          class="rounded-md bg-emerald-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-emerald-500"
        >
          New Strategy
        </a>
      </div>
      <div class="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
        @for (s of strategies(); track s.id) {
          <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4 transition hover:border-gray-600">
            <div class="flex items-center justify-between mb-2">
              <a
                [routerLink]="['/strategies', s.id]"
                class="text-sm font-medium text-gray-100 hover:text-emerald-400"
                >{{ s.displayName }}</a
              >
              <button
                (click)="toggleEnabled(s)"
                [attr.class]="
                  s.isEnabled
                    ? 'inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium transition bg-emerald-900/50 text-emerald-400'
                    : 'inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium transition bg-gray-800 text-gray-500'
                "
              >
                {{ s.isEnabled ? 'Active' : 'Disabled' }}
              </button>
            </div>
            <p class="text-xs text-gray-500 mb-1">Entry: {{ s.entryRule || '—' }} · Exit: {{ s.exitFormula || '—' }}</p>
            <p class="font-mono text-xs text-gray-600 mb-2">{{ s.id }}</p>
            @if (s.createdAtUtc) {
              <p class="text-xs text-gray-600 mb-2">Created {{ s.createdAtUtc | date: 'yyyy-MM-dd' }}</p>
            }
            @if (s.stats) {
              <div class="grid grid-cols-3 gap-2 text-xs text-gray-400">
                <div><span class="text-gray-500">Trades</span><br />{{ s.stats.totalTrades }}</div>
                <div><span class="text-gray-500">Win Rate</span><br />{{ (s.stats.winRate * 100).toFixed(0) }}%</div>
                <div>
                  <span class="text-gray-500">P/L</span><br /><span
                    [class.text-emerald-400]="s.stats.totalPnL > 0"
                    [class.text-red-400]="s.stats.totalPnL < 0"
                    >{{ s.stats.totalPnL.toFixed(0) }}</span
                  >
                </div>
              </div>
            }
          </div>
        }
      </div>
      @if (strategies().length === 0) {
        <div class="py-12 text-center text-sm text-gray-500">No strategies loaded.</div>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class StrategyListComponent implements OnInit {
  private api = inject(StrategiesApiService);
  strategies = signal<StrategySummary[]>([]);

  async ngOnInit(): Promise<void> {
    try {
      const data = await this.api.getAll();
      this.strategies.set(data);
    } catch {
      /* */
    }
  }

  async toggleEnabled(s: StrategySummary): Promise<void> {
    await this.api.toggleEnabled(s.id, !s.isEnabled);
    this.strategies.update((arr) => arr.map((x) => (x.id === s.id ? { ...x, isEnabled: !x.isEnabled } : x)));
  }
}
