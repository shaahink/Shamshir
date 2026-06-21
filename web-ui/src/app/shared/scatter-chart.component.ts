import { Component, ElementRef, inject, input, PLATFORM_ID, afterNextRender, effect, OnDestroy, ChangeDetectionStrategy } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { ColorType, createChart, LineSeries, type IChartApi, type UTCTimestamp } from 'lightweight-charts';
import { queryHost } from './dom.helper';

export interface ScatterPoint {
  x: number;
  y: number;
}

@Component({
  selector: 'app-scatter-chart',
  standalone: true,
  template: `<div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
    <h3 class="mb-2 text-sm font-medium text-gray-400">{{ title() }}</h3>
    <div class="h-72 w-full chart-host"></div>
  </div>`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ScatterChartComponent implements OnDestroy {
  readonly title = input('Scatter');
  readonly data = input<ScatterPoint[]>([]);
  readonly color = input('#10b981');
  readonly xLabel = input('X');
  readonly yLabel = input('Y');

  private el = inject(ElementRef);
  private platformId = inject(PLATFORM_ID);
  private chart: IChartApi | null = null;
  private seriesY: any = null;
  private seriesX: any = null;
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
      height: 288,
      layout: { background: { type: ColorType.Solid, color: 'transparent' }, textColor: '#9ca3af' },
      grid: { vertLines: { color: '#1f2937' }, horzLines: { color: '#1f2937' } },
      rightPriceScale: { borderColor: '#374151' },
    });
    this.seriesY = this.chart.addSeries(LineSeries, {
      color: this.color(),
      lineVisible: false,
      pointMarkersVisible: true,
      lastValueVisible: false,
    } as any);
    this.seriesX = this.chart.addSeries(LineSeries, {
      color: '#f59e0b',
      lineVisible: false,
      pointMarkersVisible: true,
      lastValueVisible: false,
    } as any);
    this.updateData();

    this.resizeObserver = new ResizeObserver(() => {
      if (!this.chart || !container) return;
      this.chart.resize(container.clientWidth, container.clientHeight);
    });
    this.resizeObserver.observe(container);
  }

  private updateData(): void {
    if (!this.seriesY || !this.seriesX || !this.chart) return;
    const ptsY = this.data().map((d, i) => ({ time: i as UTCTimestamp, value: d.y }));
    const ptsX = this.data().map((d, i) => ({ time: i as UTCTimestamp, value: d.x }));
    this.seriesY.setData(ptsY);
    this.seriesX.setData(ptsX);
  }

  ngOnDestroy(): void {
    this.resizeObserver?.disconnect();
    this.chart?.remove();
  }
}
