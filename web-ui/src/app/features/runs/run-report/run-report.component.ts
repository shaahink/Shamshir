import { Component, inject, OnInit, signal, computed } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { DatePipe, NgClass, DecimalPipe } from '@angular/common';
import { RunsStore } from '../runs.store';
import { RunsApiService } from '../runs.service';
import { StatTileComponent } from '../../../shared/stat-tile.component';
import { DataTableComponent, type ColumnDef } from '../../../shared/data-table.component';
import { EquityChartComponent, type ChartPoint } from '../../../shared/equity-chart.component';
import { BadgeComponent } from '../../../shared/badge.component';
import type { TradeSummary, JournalEntry, EquityPoint, DailyPnl } from '../../../models/api.types';

@Component({
  selector: 'app-run-report',
  standalone: true,
  imports: [RouterLink, DatePipe, NgClass, DecimalPipe, StatTileComponent, DataTableComponent, EquityChartComponent, BadgeComponent],
  template: `
    @if (store.isLoading()) { <div class="py-12 text-center text-sm text-gray-500">Loading...</div> }
    @else {
      @if (store.selectedRun(); as d) {
        <div class="space-y-6">
          <div class="flex items-center justify-between">
            <div>
              <h1 class="text-xl font-semibold">Run {{ d.runId.slice(0, 8) }}</h1>
              <p class="text-sm text-gray-500">{{ symbolsDisplay() }} {{ d.period }} {{ d.backtestFrom | date }} - {{ d.backtestTo | date }} · Balance {{ d.initialBalance | number }}</p>
            </div>
            <div class="flex gap-2">
              <a [routerLink]="['/runs', d.runId, 'monitor']" class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800">Monitor</a>
              <a [routerLink]="['/runs', d.runId, 'analyzer']" class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800">Analyzer</a>
              <a routerLink="/runs" class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800">All Runs</a>
            </div>
          </div>

          <div class="grid grid-cols-2 gap-3 md:grid-cols-4 lg:grid-cols-5">
            <app-stat-tile label="Net P/L" [value]="d.netProfit.toFixed(2)" [positive]="d.netProfit > 0" [negative]="d.netProfit < 0" />
            <app-stat-tile label="Return %" [value]="(d.initialBalance > 0 ? (d.netProfit / d.initialBalance * 100) : 0).toFixed(2) + '%'" [positive]="d.netProfit > 0" [negative]="d.netProfit < 0" />
            <app-stat-tile label="Max DD" [value]="(d.maxDrawdownPct * 100).toFixed(2) + '%'" [negative]="true" />
            <app-stat-tile label="Profit Factor" [value]="pfDisplay()" [positive]="profitFactor() > 1" />
            <app-stat-tile label="Win Rate" [value]="(d.winRatePct * 100).toFixed(1) + '%'" [positive]="d.winRatePct > 0.5" />
            <app-stat-tile label="Trades" [value]="d.totalTrades" />
            <app-stat-tile label="Gross P/L" [value]="grossDisplay()" [positive]="(d.grossPnL ?? 0) > 0" [negative]="(d.grossPnL ?? 0) < 0" />
            <app-stat-tile label="Commission" [value]="commDisplay()" [negative]="(d.commissionTotal ?? 0) !== 0" />
            <app-stat-tile label="Swap" [value]="swapDisplay()" [negative]="(d.swapTotal ?? 0) !== 0" />
            <app-stat-tile label="Avg R" [value]="avgR().toFixed(2)" [positive]="avgR() > 0" />
          </div>

          <div class="flex gap-3">
            <span class="text-xs text-gray-500">
              Net = Σ trades: <app-badge [label]="recNetOk() ? 'OK' : 'MISMATCH'" [variant]="recNetOk() ? 'success' : 'error'" />
            </span>
            <span class="text-xs text-gray-500">
              Closes = trade count: <app-badge [label]="recClosesOk() ? 'OK' : 'MISMATCH'" [variant]="recClosesOk() ? 'success' : 'error'" />
            </span>
            <span class="text-xs text-gray-500">
              ΣGross - ΣComm - ΣSwap = ΣNet: <app-badge [label]="recCostOk() ? 'OK' : 'MISMATCH'" [variant]="recCostOk() ? 'success' : 'error'" />
            </span>
          </div>

          @if (equityPoints().length > 1) {
            <app-equity-chart title="Equity & Drawdown" [data]="equityPoints()" [showDrawdown]="true" [showBalance]="true" />
          }

          @if (dailyPnl().length > 0) {
            <div>
              <h2 class="mb-3 text-sm font-medium text-gray-400">DD Timeline</h2>
              <div class="flex h-16 items-end gap-0.5">
                @for (dp of dailyPnl(); track dp.date) {
                  <div class="flex-1 rounded-t" [class.bg-emerald-600]="dp.pnl >= 0" [class.bg-red-600]="dp.pnl < 0"
                    [style.height]="barHeight(dp.pnl) + '%'" [title]="dp.date + ': ' + (dp.pnl ?? 0).toFixed(2)"></div>
                }
              </div>
            </div>
          }

          @if (trades().length > 0) {
            <div>
              <h2 class="mb-3 text-sm font-medium text-gray-400">Trades ({{ trades().length }})</h2>
              <app-data-table [columns]="tradeColumns" [data]="$any(trades())" (rowClick)="onTradeClick($event)" />
            </div>
          }

          @if (journal().length > 0) {
            <div>
              <h2 class="mb-3 text-sm font-medium text-gray-400">Journal</h2>
              <div class="flex gap-2 mb-2">
                @for (k of journalKinds; track k) {
                  @let active = (journalKind() || 'ALL') === k;
                  <button (click)="journalKind.set(k === 'ALL' ? null : k)" class="rounded px-2 py-0.5 text-xs"
                    [ngClass]="active ? 'bg-emerald-900/50 text-emerald-400' : 'text-gray-500'">{{ k }}</button>
                }
              </div>
              <div class="max-h-80 overflow-y-auto rounded-lg border border-gray-800">
                @for (entry of filteredJournal(); track entry.seq) {
                  <div class="border-b border-gray-800 px-4 py-1.5 text-xs last:border-0 hover:bg-gray-800/20">
                    <span class="text-gray-500">{{ entry.simTimeUtc | date:'MM-dd HH:mm' }}</span>
                    <app-badge [label]="(entry.eventKind ?? entry.kind) ?? '?'" [variant]="(entry.eventKind ?? entry.kind) === 'SIGNAL' ? 'warning' : (entry.eventKind ?? entry.kind) === 'CLOSE' ? 'success' : (entry.eventKind ?? entry.kind) === 'REJECTED' ? 'error' : 'neutral'" />
                    @if (entry.symbol) { <span class="ml-1 text-gray-500">{{ entry.symbol }}</span> }
                    @if (entry.decisionReason ?? entry.reason) { <span class="ml-2 text-gray-600">- {{ fmtReason((entry.decisionReason ?? entry.reason)!) }}</span> }
                  </div>
                }
              </div>
            </div>
          }
        </div>
      } @else { <div class="py-12 text-center text-sm text-gray-500">Run not found.</div> }
    }
  `,
})
export class RunReportComponent implements OnInit {
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private api = inject(RunsApiService);
  readonly store = inject(RunsStore);

