import { Component, computed, inject, OnInit, signal, ChangeDetectionStrategy, type WritableSignal } from '@angular/core';
import { Router, ActivatedRoute } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { RunsStore } from '../runs.store';
import type { StrategySummary, StartRunRequest, RiskProfile, RunRow, InventoryItem } from '../../../models/api.types';
import { StrategiesApiService } from '../../strategies/strategies.service';
import { RiskProfilesApiService } from '../../risk-profiles/risk-profiles.service';
import { AddOnPacksApiService } from '../../addon-packs/addon-packs.service';
import { RunsApiService } from '../runs.service';

const ALL_SYMBOLS = [
  'EURUSD', 'GBPUSD', 'USDJPY', 'GBPJPY', 'XAUUSD', 'AUDUSD',
  'USDCHF', 'USDCAD', 'NZDUSD', 'EURGBP', 'EURJPY', 'XAGUSD',
  'BTCUSD', 'ETHUSD', 'US30', 'NAS100',
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

interface CoverageInfo {
  decisionTf: boolean;
  m1: boolean;
}

@Component({
  selector: 'app-new-backtest',
  standalone: true,
  imports: [FormsModule, DatePipe],
  template: `
    <div class="space-y-4">
      <div class="flex items-center justify-between">
        <h1 class="text-xl font-semibold">New Backtest</h1>
        <div class="flex items-center gap-2">
          <button (click)="reuseLastParams()" [disabled]="prefilling()"
            class="rounded border border-emerald-800 px-2 py-0.5 text-xs text-emerald-400 hover:bg-emerald-900/20 disabled:opacity-50"
            title="Prefill the form with the most recent run's parameters">
            {{ prefilling() ? 'Loading…' : '⟲ Reuse last params' }}
          </button>
          @if (copiedFrom()) {
            <span class="rounded border border-gray-700 px-2 py-0.5 text-xs text-gray-400">
              copied from {{ copiedFrom()!.slice(0, 8) }}
            </span>
          }
          @for (s of savedSetups; track s.name + s.savedAt; let i = $index) {
            <button (click)="loadSetup(i)"
              class="text-xs text-gray-400 hover:text-gray-200 rounded border border-gray-700 px-2 py-0.5"
              [title]="s.startDate + ' to ' + s.endDate + ' \u00b7 ' + (s.balance || 0)">
              {{ s.name || ('#' + (i + 1)) }}
            </button>
          }
          @if (savedSetups.length === 0) {
            <span class="text-xs text-gray-600">no saved setups</span>
          }
        </div>
      </div>

      <div class="flex gap-6">
        <!-- LEFT: strategy row builder -->
        <div class="flex-1 min-w-0 space-y-5">
          <!-- Strategies -->
          <section>
            <label class="block text-xs font-medium text-gray-400 mb-2">Strategies</label>
            <div class="grid gap-2 md:grid-cols-2 xl:grid-cols-3">
              @for (s of strategies(); track s.id) {
                <label [attr.class]="stratClass(s.id)" (click)="toggleStrat(s.id)">
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
          </section>

          <!-- Symbols + Timeframes -->
          <div class="grid grid-cols-2 gap-4">
            <div>
              <label class="block text-xs font-medium text-gray-400 mb-2">Symbols</label>
              <div class="flex flex-wrap gap-1.5">
                @for (s of allSymbols; track s) {
                  <button (click)="toggleSymbol(s)" [attr.class]="symClass(s)">{{ s }}</button>
                }
              </div>
            </div>
            <div>
              <label class="block text-xs font-medium text-gray-400 mb-2">Timeframes</label>
              <div class="flex flex-wrap gap-1.5">
                @for (tf of allTimeframes; track tf) {
                  <button (click)="togglePeriod(tf)" [attr.class]="tfClass(tf)">{{ tf }}</button>
                }
              </div>
            </div>
          </div>

          <!-- Run plan table -->
          <section>
            <div class="mb-2 flex items-center justify-between">
              <label class="text-xs font-medium text-gray-400">
                Run plan &mdash; {{ rows().length }} row{{ rows().length === 1 ? '' : 's' }} ({{ enabledCount() }} enabled)
              </label>
              @if (rows().length > 0) {
                <div class="flex gap-2">
                  <button (click)="setAllEnabled(true)" class="text-xs text-emerald-400 hover:underline">enable all</button>
                  <button (click)="setAllEnabled(false)" class="text-xs text-gray-400 hover:underline">disable all</button>
                </div>
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
                      <th class="px-3 py-2 text-left font-medium w-10">On</th>
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
                        <td class="px-3 py-1.5 text-gray-200 font-mono text-xs">{{ r.strategyId }}</td>
                        <td class="px-3 py-1.5 text-gray-300">{{ r.symbol }}</td>
                        <td class="px-3 py-1.5 uppercase text-gray-300">{{ r.timeframe }}</td>
                        <td class="px-3 py-1.5">
                          <select [ngModel]="r.packId" (ngModelChange)="setRowPack(r, $event)"
                            class="w-full rounded border border-gray-700 bg-gray-800 px-2 py-1 text-xs text-gray-200">
                            <option value="">Strategy defaults</option>
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
          </section>
        </div>

        <!-- RIGHT: sticky summary panel -->
        <div class="w-80 shrink-0">
          <div class="sticky top-6 rounded-lg border border-gray-800 bg-gray-900/50 p-5 space-y-5">
            <!-- Data & Venue -->
            <section>
              <h3 class="text-xs font-semibold text-gray-400 uppercase tracking-wider mb-3">Data &amp; Venue</h3>
              <div class="space-y-3">
                <div>
                  <label class="block text-xs text-gray-500 mb-1">Date Range</label>
                  <div class="flex gap-1.5 mb-2">
                    <button (click)="setDateRange(1)" class="rounded border border-gray-700 px-2 py-0.5 text-xs text-gray-400 hover:bg-gray-800">1M</button>
                    <button (click)="setDateRange(3)" class="rounded border border-gray-700 px-2 py-0.5 text-xs text-gray-400 hover:bg-gray-800">3M</button>
                    <button (click)="setDateRange(12)" class="rounded border border-gray-700 px-2 py-0.5 text-xs text-gray-400 hover:bg-gray-800">1Y</button>
                  </div>
                  <div class="space-y-1.5">
                    <input type="date" [ngModel]="startDate()" (ngModelChange)="startDate.set($event)" class="w-full rounded border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none" />
                    <input type="date" [ngModel]="endDate()" (ngModelChange)="endDate.set($event)" class="w-full rounded border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none" />
                  </div>
                </div>
                <div>
                  <label class="block text-xs text-gray-500 mb-1">Venue</label>
                  <select [ngModel]="venue()" (ngModelChange)="venue.set($event)" class="w-full rounded border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none">
                    <option value="replay">Stored-bar replay (deterministic)</option>
                    <option value="tape">Fast tape (in-process)</option>
                    <option value="ctrader">cTrader forward-test</option>
                  </select>
                </div>

                @if (venue() === 'tape') {
                  <div>
                    <label class="block text-xs text-gray-500 mb-1">Playback Speed</label>
                    <div class="flex items-center gap-2">
                      <input type="range" [ngModel]="speed()" (ngModelChange)="speed.set($event)" min="0" max="10" step="0.5" class="flex-1 accent-emerald-500" />
                      <span class="text-xs font-mono text-gray-300 w-10 text-right">{{ speed() === 0 ? 'Paused' : speed() + '×' }}</span>
                    </div>
                    <div class="flex justify-between text-xs text-gray-600 mt-0.5">
                      <span>Paused</span><span>1×</span><span>5×</span><span>Max</span>
                    </div>
                  </div>
                }

                <!-- Coverage check -->
                @if (venue() === 'tape' && rows().length > 0) {
                  <div class="rounded border border-gray-700 p-2 space-y-1">
                    <h4 class="text-xs font-medium text-gray-400">Data Coverage</h4>
                    @if (safeRange()) {
                      <button (click)="startDate.set(safeRange()!.from); endDate.set(safeRange()!.to)"
                        class="text-xs text-emerald-400 hover:underline mb-1">
                        Snap to available: {{ safeRange()!.from }} – {{ safeRange()!.to }}
                      </button>
                    }
                    @for (cov of coverageIssues(); track cov.key) {
                      <div class="flex items-center gap-1.5 text-xs">
                        <span [class.text-emerald-400]="cov.info.decisionTf" [class.text-red-400]="!cov.info.decisionTf">
                          {{ cov.info.decisionTf ? '\u2713' : '\u2717' }}
                        </span>
                        <span class="text-gray-300">{{ cov.symbol }} {{ cov.tf }}</span>
                        @if (cov.firstBar && cov.lastBar) {
                          <span class="text-gray-600">{{ cov.firstBar | date:'yyyy-MM-dd' }} – {{ cov.lastBar | date:'yyyy-MM-dd' }}</span>
                        }
                        @if (!cov.info.decisionTf) {
                          <a routerLink="/data-manager" class="ml-auto text-xs text-emerald-400 hover:underline">download</a>
                        }
                      </div>
                    }
                    @for (cov of coverageIssues(); track cov.key) {
                      @if (!cov.info.m1) {
                        <div class="flex items-center gap-1.5 text-xs text-amber-400">
                          <span>\u26A0</span>
                          <span class="text-amber-300">{{ cov.symbol }} m1</span>
                          <span class="text-amber-500">missing (exit-fidelity fallback)</span>
                        </div>
                      }
                    }
                    @if (coverageIssues().length === 0) {
                      <span class="text-xs text-gray-500">Select symbols and timeframes</span>
                    }
                  </div>
                }
              </div>
            </section>

            <!-- Money -->
            <section>
              <h3 class="text-xs font-semibold text-gray-400 uppercase tracking-wider mb-3">Money</h3>
              <div class="space-y-2">
                <div>
                  <label class="block text-xs text-gray-500 mb-1">Initial Balance</label>
                  <div class="relative">
                    <span class="absolute left-2 top-1/2 -translate-y-1/2 text-xs text-gray-500">$</span>
                    <input type="number" [ngModel]="balance()" (ngModelChange)="balance.set($event)" class="w-full rounded border border-gray-700 bg-gray-800 pl-4 pr-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none" />
                  </div>
                </div>
                <div>
                  <label class="block text-xs text-gray-500 mb-1">Commission</label>
                  <div class="relative">
                    <input type="number" [ngModel]="commission()" (ngModelChange)="commission.set($event)" class="w-full rounded border border-gray-700 bg-gray-800 pr-10 pl-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none" />
                    <span class="absolute right-2 top-1/2 -translate-y-1/2 text-xs text-gray-500">/M</span>
                  </div>
                </div>
                <div>
                  <label class="block text-xs text-gray-500 mb-1">Spread</label>
                  <div class="relative">
                    <input type="number" [ngModel]="spread()" (ngModelChange)="spread.set($event)" step="0.1" class="w-full rounded border border-gray-700 bg-gray-800 pr-10 pl-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none" />
                    <span class="absolute right-2 top-1/2 -translate-y-1/2 text-xs text-gray-500">pips</span>
                  </div>
                </div>
                <div>
                  <label class="block text-xs text-gray-500 mb-1">Risk Profile</label>
                  <select [ngModel]="riskProfile()" (ngModelChange)="riskProfile.set($event)" class="w-full rounded border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none">
                    @for (p of riskProfiles(); track p.id) {
                      <option [value]="p.id">{{ p.displayName }}</option>
                    }
                  </select>
                </div>
              </div>
            </section>

            <!-- Protections -->
            <section>
              <h3 class="text-xs font-semibold text-gray-400 uppercase tracking-wider mb-3">Protections</h3>
              <div class="flex flex-wrap gap-1.5">
                <button (click)="governorEnabled.set(!governorEnabled())"
                  [attr.class]="chipClass(governorEnabled())"
                  title="Governor: cooling-off, profit-lock, streak management">Governor</button>
                <button (click)="dailyDdEnabled.set(!dailyDdEnabled())"
                  [attr.class]="chipClass(dailyDdEnabled())"
                  title="Daily drawdown protection">Daily DD</button>
                <button (click)="maxDdEnabled.set(!maxDdEnabled())"
                  [attr.class]="chipClass(maxDdEnabled())"
                  title="Max drawdown protection">Max DD</button>
                <button (click)="forceCloseEnabled.set(!forceCloseEnabled())"
                  [attr.class]="chipClass(forceCloseEnabled())"
                  title="Force close all positions on breach">Force Close</button>
                <button (click)="regimeEnabled.set(!regimeEnabled())"
                  [attr.class]="chipClass(regimeEnabled())"
                  title="Regime detection filter">Regime</button>
                <button (click)="toggleAllRisk()"
                  [attr.class]="chipClass(allRiskEnabled()) + ' font-mono text-xs'"
                  title="Toggle all protections on/off">{{ allRiskEnabled() ? '\u2713 all on' : '\u2717 all off' }}</button>
              </div>
            <label class="mt-2 flex items-center gap-2 text-xs text-amber-400 cursor-pointer">
              <input type="checkbox" [ngModel]="stripAddOns()" (ngModelChange)="stripAddOns.set($event)" class="h-3 w-3 rounded" />
              No add-ons (raw baseline SL/TP)
            </label>
            <label class="mt-2 flex items-center gap-2 text-xs text-gray-300 cursor-pointer"
              title="Market entries fill at next bar's open (more realistic); off = fill at signal bar close (optimistic)">
              <input type="checkbox" [ngModel]="honestFills()" (ngModelChange)="honestFills.set($event)" class="h-3 w-3 rounded" />
              Honest fills (next-bar open)
            </label>
            <label class="mt-2 flex items-center gap-2 text-xs text-gray-300 cursor-pointer"
              title="Record per-trade MAE/MFE excursion paths for exit-lab analysis (tape-only)">
              <input type="checkbox" [ngModel]="recordExcursions()" (ngModelChange)="recordExcursions.set($event)" class="h-3 w-3 rounded" />
              Record excursions (MAE/MFE path)
            </label>
            @if (venue() === 'tape') {
              <label class="mt-2 flex items-center gap-2 text-xs text-amber-300 cursor-pointer"
                title="Run the same config on both tape and cTrader venues side-by-side. Results include a reconciliation diff.">
                <input type="checkbox" [ngModel]="compareBoth()" (ngModelChange)="compareBoth.set($event)" class="h-3 w-3 rounded" />
                Compare vs cTrader (dual-venue)
              </label>
            }
            <button (click)="applyExplorationPreset()"
              [class]="chipClass(explorationMode()) + ' w-full mt-2'"
              title="SL=ATR×4, TP=none, no add-ons, governor off, record excursions — the entry signal runs bare for exit-lab calibration">
              {{ explorationMode() ? '\u2713 Exploration Mode' : 'Exploration Mode (ATR×4 SL, no TP, no add-ons)' }}
            </button>
            </section>

            <!-- Venue warnings -->
            @if (venue() === 'ctrader') {
              <div class="rounded bg-amber-900/20 px-3 py-2 text-xs text-amber-400">
                cTrader runs the first row only. Use replay or tape for multi-row plans.
              </div>
            }
            @if (venue() === 'tape' && missingCoverage().length > 0) {
              <div class="rounded bg-red-900/20 px-3 py-2 text-xs text-red-400">
                Missing data for {{ missingCoverage().join(', ') }}. Download in Data Manager first.
              </div>
            }

            @if (error()) {
              <div class="rounded bg-red-900/20 px-3 py-2 text-xs text-red-400">{{ error() }}</div>
            }

            <!-- Start button -->
            <div class="space-y-1.5">
              <button (click)="start()" [disabled]="!canStart() || starting()"
                class="w-full rounded-md bg-emerald-600 px-4 py-2.5 text-sm font-medium text-white hover:bg-emerald-500 disabled:cursor-not-allowed disabled:opacity-40">
                {{ starting() ? 'Starting...' : 'Start Backtest (' + enabledCount() + ' rows)' }}
              </button>
              @if (startDisabledReason(); as reason) {
                <p class="text-xs text-gray-500 text-center">{{ reason }}</p>
              }
              <div class="flex gap-1">
                <input
                  #nameInput
                  type="text"
                  placeholder="Setup name (optional)"
                  class="flex-1 rounded border border-gray-700 bg-gray-800 px-2 py-1 text-xs text-gray-100 placeholder-gray-500 focus:border-emerald-500 focus:outline-none"
                  (keydown.enter)="saveNamedSetup(nameInput.value); nameInput.value = ''"
                />
                <button
                  (click)="saveNamedSetup(nameInput.value); nameInput.value = ''"
                  class="rounded border border-gray-700 px-2 py-1 text-xs text-gray-400 hover:text-white hover:bg-gray-800"
                >
                  Save as
                </button>
              </div>
            </div>
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

  startDate = signal('');
  endDate = signal('');
  balance = signal(100_000);
  commission = signal(30);
  spread = signal(1);
  riskProfile = signal('standard');
  venue = signal('replay');
  speed = signal(10);
  governorEnabled = signal(true);
  dailyDdEnabled = signal(true);
  maxDdEnabled = signal(true);
  forceCloseEnabled = signal(true);
  regimeEnabled = signal(true);
  stripAddOns = signal(false);
  honestFills = signal(true);
  recordExcursions = signal(false);
  explorationMode = signal(false);
  compareBoth = signal(false);

  packs = signal<{ id: string; name: string }[]>([]);
  strategies = signal<StrategySummary[]>([]);
  riskProfiles = signal<RiskProfile[]>([
    { id: 'standard', displayName: 'Standard', riskPerTradePercent: 0.005, maxDailyDrawdownPercent: 0.05, maxTotalDrawdownPercent: 0.1, maxConcurrentPositions: 5, lotSizingMethod: 'PercentRisk', propFirmRuleSetId: 'ftmo-standard' },
  ]);
  loading = this.store.isLoading;
  error = this.store.error;
  starting = signal(false);

  inventory = signal<InventoryItem[]>([]);

  coverageMap = computed(() => {
    const inv = this.inventory();
    const map = new Map<string, { decisionTf: boolean; m1: boolean }>();
    const from = this.startDate() ? new Date(this.startDate() + 'T00:00:00').getTime() : 0;
    const to = this.endDate() ? new Date(this.endDate() + 'T00:00:00').getTime() : 0;

    for (const item of inv) {
      const key = `${item.symbol}|${item.timeframe.toLowerCase()}`;
      const itemFrom = new Date(item.firstBar.slice(0, 10) + 'T00:00:00').getTime();
      const itemTo = new Date(item.lastBar.slice(0, 10) + 'T00:00:00').getTime();
      const covers = itemFrom <= from && itemTo >= to;
      const existing = map.get(key);
      if (existing) {
        map.set(key, {
          decisionTf: existing.decisionTf || covers,
          m1: existing.m1 || covers,
        });
      } else {
        map.set(key, { decisionTf: covers, m1: covers });
      }
    }
    return map;
  });

  coverageIssues = computed(() => {
    const map = this.coverageMap();
    const inv = this.inventory();
    const issues: { key: string; symbol: string; tf: string; info: CoverageInfo; firstBar?: string; lastBar?: string }[] = [];
    const seen = new Set<string>();
    for (const r of this.rows()) {
      if (!r.enabled) continue;
      const tk = `${r.symbol}|${r.timeframe.toLowerCase()}`;
      if (seen.has(tk)) continue;
      seen.add(tk);
      const cov = map.get(tk) ?? { decisionTf: false, m1: false };
      const m1Key = `${r.symbol}|m1`;
      const m1Cov = map.get(m1Key) ?? { decisionTf: false, m1: false };
      const invEntry = inv.find(i => i.symbol === r.symbol && i.timeframe.toLowerCase() === r.timeframe.toLowerCase());
      issues.push({
        key: tk, symbol: r.symbol, tf: r.timeframe,
        info: { decisionTf: cov.decisionTf, m1: m1Cov.decisionTf },
        firstBar: invEntry?.firstBar,
        lastBar: invEntry?.lastBar,
      });
    }
    return issues;
  });

  missingCoverage = computed(() => {
    return this.coverageIssues()
      .filter((c) => !c.info.decisionTf)
      .map((c) => `${c.symbol} ${c.tf}`);
  });

  canStart = computed(() => {
    return this.enabledCount() > 0 && !!this.startDate() && !!this.endDate()
      && new Date(this.startDate()) < new Date(this.endDate())
      && this.balance() > 0
      && (this.venue() !== 'tape' || this.missingCoverage().length === 0);
  });

  safeRange = computed(() => {
    if (this.venue() !== 'tape') return null;
    const inv = this.inventory();
    if (!inv.length || !this.startDate() || !this.endDate()) return null;
    const from = new Date(this.startDate() + 'T00:00:00').getTime();
    const to = new Date(this.endDate() + 'T00:00:00').getTime();
    let maxFirst = 0;
    let minLast = Infinity;
    let found = false;
    for (const r of this.rows()) {
      if (!r.enabled) continue;
      const item = inv.find(i => i.symbol === r.symbol && i.timeframe.toLowerCase() === r.timeframe.toLowerCase());
      if (!item) return null;
      const itemFrom = new Date(item.firstBar.slice(0, 10) + 'T00:00:00').getTime();
      const itemTo = new Date(item.lastBar.slice(0, 10) + 'T00:00:00').getTime();
      if (itemFrom > from || itemTo < to) return null;
      if (itemFrom > maxFirst) maxFirst = itemFrom;
      if (itemTo < minLast) minLast = itemTo;
      found = true;
    }
    if (!found || maxFirst === 0 || minLast === Infinity) return null;
    return {
      from: new Date(maxFirst).toISOString().slice(0, 10),
      to: new Date(minLast).toISOString().slice(0, 10),
    };
  });

  startDisabledReason = computed(() => {
    if (this.enabledCount() === 0) return 'No rows enabled';
    if (!this.startDate() || !this.endDate()) return 'Select start and end dates';
    if (new Date(this.startDate()) >= new Date(this.endDate())) return 'Start date must be before end date';
    if (!this.balance() || this.balance() <= 0) return 'Balance must be greater than 0';
    if (this.venue() === 'tape' && this.missingCoverage().length > 0) return 'Data coverage missing';
    return null;
  });

  rowKeyOf = (r: BuilderRow) => rowKey(r.strategyId, r.symbol, r.timeframe);

  constructor() {
    const now = new Date();
    this.endDate.set(now.toISOString().slice(0, 10));
    const d = new Date(now);
    d.setMonth(d.getMonth() - 1);
    this.startDate.set(d.toISOString().slice(0, 10));
  }

  async ngOnInit(): Promise<void> {
    // X2: prefill FIRST — it depends on none of the lookups below, and the inventory endpoint can
    // take >10s on a cold cache (F49 scan); the old sequential order made copy-run appear dead.
    const copyFrom = this.route.snapshot.queryParamMap.get('copyFrom')
      ?? this.route.snapshot.queryParamMap.get('sourceRunId');
    const prefillDone = copyFrom ? this.prefillFromRun(copyFrom).then(() => {
      // Legacy duplicate-modal overrides land on top of the copied plan.
      const usePackId = this.route.snapshot.queryParamMap.get('usePackId');
      if (usePackId) this.rows.update((rs) => rs.map((r) => ({ ...r, packId: usePackId })));
      if (this.route.snapshot.queryParamMap.get('disableRegime') === 'true') this.regimeEnabled.set(false);
    }) : Promise.resolve();

    // Lookups load in parallel; each failure stays isolated.
    await Promise.all([
      (async () => {
        try { this.strategies.set(await this.strategiesApi.getAll()); } catch { /* */ }
      })(),
      (async () => {
        try {
          const profiles = await this.profilesApi.getAll();
          if (profiles.length > 0) {
            this.riskProfiles.set(profiles);
            if (!profiles.some((p) => p.id === this.riskProfile()) && !this.copiedFrom()) this.riskProfile.set(profiles[0].id);
          }
        } catch { /* */ }
      })(),
      (async () => {
        try {
          const pks = await this.packsApi.getAll();
          this.packs.set(pks.map((p) => ({ id: p.id, name: p.name })));
        } catch { /* */ }
      })(),
      (async () => {
        try {
          this.inventory.set(await firstValueFrom(this.http.get<InventoryItem[]>('/api/data-manager/inventory')));
        } catch { /* */ }
      })(),
      prefillDone,
    ]);

    const preset = this.route.snapshot.queryParamMap.get('preset');
    if (preset === 'exploration' && !this.explorationMode()) {
      this.applyExplorationPreset();
    }
  }

  prefilling = signal(false);
  copiedFrom = signal<string | null>(null);

  // X2: "reuse last params" — prefill from the most recent run without leaving the page.
  async reuseLastParams(): Promise<void> {
    if (this.prefilling()) return;
    this.prefilling.set(true);
    try {
      const runs = await this.runsApi.getRuns();
      if (runs.length > 0) await this.prefillFromRun(runs[0].runId);
    } finally {
      this.prefilling.set(false);
    }
  }

  private async prefillFromRun(runId: string): Promise<void> {
    try {
      const src = await this.runsApi.getRun(runId);
      if (!src) return;

      this.startDate.set((src.backtestFrom || '').slice(0, 10));
      this.endDate.set((src.backtestTo || '').slice(0, 10));
      this.balance.set(src.initialBalance || 100_000);
      if (src.commissionPerMillion != null) this.commission.set(src.commissionPerMillion);
      if (src.spreadPips != null) this.spread.set(src.spreadPips);
      if (src.riskProfileId) this.riskProfile.set(src.riskProfileId);
      if (src.venue) this.venue.set(src.venue);
      this.governorEnabled.set(src.governorEnabled !== false);
      this.regimeEnabled.set(src.regimeEnabled !== false);
      this.explorationMode.set(!!src.explorationMode);
      this.recordExcursions.set(!!src.recordExcursions);

      // Rebuild the exact run plan (strategy × symbol × TF × pack, incl. row-level disables) from
      // the persisted RunPlanJson; fall back to symbols/periods for pre-plan runs.
      const plan = this.parsePlan(src.runPlanJson);
      if (plan.length > 0) {
        this.selectedStrategyIds.set(new Set(plan.map((p) => p.strategyId)));
        this.selectedSymbols.set(new Set(plan.map((p) => p.symbol.toUpperCase())));
        this.selectedPeriods.set(new Set(plan.map((p) => p.timeframe.toLowerCase())));
        this.regenerate();
        const byKey = new Map(plan.map((p) => [rowKey(p.strategyId, p.symbol.toUpperCase(), p.timeframe.toUpperCase()), p]));
        this.rows.update((rs) => rs.map((r) => {
          const p = byKey.get(this.rowKeyOf(r));
          return p ? { ...r, enabled: true, packId: p.packId ?? '' } : { ...r, enabled: false };
        }));
      } else {
        const parse = (v: unknown): string[] => {
          try { const a = typeof v === 'string' ? JSON.parse(v) : v; return Array.isArray(a) ? a : []; } catch { return []; }
        };
        const syms = parse(src.symbols);
        const pers = parse(src.periods);
        if (syms.length) this.selectedSymbols.set(new Set(syms));
        if (pers.length) this.selectedPeriods.set(new Set(pers.map((p) => p.toLowerCase())));
        this.regenerate();
      }
      this.copiedFrom.set(runId);
    } catch (e) {
      // Source run gone or malformed — leave the form usable, but never hide the why.
      console.warn('copy-run prefill failed for', runId, e);
    }
  }

  private parsePlan(raw: string | undefined): { strategyId: string; symbol: string; timeframe: string; packId: string | null }[] {
    if (!raw) return [];
    try {
      const arr = JSON.parse(raw);
      if (!Array.isArray(arr)) return [];
      return arr
        .map((e: Record<string, unknown>) => ({
          strategyId: (e['StrategyId'] ?? e['strategyId'] ?? '') as string,
          symbol: (e['Symbol'] ?? e['symbol'] ?? '') as string,
          timeframe: (e['Timeframe'] ?? e['timeframe'] ?? '') as string,
          packId: (e['PackId'] ?? e['packId'] ?? null) as string | null,
        }))
        .filter((p) => p.strategyId && p.symbol && p.timeframe);
    } catch {
      return [];
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

  setRowEnabled(row: BuilderRow, enabled: boolean): void {
    this.rows.update((rs) => rs.map((r) => (this.rowKeyOf(r) === this.rowKeyOf(row) ? { ...r, enabled } : r)));
  }
  setRowPack(row: BuilderRow, packId: string): void {
    this.rows.update((rs) => rs.map((r) => (this.rowKeyOf(r) === this.rowKeyOf(row) ? { ...r, packId } : r)));
  }
  setAllEnabled(enabled: boolean): void {
    this.rows.update((rs) => rs.map((r) => ({ ...r, enabled })));
  }

  chipClass(enabled: boolean): string {
    return 'rounded-md border px-2 py-0.5 text-xs transition cursor-pointer '
      + (enabled ? 'border-emerald-600 bg-emerald-900/20 text-emerald-400' : 'border-gray-700 text-gray-500');
  }

  symClass(sym: string): string {
    const sel = this.selectedSymbols().has(sym);
    return 'cursor-pointer rounded-md border px-2 py-1 text-xs transition '
      + (sel ? 'border-emerald-600 bg-emerald-900/20 text-emerald-400' : 'border-gray-700 text-gray-400');
  }
  tfClass(tf: string): string {
    const sel = this.selectedPeriods().has(tf);
    return 'cursor-pointer rounded-md border px-2 py-1 text-xs uppercase transition '
      + (sel ? 'border-emerald-600 bg-emerald-900/20 text-emerald-400' : 'border-gray-700 text-gray-400');
  }
  stratClass(id: string): string {
    const sel = this.selectedStrategyIds().has(id);
    return 'flex items-center gap-2 rounded-md border p-3 cursor-pointer hover:border-gray-600 transition '
      + (sel ? 'border-emerald-600 bg-emerald-900/10' : 'border-gray-700');
  }

  setDateRange(months: number): void {
    const now = new Date();
    this.endDate.set(now.toISOString().slice(0, 10));
    const d = new Date(now);
    d.setMonth(d.getMonth() - months);
    this.startDate.set(d.toISOString().slice(0, 10));
  }

  async start(): Promise<void> {
    if (this.starting()) return;
    this.starting.set(true);
    const enabled = this.rows().filter((r) => r.enabled);
    if (enabled.length === 0) {
      this.error.set('Select at least one strategy, symbol and timeframe.');
      this.starting.set(false); return;
    }
    if (!this.startDate() || !this.endDate()) { this.error.set('Select start and end dates.'); this.starting.set(false); return; }
    if (new Date(this.startDate()) >= new Date(this.endDate())) { this.error.set('Start date must be before end date.'); this.starting.set(false); return; }
    if (!this.balance() || this.balance() <= 0) { this.error.set('Balance must be greater than 0.'); this.starting.set(false); return; }
    if (this.venue() === 'tape' && this.missingCoverage().length > 0) {
      this.error.set('Data coverage missing for: ' + this.missingCoverage().join(', '));
      this.starting.set(false); return;
    }
    this.error.set(null);

    const rows: RunRow[] = enabled.map((r) => ({
      strategyId: r.strategyId,
      symbol: r.symbol,
      timeframe: r.timeframe,
      packId: r.packId || undefined,
      enabled: true,
    }));

    const req: StartRunRequest = {
      start: this.startDate(),
      end: this.endDate(),
      balance: this.balance(),
      commissionPerMillion: this.commission(),
      spreadPips: this.spread(),
      symbols: [...new Set(enabled.map((r) => r.symbol))],
      periods: [...new Set(enabled.map((r) => r.timeframe.toLowerCase()))],
      strategyIds: [...new Set(enabled.map((r) => r.strategyId))],
      riskProfileId: this.riskProfile(),
      venue: this.venue(),
      rows,
      governorEnabled: this.governorEnabled(),
      dailyDdEnabled: this.dailyDdEnabled(),
      maxDdEnabled: this.maxDdEnabled(),
      forceCloseOnBreachEnabled: this.forceCloseEnabled(),
      disableRegime: this.regimeEnabled() ? undefined : true,
      stripAddOns: this.stripAddOns() ? true : undefined,
      speed: this.venue() === 'tape' ? this.speed() : undefined,
      honestFills: this.honestFills() ? undefined : false,
      recordExcursions: this.recordExcursions() ? true : undefined,
      explorationMode: this.explorationMode() ? true : undefined,
      compareBoth: this.compareBoth() ? true : undefined,
      idempotencyKey: crypto.randomUUID(),
    };
    this.saveSetup();
    try {
      const runId = await this.store.startBacktest(req);
      this.router.navigate(['/runs', runId, 'monitor']);
    } catch (e: unknown) {
      const err = (e as any)?.error;
      const missing = (err?.missing as any[])?.map((m: any) => `${m.symbol}/${m.timeframe}: ${m.reason}`).join('; ');
      this.error.set(err?.error || (e as any)?.message || 'Failed to start backtest');
      if (missing) this.error.update(m => m + ' (' + missing + ')');
    } finally {
      this.starting.set(false);
    }
  }

  allRiskEnabled = computed(() =>
    this.governorEnabled() && this.dailyDdEnabled() && this.maxDdEnabled() && this.forceCloseEnabled() && this.regimeEnabled()
  );

  toggleAllRisk(): void {
    const all = this.allRiskEnabled();
    this.governorEnabled.set(!all);
    this.dailyDdEnabled.set(!all);
    this.maxDdEnabled.set(!all);
    this.forceCloseEnabled.set(!all);
    this.regimeEnabled.set(!all);
  }

  applyExplorationPreset(): void {
    this.explorationMode.set(!this.explorationMode());
    if (this.explorationMode()) {
      this.stripAddOns.set(true);
      this.governorEnabled.set(false);
      this.recordExcursions.set(true);
      this.honestFills.set(true);
    } else {
      this.stripAddOns.set(false);
      this.governorEnabled.set(true);
      this.recordExcursions.set(false);
      this.honestFills.set(true);
    }
  }

  private readonly SETUP_STORAGE_KEY = 'shamshir-backtest-setups';

  private saveSetup(): void {
    try {
      const setup = {
        name: this._pendingSetupName || undefined,
        startDate: this.startDate(),
        endDate: this.endDate(),
        balance: this.balance(),
        commission: this.commission(),
        spread: this.spread(),
        riskProfile: this.riskProfile(),
        venue: this.venue(),
        governorEnabled: this.governorEnabled(),
        dailyDdEnabled: this.dailyDdEnabled(),
        maxDdEnabled: this.maxDdEnabled(),
        forceCloseEnabled: this.forceCloseEnabled(),
        regimeEnabled: this.regimeEnabled(),
        stripAddOns: this.stripAddOns(),
        honestFills: this.honestFills(),
        recordExcursions: this.recordExcursions(),
        explorationMode: this.explorationMode(),
        compareBoth: this.compareBoth(),
        strategies: [...this.selectedStrategyIds()],
        symbols: [...this.selectedSymbols()],
        periods: [...this.selectedPeriods()],
        savedAt: Date.now(),
      };
      const existing = JSON.parse(localStorage.getItem(this.SETUP_STORAGE_KEY) || '[]');
      existing.unshift(setup);
      localStorage.setItem(this.SETUP_STORAGE_KEY, JSON.stringify(existing.slice(0, 5)));
    } catch { /* */ }
  }

  private _pendingSetupName: string | null = null;

  saveNamedSetup(name: string): void {
    this._pendingSetupName = name || null;
    // save on next start() call
  }

  loadSetup(index: number): void {
    try {
      const existing = JSON.parse(localStorage.getItem(this.SETUP_STORAGE_KEY) || '[]');
      const setup = existing[index];
      if (!setup) return;
      this.startDate.set(setup.startDate || this.startDate());
      this.endDate.set(setup.endDate || this.endDate());
      this.balance.set(setup.balance ?? 100_000);
      this.commission.set(setup.commission ?? 30);
      this.spread.set(setup.spread ?? 1);
      this.riskProfile.set(setup.riskProfile || 'standard');
      this.venue.set(setup.venue || 'replay');
      this.governorEnabled.set(setup.governorEnabled ?? true);
      this.dailyDdEnabled.set(setup.dailyDdEnabled ?? true);
      this.maxDdEnabled.set(setup.maxDdEnabled ?? true);
      this.forceCloseEnabled.set(setup.forceCloseEnabled ?? true);
      this.regimeEnabled.set(setup.regimeEnabled ?? true);
      this.stripAddOns.set(setup.stripAddOns ?? false);
      this.honestFills.set(setup.honestFills ?? true);
      this.recordExcursions.set(setup.recordExcursions ?? false);
      this.explorationMode.set(setup.explorationMode ?? false);
      this.compareBoth.set(setup.compareBoth ?? false);
      if (setup.strategies?.length) this.selectedStrategyIds.set(new Set(setup.strategies));
      if (setup.symbols?.length) this.selectedSymbols.set(new Set(setup.symbols));
      if (setup.periods?.length) this.selectedPeriods.set(new Set(setup.periods));
      this.regenerate();
    } catch { /* */ }
  }

  get savedSetups(): any[] {
    try { return JSON.parse(localStorage.getItem(this.SETUP_STORAGE_KEY) || '[]'); }
    catch { return []; }
  }
}
