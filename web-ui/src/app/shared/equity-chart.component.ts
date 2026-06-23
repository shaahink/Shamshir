import { Component, input, ChangeDetectionStrategy } from '@angular/core';
import { LineSeries } from 'lightweight-charts';
import { BaseChartComponent } from './base-chart.component';
import { toUtcTimestamp } from './chart-time.helper';

export interface ChartPoint {
  time: number;
  value: number;
  balance?: number;
}

@Component({
  selector: 'app-equity-chart',
  standalone: true,
  template: `<div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
    <h3 class="mb-2 text-sm font-medium text-gray-400">{{ title() }}</h3>
    <div class="h-72 w-full chart-host"></div>
  </div>`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class EquityChartComponent extends BaseChartComponent {
  readonly title = input('Equity Curve');
  readonly data = input<ChartPoint[]>([]);
  readonly lineColor = input('#10b981');
  readonly showDrawdown = input(true);
  readonly showBalance = input(false);

  private equitySeries: any = null;
  private balanceSeries: any = null;
  private ddSeries: any = null;

  protected initChart(): void {
    const container = this.initChartBase('.chart-host', 0, 288, {
      timeScale: { timeVisible: true, borderColor: '#374151' },
      rightPriceScale: { borderColor: '#374151' },
    });
    if (!container || !this.chart) return;
    this.equitySeries = this.chart.addSeries(LineSeries, { color: this.lineColor(), lineWidth: 2 });
  }

  protected updateChart(): void {
    const points = this.data();
    if (!this.equitySeries || points.length === 0) return;
    const equityData = points.map((d) => ({ time: toUtcTimestamp(d.time), value: d.value }));
    this.equitySeries.setData(equityData);

    const showBal = this.showBalance();
    const showDD = this.showDrawdown();
    const hasBalance = showBal && points.some((p) => p.balance != null);

    if (hasBalance && !this.balanceSeries && this.chart) {
      this.balanceSeries = this.chart.addSeries(LineSeries, { color: '#60a5fa', lineWidth: 1, lineStyle: 1 });
    }
    if (hasBalance && this.balanceSeries) {
      this.balanceSeries.setData(
        points.filter((p) => p.balance != null).map((p) => ({ time: toUtcTimestamp(p.time), value: p.balance! })),
      );
    }
    if (!hasBalance && this.balanceSeries) {
      this.chart?.removeSeries(this.balanceSeries);
      this.balanceSeries = null;
    }

    if (showDD && points.length > 1) {
      let peak = points[0].value;
      const ddPoints: { time: number; value: number }[] = [];
      for (const p of points) {
        if (p.value > peak) peak = p.value;
        ddPoints.push({ time: toUtcTimestamp(p.time), value: peak > 0 ? ((p.value - peak) / peak) * 100 : 0 });
      }
      if (!this.ddSeries && this.chart) {
        this.ddSeries = this.chart.addSeries(LineSeries, { color: '#ef4444', lineWidth: 1, priceScaleId: 'dd' });
        this.chart.priceScale('dd').applyOptions({ mode: 2, invertScale: true, visible: false });
      }
      this.ddSeries?.setData(ddPoints);
    } else {
      if (this.ddSeries) {
        this.chart?.removeSeries(this.ddSeries);
        this.ddSeries = null;
      }
    }
  }
}
