import { Component, input, ChangeDetectionStrategy } from '@angular/core';
import { LineSeries, type UTCTimestamp } from 'lightweight-charts';
import { BaseChartComponent, type LegendEntry } from './base-chart.component';
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
    this.equitySeries = this.chart.addSeries(LineSeries, {
      color: this.lineColor(), lineWidth: 2, lineType: this.lineStyle(),
    });
  }

  protected updateChart(): void {
    const raw = this.data();
    if (!this.equitySeries || raw.length === 0) return;

    const byTime = new Map<number, { value: number; balance?: number }>();
    for (const d of raw) byTime.set(toUtcTimestamp(d.time) as number, { value: d.value, balance: d.balance });
    const points = [...byTime.keys()]
      .sort((a, b) => a - b)
      .map((t) => ({ time: t as UTCTimestamp, ...byTime.get(t)! }));

    this.equitySeries.setData(points.map((d) => ({ time: d.time, value: d.value })));

    const showBal = this.showBalance();
    const showDD = this.showDrawdown();
    const hasBalance = showBal && points.some((p) => p.balance != null);

    const legends: LegendEntry[] = [{ name: 'Equity', color: this.lineColor() }];

    if (hasBalance && !this.balanceSeries && this.chart) {
      this.balanceSeries = this.chart.addSeries(LineSeries, {
        color: '#60a5fa', lineWidth: 1, lineStyle: 1,
      });
    }
    if (hasBalance && this.balanceSeries) {
      this.balanceSeries.setData(
        points.filter((p) => p.balance != null).map((p) => ({ time: p.time, value: p.balance! })),
      );
      legends.push({ name: 'Balance', color: '#60a5fa' });
    }
    if (!hasBalance && this.balanceSeries) {
      this.chart?.removeSeries(this.balanceSeries);
      this.balanceSeries = null;
    }

    if (showDD && points.length > 1) {
      let peak = points[0].value;
      const ddPoints: { time: UTCTimestamp; value: number }[] = [];
      for (const p of points) {
        if (p.value > peak) peak = p.value;
        ddPoints.push({ time: p.time, value: peak > 0 ? ((p.value - peak) / peak) * 100 : 0 });
      }
      if (!this.ddSeries && this.chart) {
        this.ddSeries = this.chart.addSeries(LineSeries, {
          color: '#ef4444', lineWidth: 1, lineType: this.lineStyle(), priceScaleId: 'dd',
        });
        this.chart.priceScale('dd').applyOptions({ mode: 2, invertScale: true, visible: false });
      }
      this.ddSeries?.setData(ddPoints);
      legends.push({ name: 'Drawdown', color: '#ef4444' });
    } else {
      if (this.ddSeries) {
        this.chart?.removeSeries(this.ddSeries);
        this.ddSeries = null;
      }
    }

    this.legendEntries.set(legends);
    this.fitContent();
  }
}
