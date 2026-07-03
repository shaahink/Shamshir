import { Component, inject, input, signal, computed, OnInit, OnChanges, ChangeDetectionStrategy } from '@angular/core';
import { CandleChartComponent, type OhlcBar, type PriceMarker } from './candle-chart.component';
import type { TradeDetail } from '../models/api.types';
import { TradesApiService } from '../features/trades/trades.service';
import { formatDuration } from './format.helper';
import { markerFor } from './chart-marker.helper';

@Component({
  selector: 'app-trade-chart-card',
  standalone: true,
  imports: [CandleChartComponent],
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
            </div>
            <div class="mt-1 flex gap-3 text-xs text-gray-500">
              <span>Entry {{ t.entryPrice }}</span>
              <span>Exit {{ t.exitPrice }}</span>
              <span>SL {{ t.stopLoss }}</span>
              @if (t.takeProfit) { <span>TP {{ t.takeProfit }}</span> }
            </div>
          </div>
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
            <div class="mt-1 flex gap-2 text-xs text-gray-600">
              <span>{{ t.exitReason }}</span>
              <span>{{ fmtDuration(t.durationSeconds) }}</span>
            </div>
          </div>
        </div>

        @if (bars().length > 0) {
          <app-candle-chart
            title=""
            [bars]="bars()"
            [markers]="markers()"
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
export class TradeChartCardComponent implements OnInit, OnChanges {
  private api = inject(TradesApiService);
  readonly tradeId = input.required<string>();

  trade = signal<TradeDetail | null>(null);
  bars = signal<OhlcBar[]>([]);
  markers = signal<PriceMarker[]>([]);
  loading = signal(false);

  private loadEffect = effect(() => {
    const id = this.tradeId();
    if (id) void this.loadTrade(id);
  }, { allowSignalWrites: true });

  async ngOnInit(): Promise<void> {
    await this.loadTradeData();
  }

  async ngOnChanges(): Promise<void> {
    await this.loadTradeData();
  }

  private async loadTradeData(): Promise<void> {
    const id = this.tradeId();
    this.bars.set([]);
    this.markers.set([]);

    try {
      const t = await this.api.getById(id);
      this.trade.set(t);
    } catch {
      return;
    }

    this.loading.set(true);
    try {
      const chart = await this.api.getChart(id);
      this.bars.set(
        chart.bars.map((b: any) => ({
          time: b.time * 1000,
          open: b.open, high: b.high, low: b.low, close: b.close,
        })),
      );
      const mks = chart.markers.map((m: any) => ({
        ...markerFor(m.kind, m.price),
        time: m.time ? m.time * 1000 : undefined,
      }));
      const t = this.trade();
      if (t?.exitReason) {
        mks.push({ price: t.exitPrice, label: `Exit: ${t.exitReason}`, color: '#fbbf24', time: this.tradeCloseMs() ?? undefined });
      }
      this.markers.set(mks);
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
