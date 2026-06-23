import { Component, input, ChangeDetectionStrategy } from '@angular/core';
import { CandlestickSeries, LineSeries } from 'lightweight-charts';
import { BaseChartComponent } from './base-chart.component';
import { toUtcTimestamp } from './chart-time.helper';

export interface OhlcBar {
  time: number;
  open: number;
  high: number;
  low: number;
  close: number;
}
export interface PriceMarker {
  price: number;
  label: string;
  color: string;
}

@Component({
  selector: 'app-candle-chart',
  standalone: true,
  template: `<div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
    <h3 class="mb-2 text-sm font-medium text-gray-400">{{ title() }}</h3>
    <div class="h-96 w-full chart-host"></div>
  </div>`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CandleChartComponent extends BaseChartComponent {
  readonly title = input('Price Chart');
  readonly bars = input<OhlcBar[]>([]);
  readonly markers = input<PriceMarker[]>([]);

  private candleSeries: any = null;
  private markerLines: any[] = [];

  protected initChart(): void {
    const container = this.initChartBase('.chart-host', 0, 384, {
      timeScale: { timeVisible: true, borderColor: '#374151' },
      rightPriceScale: { borderColor: '#374151' },
    });
    if (!container || !this.chart) return;

    this.candleSeries = this.chart.addSeries(CandlestickSeries, {
      upColor: '#10b981',
      downColor: '#ef4444',
      borderUpColor: '#10b981',
      borderDownColor: '#ef4444',
      wickUpColor: '#10b981',
      wickDownColor: '#ef4444',
    });
  }

  protected updateChart(): void {
    if (!this.candleSeries || !this.chart) return;

    const candleData = this.bars().map((b) => ({
      time: toUtcTimestamp(b.time),
      open: b.open, high: b.high, low: b.low, close: b.close,
    }));
    this.candleSeries.setData(candleData.length > 0 ? candleData : []);

    this.markerLines.forEach((s) => this.chart!.removeSeries(s));
    this.markerLines = [];

    for (const m of this.markers()) {
      const ls = this.chart!.addSeries(LineSeries, {
        color: m.color, lineWidth: 1, lineStyle: 2,
        priceLineVisible: false, lastValueVisible: false,
      });
      const t0 = candleData[0]?.time ?? toUtcTimestamp(Date.now());
      const t1 = candleData[candleData.length - 1]?.time ?? t0;
      ls.setData([{ time: t0, value: m.price }, { time: t1, value: m.price }]);
      this.markerLines.push(ls);
    }
  }
}
