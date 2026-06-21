import { Component, ElementRef, inject, input, PLATFORM_ID, afterNextRender, effect, OnDestroy, ChangeDetectionStrategy } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { ColorType, createChart, HistogramSeries, type IChartApi, type UTCTimestamp } from 'lightweight-charts';
import { queryHost } from './dom.helper';

export interface HistogramBin {
  time: number;
  value: number;
}

@Component({
  selector: 'app-histogram-chart',
  standalone: true,
  template: `<div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
    <h3 class="mb-2 text-sm font-medium text-gray-400">{{ title() }}</h3>
    <div class="h-64 w-full chart-host"></div>
  </div>`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class HistogramChartComponent implements OnDestroy {
  readonly title = input('Distribution');
  readonly data = input<HistogramBin[]>([]);
  readonly color = input('#10b981');

  private el = inject(ElementRef);
  private platformId = inject(PLATFORM_ID);
  private chart: IChartApi | null = null;
  private series: any = null;

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
      height: 256,
      layout: { background: { type: ColorType.Solid, color: 'transparent' }, textColor: '#9ca3af' },
      grid: { vertLines: { color: '#1f2937' }, horzLines: { color: '#1f2937' } },
      timeScale: { visible: false },
      rightPriceScale: { borderColor: '#374151' },
    });
    this.series = this.chart.addSeries(HistogramSeries, { color: this.color() });
    this.updateData();
  }

  private updateData(): void {
    if (!this.series || !this.chart) return;
    const pts = this.data().map((d) => ({ time: (d.time / 1000) as UTCTimestamp, value: d.value }));
    this.series.setData(pts);
  }

  ngOnDestroy(): void {
    // iter-38 S8 W-A2 / NG-R7: prevent chart memory leak (chart instances never disposed before).
    this.chart?.remove();
  }
}
