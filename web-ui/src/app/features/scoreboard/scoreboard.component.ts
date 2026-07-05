import { Component, inject, signal, ChangeDetectionStrategy, OnInit } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';

interface ScoreboardCell {
  strategyId: string;
  strategyName: string;
  symbol: string;
  timeframe: string;
  enabled: boolean;
  parked: boolean;
  parkReason: string | null;
  latestAvgR: number;
  totalTrades: number;
  tradesPerWeek: number;
  lastRunId: string;
  lastRunAt: string;
  thesis: string;
}

@Component({
  selector: 'app-scoreboard',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="space-y-6">
      <div class="flex items-center justify-between">
        <div>
          <h1 class="text-xl font-semibold">Scoreboard</h1>
          <p class="mt-1 text-xs text-gray-500">Strategy × Symbol × TF — P(pass), expectancy, frequency. The owner's picker.</p>
        </div>
        <div class="flex gap-2">
          <button (click)="filter.set('all')" [class.bg-gray-800]="filter() === 'all'" class="rounded border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800">All</button>
          <button (click)="filter.set('active')" [class.bg-gray-800]="filter() === 'active'" class="rounded border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800">Active</button>
          <button (click)="filter.set('parked')" [class.bg-gray-800]="filter() === 'parked'" class="rounded border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800">Parked</button>
        </div>
      </div>

      @if (loading()) {
        <div class="py-12 text-center text-sm text-gray-500">Loading...</div>
      } @else if (error()) {
        <div class="rounded bg-red-900/20 px-3 py-2 text-xs text-red-400">{{ error() }}</div>
      } @else if (filteredCells().length === 0) {
        <div class="py-12 text-center text-sm text-gray-500">No data — run some backtests first.</div>
      } @else {
        <div class="overflow-x-auto rounded-lg border border-gray-800">
          <table class="w-full text-xs">
            <thead class="bg-gray-900 text-gray-400">
              <tr>
                <th class="px-3 py-2 text-left">Strategy</th>
                <th class="px-3 py-2 text-left">Symbol</th>
                <th class="px-3 py-2 text-left">TF</th>
                <th class="px-3 py-2 text-right">Avg R</th>
                <th class="px-3 py-2 text-right">Trades</th>
                <th class="px-3 py-2 text-right">Trades/wk</th>
                <th class="px-3 py-2 text-right">Traffic</th>
                <th class="px-3 py-2 text-center">Status</th>
                <th class="px-3 py-2 text-center">Actions</th>
              </tr>
            </thead>
            <tbody class="divide-y divide-gray-800">
              @for (c of filteredCells(); track c.strategyId + c.symbol + c.timeframe) {
                <tr class="hover:bg-gray-800/30" [class.opacity-50]="c.parked">
                  <td class="px-3 py-2">
                    <div class="font-medium text-gray-300">{{ c.strategyName }}</div>
                    <div class="text-[10px] text-gray-600">{{ c.thesis.slice(0, 60) }}{{ c.thesis.length > 60 ? '...' : '' }}</div>
                  </td>
                  <td class="px-3 py-2 text-gray-300">{{ c.symbol }}</td>
                  <td class="px-3 py-2 text-gray-300">{{ c.timeframe }}</td>
                  <td class="px-3 py-2 text-right" [style.color]="c.latestAvgR > 0 ? '#4ade80' : '#f87171'">{{ c.latestAvgR > 0 ? '+' : '' }}{{ c.latestAvgR.toFixed(2) }}R</td>
                  <td class="px-3 py-2 text-right text-gray-400">{{ c.totalTrades }}</td>
                  <td class="px-3 py-2 text-right text-gray-400">{{ c.tradesPerWeek }}</td>
                  <td class="px-3 py-2 text-right">
                    @if (c.parked) {
                      <span class="rounded bg-gray-800 px-2 py-0.5 text-gray-500">--</span>
                    } @else {
                      @if (trafficLevel(c) === 'green') {
                        <span class="rounded bg-emerald-900/30 px-2 py-0.5 text-emerald-400">OK</span>
                      } @else if (trafficLevel(c) === 'yellow') {
                        <span class="rounded bg-yellow-900/30 px-2 py-0.5 text-yellow-400">LOW</span>
                      } @else {
                        <span class="rounded bg-red-900/30 px-2 py-0.5 text-red-400">NONE</span>
                      }
                    }
                  </td>
                  <td class="px-3 py-2 text-center">
                    @if (c.parked) {
                      <span class="rounded bg-red-900/20 px-2 py-0.5 text-xs text-red-400">Parked</span>
                    } @else if (!c.enabled) {
                      <span class="rounded bg-gray-800 px-2 py-0.5 text-xs text-gray-500">Disabled</span>
                    } @else {
                      <span class="rounded bg-emerald-900/20 px-2 py-0.5 text-xs text-emerald-400">Active</span>
                    }
                  </td>
                  <td class="px-3 py-2 text-center">
                    @if (c.parked) {
                      <button (click)="unpark(c)" class="rounded border border-emerald-700 px-2 py-0.5 text-[10px] text-emerald-400 hover:bg-emerald-900/20">Unpark</button>
                    } @else {
                      <button (click)="showParkReason.set(c.strategyId + c.symbol + c.timeframe)" class="rounded border border-red-700 px-2 py-0.5 text-[10px] text-red-400 hover:bg-red-900/20">Park</button>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }

      <!-- Park reason modal -->
      @if (showParkReason(); as key) {
        <div class="fixed inset-0 z-50 flex items-center justify-center bg-black/60" (click)="showParkReason.set(null)">
          <div class="w-96 rounded-lg border border-gray-700 bg-gray-900 p-4" (click)="$event.stopPropagation()">
            <h3 class="mb-2 text-sm font-medium text-gray-300">Park Strategy Cell</h3>
            <textarea [(ngModel)]="parkReasonText" class="mb-3 w-full rounded border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-white" rows="2" placeholder="Why are you parking this?"></textarea>
            <div class="flex gap-2">
              <button (click)="doPark(key); showParkReason.set(null)" class="rounded bg-red-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-red-500">Park</button>
              <button (click)="showParkReason.set(null)" class="rounded border border-gray-700 px-3 py-1.5 text-xs text-gray-400 hover:bg-gray-800">Cancel</button>
            </div>
          </div>
        </div>
      }

      @if (parkError()) {
        <div class="rounded bg-red-900/20 px-3 py-2 text-xs text-red-400">{{ parkError() }}</div>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ScoreboardComponent implements OnInit {
  private http = inject(HttpClient);

  cells = signal<ScoreboardCell[]>([]);
  loading = signal(false);
  error = signal<string | null>(null);
  filter = signal<'all' | 'active' | 'parked'>('all');
  showParkReason = signal<string | null>(null);
  parkReasonText = '';
  parkError = signal<string | null>(null);

  filteredCells(): ScoreboardCell[] {
    const f = this.filter();
    if (f === 'active') return this.cells().filter(c => !c.parked && c.enabled);
    if (f === 'parked') return this.cells().filter(c => c.parked || !c.enabled);
    return this.cells();
  }

  trafficLevel(c: ScoreboardCell): 'green' | 'yellow' | 'red' {
    const needed = c.totalTrades > 0 && c.latestAvgR !== 0
      ? Math.log(0.05) / Math.log(1 - (c.totalTrades > 0 ? 0.005 * (c.latestAvgR > 0 ? c.latestAvgR : 0.01) : 0.001))
      : 999;
    if (isNaN(needed) || !isFinite(needed)) return 'green';
    const perWeek = c.tradesPerWeek || 0;
    if (perWeek * 4 >= needed) return 'green';
    if (perWeek * 12 >= needed) return 'yellow';
    return 'red';
  }

  async ngOnInit(): Promise<void> {
    this.loading.set(true);
    try { this.cells.set(await firstValueFrom(this.http.get<ScoreboardCell[]>('/api/scoreboard'))); }
    catch (e: any) { this.error.set(e?.message ?? 'Failed'); }
    finally { this.loading.set(false); }
  }

  async doPark(key: string): Promise<void> {
    this.parkError.set(null);
    const parts = key.split(/(.+\.)(.+\.)(.+)/);
    // key format: strategyId + symbol + timeframe concatenated
    // Parse by looking up from cells
    const cell = this.cells().find(c => c.strategyId + c.symbol + c.timeframe === key);
    if (!cell) return;
    try {
      await firstValueFrom(this.http.post(`/api/scoreboard/${cell.strategyId}/park`, {
        symbol: cell.symbol,
        timeframe: cell.timeframe,
        reason: this.parkReasonText || 'No reason given',
      }));
      this.parkReasonText = '';
      this.ngOnInit();
    } catch (e: any) { this.parkError.set(e?.error?.error ?? e?.message ?? 'Failed'); }
  }

  async unpark(c: ScoreboardCell): Promise<void> {
    try {
      await firstValueFrom(this.http.post(`/api/scoreboard/${c.strategyId}/unpark`, {
        symbol: c.symbol,
        timeframe: c.timeframe,
      }));
      this.ngOnInit();
    } catch (e: any) { this.parkError.set(e?.error?.error ?? e?.message ?? 'Failed'); }
  }
}
