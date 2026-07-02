import { Component, inject, OnInit, OnDestroy, signal, computed, ChangeDetectionStrategy } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';

import { BadgeComponent } from '../../shared/badge.component';

interface MarketDataItem {
  symbol: string;
  timeframe: string;
  source: string;
  firstBar: string;
  lastBar: string;
  barCount: number;
  m1Overlap?: boolean;
  spreadPips?: number;
}

@Component({
  selector: 'app-data-manager',
  standalone: true,
  imports: [DatePipe, FormsModule, BadgeComponent],
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
              @for (s of dlSymbols; track s) { <option [value]="s">{{ s }}</option> }
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
            <label class="block text-xs font-medium text-gray-400 mb-1">Range</label>
            <div class="flex rounded-md border border-gray-700 overflow-hidden text-xs">
              <button type="button" (click)="dlMode.set('days')"
                [class]="dlMode() === 'days' ? 'bg-emerald-600 text-white px-3 py-1.5' : 'bg-gray-800 text-gray-400 px-3 py-1.5 hover:text-gray-200'">Last N days</button>
              <button type="button" (click)="dlMode.set('range')"
                [class]="dlMode() === 'range' ? 'bg-emerald-600 text-white px-3 py-1.5' : 'bg-gray-800 text-gray-400 px-3 py-1.5 hover:text-gray-200'">Date range</button>
            </div>
          </div>
          @if (dlMode() === 'days') {
            <div>
              <label class="block text-xs font-medium text-gray-400 mb-1">Days</label>
              <select [(ngModel)]="dlDays" class="rounded-md border border-gray-700 bg-gray-800 px-3 py-1.5 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none">
                <option [value]="3">3</option>
                <option [value]="7">7</option>
                <option [value]="30">30</option>
                <option [value]="90">90</option>
                <option [value]="180">180</option>
                <option [value]="365">365</option>
              </select>
            </div>
          } @else {
            <div>
              <label class="block text-xs font-medium text-gray-400 mb-1">From</label>
              <input type="date" [(ngModel)]="dlFrom" class="rounded-md border border-gray-700 bg-gray-800 px-3 py-1.5 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none" />
            </div>
            <div>
              <label class="block text-xs font-medium text-gray-400 mb-1">To</label>
              <input type="date" [(ngModel)]="dlTo" class="rounded-md border border-gray-700 bg-gray-800 px-3 py-1.5 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none" />
            </div>
          }
          <button (click)="startDownload()" [disabled]="dlLoading()"
            class="rounded-md bg-emerald-600 px-4 py-1.5 text-sm font-medium text-white hover:bg-emerald-500 disabled:opacity-50">
            {{ dlLoading() ? 'Downloading...' : 'Download' }}
          </button>
        </div>
        @if (dlResult()) {
          <div class="mt-2 rounded p-2 text-xs" [class]="dlResult()!.status === 'failed' ? 'bg-red-900/20 text-red-400' : dlResult()!.status === 'done' ? 'bg-emerald-900/20 text-emerald-400' : 'bg-blue-900/20 text-blue-400'">
            {{ dlResult()!.symbol }} ({{ dlResult()!.tfs?.join(', ') || '—' }}) — {{ dlResult()!.status }}
            @if (dlResult()!.barsRecorded! > 0) { · {{ dlResult()!.barsRecorded!.toLocaleString() }} bars }
            @if (dlResult()!.status === 'done') { · refresh to see updated inventory }
            @if (dlResult()!.error) {
              <div class="mt-1 font-mono">{{ dlResult()!.error }}</div>
            }
          </div>
        }
        @if (dlError()) {
          <div class="mt-2 rounded bg-red-900/20 p-2 text-xs text-red-400">{{ dlError() }}</div>
        }
      </div>

      <!-- Import (cTrader-free): upload NDJSON or CSV history -->
      <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
        <h2 class="mb-1 text-sm font-medium text-gray-300">Import Market Data</h2>
        <p class="mb-3 text-xs text-gray-500">Upload an NDJSON shard or a CSV export (columns: time, open, high, low, close, [volume, symbol, timeframe]). Bars are deduped on import — no cTrader required.</p>
        <div class="flex flex-wrap items-end gap-3">
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">File</label>
            <input type="file" accept=".ndjson,.csv,.json,.txt" (change)="onFileSelect($event)"
              class="text-xs text-gray-300 file:mr-2 file:rounded file:border-0 file:bg-gray-700 file:px-3 file:py-1.5 file:text-gray-100 hover:file:bg-gray-600" />
          </div>
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Symbol <span class="text-gray-600">(CSV w/o column)</span></label>
            <input type="text" [(ngModel)]="impSymbol" placeholder="EURUSD"
              class="w-28 rounded-md border border-gray-700 bg-gray-800 px-3 py-1.5 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none" />
          </div>
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Timeframe <span class="text-gray-600">(CSV w/o column)</span></label>
            <input type="text" [(ngModel)]="impTimeframe" placeholder="H1"
              class="w-20 rounded-md border border-gray-700 bg-gray-800 px-3 py-1.5 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none" />
          </div>
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Source tag</label>
            <input type="text" [(ngModel)]="impSource" placeholder="import"
              class="w-28 rounded-md border border-gray-700 bg-gray-800 px-3 py-1.5 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none" />
          </div>
          <button (click)="importFile()" [disabled]="impLoading() || !impFile"
            class="rounded-md bg-indigo-600 px-4 py-1.5 text-sm font-medium text-white hover:bg-indigo-500 disabled:opacity-50">
            {{ impLoading() ? 'Importing...' : 'Import' }}
          </button>
        </div>
        @if (impResult()) {
          <div class="mt-2 rounded bg-emerald-900/20 p-2 text-xs text-emerald-400">
            {{ impResult()!.fileName }} ({{ impResult()!.format }}) — {{ impResult()!.barsInserted.toLocaleString() }} bars inserted
            @if (impResult()!.parseErrors > 0) { · {{ impResult()!.parseErrors }} skipped }
          </div>
        }
        @if (impError()) {
          <div class="mt-2 rounded bg-red-900/20 p-2 text-xs text-red-400">{{ impError() }}</div>
        }
      </div>

      @if (loading()) {
        <div class="py-12 text-center text-sm text-gray-500">Loading inventory...</div>
      } @else if (error()) {
        <div class="rounded-lg border border-red-800 bg-red-900/20 p-4 text-sm text-red-400">{{ error() }}</div>
      } @else if (inventory().length === 0) {
        <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-12 text-center">
          <p class="text-sm text-gray-400 mb-2">No market data available.</p>
          <p class="text-xs text-gray-500">Download via cTrader (top form) or import an NDJSON/CSV file (no cTrader required).</p>
        </div>
      } @else {
        <!-- Per-symbol storage totals -->
        <div class="flex flex-wrap gap-2">
          @for (s of perSymbol(); track s.symbol) {
            <div class="rounded-md border border-gray-800 bg-gray-900/40 px-3 py-1.5 text-xs">
              <span class="font-mono text-gray-300">{{ s.symbol }}</span>
              <span class="ml-2 text-gray-500">{{ s.bars.toLocaleString() }} bars · {{ s.tfs }} TF</span>
            </div>
          }
        </div>

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
                <th class="px-4 py-2 text-center text-xs font-medium uppercase tracking-wide text-gray-500">Coverage</th>
                <th class="px-4 py-2 text-right text-xs font-medium uppercase tracking-wide text-gray-500"></th>
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
                    <div class="flex items-center justify-center gap-1">
                      @if (item.timeframe !== 'M1') {
                        <app-badge [label]="item.m1Overlap ? 'M1 ✓' : 'No M1'" [variant]="item.m1Overlap ? 'success' : 'error'" />
                      }
                      @if (item.spreadPips != null) {
                        <span class="text-xs text-gray-500">{{ item.spreadPips.toFixed(1) }} sp</span>
                      }
                    </div>
                  </td>
                  <td class="whitespace-nowrap px-4 py-2 text-right">
                    <button (click)="deleteRow(item)" [disabled]="deletingKey() === rowKey(item)"
                      class="rounded border border-red-900 px-2 py-0.5 text-xs text-red-400 hover:bg-red-900/20 disabled:opacity-50">
                      {{ deletingKey() === rowKey(item) ? '…' : 'Delete' }}
                    </button>
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
export class DataManagerComponent implements OnInit, OnDestroy {
  private http = inject(HttpClient);
  inventory = signal<MarketDataItem[]>([]);
  loading = signal(true);
  error = signal<string | null>(null);
  deletingKey = signal<string | null>(null);

  perSymbol = computed(() => {
    const by = new Map<string, { symbol: string; bars: number; tfs: number }>();
    for (const i of this.inventory()) {
      const e = by.get(i.symbol) ?? { symbol: i.symbol, bars: 0, tfs: 0 };
      e.bars += i.barCount;
      e.tfs += 1;
      by.set(i.symbol, e);
    }
    return [...by.values()].sort((a, b) => a.symbol.localeCompare(b.symbol));
  });

  rowKey = (i: MarketDataItem) => `${i.symbol}|${i.timeframe}|${i.source}`;

  dlSymbol = 'EURUSD';
  dlSymbols = ['EURUSD','GBPUSD','USDJPY','GBPJPY','XAUUSD','AUDUSD','USDCHF','USDCAD','NZDUSD','EURGBP','EURJPY','XAGUSD'];
  allTfs = [
    { value: 'm1', label: 'M1' },
    { value: 'm5', label: 'M5' },
    { value: 'm15', label: 'M15' },
    { value: 'h1', label: 'H1' },
    { value: 'h4', label: 'H4' },
    { value: 'd1', label: 'D1' },
  ];
  dlTfs = signal<string[]>(['h1', 'm1']);
  dlDays = 7;
  dlMode = signal<'days' | 'range'>('days');
  dlFrom = '';
  dlTo = '';
  dlLoading = signal(false);
  dlResult = signal<{ symbol: string; tfs: string[]; status: string; barsRecorded?: number; error?: string } | null>(null);
  dlError = signal<string | null>(null);
  private pollTimer: ReturnType<typeof setInterval> | null = null;

  impFile: File | null = null;
  impSymbol = '';
  impTimeframe = '';
  impSource = '';
  impLoading = signal(false);
  impResult = signal<{ fileName: string; format: string; barsInserted: number; parseErrors: number } | null>(null);
  impError = signal<string | null>(null);

  ngOnInit(): void {
    const now = new Date();
    this.dlTo = now.toISOString().slice(0, 10);
    const from = new Date(now);
    from.setDate(from.getDate() - 7);
    this.dlFrom = from.toISOString().slice(0, 10);
    this.loadInventory();
  }

  ngOnDestroy(): void {
    this.stopPoll();
  }

  toggleTf(tf: string): void {
    this.dlTfs.update(tfs => tfs.includes(tf) ? tfs.filter(t => t !== tf) : [...tfs, tf]);
  }

  startDownload(): void {
    if (this.dlTfs().length === 0) { this.dlError.set('Select at least one timeframe.'); return; }
    const body: { symbol: string; tfs: string[]; days?: number; from?: string; to?: string } = {
      symbol: this.dlSymbol,
      tfs: this.dlTfs(),
    };
    if (this.dlMode() === 'range') {
      if (!this.dlFrom || !this.dlTo) { this.dlError.set('Select a From and To date.'); return; }
      if (new Date(this.dlFrom) >= new Date(this.dlTo)) { this.dlError.set('From date must be before To date.'); return; }
      body.from = this.dlFrom;
      body.to = this.dlTo;
    } else {
      body.days = this.dlDays;
    }
    this.dlLoading.set(true);
    this.dlError.set(null);
    this.dlResult.set(null);
    this.http.post<{ jobId: string; symbol: string; tfs: string[]; status: string }>('/api/data-manager/download', body).subscribe({
      next: (r) => {
        this.dlLoading.set(false);
        this.dlResult.set({ symbol: r.symbol, tfs: r.tfs, status: r.status });
        this.pollJob(r.jobId);
      },
      error: (err) => {
        this.dlError.set(err?.error?.error ?? err?.message ?? 'Download failed');
        this.dlLoading.set(false);
      },
    });
  }

  private pollJob(jobId: string): void {
    if (this.pollTimer) clearInterval(this.pollTimer);
    this.pollTimer = setInterval(() => {
      this.http.get<{ status: string; barsRecorded?: number; error?: string }>(`/api/data-manager/jobs/${jobId}`).subscribe({
        next: (j) => {
          const cur = this.dlResult();
          if (cur) this.dlResult.set({ ...cur, status: j.status, barsRecorded: j.barsRecorded, error: j.error });
          if (j.status === 'done') { this.stopPoll(); this.loadInventory(); }
          if (j.status === 'failed') this.stopPoll();
        },
        error: () => this.stopPoll(),
      });
    }, 1500);
  }

  private stopPoll(): void {
    if (this.pollTimer) { clearInterval(this.pollTimer); this.pollTimer = null; }
  }

  onFileSelect(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.impFile = input.files && input.files.length > 0 ? input.files[0] : null;
    this.impResult.set(null);
    this.impError.set(null);
  }

  importFile(): void {
    if (!this.impFile) { this.impError.set('Choose a file first.'); return; }
    const form = new FormData();
    form.append('file', this.impFile);
    if (this.impSource.trim()) form.append('source', this.impSource.trim());
    if (this.impSymbol.trim()) form.append('symbol', this.impSymbol.trim());
    if (this.impTimeframe.trim()) form.append('timeframe', this.impTimeframe.trim());
    this.impLoading.set(true);
    this.impError.set(null);
    this.impResult.set(null);
    this.http.post<{ fileName: string; format: string; barsInserted: number; parseErrors: number }>('/api/data-manager/import', form)
      .subscribe({
        next: (r) => {
          this.impLoading.set(false);
          this.impResult.set(r);
          this.loadInventory();
        },
        error: (err) => {
          this.impError.set(err?.error?.error ?? err?.message ?? 'Import failed');
          this.impLoading.set(false);
        },
      });
  }

  deleteRow(item: MarketDataItem): void {
    if (this.deletingKey()) return;
    if (!confirm(`Delete all ${item.barCount.toLocaleString()} ${item.symbol} ${item.timeframe} bars (${item.source})? This cannot be undone.`)) return;
    this.deletingKey.set(this.rowKey(item));
    this.http.post<{ deleted: number }>('/api/data-manager/delete', {
      symbol: item.symbol,
      timeframe: item.timeframe,
      source: item.source,
    }).subscribe({
      next: () => { this.deletingKey.set(null); this.loadInventory(); },
      error: (err) => {
        this.error.set(err?.error?.error ?? err?.message ?? 'Delete failed');
        this.deletingKey.set(null);
      },
    });
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
