import { Component, ElementRef, inject, input, PLATFORM_ID, afterNextRender, effect, OnDestroy } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { ColorType, createChart, LineSeries, type IChartApi, type UTCTimestamp } from 'lightweight-charts';

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
})
export class EquityChartComponent implements OnDestroy {
  readonly title = input('Equity Curve');
  readonly data = input<ChartPoint[]>([]);
  readonly lineColor = input('#10b981');
  readonly showDrawdown = input(true);
  readonly showBalance = input(false);

  private el = inject(ElementRef);
  private platformId = inject(PLATFORM_ID);
  private chart: IChartApi | null = null;
  private equitySeries: any = null;
  private balanceSeries: any = null;
  private ddSeries: any = null;

  constructor() {
    afterNextRender(() => {
      if (!isPlatformBrowser(this.platformId)) return;
      this.initChart();
    });
    effect(() => {
      this.updateData(this.data(), this.showBalance(), this.showDrawdown());
    });
  }

  private initChart(): void {
    const container = this.el.nativeElement.querySelector('.chart-host') as HTMLDivElement;
    if (!container || this.chart) return;

    this.chart = createChart(container, {
      width: container.clientWidth,
      height: 288,
      layout: { background: { type: ColorType.Solid, color: 'transparent' }, textColor: '#9ca3af' },
      grid: { vertLines: { color: '#1f2937' }, horzLines: { color: '#1f2937' } },
      timeScale: { timeVisible: true, borderColor: '#374151' },
      rightPriceScale: { borderColor: '#374151' },
    });

    this.equitySeries = this.chart.addSeries(LineSeries, { color: this.lineColor(), lineWidth: 2 });
  }

  private updateData(points: ChartPoint[], showBal: boolean, showDD: boolean): void {
    if (!this.equitySeries || points.length === 0) return;
    const equityData = points.map((d) => ({ time: (d.time / 1000) as UTCTimestamp, value: d.value }));

    const hasBalance = showBal && points.some((p) => p.balance != null);
    if (hasBalance && !this.balanceSeries && this.chart) {
      this.balanceSeries = this.chart.addSeries(LineSeries, { color: '#60a5fa', lineWidth: 1, lineStyle: 1 });
    }
    if (hasBalance && this.balanceSeries) {
      this.balanceSeries.setData(
        points
          .filter((p) => p.balance != null)
          .map((p) => ({ time: (p.time / 1000) as UTCTimestamp, value: p.balance! })),
      );
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
        ddPoints.push({ time: (p.time / 1000) as UTCTimestamp, value: peak > 0 ? ((p.value - peak) / peak) * 100 : 0 });
      }
      if (!this.ddSeries) {
        this.ddSeries = this.chart!.addSeries(LineSeries, {
          color: '#ef4444',
          lineWidth: 1,
          priceScaleId: 'dd',
        });
        this.chart!.priceScale('dd').applyOptions({ mode: 2, invertScale: true, visible: false });
      }
      this.ddSeries.setData(ddPoints);
    } else {
      if (this.ddSeries) {
        this.chart?.removeSeries(this.ddSeries);
        this.ddSeries = null;
      }
    }
    this.equitySeries.setData(equityData);
  }

  ngOnDestroy(): void {
    // iter-38 S8 W-A2 / NG-R7: prevent chart memory leak (chart instances never disposed before).
    this.chart?.remove();
  }
}
