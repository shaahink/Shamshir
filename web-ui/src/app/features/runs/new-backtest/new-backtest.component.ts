import { Component, computed, inject, OnInit, signal, ChangeDetectionStrategy, type WritableSignal } from '@angular/core';
import { Router, ActivatedRoute } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { RunsStore } from '../runs.store';
import type { StrategySummary, StartRunRequest, RiskProfile, RunRow } from '../../../models/api.types';
import { StrategiesApiService } from '../../strategies/strategies.service';
import { RiskProfilesApiService } from '../../risk-profiles/risk-profiles.service';
import { AddOnPacksApiService } from '../../addon-packs/addon-packs.service';
import { RunsApiService } from '../runs.service';

const ALL_SYMBOLS = [
  'EURUSD', 'GBPUSD', 'USDJPY', 'GBPJPY', 'XAUUSD', 'AUDUSD',
  'USDCHF', 'USDCAD', 'NZDUSD', 'EURGBP', 'EURJPY', 'XAGUSD',
];
const ALL_TIMEFRAMES = ['h1', 'h4', 'd1', 'm15', 'm5', 'm1'];

interface BuilderRow {
  strategyId: string;
  symbol: string;
  timeframe: string;
  packId: string;
  enabled: boolean;
}

const rowKey = (sid: string, sym: string, tf: string) => `${sid}|${sym}|${tf}`;

