import { Component, inject } from '@angular/core';
import { Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { RunsStore } from '../runs.store';

@Component({
  selector: 'app-new-backtest',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="mx-auto max-w-2xl space-y-6">
      <h1 class="text-xl font-semibold">New Backtest</h1>

      <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-6 space-y-4">
        <div class="grid grid-cols-2 gap-4">
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Symbol</label>
            <input
              [(ngModel)]="symbol"
              class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 placeholder-gray-500 focus:border-emerald-500 focus:outline-none"
              placeholder="EURUSD"
            />
          </div>
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Timeframe</label>
            <select
              [(ngModel)]="period"
              class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none"
            >
              <option value="m1">M1</option>
              <option value="m5">M5</option>
              <option value="m15">M15</option>
              <option value="m30">M30</option>
              <option value="h1">H1</option>
              <option value="h4">H4</option>
              <option value="d1">D1</option>
            </select>
          </div>
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Start Date</label>
            <input
              type="date"
              [(ngModel)]="startDate"
              class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none"
            />
          </div>
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">End Date</label>
            <input
              type="date"
              [(ngModel)]="endDate"
              class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none"
            />
          </div>
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Initial Balance</label>
            <input
              type="number"
              [(ngModel)]="balance"
              class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none"
            />
          </div>
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Commission (per M)</label>
            <input
              type="number"
              [(ngModel)]="commission"
              class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none"
            />
          </div>
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Spread (pips)</label>
            <input
              type="number"
              [(ngModel)]="spread"
              step="0.1"
              class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none"
            />
          </div>
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Strategy IDs (csv)</label>
            <input
              [(ngModel)]="strategyIds"
              class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 placeholder-gray-500 focus:border-emerald-500 focus:outline-none"
              placeholder="rsi-divergence,trend-breakout"
            />
          </div>
        </div>

        @if (error()) {
          <div class="rounded-md bg-red-900/20 p-3 text-sm text-red-400">{{ error() }}</div>
        }

        <button
          (click)="start()"
          [disabled]="loading()"
          class="w-full rounded-md bg-emerald-600 px-4 py-2.5 text-sm font-medium text-white hover:bg-emerald-500 disabled:opacity-50"
        >
          {{ loading() ? 'Starting...' : 'Start Backtest' }}
        </button>
      </div>
    </div>
  `,
})
export class NewBacktestComponent {
  private store = inject(RunsStore);
  private router = inject(Router);

  symbol = 'EURUSD';
  period = 'h1';
  startDate = '2024-01-01';
  endDate = '2024-01-31';
  balance = 100_000;
  commission = 30;
  spread = 1;
  strategyIds = '';

  loading = this.store.isLoading;
  error = this.store.error;

  async start(): Promise<void> {
    const runId = await this.store.startBacktest({
      symbol: this.symbol,
      period: this.period,
      start: this.startDate,
      end: this.endDate,
      balance: this.balance,
      commissionPerMillion: this.commission,
      spreadPips: this.spread,
      strategyIds: this.strategyIds || undefined,
    });
    this.router.navigate(['/runs', runId, 'monitor']);
  }
}
