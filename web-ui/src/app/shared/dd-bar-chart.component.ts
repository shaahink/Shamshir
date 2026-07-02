import { Component, input, computed, ChangeDetectionStrategy } from '@angular/core';
import { HistogramSeries, LineSeries, AreaSeries } from 'lightweight-charts';
import { BaseChartComponent, type LegendEntry } from './base-chart.component';
import { toUtcTimestamp } from './chart-time.helper';
import type { DailyPnl } from '../models/api.types';

@Component({
  selector: 'app-dd-bar-chart',
  standalone: true,
  template: `<div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
    <div class="mb-3 flex items-center justify-between">
      <h3 class="text-sm font-medium text-gray-400">Daily Drawdown</h3>
      <div class="flex gap-3 text-xs text-gray-500">
        <span>Worst day: <span class="text-red-400">{{ worstDay() }}</span></span>
        <span>Days &gt;50% limit: <span class="text-amber-400">{{ daysOverHalf() }}</span></span>
      </div>
    </div>
    <div class="h-64 w-full chart-host"></div>
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
export class DdBarChartComponent extends BaseChartComponent {
  readonly data = input<DailyPnl[]>([]);
  readonly limitPct = input(0.05);
  readonly title = input('Daily Drawdown');

  private histSeries: any = null;
  private limitLine: any = null;
  private underwaterSeries: any = null;

  worstDay = computed(() => {
    const d = this.data();
    if (d.length === 0) return '--';
    const min = Math.min(...d.map(p => p.pnl));
    return min.toFixed(2);
  });

  daysOverHalf = computed(() => {
    const d = this.data();
    const limit = this.limitPct();
    const halfLimit = limit * 0.5;
    return d.filter(p => Math.abs(p.pnl) > halfLimit).length;
  });

  protected initChart(): void {
    const container = this.initChartBase('.chart-host', 0, 256, {
      timeScale: { timeVisible: true, borderColor: '#374151' },
      rightPriceScale: { borderColor: '#374151' },
    });
    if (!container || !this.chart) return;

    this.histSeries = this.chart.addSeries(HistogramSeries, {
      color: '#ef4444',
    });
  }

  protected updateChart(): void {
    const d = this.data();
    if (!this.histSeries || !this.chart || d.length === 0) return;

    const bars = d.map((p) => ({
      time: toUtcTimestamp(new Date(p.date + 'T00:00:00Z').getTime()),
      value: p.pnl,
      color: p.pnl < 0 ? '#ef4444' : '#10b981',
    }));
    this.histSeries.setData(bars);

    // Remove old limit line
    if (this.limitLine) { this.chart.removeSeries(this.limitLine); this.limitLine = null; }
    if (this.underwaterSeries) { this.chart.removeSeries(this.underwaterSeries); this.underwaterSeries = null; }

    // Draw 5% (or configured) limit line
    const t0 = bars[0]?.time ?? toUtcTimestamp(Date.now());
    const t1 = bars[bars.length - 1]?.time ?? t0;
    const balance = 100000; // approximate — actual balance from run detail
    const limitVal = -(balance * this.limitPct());

    this.limitLine = this.chart.addSeries(LineSeries, {
      color: '#fbbf24', lineWidth: 1, lineStyle: 2,
      priceLineVisible: false, lastValueVisible: false,
    });
    this.limitLine.setData([{ time: t0, value: limitVal }, { time: t1, value: limitVal }]);

    // Underwater area (running cumulative PnL)
    this.underwaterSeries = this.chart.addSeries(AreaSeries, {
      lineColor: 'rgba(251, 191, 36, 0.4)',
      topColor: 'rgba(59, 130, 246, 0.05)',
      bottomColor: 'rgba(59, 130, 246, 0.0)',
      lineWidth: 1,
    });
    let cum = 0;
    const underwater = d.map((p) => {
      cum += p.pnl;
      return { time: toUtcTimestamp(new Date(p.date + 'T00:00:00Z').getTime()), value: cum };
    });
    this.underwaterSeries.setData(underwater);

    this.legendEntries.set([
      { name: 'Daily PnL', color: '#ef4444' },
      { name: 'Limit (' + (this.limitPct() * 100).toFixed(1) + '%)', color: '#fbbf24' },
      { name: 'Cumulative', color: 'rgba(251, 191, 36, 0.4)' },
    ]);
    this.fitContent();
  }
}