@Component({
  selector: 'app-new-backtest',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="mx-auto max-w-4xl space-y-6">
      <h1 class="text-xl font-semibold">New Backtest</h1>

      <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-6 space-y-5">
        <!-- 1. Selectors (nothing pre-selected) -->
        <div>
          <label class="block text-xs font-medium text-gray-400 mb-2">Strategies</label>
          <div class="grid gap-2 md:grid-cols-3">
            @for (s of strategies(); track s.id) {
              <label [attr.class]="stratClass(s.id)" (click)="toggleStrat(s.id)">
                <input type="checkbox" [checked]="selectedStrategyIds().has(s.id)"
                  class="h-4 w-4 rounded border-gray-600 bg-gray-800 text-emerald-500 focus:ring-emerald-500" />
                <div>
                  <div class="text-sm font-medium text-gray-200">{{ s.displayName }}</div>
                  <div class="text-xs text-gray-500">{{ s.id }}</div>
                </div>
              </label>
            }
          </div>
          @if (strategies().length === 0) {
            <p class="text-xs text-gray-500">Loading strategies...</p>
          }
        </div>

        <div class="grid grid-cols-2 gap-4">
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-2">Symbols</label>
            <div class="flex flex-wrap gap-2">
              @for (s of allSymbols; track s) {
                <label [attr.class]="symClass(s)" (click)="toggleSymbol(s)">{{ s }}</label>
              }
            </div>
          </div>
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-2">Timeframes</label>
            <div class="flex flex-wrap gap-2">
              @for (tf of allTimeframes; track tf) {
                <label [attr.class]="tfClass(tf)" (click)="togglePeriod(tf)">{{ tf }}</label>
              }
            </div>
          </div>
        </div>

        <!-- 2. The combination grid (one row per strategy × symbol × timeframe, each with its own pack) -->
        <div>
          <div class="mb-2 flex items-center justify-between">
            <label class="text-xs font-medium text-gray-400">
              Run plan — {{ rows().length }} row{{ rows().length === 1 ? '' : 's' }} ({{ enabledCount() }} enabled)
            </label>
            @if (rows().length > 0) {
              <button (click)="setAllEnabled(true)" class="text-xs text-emerald-400 hover:underline mr-2">enable all</button>
              <button (click)="setAllEnabled(false)" class="text-xs text-gray-400 hover:underline">disable all</button>
            }
          </div>
          @if (rows().length === 0) {
            <div class="rounded-md border border-dashed border-gray-700 p-4 text-center text-xs text-gray-500">
              Pick at least one strategy, symbol and timeframe to build the run plan.
            </div>
          } @else {
            <div class="overflow-hidden rounded-md border border-gray-800">
              <table class="w-full text-xs">
                <thead class="bg-gray-800/50 text-gray-400">
                  <tr>
                    <th class="px-3 py-2 text-left font-medium">On</th>
                    <th class="px-3 py-2 text-left font-medium">Strategy</th>
                    <th class="px-3 py-2 text-left font-medium">Symbol</th>
                    <th class="px-3 py-2 text-left font-medium">TF</th>
                    <th class="px-3 py-2 text-left font-medium">Add-on pack</th>
                  </tr>
                </thead>
                <tbody>
                  @for (r of rows(); track rowKeyOf(r)) {
                    <tr class="border-t border-gray-800" [class.opacity-40]="!r.enabled">
                      <td class="px-3 py-1.5">
                        <input type="checkbox" [ngModel]="r.enabled" (ngModelChange)="setRowEnabled(r, $event)"
                          class="h-4 w-4 rounded border-gray-600 bg-gray-800 text-emerald-500" />
                      </td>
                      <td class="px-3 py-1.5 text-gray-200">{{ r.strategyId }}</td>
                      <td class="px-3 py-1.5 text-gray-300">{{ r.symbol }}</td>
                      <td class="px-3 py-1.5 uppercase text-gray-300">{{ r.timeframe }}</td>
                      <td class="px-3 py-1.5">
                        <select [ngModel]="r.packId" (ngModelChange)="setRowPack(r, $event)"
                          class="w-full rounded border border-gray-700 bg-gray-800 px-2 py-1 text-xs text-gray-200">
                          <option value="">Strategy default add-ons</option>
                          @for (p of packs(); track p.id) {
                            <option [value]="p.id">{{ p.name }}</option>
                          }
                        </select>
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        </div>

        <!-- 3. Run-level settings (same for the whole backtest — D4) -->
        <div>
          <label class="block text-xs font-medium text-gray-400 mb-2">Date Range</label>
          <div class="flex gap-2 mb-2">
            <button (click)="setDateRange(1)" class="rounded-md border border-gray-700 px-3 py-1 text-xs text-gray-300 hover:bg-gray-800">Last Month</button>
            <button (click)="setDateRange(3)" class="rounded-md border border-gray-700 px-3 py-1 text-xs text-gray-300 hover:bg-gray-800">Last Quarter</button>
            <button (click)="setDateRange(12)" class="rounded-md border border-gray-700 px-3 py-1 text-xs text-gray-300 hover:bg-gray-800">Last Year</button>
          </div>
          <div class="grid grid-cols-2 gap-3">
            <input type="date" [(ngModel)]="startDate" class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none" />
            <input type="date" [(ngModel)]="endDate" class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none" />
          </div>
        </div>

        <div class="grid grid-cols-2 gap-3 md:grid-cols-3">
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Initial Balance</label>
            <input type="number" [(ngModel)]="balance" class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none" />
          </div>
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Commission (per M)</label>
            <input type="number" [(ngModel)]="commission" class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none" />
          </div>
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Spread (pips)</label>
            <input type="number" [(ngModel)]="spread" step="0.1" class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none" />
          </div>
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Risk Profile</label>
            <select [(ngModel)]="riskProfile" class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none">
              @for (p of riskProfiles(); track p.id) {
                <option [value]="p.id">{{ p.displayName }} ({{ (p.riskPerTradePercent * 100).toFixed(2) }}%/trade)</option>
              }
            </select>
          </div>
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Data Venue</label>
            <select [(ngModel)]="venue" class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none">
              <option value="replay">Stored-bar replay (deterministic)</option>
              <option value="ctrader">cTrader live forward-test</option>
            </select>
          </div>
          <div class="flex flex-col justify-end gap-1 pb-1">
            <label class="flex items-center gap-2 text-sm text-gray-400 cursor-pointer">
              <input type="checkbox" [(ngModel)]="governorEnabled" class="rounded" /> Governor
            </label>
            <label class="flex items-center gap-2 text-sm text-gray-400 cursor-pointer">
              <input type="checkbox" [(ngModel)]="dailyDdEnabled" class="rounded" /> Daily DD protection
            </label>
            <label class="flex items-center gap-2 text-sm text-gray-400 cursor-pointer">
              <input type="checkbox" [(ngModel)]="maxDdEnabled" class="rounded" /> Max DD protection
            </label>
            <label class="flex items-center gap-2 text-sm text-gray-400 cursor-pointer">
              <input type="checkbox" [(ngModel)]="forceCloseEnabled" class="rounded" /> Force close on breach
            </label>
            <label class="flex items-center gap-2 text-sm text-gray-400 cursor-pointer">
              <input type="checkbox" [(ngModel)]="regimeEnabled" class="rounded" /> Regime detection
            </label>
          </div>
        </div>

        @if (venue === 'ctrader') {
          <div class="rounded-md bg-amber-900/20 p-2 text-xs text-amber-400">
            cTrader runs the first row only (one CLI session); use replay for the full multi-row plan.
          </div>
        }

        @if (error()) {
          <div class="rounded-md bg-red-900/20 p-3 text-sm text-red-400">{{ error() }}</div>
        }

        <button (click)="start()" [disabled]="loading() || enabledCount() === 0"
          class="w-full rounded-md bg-emerald-600 px-4 py-2.5 text-sm font-medium text-white hover:bg-emerald-500 disabled:opacity-50">
          {{ loading() ? 'Starting...' : 'Start Backtest (' + enabledCount() + ' rows)' }}
        </button>
      </div>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NewBacktestComponent implements OnInit {
  private store = inject(RunsStore);
  private router = inject(Router);
  private route = inject(ActivatedRoute);
  private strategiesApi = inject(StrategiesApiService);
  private profilesApi = inject(RiskProfilesApiService);
  private packsApi = inject(AddOnPacksApiService);
  private runsApi = inject(RunsApiService);

  allSymbols = ALL_SYMBOLS;
  allTimeframes = ALL_TIMEFRAMES;

  // Nothing pre-selected (D3): a new backtest starts blank.
  selectedStrategyIds = signal<Set<string>>(new Set());
  selectedSymbols = signal<Set<string>>(new Set());
  selectedPeriods = signal<Set<string>>(new Set());
  rows = signal<BuilderRow[]>([]);
  enabledCount = computed(() => this.rows().filter((r) => r.enabled).length);

  startDate = '';
  endDate = '';
  balance = 100_000;
  commission = 30;
  spread = 1;
  riskProfile = 'standard';
  venue = 'replay';
  governorEnabled = true;
  dailyDdEnabled = true;
  maxDdEnabled = true;
  forceCloseEnabled = true;
  regimeEnabled = true;

  packs = signal<{ id: string; name: string }[]>([]);
  strategies = signal<StrategySummary[]>([]);
  riskProfiles = signal<RiskProfile[]>([
    { id: 'standard', displayName: 'Standard', riskPerTradePercent: 0.005, maxDailyDrawdownPercent: 0.05, maxTotalDrawdownPercent: 0.1, maxConcurrentPositions: 5, lotSizingMethod: 'PercentRisk', propFirmRuleSetId: 'ftmo-standard' },
  ]);
  loading = this.store.isLoading;
  error = this.store.error;

  rowKeyOf = (r: BuilderRow) => rowKey(r.strategyId, r.symbol, r.timeframe);

  constructor() {
    const now = new Date();
    this.endDate = now.toISOString().slice(0, 10);
    const d = new Date(now);
    d.setMonth(d.getMonth() - 1);
    this.startDate = d.toISOString().slice(0, 10);
  }

  async ngOnInit(): Promise<void> {
    try {
      this.strategies.set(await this.strategiesApi.getAll());
    } catch { /* */ }
    try {
      const profiles = await this.profilesApi.getAll();
      if (profiles.length > 0) {
        this.riskProfiles.set(profiles);
        if (!profiles.some((p) => p.id === this.riskProfile)) this.riskProfile = profiles[0].id;
      }
    } catch { /* keep default */ }
    try {
      const pks = await this.packsApi.getAll();
      this.packs.set(pks.map((p) => ({ id: p.id, name: p.name })));
    } catch { /* */ }

    // Duplicate flow: prefill the run-level fields + selections, then rebuild the grid.
    const sourceRunId = this.route.snapshot.queryParamMap.get('sourceRunId');
    if (sourceRunId) {
      try {
        const src = await this.runsApi.getRun(sourceRunId);
        if (src) {
          this.startDate = (src.backtestFrom || '').slice(0, 10);
          this.endDate = (src.backtestTo || '').slice(0, 10);
          this.balance = src.initialBalance || 100000;
          const parse = (v: unknown): string[] => {
            try { const a = typeof v === 'string' ? JSON.parse(v) : v; return Array.isArray(a) ? a : []; } catch { return []; }
          };
          const syms = parse(src.symbols);
          const pers = parse(src.periods);
          if (syms.length) this.selectedSymbols.set(new Set(syms));
          if (pers.length) this.selectedPeriods.set(new Set(pers.map((p) => p.toLowerCase())));
          this.regenerate();
        }
      } catch { /* keep defaults */ }
    }
  }

  // --- selection toggles (each rebuilds the grid, preserving prior per-row pack/enable choices) ---
  private toggle(setSig: WritableSignal<Set<string>>, v: string): void {
    const s = new Set(setSig());
    if (s.has(v)) s.delete(v); else s.add(v);
    setSig.set(s);
    this.regenerate();
  }
  toggleStrat(id: string) { this.toggle(this.selectedStrategyIds, id); }
  toggleSymbol(s: string) { this.toggle(this.selectedSymbols, s); }
  togglePeriod(tf: string) { this.toggle(this.selectedPeriods, tf); }

  private regenerate(): void {
    const prev = new Map(this.rows().map((r) => [this.rowKeyOf(r), r]));
    const next: BuilderRow[] = [];
    for (const sid of this.selectedStrategyIds()) {
      for (const sym of this.selectedSymbols()) {
        for (const tf of this.selectedPeriods()) {
          const k = rowKey(sid, sym.toUpperCase(), tf.toUpperCase());
          next.push(prev.get(k) ?? { strategyId: sid, symbol: sym.toUpperCase(), timeframe: tf.toUpperCase(), packId: '', enabled: true });
        }
      }
    }
    this.rows.set(next);
  }

  setRowEnabled(row: BuilderRow, enabled: boolean): void {
    this.rows.update((rs) => rs.map((r) => (this.rowKeyOf(r) === this.rowKeyOf(row) ? { ...r, enabled } : r)));
  }
  setRowPack(row: BuilderRow, packId: string): void {
    this.rows.update((rs) => rs.map((r) => (this.rowKeyOf(r) === this.rowKeyOf(row) ? { ...r, packId } : r)));
  }
  setAllEnabled(enabled: boolean): void {
    this.rows.update((rs) => rs.map((r) => ({ ...r, enabled })));
  }

  // --- chip classes ---
  symClass(sym: string): string {
    const sel = this.selectedSymbols().has(sym);
    return 'cursor-pointer rounded-md border px-2.5 py-1 text-xs transition ' + (sel ? 'border-emerald-600 bg-emerald-900/20 text-emerald-400' : 'border-gray-700 text-gray-400');
  }
  tfClass(tf: string): string {
    const sel = this.selectedPeriods().has(tf);
    return 'cursor-pointer rounded-md border px-2.5 py-1 text-xs uppercase transition ' + (sel ? 'border-emerald-600 bg-emerald-900/20 text-emerald-400' : 'border-gray-700 text-gray-400');
  }
  stratClass(id: string): string {
    const sel = this.selectedStrategyIds().has(id);
    return 'flex items-center gap-2 rounded-md border border-gray-700 p-3 cursor-pointer hover:border-gray-600 transition ' + (sel ? 'border-emerald-600 bg-emerald-900/10' : '');
  }

  setDateRange(months: number): void {
    const now = new Date();
    this.endDate = now.toISOString().slice(0, 10);
    const d = new Date(now);
    d.setMonth(d.getMonth() - months);
    this.startDate = d.toISOString().slice(0, 10);
  }

  async start(): Promise<void> {
    const enabled = this.rows().filter((r) => r.enabled);
    if (enabled.length === 0) {
      this.error.set('Select at least one strategy, symbol and timeframe (no enabled rows).');
      return;
    }
    if (!this.startDate || !this.endDate) { this.error.set('Select start and end dates'); return; }
    if (new Date(this.startDate) >= new Date(this.endDate)) { this.error.set('Start date must be before end date'); return; }
    if (!this.balance || this.balance <= 0) { this.error.set('Balance must be greater than 0'); return; }
    this.error.set(null);

    const rows: RunRow[] = enabled.map((r) => ({
      strategyId: r.strategyId,
      symbol: r.symbol,
      timeframe: r.timeframe,
      packId: r.packId || undefined,
      enabled: true,
    }));

    const req: StartRunRequest = {
      start: this.startDate,
      end: this.endDate,
      balance: this.balance,
      commissionPerMillion: this.commission,
      spreadPips: this.spread,
      symbols: [...new Set(enabled.map((r) => r.symbol))],
      periods: [...new Set(enabled.map((r) => r.timeframe.toLowerCase()))],
      strategyIds: [...new Set(enabled.map((r) => r.strategyId))],
      riskProfileId: this.riskProfile,
      venue: this.venue,
      rows,
      governorEnabled: this.governorEnabled,
      dailyDdEnabled: this.dailyDdEnabled,
      maxDdEnabled: this.maxDdEnabled,
      forceCloseOnBreachEnabled: this.forceCloseEnabled,
      disableRegime: this.regimeEnabled ? undefined : true,
    };
    const runId = await this.store.startBacktest(req);
    this.router.navigate(['/runs', runId, 'monitor']);
  }
}