  onTradeClick(row: any): void {
    if (row?.id) this.router.navigate(['/trades', row.id]);
  }

  trades = signal<TradeSummary[]>([]);
  journal = signal<JournalEntry[]>([]);
  equityPoints = signal<ChartPoint[]>([]);
  dailyPnl = signal<DailyPnl[]>([]);
  journalKind = signal<string | null>(null);
  journalKinds = ['ALL', 'SIGNAL', 'ORDER', 'FILL', 'CLOSE', 'REJECTED', 'BREACH', 'GOVERNOR', 'ENTRY_EXPIRED', 'CANCELLED'];

  filteredJournal = computed(() => { const k = this.journalKind(); return k && k !== 'ALL' ? this.journal().filter(e => (e.eventKind ?? e.kind) === k) : this.journal(); });
  grossTotal = computed(() => this.trades().reduce((s, t) => s + t.grossPnLAmount, 0));
  commTotal = computed(() => this.trades().reduce((s, t) => s + t.commissionAmount, 0));
  swapTotal = computed(() => this.trades().reduce((s, t) => s + t.swapAmount, 0));
  grossDisplay = computed(() => { const d = this.store.selectedRun(); return d ? ((d.grossPnL ?? this.trades().reduce((s, t) => s + (t.grossPnLAmount ?? 0), 0))).toFixed(2) : '0.00'; });
  commDisplay = computed(() => { const d = this.store.selectedRun(); return d ? ((d.commissionTotal ?? this.trades().reduce((s, t) => s + (t.commissionAmount ?? 0), 0))).toFixed(2) : '0.00'; });
  swapDisplay = computed(() => { const d = this.store.selectedRun(); return d ? ((d.swapTotal ?? this.trades().reduce((s, t) => s + (t.swapAmount ?? 0), 0))).toFixed(2) : '0.00'; });
  avgR = computed(() => { const t = this.trades(); return t.length ? t.reduce((s, x) => s + (x.rMultiple ?? 0), 0) / t.length : 0; });
  profitFactor = computed(() => {
    const t = this.trades(); const g = t.filter(x => (x.grossPnLAmount ?? 0) > 0).reduce((s, x) => s + (x.grossPnLAmount ?? 0), 0);
    const l = Math.abs(t.filter(x => (x.grossPnLAmount ?? 0) < 0).reduce((s, x) => s + (x.grossPnLAmount ?? 0), 0));
    return l === 0 ? (g > 0 ? Number.MAX_VALUE : 0) : g / l;
  });
  pfDisplay = computed(() => this.profitFactor() >= Number.MAX_VALUE - 1 ? '∞' : this.profitFactor().toFixed(2));
  recNetOk = computed(() => { const d = this.store.selectedRun(); return d ? Math.abs(d.netProfit - this.trades().reduce((s, t) => s + t.netPnLAmount, 0)) < 0.01 : false; });
  recClosesOk = computed(() => { const d = this.store.selectedRun(); return d ? d.totalTrades === this.trades().length : false; });
  recCostOk = computed(() => Math.abs(this.grossTotal() - this.commTotal() - this.swapTotal() - this.trades().reduce((s, t) => s + t.netPnLAmount, 0)) < 0.01);

