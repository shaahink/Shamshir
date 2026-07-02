import { Component, inject, OnInit, signal, computed, ChangeDetectionStrategy } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { DatePipe, NgClass, DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RunsStore } from '../runs.store';
import { RunsApiService } from '../runs.service';
import { AddOnPacksApiService } from '../../addon-packs/addon-packs.service';
import { StatTileComponent } from '../../../shared/stat-tile.component';
import { DataTableComponent, type ColumnDef } from '../../../shared/data-table.component';
import { EquityChartComponent, type ChartPoint } from '../../../shared/equity-chart.component';
import { ScatterChartComponent } from '../../../shared/scatter-chart.component';
import { HistogramChartComponent } from '../../../shared/histogram-chart.component';
import { BadgeComponent } from '../../../shared/badge.component';
import { TradeChartCardComponent } from '../../../shared/trade-chart-card.component';
import { downloadBlob } from '../../../shared/download.helper';
import { formatSymbols } from '../../../shared/symbols.helper';
import { exportReport as doExportReport, type ExportStats } from '../report-export.helper';
import type { TradeSummary, JournalEntry, EquityPoint, DailyPnl, StrategyPerformance, AddOnPack, BarNarrative } from '../../../models/api.types';

type JournalRow = JournalEntry & { outcome?: string | null };

type Tab = 'overview' | 'trades' | 'journal' | 'risk';

function safeJson(raw: string): Record<string, unknown> | null {
  try { return JSON.parse(raw) as Record<string, unknown>; } catch { return null; }
}

