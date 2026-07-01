import { Component, inject, OnInit, signal, ChangeDetectionStrategy } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';

interface MarketDataItem {
  symbol: string;
  timeframe: string;
  source: string;
  firstBar: string;
  lastBar: string;
  barCount: number;
}

@Component({
  selector: 'app-data-manager',
  standalone: true,
  imports: [DatePipe, RouterLink],
  template: `
    <div class="space-y-6">
      <div class="flex items-center justify-between">
        <h1 class="text-xl font-semibold">Data Manager</h1>
        <div class="text-xs text-gray-500">Market data inventory for tape replay backtests</div>
      </div>

      @if (loading()) {
        <div class="py-12 text-center text-sm text-gray-500">Loading inventory...</div>
      } @else if (error()) {
        <div class="rounded-lg border border-red-800 bg-red-900/20 p-4 text-sm text-red-400">{{ error() }}</div>
      } @else if (inventory().length === 0) {
        <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-12 text-center">
          <p class="text-sm text-gray-400 mb-2">No market data available.</p>
          <p class="text-xs text-gray-500">
            Run a cTrader backtest with <code class="bg-gray-800 px-1 rounded">--Record=true</code> to download data,
            or use the recorder cBot to pull history for tape replay.
          </p>
          <p class="text-xs text-gray-500 mt-2">
            Data is stored in <code class="bg-gray-800 px-1 rounded">src/TradingEngine.Web/data/marketdata.db</code>
          </p>
        </div>
      } @else {
        <div class="overflow-x-auto rounded-lg border border-gray-800">
          <table class="min-w-full text-sm">
            <thead class="bg-gray-900/50">
              <tr>
                <th class="px-4 py-2 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Symbol</th>
                <th class="px-4 py-2 text-left text-xs font-medium uppercase tracking-wide text-gray-500">TF</th>
                <th class="px-4 py-2 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Source</th>
                <th class="px-4 py-2 text-left text-xs font-medium uppercase tracking-wide text-gray-500">First Bar</th>
                <th class="px-4 py-2 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Last Bar</th>
                <th class="px-4 py-2 text-right text-xs font-medium uppercase tracking-wide text-gray-500"># Bars</th>
              </tr>
            </thead>
            <tbody class="divide-y divide-gray-800">
              @for (item of inventory(); track item.symbol + item.timeframe + item.source) {
                <tr class="hover:bg-gray-800/30">
                  <td class="whitespace-nowrap px-4 py-2 font-mono text-xs text-gray-300">{{ item.symbol }}</td>
                  <td class="whitespace-nowrap px-4 py-2 font-mono text-xs text-gray-400">{{ item.timeframe }}</td>
                  <td class="whitespace-nowrap px-4 py-2 font-mono text-xs text-gray-500">{{ item.source }}</td>
                  <td class="whitespace-nowrap px-4 py-2 font-mono text-xs text-gray-400">{{ item.firstBar | date:'yyyy-MM-dd HH:mm' }}</td>
                  <td class="whitespace-nowrap px-4 py-2 font-mono text-xs text-gray-400">{{ item.lastBar | date:'yyyy-MM-dd HH:mm' }}</td>
                  <td class="whitespace-nowrap px-4 py-2 text-right font-mono text-xs tabular-nums text-gray-300">{{ item.barCount.toLocaleString() }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DataManagerComponent implements OnInit {
  private http = inject(HttpClient);
  inventory = signal<MarketDataItem[]>([]);
  loading = signal(true);
  error = signal<string | null>(null);

  ngOnInit(): void {
    this.loadInventory();
  }

  private loadInventory(): void {
    this.loading.set(true);
    this.http.get<MarketDataItem[]>('/api/data-manager/inventory').subscribe({
      next: (data) => {
        this.inventory.set(data);
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(err?.message ?? 'Failed to load inventory');
        this.loading.set(false);
      },
    });
  }
}
