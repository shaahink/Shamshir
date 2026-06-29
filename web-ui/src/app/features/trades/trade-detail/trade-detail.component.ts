import { Component, inject, OnInit, signal, ChangeDetectionStrategy } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { DatePipe } from '@angular/common';
import { StatTileComponent } from '../../../shared/stat-tile.component';
import { CandleChartComponent, type OhlcBar, type PriceMarker } from '../../../shared/candle-chart.component';
import type { TradeDetail } from '../../../models/api.types';
import { TradesApiService } from '../trades.service';
import { formatDuration } from '../../../shared/format.helper';

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
          <app-candle-chart title="Price Chart" [bars]="bars()" [markers]="markers()" />
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
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TradeDetailComponent implements OnInit {
  private route = inject(ActivatedRoute);
  private api = inject(TradesApiService);
  trade = signal<TradeDetail | null>(null);
  bars = signal<OhlcBar[]>([]);
  markers = signal<PriceMarker[]>([]);

  async ngOnInit(): Promise<void> {
    const id = this.route.snapshot.paramMap.get('id');
    if (!id) return;
    const t = await this.api.getById(id);
    this.trade.set(t);
    // iter-redesign P6.2: bars + entry/exit/SL/TP markers come from the dedicated chart endpoint, which
    // resolves the run's timeframe and pads the window server-side.
    try {
      const chart = await this.api.getChart(id);
      this.bars.set(
        chart.bars.map((b) => ({ time: b.time * 1000, open: b.open, high: b.high, low: b.low, close: b.close })),
      );
      this.markers.set(chart.markers.map((m) => this.markerFor(m.kind, m.price)));
    } catch {
      /* no chart data */
    }
  }

  private markerFor(kind: string, price: number): PriceMarker {
    switch (kind) {
      case 'Entry':
        return { price, label: 'Entry', color: '#60a5fa' };
      case 'Exit':
        return { price, label: 'Exit', color: '#fb923c' };
      case 'StopLoss':
        return { price, label: 'SL', color: '#ef4444' };
      case 'TakeProfit':
        return { price, label: 'TP', color: '#10b981' };
      default:
        return { price, label: kind, color: '#9ca3af' };
    }
  }

  fmtDuration = formatDuration;
}
