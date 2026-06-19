import { Component, ElementRef, inject, input, PLATFORM_ID, afterNextRender, effect } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { ColorType, createChart, HistogramSeries, type IChartApi, type UTCTimestamp } from 'lightweight-charts';

export interface HistogramBin { time: number; value: number }

@Component({
  selector: 'app-histogram-chart',
  standalone: true,
  template: `<div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
    <h3 class="mb-2 text-sm font-medium text-gray-400">{{ title() }}</h3>
    <div class="h-64 w-full chart-host"></div>
  </div>`,
})
export class HistogramChartComponent {
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
    const container = this.el.nativeElement.querySelector('.chart-host') as HTMLDivElement;
    if (!container || this.chart) return;
    this.chart = createChart(container, {
      width: container.clientWidth, height: 256,
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
}
