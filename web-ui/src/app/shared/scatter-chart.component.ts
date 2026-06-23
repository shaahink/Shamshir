import { Component, input, ChangeDetectionStrategy } from '@angular/core';
import { LineSeries, type UTCTimestamp } from 'lightweight-charts';
import { BaseChartComponent } from './base-chart.component';

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
export class ScatterChartComponent extends BaseChartComponent {
  readonly title = input('Scatter');
  readonly data = input<ScatterPoint[]>([]);
  readonly color = input('#f472b6');
  readonly xLabel = input('X');
  readonly yLabel = input('Y');

  private xSeries: any = null;
  private ySeries: any = null;

  protected initChart(): void {
    const container = this.initChartBase('.chart-host', 0, 288, {
      timeScale: { visible: false },
      rightPriceScale: { borderColor: '#374151' },
    });
    if (!container || !this.chart) return;

    this.ySeries = this.chart.addSeries(LineSeries, { lineWidth: 2, priceLineVisible: false, lastValueVisible: false, color: this.color() });
    this.xSeries = this.chart.addSeries(LineSeries, { lineWidth: 2, priceLineVisible: false, lastValueVisible: false, color: '#60a5fa', priceScaleId: 'x' });
    this.chart.priceScale('x').applyOptions({ mode: 2, invertScale: true, visible: false });
  }

  protected updateChart(): void {
    if (!this.xSeries || !this.ySeries || !this.chart) return;
    const ptsY = this.data().map((d, i) => ({ time: i as UTCTimestamp, value: d.y }));
    const ptsX = this.data().map((d, i) => ({ time: i as UTCTimestamp, value: d.x }));
    this.xSeries.setData(ptsX);
    this.ySeries.setData(ptsY);
  }
}
