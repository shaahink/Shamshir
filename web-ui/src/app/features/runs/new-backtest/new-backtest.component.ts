import { Component, inject, OnInit, signal } from '@angular/core';
import { Router } from '@angular/router';
import { HttpClient } from '@angular/common/http';
import { FormsModule } from '@angular/forms';
import { NgClass } from '@angular/common';
import { firstValueFrom } from 'rxjs';
import { RunsStore } from '../runs.store';
import type { StrategySummary, StartRunRequest } from '../../../models/api.types';

@Component({
  selector: 'app-new-backtest',
  standalone: true,
  imports: [FormsModule, NgClass],
  template: `
    <div class="mx-auto max-w-3xl space-y-6">
      <h1 class="text-xl font-semibold">New Backtest</h1>

      <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-6 space-y-5">
        <div class="grid grid-cols-2 gap-4">
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Symbols (comma)</label>
            <input [(ngModel)]="symbols"
              class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 placeholder-gray-500 focus:border-emerald-500 focus:outline-none"
              placeholder="EURUSD,GBPUSD" />
          </div>
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Timeframes (comma)</label>
            <input [(ngModel)]="periods"
              class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 placeholder-gray-500 focus:border-emerald-500 focus:outline-none"
              placeholder="h1,h4" />
          </div>
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Start Date</label>
            <input type="date" [(ngModel)]="startDate"
              class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none" />
          </div>
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">End Date</label>
            <input type="date" [(ngModel)]="endDate"
              class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none" />
          </div>
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Initial Balance</label>
            <input type="number" [(ngModel)]="balance"
              class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none" />
          </div>
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Commission (per M)</label>
            <input type="number" [(ngModel)]="commission"
              class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none" />
          </div>
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Spread (pips)</label>
            <input type="number" [(ngModel)]="spread" step="0.1"
              class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none" />
          </div>
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Risk Profile</label>
            <select [(ngModel)]="riskProfile"
              class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none">
              <option value="standard">Standard</option>
              <option value="conservative">Conservative</option>
              <option value="aggressive">Aggressive</option>
            </select>
          </div>
        </div>

        <div>
          <label class="block text-xs font-medium text-gray-400 mb-2">Strategies</label>
          <div class="grid gap-2 md:grid-cols-2">
            @for (s of strategies(); track s.id) {
              <label class="flex items-center gap-2 rounded-md border border-gray-700 p-3 cursor-pointer hover:border-gray-600 transition"
                [ngClass]="{ 'border-emerald-600 bg-emerald-900/10': selectedStrategies().has(s.id) }">
                <input type="checkbox" [checked]="selectedStrategies().has(s.id)"
                  (change)="toggleStrategy(s.id)"
                  class="h-4 w-4 rounded border-gray-600 bg-gray-800 text-emerald-500 focus:ring-emerald-500" />
                <div>
                  <div class="text-sm font-medium text-gray-200">{{ s.displayName }}</div>
                  <div class="text-xs text-gray-500">{{ s.id }}
                    @if (s.stats?.totalTrades) {
                      · {{ s.stats.totalTrades }} trades · {{ (s.stats.winRate * 100).toFixed(0) }}% WR
                    }
                  </div>
                </div>
              </label>
            }
          </div>
          @if (strategies().length === 0) {
            <p class="text-xs text-gray-500">Loading strategies...</p>
          }
        </div>

        @if (error()) {
          <div class="rounded-md bg-red-900/20 p-3 text-sm text-red-400">{{ error() }}</div>
        }

        <button (click)="start()" [disabled]="loading()"
          class="w-full rounded-md bg-emerald-600 px-4 py-2.5 text-sm font-medium text-white hover:bg-emerald-500 disabled:opacity-50">
          {{ loading() ? 'Starting...' : 'Start Backtest' }}
        </button>
      </div>
    </div>
  `,
})
export class NewBacktestComponent implements OnInit {
  private store = inject(RunsStore);
  private router = inject(Router);
  private http = inject(HttpClient);

  symbols = 'EURUSD';
  periods = 'h1';
  startDate = '2024-01-01';
  endDate = '2024-01-31';
  balance = 100_000;
  commission = 30;
  spread = 1;
  riskProfile = 'standard';

  strategies = signal<StrategySummary[]>([]);
  selectedStrategies = signal<Set<string>>(new Set());

  loading = this.store.isLoading;
  error = this.store.error;

  async ngOnInit(): Promise<void> {
    try {
      const data = await firstValueFrom(this.http.get<StrategySummary[]>('/api/strategies'));
      this.strategies.set(data);
      if (data.length > 0) {
        const s = new Set<string>();
        data.filter(x => x.isEnabled).forEach(x => s.add(x.id));
        if (s.size === 0) s.add(data[0].id);
        this.selectedStrategies.set(s);
      }
    } catch { /* strategies endpoint may not be available */ }
  }

  toggleStrategy(id: string): void {
    const s = new Set(this.selectedStrategies());
    if (s.has(id)) s.delete(id); else s.add(id);
    this.selectedStrategies.set(s);
  }

  async start(): Promise<void> {
    const symList = this.symbols.split(',').map(x => x.trim().toUpperCase()).filter(Boolean);
    const perList = this.periods.split(',').map(x => x.trim().toLowerCase()).filter(Boolean);
    const req: StartRunRequest = {
      symbol: symList[0] || 'EURUSD',
      period: perList[0] || 'h1',
      start: this.startDate,
      end: this.endDate,
      balance: this.balance,
      commissionPerMillion: this.commission,
      spreadPips: this.spread,
      symbols: symList,
      periods: perList,
      strategyIds: [...this.selectedStrategies()],
      riskProfileId: this.riskProfile,
    };
    const runId = await this.store.startBacktest(req);
    this.router.navigate(['/runs', runId, 'monitor']);
  }
}
