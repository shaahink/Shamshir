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
import { BadgeComponent } from '../../../shared/badge.component';
import { downloadBlob } from '../../../shared/download.helper';
import { exportReport as doExportReport, type ExportStats } from '../report-export.helper';
import type { TradeSummary, JournalEntry, EquityPoint, DailyPnl, StrategyPerformance, AddOnPack } from '../../../models/api.types';

type JournalRow = JournalEntry & { outcome?: string | null };

@Component({
  selector: 'app-run-report',
  standalone: true,
  imports: [
    RouterLink,
    DatePipe,
    NgClass,
    DecimalPipe,
    FormsModule,
    StatTileComponent,
    DataTableComponent,
    EquityChartComponent,
    ScatterChartComponent,
    BadgeComponent,
  ],
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
                {{ symbolsDisplay() }} {{ d.period }} {{ d.backtestFrom | date }} - {{ d.backtestTo | date }} · Balance
                {{ d.initialBalance | number }}
              </p>
              <p class="mt-0.5 text-xs text-gray-600">
                @if (d.parentRunId) {
                  <a [routerLink]="['/runs', d.parentRunId, 'report']" class="text-emerald-500 hover:underline"
                    >⤷ duplicate of {{ d.parentRunId.slice(0, 8) }}</a
                  >
                  <span class="mx-1">·</span>
                }
                @if (d.datasetId) {
                  <span title="dataset identity (same data window)">data {{ d.datasetId.slice(0, 8) }}</span>
                }
                @if (d.configSetId) {
                  <span class="mx-1">·</span
                  ><span title="effective-config identity">cfg {{ d.configSetId.slice(0, 8) }}</span>
                }
              </p>
            </div>
            <div class="flex gap-2">
              <button
                (click)="openDuplicate(d.runId)"
                [disabled]="duplicating()"
                class="rounded-md border border-emerald-700 px-3 py-1.5 text-xs text-emerald-400 hover:bg-emerald-900/30 disabled:opacity-50"
              >
                {{ duplicating() ? 'Duplicating…' : 'Duplicate' }}
              </button>
              <a
                [href]="journalExportUrl(d.runId)"
                download
                class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800"
                >Download journal (NDJSON)</a
              >
              <a
                [href]="tradesCsvUrl(d.runId)"
                download
                class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800"
                >Export CSV</a
              >
              <button
                (click)="exportReport('json')"
                class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800"
              >
                Report JSON
              </button>
              <button
                (click)="exportReport('md')"
                class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800"
              >
                Report MD
              </button>
              <a
                [routerLink]="['/runs', d.runId, 'monitor']"
                class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800"
                >Monitor</a
              >
              <a
                [routerLink]="['/runs', d.runId, 'analyzer']"
                class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800"
                >Analyzer</a
              >
              <a
                routerLink="/runs"
                class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800"
                >All Runs</a
              >
            </div>
          </div>

          <div class="grid grid-cols-2 gap-3 md:grid-cols-4 lg:grid-cols-5">
            <app-stat-tile
              label="Net P/L"
              [value]="d.netProfit.toFixed(2)"
              [positive]="d.netProfit > 0"
              [negative]="d.netProfit < 0"
            />
            <app-stat-tile
              label="Return %"
              [value]="returnPct(d)"
              [positive]="d.netProfit > 0"
              [negative]="d.netProfit < 0"
            />
            <app-stat-tile label="Max DD" [value]="(d.maxDrawdownPct * 100).toFixed(2) + '%'" [negative]="true" />
            <app-stat-tile label="Profit Factor" [value]="pfDisplay()" [positive]="profitFactor() > 1" />
            <app-stat-tile
              label="Win Rate"
              [value]="(d.winRatePct * 100).toFixed(1) + '%'"
              [positive]="d.winRatePct > 0.5"
            />
            <app-stat-tile label="Trades" [value]="d.totalTrades" />
            <app-stat-tile
              label="Gross P/L"
              [value]="grossDisplay()"
              [positive]="d.grossPnL > 0"
              [negative]="d.grossPnL < 0"
            />
            <app-stat-tile label="Commission" [value]="commDisplay()" [negative]="d.commissionTotal !== 0" />
            <app-stat-tile label="Swap" [value]="swapDisplay()" [negative]="d.swapTotal !== 0" />
            <app-stat-tile label="Avg R" [value]="avgR().toFixed(2)" [positive]="avgR() > 0" />
          </div>

          <div class="flex gap-3">
            <span class="text-xs text-gray-500">
              Net = Σ trades:
              <app-badge [label]="recNetOk() ? 'OK' : 'MISMATCH'" [variant]="recNetOk() ? 'success' : 'error'" />
            </span>
            <span class="text-xs text-gray-500">
              Closes = trade count:
              <app-badge [label]="recClosesOk() ? 'OK' : 'MISMATCH'" [variant]="recClosesOk() ? 'success' : 'error'" />
            </span>
            <span class="text-xs text-gray-500">
              ΣGross - ΣComm - ΣSwap = ΣNet:
              <app-badge [label]="recCostOk() ? 'OK' : 'MISMATCH'" [variant]="recCostOk() ? 'success' : 'error'" />
            </span>
          </div>

          @if (equityPoints().length > 1) {
            <app-equity-chart
              title="Equity & Drawdown"
              [data]="equityPoints()"
              [showDrawdown]="true"
              [showBalance]="true"
            />
          }

          @if (dailyPnl().length > 0) {
            <div>
              <h2 class="mb-3 text-sm font-medium text-gray-400">DD Timeline</h2>
              <div class="flex h-16 items-end gap-0.5">
                @for (dp of dailyPnl(); track dp.date) {
                  <div
                    class="flex-1 rounded-t"
                    [class.bg-emerald-600]="dp.pnl >= 0"
                    [class.bg-red-600]="dp.pnl < 0"
                    [style.height]="barHeight(dp.pnl) + '%'"
                    [title]="dp.date + ': ' + dp.pnl.toFixed(2)"
                  ></div>
                }
              </div>
            </div>
          }

          @if (breakdown().length > 0) {
            <div>
              <h2 class="mb-3 text-sm font-medium text-gray-400">Per-strategy funnel (why)</h2>
              <div class="overflow-x-auto rounded-lg border border-gray-800">
                <table class="w-full text-xs">
                  <thead class="text-gray-500">
                    <tr class="border-b border-gray-800">
                      <th class="px-3 py-1.5 text-left">Strategy</th>
                      <th class="px-3 py-1.5 text-right">Bars</th>
                      <th class="px-3 py-1.5 text-right">Signals</th>
                      <th class="px-3 py-1.5 text-right">Trades</th>
                      <th class="px-3 py-1.5 text-right">Win%</th>
                      <th class="px-3 py-1.5 text-left">Top no-signal reasons</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (s of breakdown(); track s.strategyId) {
                      <tr class="border-b border-gray-800 last:border-0">
                        <td class="px-3 py-1.5 text-gray-300">{{ s.strategyId }}</td>
                        <td class="px-3 py-1.5 text-right text-gray-400">{{ s.totalBarsEvaluated }}</td>
                        <td
                          class="px-3 py-1.5 text-right"
                          [class.text-emerald-400]="s.signalsFired > 0"
                          [class.text-red-400]="s.signalsFired === 0"
                        >
                          {{ s.signalsFired }}
                        </td>
                        <td class="px-3 py-1.5 text-right text-gray-400">{{ s.tradesOpened }}</td>
                        <td class="px-3 py-1.5 text-right text-gray-400">{{ (s.winRatePct * 100).toFixed(0) }}%</td>
                        <td class="px-3 py-1.5 text-gray-500">{{ topReasons(s) }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            </div>
          }

          @if (scatterData().length > 0) {
            <app-scatter-chart title="MAE (orange) vs MFE (green) — pips per trade" [data]="scatterData()" />
          }

          @if (perBarVerdicts().length > 0) {
            <div>
              <h2 class="mb-3 text-sm font-medium text-gray-400">Per-bar "why" ({{ perBarVerdicts().length }})</h2>
              <div class="max-h-72 overflow-y-auto rounded-lg border border-gray-800">
                <table class="w-full text-xs">
                  <thead class="text-gray-500">
                    <tr class="border-b border-gray-800">
                      <th class="px-3 py-1.5 text-left">Sim time</th>
                      <th class="px-3 py-1.5 text-left">Strategy</th>
                      <th class="px-3 py-1.5 text-left">Signal</th>
                      <th class="px-3 py-1.5 text-left">Reason</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (v of perBarVerdicts(); track v.strategyId) {
                      <tr class="border-b border-gray-800 last:border-0">
                        <td class="px-3 py-1 text-gray-500">{{ v.simTimeUtc | date: 'MM-dd HH:mm' }}</td>
                        <td class="px-3 py-1 text-gray-300">{{ v.strategyId }}</td>
                        <td
                          class="px-3 py-1"
                          [class.text-emerald-400]="v.signalFired"
                          [class.text-gray-500]="!v.signalFired"
                        >
                          {{ v.signalFired ? v.direction || 'FIRED' : '—' }}
                        </td>
                        <td class="px-3 py-1 text-gray-500">{{ v.reason }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            </div>
          }

          @if (trades().length > 0) {
            <div>
              <h2 class="mb-3 text-sm font-medium text-gray-400">Trades ({{ trades().length }})</h2>
              <app-data-table [columns]="tradeColumns" [data]="trades()" trackKey="id" (rowClick)="onTradeClick($event)" />
            </div>
          }

          @if (journal().length > 0) {
            <div>
              <h2 class="mb-3 text-sm font-medium text-gray-400">Journal</h2>
              <div class="flex flex-wrap gap-2 mb-2">
                @for (k of journalKinds; track k) {
                  @let active = (journalKind() || 'ALL') === k;
                  <button
                    (click)="journalKind.set(k === 'ALL' ? null : k)"
                    class="rounded px-2 py-0.5 text-xs"
                    [ngClass]="active ? 'bg-emerald-900/50 text-emerald-400' : 'text-gray-500'"
                  >
                    {{ k }}
                  </button>
                }
              </div>
              <div class="max-h-80 overflow-y-auto rounded-lg border border-gray-800">
                @for (entry of filteredJournal(); track entry.seq) {
                  <div class="border-b border-gray-800 px-4 py-1.5 text-xs last:border-0 hover:bg-gray-800/20">
                    <span class="text-gray-500">{{ entry.simTimeUtc | date: 'MM-dd HH:mm' }}</span>
                    <app-badge [label]="kindOf(entry)" [variant]="badgeVariant(kindOf(entry))" />
                    @if (entry.outcome) {
                      <app-badge [label]="entry.outcome" [variant]="badgeVariant(entry.outcome)" />
                    }
                    @if (entry.symbol) {
                      <span class="ml-1 text-gray-500">{{ entry.symbol }}</span>
                    }
                    @if (reasonOf(entry)) {
                      <span class="ml-2 text-gray-600">- {{ reasonOf(entry) }}</span>
                    }
                  </div>
                }
              </div>
            </div>
          }
        </div>

        @if (dupOpen() && dupRunId() === d.runId) {
          <div class="fixed inset-0 z-50 flex items-center justify-center bg-black/60" (click)="dupOpen.set(false)">
            <div class="w-full max-w-sm rounded-lg border border-gray-700 bg-gray-900 p-5 shadow-xl" (click)="$event.stopPropagation()">
              <h3 class="mb-3 text-sm font-medium text-gray-300">Duplicate Run</h3>
              <div class="space-y-3">
                <div>
                  <label class="block text-xs text-gray-500 mb-1">Add-on Pack</label>
                  <select
                    [(ngModel)]="dupPackId"
                    class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-1.5 text-sm text-gray-200"
                  >
                    <option value="">None (strategy defaults)</option>
                    @for (p of packs(); track p.id) {
                      <option [value]="p.id">{{ p.name }}</option>
                    }
                  </select>
                </div>
                <div>
                  <label class="flex items-center gap-2 text-sm text-gray-400 cursor-pointer">
                    <input type="checkbox" [(ngModel)]="dupDisableRegime" class="rounded" />
                    Disable Regime Detection
                  </label>
                </div>
              </div>
              <div class="mt-4 flex gap-2 justify-end">
                <button
                  (click)="dupOpen.set(false)"
                  class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-400 hover:bg-gray-800"
                >Cancel</button>
                <button
                  (click)="confirmDuplicate()"
                  class="rounded-md bg-emerald-600 px-4 py-1.5 text-xs font-medium text-white hover:bg-emerald-500"
                >Duplicate</button>
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

  onTradeClick(row: Record<string, unknown>): void {
    if (row && typeof row['id'] === 'string') this.router.navigate(['/trades', row['id']]);
  }

  trades = signal<TradeSummary[]>([]);
  journal = signal<JournalEntry[]>([]);
  barDecisions = signal<JournalEntry[]>([]);
  breakdown = signal<StrategyPerformance[]>([]);
  equityPoints = signal<ChartPoint[]>([]);
  dailyPnl = signal<DailyPnl[]>([]);
  journalKind = signal<string | null>(null);
  duplicating = signal(false);
  private packsApi = inject(AddOnPacksApiService);
  dupRunId = signal<string | null>(null);
  dupPackId = signal('');
  dupDisableRegime = signal(false);
  packs = signal<{ id: string; name: string }[]>([]);
  dupOpen = signal(false);
  journalKinds = [
    'ALL',
    'SIGNAL',
    'ORDER',
    'FILL',
    'CLOSE',
    'REJECTED',
    'BREACH',
    'GOVERNOR',
    'ENTRY_EXPIRED',
    'CANCELLED',
    'TRAIL',
    'BREAKEVEN',
    'PARTIAL',
  ];

  journalExportUrl(runId: string): string {
    return this.api.journalExportUrl(runId);
  }
  tradesCsvUrl(runId: string): string {
    return this.api.tradesCsvUrl(runId);
  }

  // F1 — join an order's FILL / EXPIRY / REJECTION into its OrderProposed row by orderId, so the lifecycle
  // reads as one line instead of several. Applied to the full journal; the kind filter runs on the result.
  displayJournal = computed<JournalRow[]>(() => {
    const byOrder = new Map<string, JournalRow>();
    const out: JournalRow[] = [];
    for (const e of this.journal()) {
      const kind = this.kindOf(e);
      const oid = this.orderIdOf(e);
      if (
        oid &&
        (kind === 'OrderFilled' ||
          kind === 'OrderCancelled' ||
          kind === 'OrderRejected' ||
          kind === 'OrderPartiallyFilled')
      ) {
        const proposal = byOrder.get(oid);
        if (proposal) {
          proposal.outcome = kind;
          continue;
        }
      }
      const row: JournalRow = { ...e };
      if (oid && kind === 'OrderProposed') byOrder.set(oid, row);
      out.push(row);
    }
    return out;
  });

  filteredJournal = computed(() => {
    const k = this.journalKind();
    return k && k !== 'ALL' ? this.displayJournal().filter((e) => this.kindOf(e) === k) : this.displayJournal();
  });

  kindOf(e: JournalEntry): string {
    return e.eventKind ?? e.kind ?? '?';
  }

  private orderIdOf(e: JournalEntry): string | null {
    if (!e.eventJson) return null;
    try {
      const o = JSON.parse(e.eventJson);
      return o.OrderId ?? o.orderId ?? null;
    } catch {
      return null;
    }
  }

  badgeVariant(kind: string): 'success' | 'error' | 'warning' | 'neutral' {
    switch (kind) {
      case 'CLOSE':
      case 'OrderFilled':
      case 'TP':
        return 'success';
      case 'REJECTED':
      case 'OrderRejected':
      case 'BREACH':
      case 'SL':
        return 'error';
      case 'SIGNAL':
      case 'OrderProposed':
      case 'GOVERNOR':
      case 'ENTRY_EXPIRED':
      case 'OrderCancelled':
      case 'TRAIL':
      case 'BREAKEVEN':
      case 'PARTIAL':
        return 'warning';
      default:
        return 'neutral';
    }
  }

  reasonOf(e: JournalEntry): string {
    return this.fmtReason(e.decisionReason ?? e.reason ?? null);
  }

  grossTotal = computed(() => this.trades().reduce((s, t) => s + t.grossPnLAmount, 0));
  commTotal = computed(() => this.trades().reduce((s, t) => s + t.commissionAmount, 0));
  swapTotal = computed(() => this.trades().reduce((s, t) => s + t.swapAmount, 0));
  grossDisplay = computed(() => {
    const d = this.store.selectedRun();
    return d ? (d.grossPnL ?? this.trades().reduce((s, t) => s + (t.grossPnLAmount ?? 0), 0)).toFixed(2) : '0.00';
  });
  commDisplay = computed(() => {
    const d = this.store.selectedRun();
    return d
      ? (d.commissionTotal ?? this.trades().reduce((s, t) => s + (t.commissionAmount ?? 0), 0)).toFixed(2)
      : '0.00';
  });
  swapDisplay = computed(() => {
    const d = this.store.selectedRun();
    return d ? (d.swapTotal ?? this.trades().reduce((s, t) => s + (t.swapAmount ?? 0), 0)).toFixed(2) : '0.00';
  });
  avgR = computed(() => {
    const t = this.trades();
    return t.length ? t.reduce((s, x) => s + (x.rMultiple ?? 0), 0) / t.length : 0;
  });
  profitFactor = computed(() => {
    const t = this.trades();
    const g = t.filter((x) => (x.grossPnLAmount ?? 0) > 0).reduce((s, x) => s + (x.grossPnLAmount ?? 0), 0);
    const l = Math.abs(t.filter((x) => (x.grossPnLAmount ?? 0) < 0).reduce((s, x) => s + (x.grossPnLAmount ?? 0), 0));
    return l === 0 ? (g > 0 ? Number.MAX_VALUE : 0) : g / l;
  });
  pfDisplay = computed(() => (this.profitFactor() >= Number.MAX_VALUE - 1 ? '∞' : this.profitFactor().toFixed(2)));

  returnPct(d: { netProfit: number; initialBalance: number }): string {
    return d.initialBalance > 0 ? ((d.netProfit / d.initialBalance) * 100).toFixed(2) + '%' : '—';
  }
  recNetOk = computed(() => {
    const d = this.store.selectedRun();
    return d ? Math.abs(d.netProfit - this.trades().reduce((s, t) => s + t.netPnLAmount, 0)) < 0.01 : false;
  });
  recClosesOk = computed(() => {
    const d = this.store.selectedRun();
    return d ? d.totalTrades === this.trades().length : false;
  });
  recCostOk = computed(
    () =>
      Math.abs(
        this.grossTotal() - this.commTotal() - this.swapTotal() - this.trades().reduce((s, t) => s + t.netPnLAmount, 0),
      ) < 0.01,
  );

  topReasons(s: StrategyPerformance): string {
    return (s.topRejections ?? []).map((r) => r.reason + ' (' + r.count + ')').join(', ') || '\u2014';
  }

  // F4 — MAE/MFE scatter from the run's trades (x = MAE as negative pips, y = MFE).
  scatterData = computed(() =>
    this.trades().map((t) => ({ x: -(t.maxAdverseExcursion ?? 0), y: t.maxFavorableExcursion ?? 0 })),
  );

  // T4: per-bar "why" flattened from the server-paged bar-decisions endpoint.
  perBarVerdicts = computed(() =>
    this.barDecisions()
      .flatMap((e) =>
        (e.strategyVerdicts ?? []).map((v) => ({
          simTimeUtc: e.simTimeUtc,
          strategyId: v.strategyId,
          signalFired: v.signalFired,
          direction: v.direction,
          reason: v.reason,
        })),
      ),
  );

  // F4 — client-side report export (JSON / Markdown) off the data the report already has.
  exportReport(fmt: 'json' | 'md'): void {
    const d = this.store.selectedRun();
    if (!d) return;
    const stats: ExportStats = {
      symbols: this.symbolsDisplay(),
      profitFactor: this.pfDisplay(),
      avgR: this.avgR().toFixed(2),
    };
    doExportReport(d, stats, this.breakdown(), this.trades(), fmt);
  }

  // F1 — render a violation/decision reason as a readable name, never raw JSON / [object Object].
  fmtReason(reason: string | null): string {
    if (!reason) return '';
    if (reason.startsWith('[') || reason.startsWith('{')) {
      try {
        const parsed = JSON.parse(reason);
        if (Array.isArray(parsed)) return parsed.map((v: any) => this.nameOf(v)).join(', ');
        return this.nameOf(parsed);
      } catch {
        return reason;
      }
    }
    return reason;
  }

  private nameOf(v: any): string {
    if (v == null) return '';
    if (typeof v !== 'object') return String(v);
    return (
      v.name ??
      v.code ??
      v.reason ??
      v.message ??
      Object.entries(v)
        .map(([k, val]) => k + '=' + val)
        .join(' ')
    );
  }

  barHeight(pnl: number): number {
    const arr = this.dailyPnl();
    const m = arr.length > 0 ? Math.max(...arr.map((d) => Math.abs(d.pnl ?? 0)), 1) : 1;
    return Math.min(100, (Math.abs(pnl ?? 0) / m) * 100);
  }

  symbolsDisplay(): string {
    const d = this.store.selectedRun();
    if (!d) return '';
    try {
      const s = typeof d.symbols === 'string' ? JSON.parse(d.symbols) : d.symbols;
      if (Array.isArray(s) && s.length > 1) return s.join(', ');
    } catch {}
    return d.symbol;
  }

  async openDuplicate(runId: string): Promise<void> {
    this.dupRunId.set(runId);
    this.dupPackId.set('');
    this.dupDisableRegime.set(false);
    this.dupOpen.set(true);
    if (this.packs().length === 0) {
      try {
        const pks = await this.packsApi.getAll();
        this.packs.set(pks.map((p) => ({ id: p.id, name: p.name })));
      } catch { /* keep empty list */ }
    }
  }

  async confirmDuplicate(): Promise<void> {
    const runId = this.dupRunId();
    if (!runId || this.duplicating()) return;
    this.duplicating.set(true);
    this.dupOpen.set(false);
    try {
      const body: { usePackId?: string; disableRegime?: boolean } = {};
      const packId = this.dupPackId();
      if (packId) body.usePackId = packId;
      if (this.dupDisableRegime()) body.disableRegime = true;
      const res = await this.api.duplicateRun(runId, body);
      if (res?.runId) this.router.navigate(['/runs', res.runId, 'monitor']);
    } finally {
      this.duplicating.set(false);
      this.dupRunId.set(null);
    }
  }

  async duplicate(runId: string): Promise<void> {
    if (this.duplicating()) return;
    this.duplicating.set(true);
    try {
      const res = await this.api.duplicateRun(runId);
      if (res?.runId) this.router.navigate(['/runs', res.runId, 'monitor']);
    } finally {
      this.duplicating.set(false);
    }
  }

  tradeColumns: ColumnDef[] = [
    { key: 'symbol', label: 'Sym' },
    { key: 'direction', label: 'Dir' },
    { key: 'lots', label: 'Lots', format: 'number' },
    { key: 'entryPrice', label: 'Entry', format: 'number' },
    { key: 'exitPrice', label: 'Exit', format: 'number' },
    {
      key: 'grossPnLAmount',
      label: 'Gross',
      format: 'currency',
      colorFn: (v: number) => (v >= 0 ? '#34d399' : '#f87171'),
    },
    { key: 'commissionAmount', label: 'Comm', format: 'currency' },
    { key: 'swapAmount', label: 'Swap', format: 'currency' },
    { key: 'netPnLAmount', label: 'Net', format: 'currency', colorFn: (v: number) => (v >= 0 ? '#34d399' : '#f87171') },
    { key: 'pnLPips', label: 'Pips', format: 'pips' },
    { key: 'rMultiple', label: 'R', format: 'number' },
    { key: 'maxAdverseExcursion', label: 'MAE', format: 'pips' },
    { key: 'maxFavorableExcursion', label: 'MFE', format: 'pips' },
    { key: 'exitReason', label: 'Exit' },
    { key: 'strategyId', label: 'Strategy' },
    { key: 'durationSeconds', label: 'Hold', format: 'duration' },
  ];

  async ngOnInit(): Promise<void> {
    const runId = this.route.snapshot.paramMap.get('runId');
    if (!runId) return;
    await this.store.loadRun(runId);
    try {
      this.trades.set(await this.api.getRunTrades(runId));
    } catch {
      /* no trades */
    }
    try {
      this.journal.set(await this.api.getRunJournal(runId, undefined, undefined, 200));
    } catch {
      /* no journal */
    }
    try {
      this.barDecisions.set(await this.api.getBarDecisions(runId, undefined, 500));
    } catch {
      /* no bar decisions */
    }
    try {
      this.breakdown.set(await this.api.getStrategyBreakdown(runId));
    } catch {
      /* no breakdown */
    }
    try {
      this.equityPoints.set(
        (await this.api.getRunEquity(runId)).map((p: EquityPoint) => ({
          time: new Date(p.timestampUtc).getTime(),
          value: p.equity,
          balance: p.balance,
        })),
      );
    } catch {
      /* no equity */
    }
    try {
      this.dailyPnl.set(await this.api.getRunDailyPnl(runId));
    } catch {
      /* no daily PnL */
    }
  }
}
