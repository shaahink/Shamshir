import { Component, input, ChangeDetectionStrategy, Directive } from '@angular/core';
import { HistogramSeries } from 'lightweight-charts';
import { BaseChartComponent } from './base-chart.component';

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
export class HistogramChartComponent extends BaseChartComponent {
  readonly title = input('Distribution');
  readonly data = input<HistogramBin[]>([]);
  readonly color = input('#10b981');

  private series: any = null;

  protected initChart(): void {
    const container = this.initChartBase('.chart-host', 0, 256);
    if (!container || !this.chart) return;
    this.chart.applyOptions({ timeScale: { visible: false }, rightPriceScale: { borderColor: '#374151' } });
    this.series = this.chart.addSeries(HistogramSeries, { color: this.color() });
  }

  protected updateChart(): void {
    if (!this.series || !this.chart) return;
    const pts = this.data().map((d) => ({ time: d.time as any, value: d.value }));
    this.series.setData(pts);
  }
}
