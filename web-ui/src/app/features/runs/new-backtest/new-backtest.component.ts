import { DatePipe, NgClass } from '@angular/common';
import { Component, computed, inject, OnInit, signal, ChangeDetectionStrategy, type WritableSignal } from '@angular/core';
import { Router, ActivatedRoute } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
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

interface InventoryEntry {
  symbol: string;
  timeframe: string;
  source: string;
  firstBar: string;
  lastBar: string;
  barCount: number;
}

const rowKey = (sid: string, sym: string, tf: string) => `${sid}|${sym}|${tf}`;

@Component({
  selector: 'app-new-backtest',
  standalone: true,
  imports: [FormsModule, DatePipe, NgClass],
  template: `
    <div class="space-y-4">
      <h1 class="text-xl font-semibold">New Backtest</h1>

      <div class="flex items-center gap-4 mb-1">
        <span class="text-xs text-gray-500">Load setup:</span>
        @for (s of savedSetups; track s.savedAt; let i = $index) {
          <button (click)="loadSetup(i)"
            class="text-xs text-gray-400 hover:text-gray-200 rounded border border-gray-700 px-2 py-0.5"
            [title]="s.startDate + ' to ' + s.endDate + ' · ' + (s.balance || 0)">
            {{ (s.savedAt | date:'MM-dd HH:mm') || 'Saved' }}
          </button>
        }
        @if (savedSetups.length === 0) {
          <span class="text-xs text-gray-600">none yet</span>
        }
      </div>

      <div class="grid grid-cols-1 gap-6 lg:grid-cols-3">
        <!-- LEFT PANE: strategy row builder -->
        <div class="lg:col-span-2 space-y-5">
          <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-5 space-y-5">
            <!-- Section: Data & Venue -->
            <fieldset>
              <legend class="mb-3 text-xs font-medium uppercase tracking-wide text-gray-500">Data & Venue</legend>
              <div class="grid grid-cols-2 gap-3">
                <div>
                  <label class="block text-xs font-medium text-gray-400 mb-1">Venue</label>
                  <select [(ngModel)]="venue" class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none">
                    <option value="replay">Stored-bar replay</option>
                    <option value="tape">Tape replay (fast, market data required)</option>
                    <option value="ctrader">cTrader (oracle)</option>
                  </select>
                </div>
                <div>
                  <label class="block text-xs font-medium text-gray-400 mb-1">Risk Profile</label>
                  <select [(ngModel)]="riskProfile" class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none">
                    @for (p of riskProfiles(); track p.id) {
                      <option [value]="p.id">{{ p.displayName }} ({{ (p.riskPerTradePercent * 100).toFixed(2) }}%/trade)</option>
                    }
                  </select>
                </div>
              </div>
            </fieldset>

            <!-- Section: Strategies -->
            <fieldset>
              <legend class="mb-3 text-xs font-medium uppercase tracking-wide text-gray-500">Strategies</legend>
              <div class="grid gap-2 md:grid-cols-2">
                @for (s of strategies(); track s.id) {
                  <label [attr.class]="stratClass(s.id)" (click)="toggleStrat(s.id)">
                    <input type="checkbox" [checked]="selectedStrategyIds().has(s.id)"
                      class="h-4 w-4 rounded border-gray-600 bg-gray-800 text-emerald-500 focus:ring-emerald-500" />
                    <div>
                      <div class="text-sm font-medium text-gray-200">{{ s.displayName }}</div>
                      <div class="text-xs text-gray-500">{{ s.entryRule || '—' }} · {{ s.exitFormula || '—' }}</div>
                    </div>
                  </label>
                }
              </div>
            </fieldset>

            <!-- Section: Symbols & Timeframes -->
            <fieldset>
              <legend class="mb-3 text-xs font-medium uppercase tracking-wide text-gray-500">Symbols & Timeframes</legend>
              <div class="grid grid-cols-1 gap-4 md:grid-cols-2">
                <div>
                  <label class="block text-xs font-medium text-gray-400 mb-2">Symbols</label>
                  <div class="flex flex-wrap gap-1.5">
                    @for (s of allSymbols; track s) {
                      <label [attr.class]="symClass(s)" (click)="toggleSymbol(s)">{{ s }}</label>
                    }
                  </div>
                </div>
                <div>
                  <label class="block text-xs font-medium text-gray-400 mb-2">Timeframes</label>
                  <div class="flex flex-wrap gap-1.5">
                    @for (tf of allTimeframes; track tf) {
                      <label [attr.class]="tfClass(tf)" (click)="togglePeriod(tf)">{{ tf }}</label>
                    }
                  </div>
                </div>
              </div>
            </fieldset>

            <!-- Section: Run Plan table -->
            <fieldset>
              <div class="mb-2 flex items-center justify-between">
                <legend class="text-xs font-medium uppercase tracking-wide text-gray-500">
                  Run Plan — {{ rows().length }} row{{ rows().length === 1 ? '' : 's' }} ({{ enabledCount() }} enabled)
                </legend>
                @if (rows().length > 0) {
                  <div class="flex gap-2">
                    <button (click)="setAllEnabled(true)" class="text-xs text-emerald-400 hover:underline">enable all</button>
                    <button (click)="setAllEnabled(false)" class="text-xs text-gray-400 hover:underline">disable all</button>
                  </div>
                }
              </div>
              @if (rows().length === 0) {
                <div class="rounded-md border border-dashed border-gray-700 p-4 text-center text-xs text-gray-500">
                  Pick at least one strategy, symbol and timeframe.
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
                              <option value="">Defaults</option>
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
            </fieldset>

            <!-- Section: Date Range -->
            <fieldset>
              <legend class="mb-3 text-xs font-medium uppercase tracking-wide text-gray-500">Date Range</legend>
              <div class="flex gap-2 mb-2">
                <button (click)="setDateRange(1)" class="rounded-md border border-gray-700 px-3 py-1 text-xs text-gray-300 hover:bg-gray-800">1M</button>
                <button (click)="setDateRange(3)" class="rounded-md border border-gray-700 px-3 py-1 text-xs text-gray-300 hover:bg-gray-800">3M</button>
                <button (click)="setDateRange(6)" class="rounded-md border border-gray-700 px-3 py-1 text-xs text-gray-300 hover:bg-gray-800">6M</button>
                <button (click)="setDateRange(12)" class="rounded-md border border-gray-700 px-3 py-1 text-xs text-gray-300 hover:bg-gray-800">1Y</button>
              </div>
              <div class="grid grid-cols-2 gap-3">
                <input type="date" [(ngModel)]="startDate" class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none" />
                <input type="date" [(ngModel)]="endDate" class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none" />
              </div>
            </fieldset>
          </div>
        </div>

        <!-- RIGHT PANE: sticky summary -->
        <div class="lg:col-span-1">
          <div class="sticky top-6 space-y-4 rounded-lg border border-gray-800 bg-gray-900/50 p-5">
            <!-- Data Coverage -->
            <div>
              <h3 class="mb-2 text-xs font-medium uppercase tracking-wide text-gray-500">Data Coverage</h3>
              @if (coverageChecked()) {
                <div class="space-y-1 text-xs">
                  @for (c of coverageRows(); track c.key) {
                    <div class="flex items-center justify-between rounded px-2 py-1" [ngClass]="{ 'bg-gray-800/30': c.key.startsWith('\u2500') }">
                      <span class="text-gray-300">{{ c.label }}</span>
                      <span>
                        @if (c.main) {
                          <span class="text-emerald-400" title="Decision-bar data available">✓ decision</span>
                        } @else {
                          <span class="text-red-400" title="No decision-bar data">✗ decision</span>
                        }
                        @if (c.m1 !== null) {
                          <span class="ml-1">
                            @if (c.m1) {
                              <span class="text-emerald-400" title="M1 fine-bars available for wick fidelity">· M1</span>
                            } @else {
                              <span class="text-amber-400" title="No M1; exit resolution will be single-bar">· no M1</span>
                            }
                          </span>
                        }
                      </span>
                    </div>
                  }
                  @if (missingData().length > 0) {
                    <div class="mt-2 rounded bg-amber-900/20 p-2 text-amber-400">
                      Missing data for {{ missingData().length }} selection(s).
                      <a routerLink="/data-manager" class="ml-1 underline hover:text-amber-300">Download data</a>
                    </div>
                  }
                </div>
              } @else {
                <p class="text-xs text-gray-600">Pick symbols & timeframes to check</p>
              }
            </div>

            <hr class="border-gray-800" />

            <!-- Money -->
            <div>
              <h3 class="mb-2 text-xs font-medium uppercase tracking-wide text-gray-500">Money</h3>
              <div class="space-y-2">
                <div>
                  <label class="block text-xs text-gray-400 mb-0.5">Initial Balance <span class="text-gray-600">$</span></label>
                  <input type="number" [(ngModel)]="balance" class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none" />
                </div>
                <div class="grid grid-cols-2 gap-2">
                  <div>
                    <label class="block text-xs text-gray-400 mb-0.5">Commission <span class="text-gray-600">/M $</span></label>
                    <input type="number" [(ngModel)]="commission" class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none" />
                  </div>
                  <div>
                    <label class="block text-xs text-gray-400 mb-0.5">Spread <span class="text-gray-600">pips</span></label>
                    <input type="number" [(ngModel)]="spread" step="0.1" class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none" />
                  </div>
                </div>
              </div>
            </div>

            <hr class="border-gray-800" />

            <!-- Protections -->
            <div>
              <h3 class="mb-2 text-xs font-medium uppercase tracking-wide text-gray-500">Protections</h3>
              <div class="flex flex-wrap gap-1.5">
                <button (click)="toggleProtection('governor')" [class]="protChipClass(governorEnabled)">Governor</button>
                <button (click)="toggleProtection('dailyDd')" [class]="protChipClass(dailyDdEnabled)">Daily DD</button>
                <button (click)="toggleProtection('maxDd')" [class]="protChipClass(maxDdEnabled)">Max DD</button>
                <button (click)="toggleProtection('forceClose')" [class]="protChipClass(forceCloseEnabled)">Force Close</button>
                <button (click)="toggleProtection('regime')" [class]="protChipClass(regimeEnabled)">Regime</button>
                <button (click)="toggleProtection('stripAddOns')" [class]="protChipClass(stripAddOns) + ' border-amber-700 text-amber-400'">Raw SL/TP</button>
              </div>
              <button (click)="toggleAllProtections()" class="mt-2 text-xs text-gray-500 hover:text-gray-300">
                {{ allProtectionsOn() ? 'Turn all off' : 'Turn all on' }}
              </button>
            </div>

            @if (venue === 'ctrader') {
              <div class="rounded-md bg-amber-900/20 p-2 text-xs text-amber-400">
                cTrader runs first row only (one CLI session).
              </div>
            }
            @if (venue === 'tape') {
              <div class="rounded-md bg-blue-900/20 p-2 text-xs text-blue-400">
                Tape replay runs in-process against downloaded market data.
              </div>
            }

            @if (error()) {
              <div class="rounded-md bg-red-900/20 p-3 text-sm text-red-400">{{ error() }}</div>
            }

            <button (click)="start()" [disabled]="loading() || enabledCount() === 0"
              class="w-full rounded-md bg-emerald-600 px-4 py-3 text-sm font-medium text-white hover:bg-emerald-500 disabled:opacity-50">
              {{ loading() ? 'Starting...' : 'Start Backtest (' + enabledCount() + ' rows)' }}
            </button>
          </div>
        </div>
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
  private http = inject(HttpClient);

  allSymbols = ALL_SYMBOLS;
  allTimeframes = ALL_TIMEFRAMES;

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
  stripAddOns = false;

  packs = signal<{ id: string; name: string }[]>([]);
  strategies = signal<StrategySummary[]>([]);
  riskProfiles = signal<RiskProfile[]>([
    { id: 'standard', displayName: 'Standard', riskPerTradePercent: 0.005, maxDailyDrawdownPercent: 0.05, maxTotalDrawdownPercent: 0.1, maxConcurrentPositions: 5, lotSizingMethod: 'PercentRisk', propFirmRuleSetId: 'ftmo-standard' },
  ]);
  loading = this.store.isLoading;
  error = this.store.error;

  inventory = signal<InventoryEntry[]>([]);
  coverageChecked = computed(() => this.selectedSymbols().size > 0 && this.selectedPeriods().size > 0);

  coverageRows = computed(() => {
    const rows: { key: string; label: string; main: boolean; m1: boolean | null }[] = [];
    const inv = this.inventory();
    const startD = this.startDate ? new Date(this.startDate) : null;
    const endD = this.endDate ? new Date(this.endDate) : null;

    for (const sym of this.selectedSymbols()) {
      for (const tf of this.selectedPeriods()) {
        const symUpper = sym.toUpperCase();
        const tfUpper = tf.toUpperCase();
        const key = `${symUpper} ${tfUpper}`;
        const match = inv.find(i => i.symbol === symUpper && i.timeframe === tfUpper);

        let hasMain = false;
        if (match && startD && endD && match.firstBar && match.lastBar) {
          const first = new Date(match.firstBar);
          const last = new Date(match.lastBar);
          hasMain = first <= startD && last >= endD;
        }

        let hasM1: boolean | null = null;
        if (tfUpper !== 'M1') {
          const m1match = inv.find(i => i.symbol === symUpper && i.timeframe === 'M1');
          if (m1match && startD && endD && m1match.firstBar && m1match.lastBar) {
            const first = new Date(m1match.firstBar);
            const last = new Date(m1match.lastBar);
            hasM1 = first <= startD && last >= endD;
          } else {
            hasM1 = false;
          }
        }

        rows.push({ key, label: key, main: hasMain, m1: hasM1 });
      }
    }
    return rows;
  });

  missingData = computed(() => this.coverageRows().filter(r => !r.main));

  allProtectionsOn = computed(() =>
    this.governorEnabled && this.dailyDdEnabled && this.maxDdEnabled && this.forceCloseEnabled && this.regimeEnabled
  );

  rowKeyOf = (r: BuilderRow) => rowKey(r.strategyId, r.symbol, r.timeframe);

  constructor() {
    const now = new Date();
    this.endDate = now.toISOString().slice(0, 10);
    const d = new Date(now);
    d.setMonth(d.getMonth() - 1);
    this.startDate = d.toISOString().slice(0, 10);
  }

  async ngOnInit(): Promise<void> {
    try { this.strategies.set(await this.strategiesApi.getAll()); } catch { /* */ }
    try {
      const profiles = await this.profilesApi.getAll();
      if (profiles.length > 0) { this.riskProfiles.set(profiles); if (!profiles.some((p) => p.id === this.riskProfile)) this.riskProfile = profiles[0].id; }
    } catch { /* */ }
    try {
      const pks = await this.packsApi.getAll();
      this.packs.set(pks.map((p) => ({ id: p.id, name: p.name })));
    } catch { /* */ }
    try {
      const inv = await firstValueFrom(this.http.get<InventoryEntry[]>('/api/data-manager/inventory'));
      this.inventory.set(inv ?? []);
    } catch { /* */ }

    const sourceRunId = this.route.snapshot.queryParamMap.get('sourceRunId');
    if (sourceRunId) {
      try {
        const src = await this.runsApi.getRun(sourceRunId);
        if (src) {
          this.startDate = (src.backtestFrom || '').slice(0, 10);
          this.endDate = (src.backtestTo || '').slice(0, 10);
          this.balance = src.initialBalance || 100000;
          const parse = (v: unknown): string[] => { try { const a = typeof v === 'string' ? JSON.parse(v) : v; return Array.isArray(a) ? a : []; } catch { return []; } };
          const syms = parse(src.symbols);
          const pers = parse(src.periods);
          if (syms.length) this.selectedSymbols.set(new Set(syms));
          if (pers.length) this.selectedPeriods.set(new Set(pers.map((p) => p.toLowerCase())));
          this.regenerate();
        }
      } catch { /* */ }
    }
  }

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

  setRowEnabled(row: BuilderRow, enabled: boolean): void { this.rows.update((rs) => rs.map((r) => (this.rowKeyOf(r) === this.rowKeyOf(row) ? { ...r, enabled } : r))); }
  setRowPack(row: BuilderRow, packId: string): void { this.rows.update((rs) => rs.map((r) => (this.rowKeyOf(r) === this.rowKeyOf(row) ? { ...r, packId } : r))); }
  setAllEnabled(enabled: boolean): void { this.rows.update((rs) => rs.map((r) => ({ ...r, enabled }))); }

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

  protChipClass(on: boolean): string {
    return 'cursor-pointer rounded-md border px-2.5 py-1 text-xs font-medium transition ' + (on ? 'border-emerald-500 bg-emerald-900/20 text-emerald-400' : 'border-gray-700 text-gray-500 hover:text-gray-300');
  }

  toggleProtection(p: string): void {
    switch (p) {
      case 'governor': this.governorEnabled = !this.governorEnabled; break;
      case 'dailyDd': this.dailyDdEnabled = !this.dailyDdEnabled; break;
      case 'maxDd': this.maxDdEnabled = !this.maxDdEnabled; break;
      case 'forceClose': this.forceCloseEnabled = !this.forceCloseEnabled; break;
      case 'regime': this.regimeEnabled = !this.regimeEnabled; break;
      case 'stripAddOns': this.stripAddOns = !this.stripAddOns; break;
    }
  }

  toggleAllProtections(): void {
    const all = this.allProtectionsOn();
    this.governorEnabled = !all;
    this.dailyDdEnabled = !all;
    this.maxDdEnabled = !all;
    this.forceCloseEnabled = !all;
    this.regimeEnabled = !all;
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
    if (enabled.length === 0) { this.error.set('No enabled rows.'); return; }
    if (!this.startDate || !this.endDate) { this.error.set('Select start and end dates'); return; }
    if (new Date(this.startDate) >= new Date(this.endDate)) { this.error.set('Start date must be before end date'); return; }
    if (!this.balance || this.balance <= 0) { this.error.set('Balance must be greater than 0'); return; }
    this.error.set(null);

    const rows: RunRow[] = enabled.map((r) => ({
      strategyId: r.strategyId, symbol: r.symbol, timeframe: r.timeframe, packId: r.packId || undefined, enabled: true,
    }));

    const req: StartRunRequest = {
      start: this.startDate, end: this.endDate, balance: this.balance,
      commissionPerMillion: this.commission, spreadPips: this.spread,
      symbols: [...new Set(enabled.map((r) => r.symbol))],
      periods: [...new Set(enabled.map((r) => r.timeframe.toLowerCase()))],
      strategyIds: [...new Set(enabled.map((r) => r.strategyId))],
      riskProfileId: this.riskProfile, venue: this.venue, rows,
      governorEnabled: this.governorEnabled, dailyDdEnabled: this.dailyDdEnabled,
      maxDdEnabled: this.maxDdEnabled, forceCloseOnBreachEnabled: this.forceCloseEnabled,
      disableRegime: this.regimeEnabled ? undefined : true,
      stripAddOns: this.stripAddOns ? true : undefined,
    };
    this.saveSetup();
    const runId = await this.store.startBacktest(req);
    this.router.navigate(['/runs', runId, 'monitor']);
  }

  private readonly SETUP_STORAGE_KEY = 'shamshir-backtest-setups';

  private saveSetup(): void {
    try {
      const setup = {
        startDate: this.startDate, endDate: this.endDate, balance: this.balance,
        commission: this.commission, spread: this.spread, riskProfile: this.riskProfile,
        venue: this.venue, governorEnabled: this.governorEnabled,
        dailyDdEnabled: this.dailyDdEnabled, maxDdEnabled: this.maxDdEnabled,
        forceCloseEnabled: this.forceCloseEnabled, regimeEnabled: this.regimeEnabled,
        stripAddOns: this.stripAddOns,
        strategies: [...this.selectedStrategyIds()], symbols: [...this.selectedSymbols()],
        periods: [...this.selectedPeriods()], savedAt: Date.now(),
      };
      const existing = JSON.parse(localStorage.getItem(this.SETUP_STORAGE_KEY) || '[]');
      existing.unshift(setup);
      localStorage.setItem(this.SETUP_STORAGE_KEY, JSON.stringify(existing.slice(0, 5)));
    } catch { /* */ }
  }

  loadSetup(index: number): void {
    try {
      const existing = JSON.parse(localStorage.getItem(this.SETUP_STORAGE_KEY) || '[]');
      const setup = existing[index];
      if (!setup) return;
      this.startDate = setup.startDate || this.startDate;
      this.endDate = setup.endDate || this.endDate;
      this.balance = setup.balance ?? this.balance;
      this.commission = setup.commission ?? this.commission;
      this.spread = setup.spread ?? this.spread;
      this.riskProfile = setup.riskProfile || this.riskProfile;
      this.venue = setup.venue || this.venue;
      this.governorEnabled = setup.governorEnabled ?? this.governorEnabled;
      this.dailyDdEnabled = setup.dailyDdEnabled ?? this.dailyDdEnabled;
      this.maxDdEnabled = setup.maxDdEnabled ?? this.maxDdEnabled;
      this.forceCloseEnabled = setup.forceCloseEnabled ?? this.forceCloseEnabled;
      this.regimeEnabled = setup.regimeEnabled ?? this.regimeEnabled;
      this.stripAddOns = setup.stripAddOns ?? this.stripAddOns;
      if (setup.strategies?.length) this.selectedStrategyIds.set(new Set(setup.strategies));
      if (setup.symbols?.length) this.selectedSymbols.set(new Set(setup.symbols));
      if (setup.periods?.length) this.selectedPeriods.set(new Set(setup.periods));
      this.regenerate();
    } catch { /* */ }
  }

  get savedSetups(): any[] {
    try { return JSON.parse(localStorage.getItem(this.SETUP_STORAGE_KEY) || '[]'); } catch { return []; }
  }
}
