import { Component, inject, OnInit, signal, ChangeDetectionStrategy } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';

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
  imports: [DatePipe, FormsModule],
  template: `
    <div class="space-y-6">
      <div class="flex items-center justify-between">
        <h1 class="text-xl font-semibold">Data Manager</h1>
        <div class="text-xs text-gray-500">Market data inventory for tape replay backtests</div>
      </div>

      <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
        <h2 class="mb-3 text-sm font-medium text-gray-300">Download Market Data</h2>
        <div class="flex flex-wrap items-end gap-3">
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Symbol</label>
            <select [(ngModel)]="dlSymbol" class="rounded-md border border-gray-700 bg-gray-800 px-3 py-1.5 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none">
              <option value="EURUSD">EURUSD</option>
              <option value="GBPUSD">GBPUSD</option>
              <option value="USDJPY">USDJPY</option>
              <option value="AUDUSD">AUDUSD</option>
              <option value="USDCAD">USDCAD</option>
              <option value="NZDUSD">NZDUSD</option>
            </select>
          </div>
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Timeframes</label>
            <div class="flex gap-2">
              @for (tf of allTfs; track tf.value) {
                <label class="flex items-center gap-1 text-xs text-gray-400 cursor-pointer">
                  <input type="checkbox" [checked]="dlTfs().includes(tf.value)" (change)="toggleTf(tf.value)" class="rounded" /> {{ tf.label }}
                </label>
              }
            </div>
          </div>
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Days</label>
            <select [(ngModel)]="dlDays" class="rounded-md border border-gray-700 bg-gray-800 px-3 py-1.5 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none">
              <option [value]="3">3</option>
              <option [value]="7">7</option>
              <option [value]="30">30</option>
              <option [value]="90">90</option>
              <option [value]="180">180</option>
            </select>
          </div>
          <button (click)="startDownload()" [disabled]="dlLoading()"
            class="rounded-md bg-emerald-600 px-4 py-1.5 text-sm font-medium text-white hover:bg-emerald-500 disabled:opacity-50">
            {{ dlLoading() ? 'Downloading...' : 'Download' }}
          </button>
        </div>
        @if (dlResult()) {
          <div class="mt-2 rounded bg-emerald-900/20 p-2 text-xs text-emerald-400">
            Downloaded {{ dlResult()?.barsRecorded }} bars for {{ dlResult()?.symbol }} ({{ dlResult()?.tfs?.join(', ') }}) — refresh to see updated inventory.
          </div>
        }
        @if (dlError()) {
          <div class="mt-2 rounded bg-red-900/20 p-2 text-xs text-red-400">{{ dlError() }}</div>
        }
      </div>

      @if (loading()) {
        <div class="py-12 text-center text-sm text-gray-500">Loading inventory...</div>
      } @else if (error()) {
        <div class="rounded-lg border border-red-800 bg-red-900/20 p-4 text-sm text-red-400">{{ error() }}</div>
      } @else if (inventory().length === 0) {
        <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-12 text-center">
          <p class="text-sm text-gray-400 mb-2">No market data available.</p>
          <p class="text-xs text-gray-500">Use the download form above or run a recorder backtest.</p>
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
                <th class="px-4 py-2 text-center text-xs font-medium uppercase tracking-wide text-gray-500">M1 Overlap</th>
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
                  <td class="whitespace-nowrap px-4 py-2 text-center">
                    @if (item.timeframe.toLowerCase() !== 'm1') {
                      @if (hasM1Overlap(item.symbol, item.firstBar, item.lastBar)) {
                        <span class="rounded bg-emerald-900/60 px-1.5 py-0.5 text-xs text-emerald-400">&check; m1</span>
                      } @else {
                        <span class="rounded bg-red-900/40 px-1.5 py-0.5 text-xs text-red-400">&cross; no m1</span>
                      }
                    } @else {
                      <span class="text-xs text-gray-600">&mdash;</span>
                    }
                  </td>
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

  dlSymbol = 'EURUSD';
  allTfs = [
    { value: 'm1', label: 'M1' },
    { value: 'h1', label: 'H1' },
    { value: 'h4', label: 'H4' },
    { value: 'd1', label: 'D1' },
  ];
  dlTfs = signal<string[]>(['h1', 'm1']);
  dlDays = 7;
  dlLoading = signal(false);
  dlResult = signal<{ symbol: string; tfs: string[]; barsRecorded: number } | null>(null);
  dlError = signal<string | null>(null);

  ngOnInit(): void {
    this.loadInventory();
  }

  toggleTf(tf: string): void {
    this.dlTfs.update(tfs => tfs.includes(tf) ? tfs.filter(t => t !== tf) : [...tfs, tf]);
  }

  startDownload(): void {
    this.dlLoading.set(true);
    this.dlError.set(null);
    this.dlResult.set(null);
    this.http.post<{ symbol: string; tfs: string[]; barsRecorded: number }>('/api/data-manager/download', {
      symbol: this.dlSymbol,
      tfs: this.dlTfs(),
      days: this.dlDays,
    }).subscribe({
      next: (r) => {
        this.dlResult.set(r);
        this.dlLoading.set(false);
        this.loadInventory();
      },
      error: (err) => {
        this.dlError.set(err?.error?.error ?? err?.message ?? 'Download failed');
        this.dlLoading.set(false);
        this.loadInventory();
      },
    });
  }

  hasM1Overlap(symbol: string, firstBar: string, lastBar: string): boolean {
    const from = new Date(firstBar).getTime();
    const to = new Date(lastBar).getTime();
    return this.inventory().some(
      i => i.symbol === symbol && i.timeframe.toLowerCase() === 'm1'
        && new Date(i.firstBar).getTime() <= to
        && new Date(i.lastBar).getTime() >= from
    );
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
