import { Component, inject, OnInit, signal } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { DatePipe } from '@angular/common';
import { StatTileComponent } from '../../../shared/stat-tile.component';
import { CandleChartComponent, type OhlcBar, type PriceMarker } from '../../../shared/candle-chart.component';
import type { TradeSummary, BarData } from '../../../models/api.types';
import { TradesApiService } from '../trades.service';

@Component({
  selector: 'app-trade-detail',
  standalone: true,
  imports: [DatePipe, StatTileComponent, CandleChartComponent],
  template: `
    <div class="space-y-6">
      <h1 class="text-xl font-semibold">Trade Detail</h1>
      @if (trade(); as t) {
        <div class="grid grid-cols-2 gap-3 md:grid-cols-4">
          <app-stat-tile label="Symbol" [value]="t.symbol" />
          <app-stat-tile label="Direction" [value]="t.direction" [positive]="t.direction === 'Long'" />
          <app-stat-tile label="Lots" [value]="t.lots" />
          <app-stat-tile
            label="Net P/L"
            [value]="t.netPnLAmount.toFixed(2)"
            [positive]="t.netPnLAmount > 0"
            [negative]="t.netPnLAmount < 0"
          />
          <app-stat-tile label="Entry" [value]="t.entryPrice" /> <app-stat-tile label="Exit" [value]="t.exitPrice" />
          <app-stat-tile label="Pips" [value]="t.pnLPips.toFixed(1)" />
          <app-stat-tile label="R" [value]="t.rMultiple.toFixed(2)" [positive]="t.rMultiple > 1" />
          <app-stat-tile label="Commission" [value]="t.commissionAmount.toFixed(2)" />
          <app-stat-tile label="Swap" [value]="t.swapAmount.toFixed(2)" />
          <app-stat-tile
            label="Gross P/L"
            [value]="t.grossPnLAmount.toFixed(2)"
            [positive]="t.grossPnLAmount > 0"
            [negative]="t.grossPnLAmount < 0"
          />
          <app-stat-tile label="MAE" [value]="t.maxAdverseExcursion.toFixed(1)" />
          <app-stat-tile label="MFE" [value]="t.maxFavorableExcursion.toFixed(1)" />
          <app-stat-tile label="Hold" [value]="fmtDuration(t.durationSeconds)" />
          <app-stat-tile label="Opened" [value]="t.openedAtUtc | date: 'MM-dd HH:mm'" />
          <app-stat-tile label="Closed" [value]="t.closedAtUtc | date: 'MM-dd HH:mm'" />
        </div>
        <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
          <h2 class="mb-1 text-sm font-medium text-gray-400">Exit: {{ t.exitReason }}</h2>
          <p class="text-xs text-gray-500">Strategy: {{ t.strategyId }}</p>
        </div>

        @if (bars().length > 0) {
          <app-candle-chart title="Price Chart" [bars]="bars()" [markers]="chartMarkers()" />
        } @else {
          <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-12 text-center">
            <p class="text-sm text-gray-500">No price data for this window.</p>
          </div>
        }
      } @else {
        <div class="py-12 text-center text-sm text-gray-500">Trade not found.</div>
      }
    </div>
  `,
})
export class TradeDetailComponent implements OnInit {
  private route = inject(ActivatedRoute);
  private api = inject(TradesApiService);
  trade = signal<TradeSummary | null>(null);
  bars = signal<OhlcBar[]>([]);

  chartMarkers = (): PriceMarker[] => {
    const t = this.trade();
    if (!t) return [];
    const markers: PriceMarker[] = [
      { price: t.entryPrice, label: 'Entry', color: '#60a5fa' },
      { price: t.exitPrice, label: 'Exit', color: '#fb923c' },
    ];
    // iter-38 W-A1: the trade-detail API (TradeDetailResponse) emits stopLoss/takeProfit, not slPrice/tpPrice.
    if (t.stopLoss && t.stopLoss > 0) markers.push({ price: t.stopLoss, label: 'SL', color: '#ef4444' });
    if (t.takeProfit && t.takeProfit > 0) markers.push({ price: t.takeProfit, label: 'TP', color: '#10b981' });
    return markers;
  };

  async ngOnInit(): Promise<void> {
    const id = this.route.snapshot.paramMap.get('id');
    if (!id) return;
    const t = await this.api.getById(id);
    this.trade.set(t);
    // T1: order-safe window — never assume openedAtUtc < closedAtUtc (a wall-clock-stamped entry can
    // invert the order). Build [min-pad, max+pad] so /api/bars always gets a valid (from <= to) range.
    const a = new Date(t.openedAtUtc).getTime();
    const b = new Date(t.closedAtUtc).getTime();
    const lo = Math.min(a, b);
    const hi = Math.max(a, b);
    const pad = Math.max((hi - lo) * 2, 3_600_000);
    const from = new Date(lo - pad);
    const to = new Date(hi + pad);
    const tf = t.timeframe || 'H1';
    try {
      const bars = await this.api.getBars(
        t.symbol,
        tf,
        from.toISOString(),
        to.toISOString(),
      );
      this.bars.set(
        bars.map((b: BarData) => ({ time: b.time * 1000, open: b.open, high: b.high, low: b.low, close: b.close })),
      );
    } catch {
      /* no bars */
    }
  }

  fmtDuration(s: number): string {
    const h = Math.floor(s / 3600);
    const m = Math.floor((s % 3600) / 60);
    return h > 0 ? `${h}h ${m}m` : `${m}m`;
  }
}
