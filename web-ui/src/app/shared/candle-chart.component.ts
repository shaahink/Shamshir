import { Component, input, ChangeDetectionStrategy } from '@angular/core';
import { CandlestickSeries, LineSeries } from 'lightweight-charts';
import { BaseChartComponent, type LegendEntry } from './base-chart.component';
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
  time?: number;
}

@Component({
  selector: 'app-candle-chart',
  standalone: true,
  template: `<div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
    <h3 class="mb-2 text-sm font-medium text-gray-400">{{ title() }}</h3>
    <div class="h-96 w-full chart-host"></div>
    @if (legendEntries().length) {
      <div class="mt-2 flex flex-wrap gap-3">
        @for (e of legendEntries(); track e.name) {
          <span class="flex items-center gap-1 text-xs text-gray-500">
            <span class="inline-block h-2 w-2 rounded-full" [style.background]="e.color"></span>
            {{ e.name }}
          </span>
        }
      </div>
    }
  </div>`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CandleChartComponent extends BaseChartComponent {
  readonly title = input('Price Chart');
  readonly bars = input<OhlcBar[]>([]);
  readonly markers = input<PriceMarker[]>([]);
  readonly tradeOpenTime = input<number | null>(null);
  readonly tradeCloseTime = input<number | null>(null);
  readonly tradeDirection = input<string | null>(null);

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

    const legends: LegendEntry[] = [];
    const markers = this.markers();

    // C1: split markers into time-anchored (entry/exit arrows on candle) and
    // horizontal lines (SL/TP levels spanning the full chart).
    const pointMarkers: PriceMarker[] = [];
    const lineMarkers: PriceMarker[] = [];
    for (const m of markers) {
      if (m.time && (m.label === 'Entry' || m.label === 'Exit')) {
        pointMarkers.push(m);
      } else {
        lineMarkers.push(m);
      }
    }

    // Render time-anchored point markers as candlestick series markers
    if (pointMarkers.length > 0) {
      const candleMarks = pointMarkers.map((m) => ({
        time: toUtcTimestamp(m.time!),
        position: m.label === 'Entry'
          ? (this.tradeDirection() === 'Short' ? 'aboveBar' : 'belowBar')
          : (this.tradeDirection() === 'Short' ? 'belowBar' : 'aboveBar'),
        color: m.color,
        shape: m.label === 'Entry' ? 'arrowUp' : 'arrowDown',
        text: m.label,
        size: 3,
      }));
      this.candleSeries.setMarkers(candleMarks);
    } else {
      this.candleSeries.setMarkers([]);
    }

    // Render horizontal price lines for SL/TP
    const t0 = candleData[0]?.time ?? toUtcTimestamp(Date.now());
    const t1 = candleData[candleData.length - 1]?.time ?? t0;
    for (const m of lineMarkers) {
      const ls = this.chart!.addSeries(LineSeries, {
        color: m.color, lineWidth: 1, lineStyle: 2,
        priceLineVisible: false, lastValueVisible: false,
      });
      ls.setData([{ time: t0, value: m.price }, { time: t1, value: m.price }]);
      this.markerLines.push(ls);
      legends.push({ name: m.label, color: m.color });
    }

    this.legendEntries.set(legends);
    this.fitContent();

    const openT = this.tradeOpenTime();
    const closeT = this.tradeCloseTime();
    if (openT != null && closeT != null && this.chart) {
      const shadeColor = this.tradeDirection() === 'Short'
        ? 'rgba(239, 68, 68, 0.06)'
        : 'rgba(16, 185, 129, 0.06)';
      const openTime = toUtcTimestamp(openT);
      const closeTime = toUtcTimestamp(closeT);
      const prices = markers.map(m => m.price);
      const minP = prices.length > 0 ? Math.min(...prices) : 0;
      const maxP = prices.length > 0 ? Math.max(...prices) : 1;
      const openLine = this.chart.addSeries(LineSeries, {
        color: '#60a5fa', lineWidth: 1, lineStyle: 2,
        priceLineVisible: false, lastValueVisible: false,
      });
      openLine.setData([{ time: openTime, value: minP * 0.999 }, { time: openTime, value: maxP * 1.001 }]);
      this.markerLines.push(openLine);
      const closeLine = this.chart.addSeries(LineSeries, {
        color: '#fb923c', lineWidth: 1, lineStyle: 2,
        priceLineVisible: false, lastValueVisible: false,
      });
      closeLine.setData([{ time: closeTime, value: minP * 0.999 }, { time: closeTime, value: maxP * 1.001 }]);
      this.markerLines.push(closeLine);
    }
  }
}
