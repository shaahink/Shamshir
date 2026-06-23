import { Component, ElementRef, inject, input, PLATFORM_ID, afterNextRender, effect, OnDestroy, ChangeDetectionStrategy } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import {
  ColorType,
  createChart,
  CandlestickSeries,
  LineSeries,
  type IChartApi,
  type UTCTimestamp,
} from 'lightweight-charts';
import { queryHost } from './dom.helper';
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
export class CandleChartComponent implements OnDestroy {
  readonly title = input('Price Chart');
  readonly bars = input<OhlcBar[]>([]);
  readonly markers = input<PriceMarker[]>([]);

  private el = inject(ElementRef);
  private platformId = inject(PLATFORM_ID);
  private chart: IChartApi | null = null;
  private candleSeries: any = null;
  private markerLines: any[] = [];
  private resizeObserver: ResizeObserver | null = null;

  constructor() {
    afterNextRender(() => {
      if (!isPlatformBrowser(this.platformId)) return;
      this.initChart();
    });
    effect(() => this.updateData());
  }

  private initChart(): void {
    const container = queryHost(this.el, '.chart-host') as HTMLDivElement;
    if (!container || this.chart) return;

    this.chart = createChart(container, {
      width: container.clientWidth,
      height: 384,
      layout: { background: { type: ColorType.Solid, color: 'transparent' }, textColor: '#9ca3af' },
      grid: { vertLines: { color: '#1f2937' }, horzLines: { color: '#1f2937' } },
      timeScale: { timeVisible: true, borderColor: '#374151' },
      rightPriceScale: { borderColor: '#374151' },
    });

    this.candleSeries = this.chart.addSeries(CandlestickSeries, {
      upColor: '#10b981',
      downColor: '#ef4444',
      borderUpColor: '#10b981',
      borderDownColor: '#ef4444',
      wickUpColor: '#10b981',
      wickDownColor: '#ef4444',
    });

    // iter-38 W-A3: respond to viewport changes so charts don't clip/leave dead space on resize.
    this.resizeObserver = new ResizeObserver(() => {
      if (!this.chart || !container) return;
      this.chart.resize(container.clientWidth, container.clientHeight);
    });
    this.resizeObserver.observe(container);
  }

  private updateData(): void {
    if (!this.candleSeries) return;

    const candleData = this.bars().map((b) => ({
      time: toUtcTimestamp(b.time),
      open: b.open,
      high: b.high,
      low: b.low,
      close: b.close,
    }));
    this.candleSeries.setData(candleData.length > 0 ? candleData : []);

    this.markerLines.forEach((s) => this.chart?.removeSeries(s));
    this.markerLines = [];

    for (const m of this.markers()) {
      const ls = this.chart!.addSeries(LineSeries, {
        color: m.color,
        lineWidth: 1,
        lineStyle: 2,
        priceLineVisible: false,
        lastValueVisible: false,
      });
      const t0 = candleData[0]?.time ?? toUtcTimestamp(Date.now());
      const t1 = candleData[candleData.length - 1]?.time ?? t0;
      ls.setData([
        { time: t0, value: m.price },
        { time: t1, value: m.price },
      ]);
      this.markerLines.push(ls);
    }
  }
  ngOnDestroy(): void {
    this.resizeObserver?.disconnect();
    this.chart?.remove();
  }
}
