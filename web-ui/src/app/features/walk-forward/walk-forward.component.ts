import { Component, inject, signal, ChangeDetectionStrategy, OnInit, OnDestroy } from '@angular/core';
import { FormsModule } from '@angular/forms';
import * as signalR from '@microsoft/signalr';
import { WalkForwardApiService, type WalkForwardJob, type WalkForwardRequest } from './walk-forward.service';
import { EquityChartComponent, type ChartPoint } from '../../shared/equity-chart.component';

@Component({
  selector: 'app-walk-forward',
  standalone: true,
  imports: [FormsModule, EquityChartComponent],
  template: `
    <div class="space-y-6">
      <h1 class="text-xl font-semibold">Walk-Forward</h1>
      <p class="text-xs text-gray-500">Rolling train/test windows — sweep on train, freeze best cell, run on test.</p>

      <!-- Start form -->
      <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
        <div class="grid grid-cols-2 gap-3 md:grid-cols-4 lg:grid-cols-6">
          <label class="text-xs text-gray-400">
            From
            <input [(ngModel)]="from" class="mt-1 w-full rounded border border-gray-700 bg-gray-800 px-2 py-1 text-xs text-white" placeholder="2025-01-01" />
          </label>
          <label class="text-xs text-gray-400">
            To
            <input [(ngModel)]="to" class="mt-1 w-full rounded border border-gray-700 bg-gray-800 px-2 py-1 text-xs text-white" placeholder="2025-06-01" />
          </label>
          <label class="text-xs text-gray-400">
            Folds
            <input [(ngModel)]="folds" type="number" min="2" max="8" class="mt-1 w-full rounded border border-gray-700 bg-gray-800 px-2 py-1 text-xs text-white" />
          </label>
          <label class="text-xs text-gray-400">
            Train %
            <input [(ngModel)]="trainFraction" type="number" step="0.05" min="0.5" max="0.9" class="mt-1 w-full rounded border border-gray-700 bg-gray-800 px-2 py-1 text-xs text-white" />
          </label>
          <label class="text-xs text-gray-400">
            Balance
            <input [(ngModel)]="balance" type="number" step="10000" class="mt-1 w-full rounded border border-gray-700 bg-gray-800 px-2 py-1 text-xs text-white" />
          </label>
          <label class="text-xs text-gray-400">
            Param (key=val,val)
            <input [(ngModel)]="paramInput" class="mt-1 w-full rounded border border-gray-700 bg-gray-800 px-2 py-1 text-xs text-white" placeholder="slMultiple=1,2,3,4" />
          </label>
        </div>
        <div class="mt-3 flex gap-3">
          <label class="text-xs text-gray-400 flex-1">
            Strategies (comma-separated)
            <input [(ngModel)]="strategies" class="mt-1 w-full rounded border border-gray-700 bg-gray-800 px-2 py-1 text-xs text-white" placeholder="trend-breakout,macd-momentum" />
          </label>
          <label class="text-xs text-gray-400">
            Symbols
            <input [(ngModel)]="symbols" class="mt-1 w-full rounded border border-gray-700 bg-gray-800 px-2 py-1 text-xs text-white" placeholder="EURUSD" />
          </label>
          <label class="text-xs text-gray-400">
            Timeframes
            <input [(ngModel)]="timeframes" class="mt-1 w-full rounded border border-gray-700 bg-gray-800 px-2 py-1 text-xs text-white" placeholder="H1" />
          </label>
        </div>
        <button (click)="start()" [disabled]="running()"
          class="mt-3 rounded bg-emerald-600 px-4 py-2 text-xs font-medium text-white hover:bg-emerald-500 disabled:opacity-40">
          {{ running() ? 'Running...' : 'Start Walk-Forward' }}
        </button>
      </div>

      @if (error()) {
        <div class="rounded bg-red-900/20 px-3 py-2 text-xs text-red-400">{{ error() }}</div>
      }

      <!-- Progress -->
      @if (job(); as j) {
        <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
          <div class="mb-2 text-sm text-gray-300">
            Status: <span [class.text-emerald-400]="j.status === 'completed'" [class.text-yellow-400]="j.status === 'running'" [class.text-red-400]="j.status === 'failed'">{{ j.status }}</span>
            @if (j.totalWindows > 0) {
              · Windows: {{ j.completedWindows }}/{{ j.totalWindows }}
              · Trials: {{ trialCount() }}
            }
          </div>
          @if (j.status === 'running') {
            <div class="h-1.5 overflow-hidden rounded-full bg-gray-700">
              <div class="h-full rounded-full bg-emerald-500 transition-all" [style.width]="((j.completedWindows / j.totalWindows) * 100).toFixed(0) + '%'"></div>
            </div>
          }
        </div>
      }

      <!-- Windows table -->
      @if (job()?.windows?.length) {
        <div class="overflow-x-auto rounded-lg border border-gray-800">
          <table class="w-full text-xs">
            <thead class="bg-gray-900 text-gray-400">
              <tr>
                <th class="px-2 py-1 text-left">#</th>
                <th class="px-2 py-1 text-left">Train</th>
                <th class="px-2 py-1 text-left">Test</th>
                <th class="px-2 py-1 text-left">Params</th>
                <th class="px-2 py-1 text-right">P/L</th>
                <th class="px-2 py-1 text-right">Trades</th>
                <th class="px-2 py-1 text-right">Win%</th>
                <th class="px-2 py-1 text-right">Trials</th>
              </tr>
            </thead>
            <tbody class="divide-y divide-gray-800">
              @for (w of job()!.windows!; track w.windowIndex) {
                <tr>
                  <td class="px-2 py-1 text-gray-300">{{ w.windowIndex }}</td>
                  <td class="px-2 py-1 text-gray-500">{{ trainShort(w) }}</td>
                  <td class="px-2 py-1 text-gray-500">{{ testShort(w) }}</td>
                  <td class="px-2 py-1 text-gray-300 text-xs font-mono">{{ paramsShort(w.chosenParamsJson) }}</td>
                  <td class="px-2 py-1 text-right" [style.color]="w.testNetProfit > 0 ? '#4ade80' : '#f87171'">{{ w.testNetProfit.toFixed(2) }}</td>
                  <td class="px-2 py-1 text-right text-gray-400">{{ w.testTotalTrades }}</td>
                  <td class="px-2 py-1 text-right text-gray-400">{{ (w.testWinRatePct * 100).toFixed(1) }}%</td>
                  <td class="px-2 py-1 text-right text-gray-500">{{ w.trialsCount }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>

        <!-- Stitched equity -->
        @if (stitchedEquity().length) {
          <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
            <h2 class="mb-3 text-sm font-medium text-gray-400">OOS Stitched Equity</h2>
            <app-equity-chart [data]="stitchedEquity()" />
          </div>
        }
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WalkForwardComponent implements OnInit, OnDestroy {
  private api = inject(WalkForwardApiService);

  from = '2025-01-01';
  to = '2025-06-01';
  folds = 4;
  trainFraction = 0.7;
  balance = 100000;
  strategies = 'trend-breakout';
  symbols = 'EURUSD';
  timeframes = 'H1';
  paramInput = 'slMultiple=1,2,3,4';

  running = signal(false);
  job = signal<WalkForwardJob | null>(null);
  error = signal<string | null>(null);
  stitchedEquity = signal<ChartPoint[]>([]);

  private connection: signalR.HubConnection | null = null;

  trialCount(): number {
    const ws = this.job()?.windows;
    if (!ws?.length) return 0;
    return ws.reduce((s, w) => s + w.trialsCount, 0);
  }

  trainShort(w: any): string { return (w.trainFrom || '').slice(0, 10); }
  testShort(w: any): string { return (w.testFrom || '').slice(0, 10); }
  paramsShort(json: string): string { try { const o = JSON.parse(json); return o.paramKey + '=' + o.paramValue; } catch { return json?.slice(0, 20) ?? ''; } }

  ngOnInit(): void { }

  ngOnDestroy(): void {
    this.connection?.stop();
  }

  async start(): Promise<void> {
    this.error.set(null);
    this.running.set(true);
    this.job.set(null);
    this.stitchedEquity.set([]);

    const paramKey = this.paramInput.split('=')[0].trim();
    const paramVals = (this.paramInput.split('=')[1] || '').split(',').map(v => parseFloat(v.trim())).filter(v => !isNaN(v));
    if (!paramKey || !paramVals.length) { this.error.set('Invalid param format: key=val,val,...'); this.running.set(false); return; }

    const req: WalkForwardRequest = {
      from: this.from,
      to: this.to,
      folds: this.folds,
      trainFraction: this.trainFraction,
      strategies: this.strategies.split(',').map(s => s.trim()).filter(Boolean),
      symbols: this.symbols.split(',').map(s => s.trim()).filter(Boolean),
      timeframes: this.timeframes.split(',').map(s => s.trim()).filter(Boolean),
      paramGrid: { [paramKey]: paramVals },
      balance: this.balance,
    };

    try {
      const resp = await this.api.start(req);
      await this.connectSignalR(resp.jobId);
    } catch (e: any) {
      this.error.set(e?.error?.error ?? e?.message ?? 'Failed');
      this.running.set(false);
    }
  }

  private async connectSignalR(jobId: string): Promise<void> {
    this.connection?.stop();
    this.connection = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/walk-forward')
      .withAutomaticReconnect()
      .build();

    this.connection.on('WindowCompleted', (data) => { this.refresh(); this.buildStitchedEquity(); });
    this.connection.on('JobCompleted', () => { this.refresh(); this.buildStitchedEquity(); this.running.set(false); });
    this.connection.on('JobFailed', (id, msg) => { this.error.set(msg); this.refresh(); this.running.set(false); });

    await this.connection.start();
    await this.connection.invoke('JoinJob', jobId);
    this.refresh();
  }

  private async refresh(): Promise<void> {
    if (!this.job()) return;
    try { this.job.set(await this.api.getJob(this.job()!.id)); } catch { /* */ }
  }

  private buildStitchedEquity(): void {
    const ws = this.job()?.windows;
    if (!ws?.length) return;
    let e = 100000;
    const pts: ChartPoint[] = [];
    for (const w of ws) {
      e += w.testNetProfit;
      pts.push({ time: new Date(w.testTo).getTime(), value: e, balance: e });
    }
    this.stitchedEquity.set(pts);
  }
}