  fmtReason(reason: string | null): string {
    if (!reason) return '';
    if (reason.startsWith('[') || reason.startsWith('{')) {
      try {
        const parsed = JSON.parse(reason);
        if (Array.isArray(parsed)) return parsed.map((v: any) => v.name || v.code || v.reason || JSON.stringify(v)).join(', ');
        if (parsed.reason) return parsed.reason;
      } catch { return reason; }
    }
    return reason;
  }

  barHeight(pnl: number): number { const arr = this.dailyPnl(); const m = arr.length > 0 ? Math.max(...arr.map(d => Math.abs(d.pnl ?? 0)), 1) : 1; return Math.min(100, (Math.abs(pnl ?? 0) / m) * 100); }

  symbolsDisplay(): string { const d = this.store.selectedRun(); if (!d) return ''; try { const s = typeof d.symbols === 'string' ? JSON.parse(d.symbols) : d.symbols; if (Array.isArray(s) && s.length > 1) return s.join(', '); } catch {} return d.symbol; }

  tradeColumns: ColumnDef[] = [
    { key: 'symbol', label: 'Sym' }, { key: 'direction', label: 'Dir' }, { key: 'lots', label: 'Lots', format: 'number' },
    { key: 'entryPrice', label: 'Entry', format: 'number' }, { key: 'exitPrice', label: 'Exit', format: 'number' },
    { key: 'grossPnLAmount', label: 'Gross', format: 'currency', colorFn: (v: number) => v >= 0 ? '#34d399' : '#f87171' },
    { key: 'commissionAmount', label: 'Comm', format: 'currency' }, { key: 'swapAmount', label: 'Swap', format: 'currency' },
    { key: 'netPnLAmount', label: 'Net', format: 'currency', colorFn: (v: number) => v >= 0 ? '#34d399' : '#f87171' },
    { key: 'pnLPips', label: 'Pips', format: 'pips' }, { key: 'rMultiple', label: 'R', format: 'number' },
    { key: 'maxAdverseExcursion', label: 'MAE', format: 'pips' }, { key: 'maxFavorableExcursion', label: 'MFE', format: 'pips' },
    { key: 'exitReason', label: 'Exit' }, { key: 'strategyId', label: 'Strategy' }, { key: 'durationSeconds', label: 'Hold', format: 'duration' },
  ];

  async ngOnInit(): Promise<void> {
    const runId = this.route.snapshot.paramMap.get('runId');
    if (!runId) return;
    await this.store.loadRun(runId);
    try { this.trades.set(await this.api.getRunTrades(runId)); } catch { /* no trades */ }
    try { this.journal.set(await this.api.getRunJournal(runId, undefined, undefined, 200)); } catch { /* no journal */ }
    try { this.equityPoints.set((await this.api.getRunEquity(runId)).map((p: EquityPoint) => ({ time: new Date(p.timestampUtc).getTime(), value: p.equity, balance: p.balance }))); } catch { /* no equity */ }
    try { this.dailyPnl.set(await this.api.getRunDailyPnl(runId)); } catch { /* no daily PnL */ }
  }
}
