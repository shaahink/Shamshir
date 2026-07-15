import { Component, input, ChangeDetectionStrategy } from '@angular/core';
import { CandlestickSeries, LineSeries, LineType, createSeriesMarkers, type ISeriesMarkersPluginApi, type Time } from 'lightweight-charts';
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
// X3: the stop's journey (initial SL + BREAKEVEN/TRAIL moves), drawn as a stepped line.
export interface StopPathSegment {
  time: number; // ms
  price: number;
  kind: string;
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
  readonly stopPath = input<StopPathSegment[]>([]);
  readonly tradeOpenTime = input<number | null>(null);
  readonly tradeCloseTime = input<number | null>(null);
  readonly tradeDirection = input<string | null>(null);

  private candleSeries: any = null;
  private markerLines: any[] = [];
  private seriesMarkers: ISeriesMarkersPluginApi<Time> | null = null;

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
    // X3: v5 removed series.setMarkers() — markers live in this plugin now. The old call threw and
    // killed the whole update, which is one reason the trade chart never showed entry/exit arrows.
    this.seriesMarkers = createSeriesMarkers(this.candleSeries, []);
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
    const isShort = this.tradeDirection() === 'Short';
    const openT = this.tradeOpenTime();
    const closeT = this.tradeCloseTime();

    // Entry/exit = time-anchored directional arrows on the candles; SL/TP = level lines spanning
    // the trade window (not the whole chart); anything else = a full-width reference line.
    const arrowMarkers: PriceMarker[] = [];
    const levelMarkers: PriceMarker[] = [];
    for (const m of markers) {
      if (m.time && (m.label === 'Entry' || m.label.startsWith('Exit'))) arrowMarkers.push(m);
      else levelMarkers.push(m);
    }

    if (this.seriesMarkers) {
      const candleMarks = arrowMarkers
        .sort((a, b) => a.time! - b.time!)
        .map((m) => {
          const isEntry = m.label === 'Entry';
          // Long entry: buy below the bar; Long exit: sell above it. Mirrored for shorts.
          const below = isEntry ? !isShort : isShort;
          return {
            time: toUtcTimestamp(m.time!),
            position: below ? ('belowBar' as const) : ('aboveBar' as const),
            color: isEntry ? (isShort ? '#f87171' : '#34d399') : '#fbbf24',
            shape: below ? ('arrowUp' as const) : ('arrowDown' as const),
            text: `${m.label} ${this.fmtPrice(m.price)}`,
            size: 2,
          };
        });
      this.seriesMarkers.setMarkers(candleMarks);
    }

    // X3: the stop as it actually walked — stepped line from entry to exit through every BE/TRAIL move.
    const stops = this.stopPath();
    if (stops.length > 0 && closeT != null) {
      const stepData = stops
        .slice()
        .sort((a, b) => a.time - b.time)
        .map((p) => ({ time: toUtcTimestamp(p.time), value: p.price }));
      const last = stepData[stepData.length - 1];
      if (last.time < toUtcTimestamp(closeT))
        stepData.push({ time: toUtcTimestamp(closeT), value: last.value });
      const slSeries = this.chart.addSeries(LineSeries, {
        color: '#ef4444', lineWidth: 1, lineStyle: 2, lineType: LineType.WithSteps,
        priceLineVisible: false, lastValueVisible: false, pointMarkersVisible: true,
      });
      slSeries.setData(this.dedupeByTime(stepData));
      this.markerLines.push(slSeries);
      legends.push({ name: stops.length > 1 ? `SL (${stops.length - 1} moves)` : 'SL', color: '#ef4444' });
    }

    // SL/TP level lines: across the trade window when we know it, else full-width fallback.
    const t0 = candleData[0]?.time ?? toUtcTimestamp(Date.now());
    const t1 = candleData[candleData.length - 1]?.time ?? t0;
    for (const m of levelMarkers) {
      if (m.label === 'SL' && stops.length > 0) continue; // the stepped path already draws the stop
      const from = m.time != null ? toUtcTimestamp(m.time) : (openT != null ? toUtcTimestamp(openT) : t0);
      const to = closeT != null ? toUtcTimestamp(closeT) : t1;
      const ls = this.chart.addSeries(LineSeries, {
        color: m.color, lineWidth: 1, lineStyle: 2,
        priceLineVisible: false, lastValueVisible: false,
      });
      ls.setData(from < to ? [{ time: from, value: m.price }, { time: to, value: m.price }] : [{ time: t0, value: m.price }, { time: t1, value: m.price }]);
      this.markerLines.push(ls);
      legends.push({ name: `${m.label} ${this.fmtPrice(m.price)}`, color: m.color });
    }

    // NOTE: no vertical open/close lines — lightweight-charts cannot take two points at one
    // timestamp in a series (the pre-X3 code tried exactly that and threw). The entry/exit
    // arrows mark the trade window instead.

    this.legendEntries.set(legends);
    this.fitContent();
  }

  private fmtPrice(p: number): string {
    return p >= 100 ? p.toFixed(2) : p.toFixed(5).replace(/0+$/, '').replace(/\.$/, '');
  }

  // lightweight-charts rejects duplicate timestamps; two stop moves inside one bar keep the last.
  private dedupeByTime<T extends { time: any }>(points: T[]): T[] {
    const out: T[] = [];
    for (const p of points) {
      if (out.length > 0 && out[out.length - 1].time === p.time) out[out.length - 1] = p;
      else out.push(p);
    }
    return out;
  }
}
