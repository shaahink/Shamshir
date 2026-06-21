import { Component, inject, OnInit, signal, ChangeDetectionStrategy } from '@angular/core';
import { Router } from '@angular/router';
import { HttpClient } from '@angular/common/http';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { RunsStore } from '../runs.store';
import type { StrategySummary, StartRunRequest } from '../../../models/api.types';

const ALL_SYMBOLS = [
  'EURUSD',
  'GBPUSD',
  'USDJPY',
  'GBPJPY',
  'XAUUSD',
  'AUDUSD',
  'USDCHF',
  'USDCAD',
  'NZDUSD',
  'EURGBP',
  'EURJPY',
  'XAGUSD',
];
const ALL_TIMEFRAMES = ['h1', 'h4', 'd1', 'm15', 'm5', 'm1'];

@Component({
  selector: 'app-new-backtest',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="mx-auto max-w-3xl space-y-6">
      <h1 class="text-xl font-semibold">New Backtest</h1>

      <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-6 space-y-5">
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

        <div>
          <label class="block text-xs font-medium text-gray-400 mb-2">Date Range</label>
          <div class="flex gap-2 mb-2">
            <button
              (click)="setDateRange(1)"
              class="rounded-md border border-gray-700 px-3 py-1 text-xs text-gray-300 hover:bg-gray-800"
            >
              Last Month
            </button>
            <button
              (click)="setDateRange(3)"
              class="rounded-md border border-gray-700 px-3 py-1 text-xs text-gray-300 hover:bg-gray-800"
            >
              Last Quarter
            </button>
            <button
              (click)="setDateRange(12)"
              class="rounded-md border border-gray-700 px-3 py-1 text-xs text-gray-300 hover:bg-gray-800"
            >
              Last Year
            </button>
          </div>
          <div class="grid grid-cols-2 gap-3">
            <input
              type="date"
              [(ngModel)]="startDate"
              class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none"
            /><input
              type="date"
              [(ngModel)]="endDate"
              class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none"
            />
          </div>
        </div>

        <div class="grid grid-cols-2 gap-3">
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Initial Balance</label
            ><input
              type="number"
              [(ngModel)]="balance"
              class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none"
            />
          </div>
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Commission (per M)</label
            ><input
              type="number"
              [(ngModel)]="commission"
              class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none"
            />
          </div>
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Spread (pips)</label
            ><input
              type="number"
              [(ngModel)]="spread"
              step="0.1"
              class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none"
            />
          </div>
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Risk Profile</label
            ><select
              [(ngModel)]="riskProfile"
              class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none"
            >
              @for (p of riskProfiles(); track p.id) {
                <option [value]="p.id">
                  {{ p.displayName }} ({{ (p.riskPerTradePercent * 100).toFixed(2) }}%/trade)
                </option>
              }
            </select>
          </div>
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Data Venue</label
            ><select
              [(ngModel)]="venue"
              class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none"
            >
              <option value="">Default (replay)</option>
              <option value="replay">Replay (stored bars)</option>
              <option value="ctrader">cTrader (live stream)</option>
            </select>
          </div>
        </div>

        <div>
          <label class="block text-xs font-medium text-gray-400 mb-2">Strategies</label>
          <div class="grid gap-2 md:grid-cols-2">
            @for (s of strategies(); track s.id) {
              <label [attr.class]="stratClass(s.id)" (click)="toggleStrat(s.id)"
                ><input
                  type="checkbox"
                  [checked]="selectedStrategyIds().has(s.id)"
                  class="h-4 w-4 rounded border-gray-600 bg-gray-800 text-emerald-500 focus:ring-emerald-500"
                />
                <div>
                  <div class="text-sm font-medium text-gray-200">{{ s.displayName }}</div>
                  <div class="text-xs text-gray-500">
                    {{ s.id }}
                    @if (s.stats?.totalTrades) {
                      · {{ s.stats.totalTrades }} trades
                    }
                  </div>
                </div></label
              >
            }
          </div>
          @if (strategies().length === 0) {
            <p class="text-xs text-gray-500">Loading strategies...</p>
          }
        </div>

        @if (selectedStrategyIds().size > 0) {
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-2">Per-strategy overrides (optional)</label>
            @for (s of strategies(); track s.id) {
              @if (selectedStrategyIds().has(s.id)) {
                <div class="mb-1">
                  <span class="text-xs text-gray-500">{{ s.displayName || s.id }}</span>
                  <textarea
                    [ngModel]="overrideFor(s.id)"
                    (ngModelChange)="setOverride(s.id, $event)"
                    placeholder='JSON overrides e.g. {"Param1":42}'
                    class="mt-0.5 w-full rounded border border-gray-700 bg-gray-800 px-2 py-1 text-xs text-gray-300 font-mono focus:border-emerald-500 focus:outline-none"
                    rows="2"
                  ></textarea>
                </div>
              }
            }
          </div>
        }

        <div class="rounded-lg border border-gray-700 bg-gray-800/30 p-3">
          <h4 class="text-xs font-medium text-gray-400 mb-2">Resolved config preview</h4>
          <div class="grid grid-cols-2 gap-x-4 gap-y-0.5 text-xs">
            <span class="text-gray-500">Symbols:</span><span class="text-gray-300">{{ symListStr() }}</span>
            <span class="text-gray-500">Timeframes:</span><span class="text-gray-300">{{ perListStr() }}</span>
            <span class="text-gray-500">Dates:</span><span class="text-gray-300">{{ startDate }} → {{ endDate }}</span>
            <span class="text-gray-500">Balance:</span><span class="text-gray-300">{{ balance }}</span>
            <span class="text-gray-500">Comm/Spread:</span
            ><span class="text-gray-300">{{ commission }}/M · {{ spread }} pips</span>
            <span class="text-gray-500">Risk Profile:</span><span class="text-gray-300">{{ riskProfile }}</span>
            <span class="text-gray-500">Venue:</span
            ><span class="text-gray-300">{{ venue || '(default · replay)' }}</span>
            <span class="text-gray-500">Strategies:</span><span class="text-gray-300">{{ stratListStr() }}</span>
          </div>
        </div>

        @if (error()) {
          <div class="rounded-md bg-red-900/20 p-3 text-sm text-red-400">{{ error() }}</div>
        }

        <button
          (click)="start()"
          [disabled]="loading()"
          class="w-full rounded-md bg-emerald-600 px-4 py-2.5 text-sm font-medium text-white hover:bg-emerald-500 disabled:opacity-50"
        >
          {{ loading() ? 'Starting...' : 'Start Backtest' }}
        </button>
      </div>
    </div>
  \`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NewBacktestComponent implements OnInit {
  private store = inject(RunsStore);
  private router = inject(Router);
  private http = inject(HttpClient);

  allSymbols = ALL_SYMBOLS;
  allTimeframes = ALL_TIMEFRAMES;
  selectedSymbols = signal(new Set<string>(['EURUSD']));
  selectedPeriods = signal(new Set<string>(['h1']));
  startDate = '';
  endDate = '';
  balance = 100_000;
  commission = 30;
  spread = 1;
  riskProfile = 'standard';
  venue = '';
  strategies = signal<any[]>([]);
  riskProfiles = signal<any[]>([{ id: 'standard', displayName: 'Standard', riskPerTradePercent: 0.005 }]);
  selectedStrategyIds = signal<Set<string>>(new Set());
  overridesText = signal<Record<string, string>>({});
  loading = this.store.isLoading;
  error = this.store.error;

  symListStr(): string {
    return [...this.selectedSymbols()].join(', ') || '(none)';
  }
  perListStr(): string {
    return [...this.selectedPeriods()].map((p) => p.toUpperCase()).join(', ') || '(none)';
  }
  stratListStr(): string {
    return [...this.selectedStrategyIds()].join(', ') || '(enabled defaults)';
  }
  overrideFor(id: string): string {
    return this.overridesText()[id] ?? '';
  }
  setOverride(id: string, v: string): void {
    this.overridesText.update((o) => ({ ...o, [id]: v }));
  }

  constructor() {
    const now = new Date();
    this.endDate = now.toISOString().slice(0, 10);
    const d = new Date(now);
    d.setMonth(d.getMonth() - 1);
    this.startDate = d.toISOString().slice(0, 10);
  }

  async ngOnInit(): Promise<void> {
    try {
      const res = await firstValueFrom(this.http.get<any>('/api/strategies'));
      const data: any[] = Array.isArray(res) ? res : res.strategies || res.configs || [];
      this.strategies.set(data);
      const s = new Set<string>();
      data.filter((x: any) => x.isEnabled).forEach((x: any) => s.add(x.id));
      if (s.size === 0 && data.length > 0) s.add(data[0].id);
      this.selectedStrategyIds.set(s);
    } catch {
      /* */
    }

    try {
      const rp = await firstValueFrom(this.http.get<any>('/api/risk-profiles'));
      const profiles: any[] = Array.isArray(rp) ? rp : rp.profiles || [];
      if (profiles.length > 0) {
        this.riskProfiles.set(profiles);
        if (!profiles.some((p) => p.id === this.riskProfile)) this.riskProfile = profiles[0].id;
      }
    } catch {
      /* keep default */
    }
  }

  toggleSymbol(sym: string) {
    const s = new Set(this.selectedSymbols());
    if (s.has(sym)) s.delete(sym);
    else s.add(sym);
    this.selectedSymbols.set(s);
  }
  togglePeriod(tf: string) {
    const s = new Set(this.selectedPeriods());
    if (s.has(tf)) s.delete(tf);
    else s.add(tf);
    this.selectedPeriods.set(s);
  }
  toggleStrat(id: string) {
    const s = new Set(this.selectedStrategyIds());
    if (s.has(id)) s.delete(id);
    else s.add(id);
    this.selectedStrategyIds.set(s);
  }
  symClass(sym: string): string {
    const sel = this.selectedSymbols().has(sym);
    return `cursor-pointer rounded-md border px-2.5 py-1 text-xs transition ${sel ? 'border-emerald-600 bg-emerald-900/20 text-emerald-400' : 'border-gray-700 text-gray-400'}`;
  }
  tfClass(tf: string): string {
    const sel = this.selectedPeriods().has(tf);
    return `cursor-pointer rounded-md border px-2.5 py-1 text-xs uppercase transition ${sel ? 'border-emerald-600 bg-emerald-900/20 text-emerald-400' : 'border-gray-700 text-gray-400'}`;
  }
  stratClass(id: string): string {
    const sel = this.selectedStrategyIds().has(id);
    return `flex items-center gap-2 rounded-md border border-gray-700 p-3 cursor-pointer hover:border-gray-600 transition ${sel ? 'border-emerald-600 bg-emerald-900/10' : ''}`;
  }

  setDateRange(months: number): void {
    const now = new Date();
    this.endDate = now.toISOString().slice(0, 10);
    const d = new Date(now);
    d.setMonth(d.getMonth() - months);
    this.startDate = d.toISOString().slice(0, 10);
  }

  async start(): Promise<void> {
    const symList = [...this.selectedSymbols()];
    const perList = [...this.selectedPeriods()];
    const stratIds = [...this.selectedStrategyIds()];
    if (symList.length === 0) {
      this.error.set('Select at least one symbol');
      return;
    }
    if (perList.length === 0) {
      this.error.set('Select at least one timeframe');
      return;
    }
    if (stratIds.length === 0) {
      this.error.set('Select at least one strategy');
      return;
    }
    if (!this.startDate || !this.endDate) {
      this.error.set('Select start and end dates');
      return;
    }
    if (new Date(this.startDate) >= new Date(this.endDate)) {
      this.error.set('Start date must be before end date');
      return;
    }
    if (!this.balance || this.balance <= 0) {
      this.error.set('Balance must be greater than 0');
      return;
    }
    this.error.set(null);

    // F8: parse any per-strategy JSON overrides (skip empty / invalid).
    let strategyOverrides: Record<string, Record<string, unknown>> | undefined;
    const texts = this.overridesText();
    for (const id of stratIds) {
      const raw = (texts[id] ?? '').trim();
      if (!raw) continue;
      try {
        const parsed = JSON.parse(raw);
        if (parsed && typeof parsed === 'object') {
          strategyOverrides ??= {};
          strategyOverrides[id] = parsed;
        }
      } catch {
        this.error.set(`Invalid JSON override for strategy "${id}"`);
        return;
      }
    }

    const req: StartRunRequest = {
      symbol: symList[0] || 'EURUSD',
      period: perList[0] || 'h1',
      start: this.startDate,
      end: this.endDate,
      balance: this.balance,
      commissionPerMillion: this.commission,
      spreadPips: this.spread,
      symbols: symList,
      periods: perList,
      strategyIds: stratIds,
      riskProfileId: this.riskProfile,
      venue: this.venue,
      strategyOverrides,
    };
    const runId = await this.store.startBacktest(req);
    this.router.navigate(['/runs', runId, 'monitor']);
  }
}
