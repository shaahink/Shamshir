import { Component, inject, input, output, signal, computed, OnInit, OnDestroy, effect, ChangeDetectionStrategy } from '@angular/core';
import { DatePipe } from '@angular/common';
import { CandleChartComponent, type OhlcBar, type PriceMarker, type StopPathSegment } from './candle-chart.component';
import type { TradeDetail } from '../models/api.types';
import { TradesApiService } from '../features/trades/trades.service';
import { formatDuration } from './format.helper';
import { markerFor } from './chart-marker.helper';

const CONTEXT_CHOICES = [20, 50, 100];

@Component({
  selector: 'app-trade-chart-card',
  standalone: true,
  imports: [CandleChartComponent, DatePipe],
  template: `
    <div class="rounded-lg border border-gray-800 bg-gray-900/30 p-4">
      @if (trade(); as t) {
        <div class="mb-3 flex items-start justify-between">
          <div>
            <div class="flex items-center gap-2">
              <span class="text-sm font-medium text-gray-200">{{ t.symbol }}</span>
              <span [class.text-emerald-400]="t.direction === 'Long'" [class.text-red-400]="t.direction !== 'Long'" class="text-xs">
                {{ t.direction }}
              </span>
              <span class="text-xs text-gray-600">{{ t.strategyId }}</span>
              @if (showNav() && t.tradeCount) {
                <span class="ml-1 text-xs text-gray-500">{{ t.tradeIndex }}/{{ t.tradeCount }}</span>
              }
            </div>
            <div class="mt-1 flex gap-3 text-xs text-gray-500">
              <span>Entry {{ t.entryPrice }} &#64; {{ t.openedAtUtc | date: 'MM-dd HH:mm' }}</span>
              <span>Exit {{ t.exitPrice }} &#64; {{ t.closedAtUtc | date: 'MM-dd HH:mm' }}</span>
              <span>SL {{ t.stopLoss }}</span>
              @if (t.takeProfit) { <span>TP {{ t.takeProfit }}</span> }
            </div>
          </div>
          <div class="flex items-start gap-3">
            @if (showNav()) {
              <div class="flex gap-1">
                <button (click)="navigate.emit(t.prevTradeId!)" [disabled]="!t.prevTradeId"
                  class="rounded border border-gray-700 px-2 py-1 text-xs text-gray-300 hover:bg-gray-800 disabled:opacity-30 disabled:cursor-not-allowed"
                  title="Previous trade in run">&larr; Prev</button>
                <button (click)="navigate.emit(t.nextTradeId!)" [disabled]="!t.nextTradeId"
                  class="rounded border border-gray-700 px-2 py-1 text-xs text-gray-300 hover:bg-gray-800 disabled:opacity-30 disabled:cursor-not-allowed"
                  title="Next trade in run">Next &rarr;</button>
              </div>
            }
            <div class="text-right">
              <div
                class="text-sm font-mono"
                [class.text-emerald-400]="t.netPnLAmount > 0"
                [class.text-red-400]="t.netPnLAmount < 0"
              >
                {{ t.netPnLAmount > 0 ? '+' : '' }}{{ t.netPnLAmount.toFixed(2) }}
              </div>
              <div class="text-xs text-gray-500">
                {{ t.pnLPips.toFixed(1) }}p · {{ t.rMultiple.toFixed(2) }}R
              </div>
              <div class="mt-1 flex justify-end gap-2 text-xs text-gray-600">
                <span>{{ t.exitReason }}</span>
                <span>{{ fmtDuration(t.durationSeconds) }}</span>
              </div>
            </div>
          </div>
        </div>

        <div class="mb-2 flex items-center gap-1">
          <span class="text-xs text-gray-600">Context:</span>
          @for (n of contextChoices; track n) {
            <button (click)="setContext(n)"
              class="rounded border px-1.5 py-0.5 text-xs"
              [class.border-emerald-700]="contextBars() === n"
              [class.text-emerald-400]="contextBars() === n"
              [class.border-gray-700]="contextBars() !== n"
              [class.text-gray-500]="contextBars() !== n">{{ n }}</button>
          }
          <span class="ml-1 text-xs text-gray-600">bars</span>
        </div>

        @if (bars().length > 0) {
          <app-candle-chart
            title=""
            [bars]="bars()"
            [markers]="markers()"
            [stopPath]="stopPath()"
            [tradeOpenTime]="tradeOpenMs()"
            [tradeCloseTime]="tradeCloseMs()"
            [tradeDirection]="trade()?.direction ?? null"
          />
        } @else if (loading()) {
          <div class="flex h-48 items-center justify-center text-xs text-gray-600">Loading chart...</div>
        } @else {
          <div class="flex h-24 items-center justify-center text-xs text-gray-600">No price data</div>
        }
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TradeChartCardComponent implements OnInit, OnDestroy {
  private api = inject(TradesApiService);
  readonly tradeId = input.required<string>();
  // X3: prev/next navigation — host decides what "navigate" means (route change or selection swap).
  readonly showNav = input(false);
  readonly navigate = output<string>();

  trade = signal<TradeDetail | null>(null);
  bars = signal<OhlcBar[]>([]);
  markers = signal<PriceMarker[]>([]);
  stopPath = signal<StopPathSegment[]>([]);
  loading = signal(false);
  contextBars = signal(20);
  contextChoices = CONTEXT_CHOICES;

  private loadEffect = effect(() => {
    const id = this.tradeId();
    if (id) void this.loadTrade(id);
  }, { allowSignalWrites: true });

  async ngOnInit(): Promise<void> {
    const id = this.tradeId();
    if (id) await this.loadTrade(id);
  }

  ngOnDestroy(): void {
    this.loadEffect.destroy();
  }

  setContext(n: number): void {
    if (this.contextBars() === n) return;
    this.contextBars.set(n);
    void this.loadChart(this.tradeId());
  }

  private async loadTrade(id: string): Promise<void> {
    this.bars.set([]);
    this.markers.set([]);
    this.stopPath.set([]);

    try {
      const t = await this.api.getById(id);
      this.trade.set(t);
    } catch {
      return;
    }
    await this.loadChart(id);
  }

  private async loadChart(id: string): Promise<void> {
    this.loading.set(true);
    try {
      const chart = await this.api.getChart(id, this.contextBars());
      this.bars.set(
        chart.bars.map((b: any) => ({
          time: b.time * 1000,
          open: b.open, high: b.high, low: b.low, close: b.close,
        })),
      );
      const t = this.trade();
      const mks = chart.markers.map((m) => {
        const marker = markerFor(m.kind, m.price, m.time * 1000);
        if (m.kind === 'Exit' && t?.exitReason) marker.label = `Exit: ${t.exitReason}`;
        return marker;
      });
      this.markers.set(mks);
      this.stopPath.set((chart.stopPath ?? []).map((p) => ({ time: p.time * 1000, price: p.price, kind: p.kind })));
    } catch {
      /* no chart data */
    } finally {
      this.loading.set(false);
    }
  }

  tradeOpenMs = computed(() => {
    const t = this.trade();
    return t ? new Date(t.openedAtUtc).getTime() : null;
  });
  tradeCloseMs = computed(() => {
    const t = this.trade();
    return t ? new Date(t.closedAtUtc).getTime() : null;
  });

  fmtDuration = formatDuration;
}
