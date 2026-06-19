import { Component, ElementRef, inject, input, PLATFORM_ID, afterNextRender, effect } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { ColorType, createChart, LineSeries, type IChartApi, type UTCTimestamp } from 'lightweight-charts';

export interface ScatterPoint { x: number; y: number }

@Component({
  selector: 'app-scatter-chart',
  standalone: true,
  template: `<div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
    <h3 class="mb-2 text-sm font-medium text-gray-400">{{ title() }}</h3>
    <div class="h-72 w-full chart-host"></div>
  </div>`,
})
export class ScatterChartComponent {
  readonly title = input('Scatter');
  readonly data = input<ScatterPoint[]>([]);
  readonly color = input('#10b981');
  readonly xLabel = input('X');
  readonly yLabel = input('Y');

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
      width: container.clientWidth, height: 288,
      layout: { background: { type: ColorType.Solid, color: 'transparent' }, textColor: '#9ca3af' },
      grid: { vertLines: { color: '#1f2937' }, horzLines: { color: '#1f2937' } },
      rightPriceScale: { borderColor: '#374151' },
    });
    this.series = this.chart.addSeries(LineSeries, {
      color: this.color(), lineVisible: false, pointMarkersVisible: true,
    } as any);
    this.updateData();
  }

  private updateData(): void {
    if (!this.series || !this.chart) return;
    const pts = this.data().map((d, i) => ({
      time: i as UTCTimestamp, value: d.y,
    }));
    this.series.setData(pts);
  }
}
