import { Component, inject, OnInit, signal, computed, ChangeDetectionStrategy, DestroyRef } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { interval } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

interface MarketDataItem {
  symbol: string;
  timeframe: string;
  source: string;
  firstBar: string;
  lastBar: string;
  barCount: number;
}

interface DownloadJobResponse {
  jobId: string;
  symbol: string;
  tfs: string[];
  status: string;
  barsRecorded?: number;
  linesProcessed?: number;
  filesTotal?: number;
  filesProcessed?: number;
  error?: string;
  statusDetails?: string;
  createdAtUtc?: string;
  completedAtUtc?: string;
}

interface DownloadMultiResponse {
  jobs: DownloadJobResponse[];
}

interface PendingShard {
  fileName: string;
  relativePath: string;
  symbol: string;
  timeframe: string;
  sizeBytes: number;
  lastModifiedUtc: string;
}

interface PendingShardsResponse {
  shardsRoot: string;
  files: PendingShard[];
}

type DatePreset = '30d' | '90d' | '180d' | '1y' | '2y' | '5y' | '2020';

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

        <div class="mb-3">
          <label class="block text-xs font-medium text-gray-400 mb-1">Symbols</label>
          <div class="flex flex-wrap gap-x-3 gap-y-1">
            @for (s of dlSymbols; track s) {
              <label class="flex items-center gap-1 text-xs text-gray-400 cursor-pointer hover:text-gray-300">
                <input type="checkbox" [checked]="dlSelectedSymbols().includes(s)"
                  (change)="toggleSymbol(s)" class="rounded" />
                {{ s }}
              </label>
            }
          </div>
        </div>

        <div class="flex flex-wrap items-end gap-3">
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Timeframes</label>
            <div class="flex gap-2">
              @for (tf of allTfs; track tf.value) {
                <label class="flex items-center gap-1 text-xs text-gray-400 cursor-pointer">
                  <input type="checkbox" [checked]="dlTfs().includes(tf.value)"
                    (change)="toggleTf(tf.value)" class="rounded" /> {{ tf.label }}
                </label>
              }
            </div>
          </div>
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Period</label>
            <select [(ngModel)]="dlPreset" (ngModelChange)="onPresetChange($event)"
              class="rounded-md border border-gray-700 bg-gray-800 px-3 py-1.5 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none">
              <option value="30d">30 days</option>
              <option value="90d">90 days</option>
              <option value="180d">180 days</option>
              <option value="1y">1 year</option>
              <option value="2y">2 years</option>
              <option value="5y">5 years</option>
              <option value="2020">Since 2020</option>
            </select>
          </div>
          <div class="flex items-end gap-2">
            <div class="flex items-center gap-1.5 pb-1.5">
              <input type="checkbox" id="keepShards" [(ngModel)]="keepShards" class="rounded" />
              <label for="keepShards" class="text-xs text-gray-500 cursor-pointer"
                title="Keep NDJSON files in data/shards/archive/ for inspection">Keep files</label>
            </div>
            <button (click)="startDownload()" [disabled]="dlLoading() || dlSelectedSymbols().length === 0"
              class="rounded-md bg-emerald-600 px-4 py-1.5 text-sm font-medium text-white hover:bg-emerald-500 disabled:opacity-50">
              {{ dlLoading() ? 'Starting...' : 'Download ' + dlCountLabel() }}
            </button>
          </div>
        </div>
        @if (dlError()) {
          <div class="mt-2 rounded bg-red-900/20 p-2 text-xs text-red-400">{{ dlError() }}</div>
        }

        @if (activeJobs().length > 0) {
          <div class="mt-3 space-y-1">
            <div class="text-xs text-gray-500 mb-1">
              Active Jobs ({{ runningJobCount() }} running,
              {{ activeJobs().length - runningJobCount() }} finished)
            </div>
            @for (job of activeJobs(); track job.jobId) {
              <div class="flex items-center gap-2 rounded border border-gray-700 bg-gray-800/50 px-3 py-2 text-xs">
                <span [class]="jobStatusColor(job.status)">{{ job.status }}</span>
                <span class="text-gray-300 font-mono">{{ job.symbol }}</span>
                <span class="text-gray-500">({{ job.tfs.join(', ') }})</span>
                @if (job.barsRecorded) { <span class="text-gray-500">{{ job.barsRecorded }} bars</span> }
                @if (job.error) { <span class="text-red-400 truncate max-w-48">{{ job.error }}</span> }
                @if (job.status === 'done' || job.status === 'failed') {
                  <button (click)="dismissJob(job.jobId)" class="ml-auto text-gray-500 hover:text-gray-300">x</button>
                }
              </div>
            }
          </div>
        }
      </div>

      @if (pendingFiles().length > 0) {
        <div class="rounded-lg border border-amber-800 bg-amber-900/10 p-4">
          <div class="flex items-center justify-between mb-2">
            <h2 class="text-sm font-medium text-amber-400">
              Pending Shards ({{ pendingFiles().length }} file{{ pendingFiles().length !== 1 ? 's' : '' }},
              {{ fmtPendingSize() }})
            </h2>
            <button (click)="ingestShards()" [disabled]="ingesting()"
              class="rounded-md bg-amber-600 px-3 py-1 text-xs font-medium text-white hover:bg-amber-500 disabled:opacity-50">
              {{ ingesting() ? 'Ingesting...' : 'Ingest All' }}
            </button>
          </div>
          @if (activeIngestJob(); as job) {
            <div class="mb-2 rounded border border-amber-700 bg-gray-800/30 p-2">
              <div class="flex items-center justify-between text-xs mb-1">
                <span class="text-amber-400">Ingesting...</span>
                <span class="text-gray-500">{{ job.statusDetails || '' }}</span>
              </div>
              <div class="h-1.5 rounded-full bg-gray-700">
                <div class="h-full rounded-full bg-amber-500 transition-all duration-300"
                  [style.width]="((job.filesProcessed ?? 0) / (job.filesTotal || 1) * 100) + '%'"></div>
              </div>
              <div class="flex justify-between text-xs text-gray-500 mt-1">
                <span>{{ job.filesProcessed ?? 0 }}/{{ job.filesTotal || '?' }} files</span>
                <span>{{ (job.barsRecorded ?? 0).toLocaleString() }} bars</span>
              </div>
            </div>
          }
          <p class="text-xs text-amber-500/70 mb-2">
            NDJSON files in <code class="text-amber-400/80">{{ shardsRoot() || 'data/shards' }}</code>.
            Download data from cTrader then hit Ingest All to import into marketdata.db.
          </p>
          <div class="mt-2 max-h-48 overflow-y-auto space-y-0.5">
            @for (f of pendingFiles(); track f.relativePath) {
              <div class="flex items-center gap-2 rounded bg-gray-800/30 px-2 py-1 text-xs">
                <span class="font-mono text-gray-300">{{ f.fileName }}</span>
                <span class="text-gray-500">{{ f.symbol }} {{ f.timeframe }}</span>
                <span class="text-gray-600">{{ fmtSize(f.sizeBytes) }}</span>
                <span class="ml-auto text-gray-600">{{ f.lastModifiedUtc | date:'MM-dd HH:mm' }}</span>
              </div>
            }
          </div>
        </div>
      }

      @if (loading()) {
        <div class="py-12 text-center text-sm text-gray-500">Loading inventory...</div>
      } @else if (error()) {
        <div class="rounded-lg border border-red-800 bg-red-900/20 p-4 text-sm text-red-400">{{ error() }}</div>
      } @else if (inventory().length === 0) {
        <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-12 text-center">
          <p class="text-sm text-gray-400 mb-2">No market data available.</p>
          <p class="text-xs text-gray-500">Use the download form above or drop NDJSON shards in data/shards/.</p>
        </div>
      } @else {
        <div class="flex flex-wrap gap-2 mb-3">
          @for (s of perSymbol(); track s.symbol) {
            <div class="rounded-md border border-gray-800 bg-gray-900/40 px-3 py-1.5 text-xs">
              <span class="font-mono text-gray-300">{{ s.symbol }}</span>
              <span class="ml-2 text-gray-500">{{ s.bars.toLocaleString() }} bars &middot; {{ s.tfs }} TF</span>
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
                <th class="px-4 py-2 text-center text-xs font-medium uppercase tracking-wide text-gray-500">M1 Overlap</th>
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
                  <td class="whitespace-nowrap px-4 py-2 text-right">
                    <button (click)="deleteRow(item)" [disabled]="deletingKey() === rowKey(item)"
                      class="rounded border border-red-900 px-2 py-0.5 text-xs text-red-400 hover:bg-red-900/20 disabled:opacity-50">
                      {{ deletingKey() === rowKey(item) ? '...' : 'Delete' }}
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
export class DataManagerComponent implements OnInit {
  private http = inject(HttpClient);
  private destroyRef = inject(DestroyRef);
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

  dlSymbols = ['EURUSD', 'GBPUSD', 'USDJPY', 'GBPJPY', 'XAUUSD', 'AUDUSD', 'USDCHF', 'USDCAD', 'NZDUSD', 'EURGBP', 'EURJPY', 'XAGUSD', 'BTCUSD', 'ETHUSD', 'US30', 'NAS100'];
  dlSelectedSymbols = signal<string[]>(['EURUSD']);
  allTfs = [
    { value: 'm1', label: 'M1' },
    { value: 'm5', label: 'M5' },
    { value: 'm15', label: 'M15' },
    { value: 'h1', label: 'H1' },
    { value: 'h4', label: 'H4' },
    { value: 'd1', label: 'D1' },
  ];
  dlTfs = signal<string[]>(['h1', 'm1']);
  dlPreset: DatePreset = '1y';
  dlLoading = signal(false);
  dlError = signal<string | null>(null);
  keepShards = false;
  activeJobs = signal<DownloadJobResponse[]>([]);
  runningJobCount = computed(() => this.activeJobs().filter(j => j.status !== 'done' && j.status !== 'failed').length);
  dlCountLabel = computed(() => {
    const n = this.dlSelectedSymbols().length;
    return n === 0 ? '' : n === 1 ? this.dlSelectedSymbols()[0] : `${n} symbols`;
  });

  pendingFiles = signal<PendingShard[]>([]);
  shardsRoot = signal<string>('');
  ingesting = computed(() => this.activeIngestJob() !== null);
  ingestResult = signal<{ filesProcessed: number; barsIngested: number; errors: string[] } | null>(null);

  activeIngestJob = computed(() => {
    return this.activeJobs().find(j => j.status === 'ingesting' && (!j.tfs || j.tfs.length === 0)) ?? null;
  });

  ngOnInit(): void {
    this.loadInventory();
    this.loadPendingShards();
    interval(2000)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.pollActiveJobs());
  }

  toggleSymbol(s: string): void {
    this.dlSelectedSymbols.update(
      cur => cur.includes(s) ? cur.filter(x => x !== s) : [...cur, s]
    );
  }

  toggleTf(tf: string): void {
    this.dlTfs.update(tfs => tfs.includes(tf) ? tfs.filter(t => t !== tf) : [...tfs, tf]);
  }

  onPresetChange(p: DatePreset): void {
    // all date math done server-side based on the preset string
  }

  startDownload(): void {
    const syms = this.dlSelectedSymbols();
    if (syms.length === 0) return;
    this.dlLoading.set(true);
    this.dlError.set(null);

    const body: any = {
      symbols: syms,
      tfs: this.dlTfs(),
      keepShards: this.keepShards,
    };

    const days = this.presetToDays(this.dlPreset);
    if (days !== null) {
      body.days = days;
    }

    this.http.post<DownloadMultiResponse>('/api/data-manager/download', body).subscribe({
      next: (r) => {
        this.dlLoading.set(false);
        if (r.jobs) {
          this.activeJobs.update(jobs => [...jobs, ...r.jobs]);
          for (const j of r.jobs) this.pollJob(j.jobId);
        }
      },
      error: (err) => {
        this.dlError.set(err?.error?.error ?? err?.message ?? 'Download failed');
        this.dlLoading.set(false);
      },
    });
  }

  private presetToDays(p: DatePreset): number | null {
    switch (p) {
      case '30d': return 30;
      case '90d': return 90;
      case '180d': return 180;
      case '1y': return 365;
      case '2y': return 730;
      case '5y': return 1825;
      case '2020': return -1;
      default: return 365;
    }
  }

  dismissJob(jobId: string): void {
    this.activeJobs.update(jobs => jobs.filter(j => j.jobId !== jobId));
  }

  jobStatusColor(status: string): string {
    switch (status) {
      case 'done': return 'rounded bg-emerald-900/60 px-1.5 py-0.5 text-emerald-400';
      case 'failed': return 'rounded bg-red-900/60 px-1.5 py-0.5 text-red-400';
      case 'running': case 'recording': case 'ingesting': return 'rounded bg-blue-900/60 px-1.5 py-0.5 text-blue-400';
      default: return 'rounded bg-gray-700 px-1.5 py-0.5 text-gray-400';
    }
  }

  ingestShards(): void {
    this.ingestResult.set(null);
    this.http.post<DownloadJobResponse>('/api/data-manager/ingest-shards', {}).subscribe({
      next: (r) => {
        this.activeJobs.update(jobs => [...jobs, r]);
        this.pollJob(r.jobId);
      },
      error: (err) => {
        this.ingestResult.set({ filesProcessed: 0, barsIngested: 0, errors: [err?.error?.error ?? err?.message ?? 'Ingest failed'] });
      },
    });
  }

  fmtSize(bytes: number): string {
    if (bytes < 1024) return bytes + ' B';
    if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
    return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
  }

  fmtPendingSize(): string {
    const total = this.pendingFiles().reduce((s, f) => s + f.sizeBytes, 0);
    return this.fmtSize(total);
  }

  private pollActiveJobs(): void {
    for (const job of this.activeJobs()) {
      if (job.status === 'done' || job.status === 'failed') continue;
      this.pollJob(job.jobId);
    }
  }

  private pollJob(jobId: string): void {
    this.http.get<DownloadJobResponse>(`/api/data-manager/jobs/${jobId}`).subscribe({
      next: (updated) => {
        this.activeJobs.update(jobs => jobs.map(j => j.jobId === jobId ? updated : j));
        if (updated.status === 'done' || updated.status === 'failed') {
          this.loadInventory();
          this.loadPendingShards();
        }
      },
      error: () => { /* job may not exist yet */ },
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

  private loadPendingShards(): void {
    this.http.get<PendingShardsResponse>('/api/data-manager/pending-shards').subscribe({
      next: (r) => {
        this.pendingFiles.set(r.files ?? []);
        this.shardsRoot.set(r.shardsRoot ?? '');
      },
      error: () => { /* endpoint may not exist yet */ },
    });
  }
}