@Component({
  selector: 'app-run-report',
  standalone: true,
  imports: [RouterLink, DatePipe, NgClass, DecimalPipe, FormsModule, StatTileComponent, DataTableComponent, EquityChartComponent, ScatterChartComponent, HistogramChartComponent, BadgeComponent, TradeChartCardComponent],
  template: `
    @if (store.isLoading()) {
      <div class="py-12 text-center text-sm text-gray-500">Loading...</div>
    } @else {
      @if (store.selectedRun(); as d) {
        <div class="space-y-6">
          <div class="flex items-center justify-between">
            <div>
              <h1 class="text-xl font-semibold">Run {{ d.runId.slice(0, 8) }}</h1>
              <p class="text-sm text-gray-500">
                {{ symbolsDisplay() }} {{ d.period }} {{ d.backtestFrom | date }} - {{ d.backtestTo | date }} · Balance {{ d.initialBalance | number }}
                · <span class="uppercase">{{ d.venue || 'replay' }}</span>
              </p>
            </div>
            <div class="flex gap-2">
              <button (click)="openDuplicate(d.runId)" [disabled]="duplicating()" class="rounded-md border border-emerald-700 px-3 py-1.5 text-xs text-emerald-400 hover:bg-emerald-900/30 disabled:opacity-50">{{ duplicating() ? 'Duplicating…' : 'Duplicate' }}</button>
              <a [href]="journalExportUrl(d.runId)" download class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800">Journal NDJSON</a>
              <a [href]="tradesCsvUrl(d.runId)" download class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800">CSV</a>
              <button (click)="exportReport('json')" class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800">JSON</button>
              <button (click)="exportReport('md')" class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800">MD</button>
              <a [routerLink]="['/runs', d.runId, 'monitor']" class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800">Monitor</a>
              <a [routerLink]="['/runs', d.runId, 'analyzer']" class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800">Analyzer</a>
              <a routerLink="/runs/all" class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800">All Runs</a>
            </div>
          </div>

          <!-- Tab bar -->
          <div class="flex gap-1 border-b border-gray-800">
            @for (t of tabs; track t.id) {
              <button (click)="activeTab.set(t.id)" [class]="tabClass(t.id)">{{ t.label }}</button>
            }
          </div>

          @switch (activeTab()) {
            @case ('overview') {
              <div class="space-y-6">
                <!-- Reconcile badges -->
                <div class="flex gap-3">
                  <span class="text-xs text-gray-500">Net = Σ trades: <app-badge [label]="recNetOk() ? 'OK' : 'MISMATCH'" [variant]="recNetOk() ? 'success' : 'error'" /></span>
                  <span class="text-xs text-gray-500">Closes = trades: <app-badge [label]="recClosesOk() ? 'OK' : 'MISMATCH'" [variant]="recClosesOk() ? 'success' : 'error'" /></span>
                  <span class="text-xs text-gray-500">ΣGross-Comm-Swap = ΣNet: <app-badge [label]="recCostOk() ? 'OK' : 'MISMATCH'" [variant]="recCostOk() ? 'success' : 'error'" /></span>
                </div>

                <!-- Key stats -->
                <div class="grid grid-cols-2 gap-3 md:grid-cols-5">
                  <app-stat-tile label="Net P/L" [value]="d.netProfit.toFixed(2)" [positive]="d.netProfit > 0" [negative]="d.netProfit < 0" />
                  <app-stat-tile label="Return %" [value]="returnPct(d)" [positive]="d.netProfit > 0" [negative]="d.netProfit < 0" />
                  <app-stat-tile label="Max DD" [value]="(d.maxDrawdownPct * 100).toFixed(2) + '%'" [negative]="true" />
                  <app-stat-tile label="Profit Factor" [value]="pfDisplay()" [positive]="profitFactor() > 1" />
                  <app-stat-tile label="Win Rate" [value]="(d.winRatePct * 100).toFixed(1) + '%'" [positive]="d.winRatePct > 0.5" />
                  <app-stat-tile label="Trades" [value]="d.totalTrades" />
                  <app-stat-tile label="Gross P/L" [value]="grossDisplay()" [positive]="d.grossPnL > 0" [negative]="d.grossPnL < 0" />
                  <app-stat-tile label="Commission" [value]="commDisplay()" [negative]="d.commissionTotal !== 0" />
                  <app-stat-tile label="Swap" [value]="swapDisplay()" [negative]="d.swapTotal !== 0" />
                  <app-stat-tile label="Avg R" [value]="avgR().toFixed(2)" [positive]="avgR() > 0" />
                </div>

                <!-- Equity chart -->
                @if (equityPoints().length > 1) {
                  <app-equity-chart title="Equity & Drawdown" [data]="equityPoints()" [showDrawdown]="true" [showBalance]="true" />
                }

                <!-- Daily PnL histogram -->
                @if (dailyHistogram().length > 0) {
                  <app-histogram-chart title="Daily PnL" [data]="dailyHistogram()" [color]="'#10b981'" />
                }

                <!-- Underwater equity (drawdown area) -->
                @if (underwaterPoints().length > 1) {
                  <app-equity-chart title="Underwater Drawdown" [data]="underwaterPoints()" [showDrawdown]="false" [showBalance]="false" />
                }

                <!-- Run plan -->
                @if (runPlan().length > 0) {
                  <div class="rounded-lg border border-gray-800 bg-gray-900/40 p-4">
                    <div class="mb-2 flex flex-wrap items-center gap-2">
                      <h2 class="text-sm font-medium text-gray-400">Run Plan ({{ runPlan().length }} rows)</h2>
                      <span [class]="chipClass(d.governorEnabled !== false)">Governor: {{ d.governorEnabled !== false ? 'On' : 'Off' }}</span>
                      <span [class]="chipClass(d.regimeEnabled !== false)">Regime: {{ d.regimeEnabled !== false ? 'On' : 'Off' }}</span>
                      <span class="rounded border border-gray-700 px-2 py-0.5 text-xs text-gray-300">Comm {{ d.commissionPerMillion ?? 0 }}/M · Spread {{ d.spreadPips ?? 0 }} pips</span>
                    </div>
                    <div class="overflow-hidden rounded border border-gray-800">
                      <table class="w-full text-xs">
                        <thead class="bg-gray-800/50 text-gray-500"><tr><th class="px-3 py-1.5 text-left">Strategy</th><th class="px-3 py-1.5 text-left">Symbol</th><th class="px-3 py-1.5 text-left">TF</th><th class="px-3 py-1.5 text-left">Pack</th></tr></thead>
                        <tbody>@for (r of runPlan(); track $index) { <tr class="border-t border-gray-800"><td class="px-3 py-1 text-gray-200">{{ r.strategyId }}</td><td class="px-3 py-1 text-gray-300">{{ r.symbol }}</td><td class="px-3 py-1 uppercase text-gray-300">{{ r.timeframe }}</td><td class="px-3 py-1 text-gray-400">{{ r.packId || '—' }}</td></tr> }</tbody>
                      </table>
                    </div>
                  </div>
                }

                <!-- Scatter MAE/MFE -->
                @if (scatterData().length > 0) {
                  <app-scatter-chart title="MAE (orange) vs MFE (green) — pips per trade" [data]="scatterData()" />
                }
              </div>
            }

            @case ('trades') {
              <div class="space-y-4">
                <!-- Trade detail expand -->
                @if (expandedTradeId()) {
                  <div class="mb-4 rounded-lg border border-gray-800 bg-gray-900/50 p-4 space-y-3">
                    <button (click)="expandedTradeId.set(null)" class="mb-1 text-xs text-gray-500 hover:text-gray-300">&larr; Close trade detail</button>
                    @if (expandedTradeNarrative(); as tn) {
                      <div class="flex gap-6">
                        <div class="flex-1">
                          <h3 class="mb-1 text-xs font-medium text-gray-500">Why entered</h3>
                          <p class="text-sm text-gray-300">{{ tn.entryReason || '—' }}</p>
                          @if (tn.entryRegime) { <p class="text-xs text-gray-500">Regime: {{ tn.entryRegime }}</p> }
                        </div>
                        <div class="flex-1">
                          <h3 class="mb-1 text-xs font-medium text-gray-500">Why exited</h3>
                          <p class="text-sm text-gray-300">{{ tn.exitReason }}</p>
                          @if (tn.exitDetail) { <p class="text-xs text-gray-500">Exit at {{ $any(tn.exitDetail).exitPrice }}, net {{ $any(tn.exitDetail).netAmt?.toFixed(2) }}, R {{ $any(tn.exitDetail).rMultiple?.toFixed(2) }}</p> }
                        </div>
                      </div>
                    }
                    <app-trade-chart-card [tradeId]="expandedTradeId()!" />
                  </div>
                }

                <!-- Column chooser -->
                <div class="flex flex-wrap gap-1.5">
                  @for (col of allTradeCols; track col.key) {
                    <button (click)="toggleTradeCol(col.key)" [class]="tradeColChipClass(col.key)">{{ col.label }}</button>
                  }
                </div>

                <!-- Trades table -->
                @if (trades().length > 0) {
                  <app-data-table [columns]="visibleTradeCols()" [data]="trades()" trackKey="id" (rowClick)="onTradeClick($event)" />
                } @else {
                  <div class="py-8 text-center text-sm text-gray-500">No trades</div>
                }
              </div>
            }

            @case ('journal') {
              <div class="space-y-4">
                <div class="flex flex-wrap gap-2">
                  @for (k of journalKinds; track k) {
                    @let active = (journalKind() || 'ALL') === k;
                    <button (click)="journalKind.set(k === 'ALL' ? null : k)" [ngClass]="active ? 'rounded bg-emerald-900/50 px-2 py-0.5 text-xs text-emerald-400' : 'rounded px-2 py-0.5 text-xs text-gray-500'">{{ kindLabel(k) }}</button>
                  }
                </div>
                @if (journalTableData().length > 0) {
                  <app-data-table [columns]="journalColumns" [data]="journalTableData()" trackKey="seq" [searchable]="false" />
                } @else {
                  <div class="py-8 text-center text-sm text-gray-500">No journal entries</div>
                }
              </div>
            }

            @case ('risk') {
              <div class="space-y-6">
                <!-- Per-strategy breakdown -->
                @if (breakdown().length > 0) {
                  <div>
                    <h2 class="mb-3 text-sm font-medium text-gray-400">Per-Strategy Funnel</h2>
                    <div class="overflow-x-auto rounded-lg border border-gray-800">
                      <table class="w-full text-xs">
                        <thead class="text-gray-500"><tr class="border-b border-gray-800"><th class="px-3 py-1.5 text-left">Strategy</th><th class="px-3 py-1.5 text-right">Bars</th><th class="px-3 py-1.5 text-right">Signals</th><th class="px-3 py-1.5 text-right">Trades</th><th class="px-3 py-1.5 text-right">Win%</th><th class="px-3 py-1.5 text-left">Top rejections</th></tr></thead>
                        <tbody>@for (s of breakdown(); track s.strategyId) { <tr class="border-b border-gray-800 last:border-0"><td class="px-3 py-1.5 text-gray-300">{{ s.strategyId }}</td><td class="px-3 py-1.5 text-right text-gray-400">{{ s.totalBarsEvaluated }}</td><td class="px-3 py-1.5 text-right" [class.text-emerald-400]="s.signalsFired > 0" [class.text-red-400]="s.signalsFired === 0">{{ s.signalsFired }}</td><td class="px-3 py-1.5 text-right text-gray-400">{{ s.tradesOpened }}</td><td class="px-3 py-1.5 text-right text-gray-400">{{ (s.winRatePct * 100).toFixed(0) }}%</td><td class="px-3 py-1.5 text-gray-500">{{ topReasons(s) }}</td></tr> }</tbody>
                      </table>
                    </div>
                    <div class="mt-3 space-y-2">
                      @for (s of breakdown(); track s.strategyId) {
                        @if (s.topRejections?.length) {
                          <div><div class="text-xs text-gray-500 mb-0.5">{{ s.strategyId }}</div>
                            <div class="flex gap-0.5 items-end">@for (r of s.topRejections; track r.reason) { <div class="text-center" [style.flex]="1"><div [class]="rejectionBarColor(r.reason) + ' rounded-t min-w-4'" [style.height.px]="rejectionBarHeight(r.count, s.topRejections)" [title]="r.reason + ': ' + r.count"></div><div class="text-xs text-gray-500 truncate" [title]="r.reason">{{ r.reason }}</div></div> }</div>
                          </div>
                        }
                      }
                    </div>
                  </div>
                }

                <!-- Profiling -->
                <div class="rounded-lg border border-gray-800 bg-gray-900/40 p-4">
                  <h2 class="mb-3 text-sm font-medium text-gray-400">Profiling</h2>
                  <div class="grid grid-cols-3 gap-3">
                    <app-stat-tile label="Wall elapsed" [value]="wallElapsedDisplay()" />
                    <app-stat-tile label="Bars/sec" [value]="barsPerSecDisplay()" />
                    <app-stat-tile label="Total bars" [value]="totalBarsDisplay()" />
                  </div>
                </div>

                <!-- Per-bar verdicts -->
                @if (perBarVerdicts().length > 0) {
                  <div>
                    <h2 class="mb-3 text-sm font-medium text-gray-400">Per-Bar Verdicts ({{ perBarVerdicts().length }})</h2>
                    <div class="max-h-72 overflow-y-auto rounded-lg border border-gray-800">
                      <table class="w-full text-xs">
                        <thead class="text-gray-500"><tr class="border-b border-gray-800"><th class="px-3 py-1.5 text-left">Time</th><th class="px-3 py-1.5 text-left">Strategy</th><th class="px-3 py-1.5 text-left">Signal</th><th class="px-3 py-1.5 text-left">Reason</th></tr></thead>
                        <tbody>@for (v of perBarVerdicts(); track $index) { <tr class="border-b border-gray-800 last:border-0"><td class="px-3 py-1 text-gray-500">{{ v.simTimeUtc | date: 'MM-dd HH:mm' }}</td><td class="px-3 py-1 text-gray-300">{{ v.strategyId }}</td><td class="px-3 py-1" [class.text-emerald-400]="v.signalFired" [class.text-gray-500]="!v.signalFired">{{ v.signalFired ? v.direction || 'FIRED' : '—' }}</td><td class="px-3 py-1 text-gray-500">{{ v.reason }}</td></tr> }</tbody>
                      </table>
                    </div>
                  </div>
                }

                <!-- Bar Inspector -->
                @if (activeBars().length > 0) {
                  <div>
                    <h2 class="mb-3 text-sm font-medium text-gray-400">Bar Inspector — active bars ({{ activeBars().length }} of {{ runBars().length }})</h2>
                    <div class="max-h-96 overflow-y-auto rounded-lg border border-gray-800">
                      <table class="w-full text-xs">
                        <thead class="sticky top-0 bg-gray-900 text-gray-500"><tr class="border-b border-gray-800"><th class="px-3 py-1.5 text-left">Time</th><th class="px-3 py-1.5 text-left">Regime</th><th class="px-3 py-1.5 text-left">Signals</th><th class="px-3 py-1.5 text-right">Prop</th><th class="px-3 py-1.5 text-right">Fill</th><th class="px-3 py-1.5 text-right">Close</th><th class="px-3 py-1.5 text-left">Rejections</th><th class="px-3 py-1.5 text-right">Equity</th><th class="px-3 py-1.5 text-right">Pos</th></tr></thead>
                        <tbody>@for (b of activeBars(); track b.firstSeq) { <tr class="border-b border-gray-800 last:border-0"><td class="px-3 py-1 text-gray-500">{{ b.simTimeUtc | date: 'MM-dd HH:mm' }}</td><td class="px-3 py-1 text-gray-400">{{ b.regime || '—' }}</td><td class="px-3 py-1 text-emerald-400">{{ firedSignals(b) }}</td><td class="px-3 py-1 text-right text-gray-300">{{ b.proposalCount || '' }}</td><td class="px-3 py-1 text-right text-gray-300">{{ b.fillCount || '' }}</td><td class="px-3 py-1 text-right text-gray-300">{{ b.closeCount || '' }}</td><td class="px-3 py-1 text-amber-400">{{ b.gateRejections.join('; ') }}</td><td class="px-3 py-1 text-right font-mono text-gray-300">{{ b.risk ? b.risk.equity.toFixed(0) : '—' }}</td><td class="px-3 py-1 text-right text-gray-400">{{ b.risk ? b.risk.openPositions : '—' }}</td></tr> }</tbody>
                      </table>
                    </div>
                  </div>
                }
              </div>
            }
          }
        </div>

        <!-- Duplicate modal -->
        @if (dupOpen() && dupRunId() === d.runId) {
          <div class="fixed inset-0 z-50 flex items-center justify-center bg-black/60" (click)="dupOpen.set(false)">
            <div class="w-full max-w-sm rounded-lg border border-gray-700 bg-gray-900 p-5 shadow-xl" (click)="$event.stopPropagation()">
              <h3 class="mb-3 text-sm font-medium text-gray-300">Duplicate Run</h3>
              <div class="space-y-3">
                <div><label class="block text-xs text-gray-500 mb-1">Add-on Pack</label><select [(ngModel)]="dupPackId" class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-1.5 text-sm text-gray-200"><option value="">None</option>@for (p of packs(); track p.id) { <option [value]="p.id">{{ p.name }}</option> }</select></div>
                <div><label class="flex items-center gap-2 text-sm text-gray-400 cursor-pointer"><input type="checkbox" [(ngModel)]="dupDisableRegime" class="rounded" /> Disable Regime</label></div>
              </div>
              <div class="mt-4 flex gap-2 justify-end">
                <button (click)="dupOpen.set(false)" class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-400 hover:bg-gray-800">Cancel</button>
                <button (click)="confirmDuplicate()" class="rounded-md bg-emerald-600 px-4 py-1.5 text-xs font-medium text-white hover:bg-emerald-500">Duplicate</button>
              </div>
            </div>
          </div>
        }
      } @else {
        <div class="py-12 text-center text-sm text-gray-500">Run not found.</div>
      }
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RunReportComponent implements OnInit {
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private api = inject(RunsApiService);
  readonly store = inject(RunsStore);

  activeTab = signal<Tab>('overview');

  tabs: { id: Tab; label: string }[] = [
    { id: 'overview', label: 'Overview' },
    { id: 'trades', label: 'Trades' },
    { id: 'journal', label: 'Journal' },
    { id: 'risk', label: 'Costs & Risk' },
  ];

  tabClass(id: Tab): string {
    const active = this.activeTab() === id;
    return 'rounded-t-md border-b-2 px-4 py-2 text-sm transition ' + (active ? 'border-emerald-500 bg-gray-800 text-white' : 'border-transparent text-gray-400 hover:text-white');
  }

  // Column chooser
  allTradeCols: { key: string; label: string; format?: string }[] = [
    { key: 'symbol', label: 'Sym' },
    { key: 'direction', label: 'Dir' },
    { key: 'entryPrice', label: 'Entry', format: 'number' },
    { key: 'exitPrice', label: 'Exit', format: 'number' },
    { key: 'netPnLAmount', label: 'Net', format: 'currency' },
    { key: 'rMultiple', label: 'R', format: 'number' },
    { key: 'pnLPips', label: 'Pips', format: 'pips' },
    { key: 'exitReason', label: 'Exit' },
    { key: 'strategyId', label: 'Strategy' },
    { key: 'durationSeconds', label: 'Hold', format: 'duration' },
    { key: 'lots', label: 'Lots', format: 'number' },
    { key: 'stopLoss', label: 'SL', format: 'number' },
    { key: 'takeProfit', label: 'TP', format: 'number' },
    { key: 'grossPnLAmount', label: 'Gross', format: 'currency' },
    { key: 'commissionAmount', label: 'Comm', format: 'currency' },
    { key: 'swapAmount', label: 'Swap', format: 'currency' },
    { key: 'maxAdverseExcursion', label: 'MAE', format: 'number' },
    { key: 'maxFavorableExcursion', label: 'MFE', format: 'number' },
    { key: 'entryType', label: 'Type' },
    { key: 'entryReason', label: 'Why entered' },
  ];

  defaultTradeCols = ['symbol', 'direction', 'entryPrice', 'exitPrice', 'netPnLAmount', 'rMultiple', 'pnLPips', 'exitReason', 'strategyId', 'durationSeconds'];
  shownTradeColKeys = signal<Set<string>>(new Set(this.defaultTradeCols));

  visibleTradeCols = computed(() => this.allTradeCols.filter(c => this.shownTradeColKeys().has(c.key)).map(c => ({
    key: c.key, label: c.label, format: c.format as ColumnDef['format'],
    ...(c.key === 'netPnLAmount' || c.key === 'grossPnLAmount' ? { colorFn: (v: number) => (v >= 0 ? '#34d399' : '#f87171') } : {}),
  } as ColumnDef)));

  toggleTradeCol(key: string): void {
    const set = new Set(this.shownTradeColKeys());
    if (set.has(key)) set.delete(key); else set.add(key);
    this.shownTradeColKeys.set(set);
  }

  tradeColChipClass(key: string): string {
    const on = this.shownTradeColKeys().has(key);
    return 'cursor-pointer rounded border px-2 py-0.5 text-xs transition ' + (on ? 'border-emerald-600 bg-emerald-900/20 text-emerald-400' : 'border-gray-700 text-gray-500 hover:text-gray-300');
  }

  onTradeClick(row: Record<string, unknown>): void {
    if (row && typeof row['id'] === 'string') {
      const id = row['id'];
      this.expandedTradeId.set(this.expandedTradeId() === id ? null : id);
    }
  }

  trades = signal<TradeSummary[]>([]);
  expandedTradeId = signal<string | null>(null);
  expandedTradeNarrative = computed(() => {
    const id = this.expandedTradeId();
    if (!id) return null;
    const t = this.trades().find(x => x.id === id);
    if (!t) return null;
    const exitDetail = t.exitDetailJson ? safeJson(t.exitDetailJson) : null;
    return { entryReason: t.entryReason || null, entryRegime: t.entryRegime || null, exitReason: t.exitReason, exitDetail };
  });
  journal = signal<JournalEntry[]>([]);
  barDecisions = signal<JournalEntry[]>([]);
  runBars = signal<BarNarrative[]>([]);
  breakdown = signal<StrategyPerformance[]>([]);
  equityPoints = signal<ChartPoint[]>([]);
  dailyPnl = signal<DailyPnl[]>([]);
  journalKind = signal<string | null>(null);
  duplicating = signal(false);

  activeBars(): BarNarrative[] { return this.runBars().filter(b => b.proposalCount > 0 || b.fillCount > 0 || b.closeCount > 0 || b.rejectionCount > 0 || b.verdicts.some(v => v.signalFired)); }
  firedSignals(b: BarNarrative): string { return b.verdicts.filter(v => v.signalFired).map(v => v.strategyId + (v.direction ? ' ' + v.direction : '')).join(', '); }

  runPlan = computed(() => {
    const raw = this.store.selectedRun()?.runPlanJson;
    if (!raw) return [];
    try { const arr = JSON.parse(raw); return Array.isArray(arr) ? arr.map((e: Record<string, unknown>) => ({ strategyId: (e['StrategyId'] ?? e['strategyId']) as string, symbol: (e['Symbol'] ?? e['symbol']) as string, timeframe: (e['Timeframe'] ?? e['timeframe']) as string, packId: (e['PackId'] ?? e['packId'] ?? null) as string | null })) : []; }
    catch { return []; }
  });

  chipClass(on: boolean): string { return 'rounded border px-2 py-0.5 text-xs ' + (on ? 'border-emerald-700 text-emerald-400' : 'border-gray-700 text-gray-500'); }
  private packsApi = inject(AddOnPacksApiService);
  dupRunId = signal<string | null>(null);
  dupPackId = signal('');
  dupDisableRegime = signal(false);
  packs = signal<{ id: string; name: string }[]>([]);
  dupOpen = signal(false);
  journalKinds = ['ALL', 'OrderProposed', 'OrderSubmitted', 'OrderFilled', 'OrderPartiallyFilled', 'OrderRejected', 'OrderCancelled', 'TRAIL', 'BREAKEVEN', 'PARTIAL', 'ADDON_RESOLVED', 'BarClosed', 'EquityObserved', 'DayRolled', 'CloseRequested'];
  kindLabel(k: string): string {
    const map: Record<string, string> = { ALL: 'All', OrderProposed: 'Signal', OrderSubmitted: 'Order', OrderFilled: 'Fill', OrderPartiallyFilled: 'Fill', OrderRejected: 'Rejected', OrderCancelled: 'Cancelled', TRAIL: 'Trail', BREAKEVEN: 'Breakeven', PARTIAL: 'Partial', ADDON_RESOLVED: 'AddOn', BarClosed: 'Bar', EquityObserved: 'Equity', DayRolled: 'Roll', CloseRequested: 'Close' };
    return map[k] ?? k;
  }

  journalExportUrl(runId: string): string { return this.api.journalExportUrl(runId); }
  tradesCsvUrl(runId: string): string { return this.api.tradesCsvUrl(runId); }

  displayJournal = computed<JournalRow[]>(() => {
    const byOrder = new Map<string, JournalRow>();
    const out: JournalRow[] = [];
    for (const e of this.journal()) {
      const kind = this.kindOf(e);
      const oid = this.orderIdOf(e);
      if (oid && (kind === 'OrderFilled' || kind === 'OrderCancelled' || kind === 'OrderRejected' || kind === 'OrderPartiallyFilled')) {
        const proposal = byOrder.get(oid);
        if (proposal && !this.isCloseFill(e)) { proposal.outcome = kind; continue; }
      }
      const row: JournalRow = { ...e };
      if (oid && kind === 'OrderProposed') byOrder.set(oid, row);
      out.push(row);
    }
    return out;
  });

  filteredJournal = computed(() => {
    const k = this.journalKind();
    return k && k !== 'ALL' ? this.displayJournal().filter(e => this.kindOf(e) === k) : this.displayJournal();
  });

  kindOf(e: JournalEntry): string { return e.eventKind ?? e.kind ?? '?'; }
  private orderIdOf(e: JournalEntry): string | null { if (!e.eventJson) return null; try { const o = JSON.parse(e.eventJson); return o.OrderId ?? o.orderId ?? null; } catch { return null; } }
  private isCloseFill(e: JournalEntry): boolean { if (!e.eventJson) return false; try { const o = JSON.parse(e.eventJson); return !!(o.closeReason ?? o.CloseReason); } catch { return false; } }

  grossTotal = computed(() => this.trades().reduce((s, t) => s + t.grossPnLAmount, 0));
  commTotal = computed(() => this.trades().reduce((s, t) => s + t.commissionAmount, 0));
  swapTotal = computed(() => this.trades().reduce((s, t) => s + t.swapAmount, 0));
  grossDisplay = computed(() => { const d = this.store.selectedRun(); return d ? (d.grossPnL ?? this.trades().reduce((s, t) => s + (t.grossPnLAmount ?? 0), 0)).toFixed(2) : '0.00'; });
  commDisplay = computed(() => { const d = this.store.selectedRun(); return d ? (d.commissionTotal ?? this.trades().reduce((s, t) => s + (t.commissionAmount ?? 0), 0)).toFixed(2) : '0.00'; });
  swapDisplay = computed(() => { const d = this.store.selectedRun(); return d ? (d.swapTotal ?? this.trades().reduce((s, t) => s + (t.swapAmount ?? 0), 0)).toFixed(2) : '0.00'; });
  avgR = computed(() => { const t = this.trades(); return t.length ? t.reduce((s, x) => s + (x.rMultiple ?? 0), 0) / t.length : 0; });
  profitFactor = computed(() => { const t = this.trades(); const g = t.filter(x => (x.grossPnLAmount ?? 0) > 0).reduce((s, x) => s + (x.grossPnLAmount ?? 0), 0); const l = Math.abs(t.filter(x => (x.grossPnLAmount ?? 0) < 0).reduce((s, x) => s + (x.grossPnLAmount ?? 0), 0)); return l === 0 ? (g > 0 ? Number.MAX_VALUE : 0) : g / l; });
  pfDisplay = computed(() => (this.profitFactor() >= Number.MAX_VALUE - 1 ? '∞' : this.profitFactor().toFixed(2)));

  wallElapsedDisplay = computed(() => this.formatDuration(this.store.selectedRun()?.wallElapsedMs ?? 0));
  barsPerSecDisplay = computed(() => { const v = this.store.selectedRun()?.barsPerSec ?? 0; return v > 0 ? v.toFixed(1) : '—'; });
  totalBarsDisplay = computed(() => { const v = this.store.selectedRun()?.totalBars; return v ? v.toLocaleString() : '—'; });
  private formatDuration(ms: number): string { if (ms <= 0) return '—'; const s = Math.floor(ms / 1000); if (s < 60) return s + 's'; const m = Math.floor(s / 60); if (m < 60) return m + 'm ' + (s % 60) + 's'; const h = Math.floor(m / 60); return h + 'h ' + (m % 60) + 'm'; }

  returnPct(d: { netProfit: number; initialBalance: number }): string { return d.initialBalance > 0 ? ((d.netProfit / d.initialBalance) * 100).toFixed(2) + '%' : '—'; }
  recNetOk = computed(() => { const d = this.store.selectedRun(); return d ? Math.abs(d.netProfit - this.trades().reduce((s, t) => s + t.netPnLAmount, 0)) < 0.01 : false; });
  recClosesOk = computed(() => { const d = this.store.selectedRun(); return d ? d.totalTrades === this.trades().length : false; });
  recCostOk = computed(() => Math.abs(this.grossTotal() - this.commTotal() - this.swapTotal() - this.trades().reduce((s, t) => s + t.netPnLAmount, 0)) < 0.01);

  topReasons(s: StrategyPerformance): string { return (s.topRejections ?? []).map(r => r.reason + ' (' + r.count + ')').join(', ') || '—'; }
  rejectionBarHeight(count: number, rejections: { count: number }[]): number { const max = Math.max(...rejections.map(r => r.count), 1); return Math.max(4, (count / max) * 60); }
  rejectionBarColor(reason: string): string { return reason.startsWith('GATE:') ? 'bg-amber-700/60' : 'bg-emerald-700/60'; }
  scatterData = computed(() => this.trades().map(t => ({ x: -(t.maxAdverseExcursion ?? 0), y: t.maxFavorableExcursion ?? 0 })));

  perBarVerdicts = computed(() => this.barDecisions().flatMap(e => (e.strategyVerdicts ?? []).map(v => ({ simTimeUtc: e.simTimeUtc, strategyId: v.strategyId, signalFired: v.signalFired, direction: v.direction, reason: v.reason }))));

  exportReport(fmt: 'json' | 'md'): void { const d = this.store.selectedRun(); if (!d) return; const stats: ExportStats = { symbols: this.symbolsDisplay(), profitFactor: this.pfDisplay(), avgR: this.avgR().toFixed(2) }; doExportReport(d, stats, this.breakdown(), this.trades(), fmt); }

  fmtReason(reason: string | null): string { if (!reason) return ''; if (reason.startsWith('[') || reason.startsWith('{')) { try { const parsed = JSON.parse(reason); if (Array.isArray(parsed)) return parsed.map((v: any) => this.nameOf(v)).join(', '); return this.nameOf(parsed); } catch { return reason; } } return reason; }
  private nameOf(v: any): string { if (v == null) return ''; if (typeof v !== 'object') return String(v); return v.name ?? v.code ?? v.reason ?? v.message ?? Object.entries(v).map(([k, val]) => k + '=' + val).join(' '); }

  barHeight(pnl: number): number { const arr = this.dailyPnl(); const m = arr.length > 0 ? Math.max(...arr.map(d => Math.abs(d.pnl ?? 0)), 1) : 1; return Math.min(100, (Math.abs(pnl ?? 0) / m) * 100); }

  dailyHistogram = computed(() => this.dailyPnl().map(d => ({ time: new Date(d.date).getTime() / 1000, value: d.pnl })));
  underwaterPoints = computed(() => {
    const pts = this.equityPoints();
    if (pts.length < 2) return [];
    let peak = pts[0].value;
    return pts.map(p => {
      if (p.value > peak) peak = p.value;
      return { time: p.time, value: peak > 0 ? ((p.value - peak) / peak) * 100 : 0 };
    });
  });
  symbolsDisplay = () => formatSymbols(this.store.selectedRun() ?? { symbols: '', symbol: '' });

  async openDuplicate(runId: string): Promise<void> { this.dupRunId.set(runId); this.dupPackId.set(''); this.dupDisableRegime.set(false); this.dupOpen.set(true); if (this.packs().length === 0) { try { const pks = await this.packsApi.getAll(); this.packs.set(pks.map(p => ({ id: p.id, name: p.name }))); } catch { /* */ } } }
  async confirmDuplicate(): Promise<void> { const runId = this.dupRunId(); if (!runId || this.duplicating()) return; this.duplicating.set(true); this.dupOpen.set(false); const params = new URLSearchParams(); params.set('sourceRunId', runId); const packId = this.dupPackId(); if (packId) params.set('usePackId', packId); if (this.dupDisableRegime()) params.set('disableRegime', 'true'); this.router.navigate(['/runs/new'], { queryParams: Object.fromEntries(params) }); this.duplicating.set(false); }
  async duplicate(runId: string): Promise<void> { if (this.duplicating()) return; this.duplicating.set(true); try { const res = await this.api.duplicateRun(runId); if (res?.runId) this.router.navigate(['/runs', res.runId, 'monitor']); } finally { this.duplicating.set(false); } }

  journalColumns: ColumnDef[] = [{ key: 'simTime', label: 'Sim Time' }, { key: 'kind', label: 'Kind' }, { key: 'outcome', label: 'Outcome' }, { key: 'symbol', label: 'Symbol' }, { key: 'strategy', label: 'Strategy' }, { key: 'reason', label: 'Reason' }];
  journalTableData = computed(() => this.filteredJournal().map(e => ({ seq: e.seq, simTime: new Date(e.simTimeUtc).toLocaleString(), kind: this.kindOf(e), outcome: e.outcome ?? null, symbol: e.symbol ?? null, strategy: e.strategyId ?? null, reason: this.fmtReason(e.decisionReason ?? e.reason ?? null) })));

  async ngOnInit(): Promise<void> {
    const runId = this.route.snapshot.paramMap.get('runId');
    if (!runId) return;
    await this.store.loadRun(runId);
    try { this.trades.set(await this.api.getRunTrades(runId)); } catch { /* */ }
    try { this.journal.set(await this.api.getRunJournal(runId, undefined, undefined, 200)); } catch { /* */ }
    try { this.barDecisions.set(await this.api.getBarDecisions(runId, undefined, 500)); } catch { /* */ }
    try { this.runBars.set(await this.api.getRunBars(runId)); } catch { /* */ }
    try { this.breakdown.set(await this.api.getStrategyBreakdown(runId)); } catch { /* */ }
    try { this.equityPoints.set((await this.api.getRunEquity(runId)).map((p: EquityPoint) => ({ time: new Date(p.timestampUtc).getTime(), value: p.equity, balance: p.balance }))); } catch { /* */ }
    try { this.dailyPnl.set(await this.api.getRunDailyPnl(runId)); } catch { /* */ }
  }
}
