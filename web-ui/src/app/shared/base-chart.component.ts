import { inject, OnDestroy, PLATFORM_ID, afterNextRender, effect, ElementRef, Directive } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { ColorType, createChart, type IChartApi, type UTCTimestamp } from 'lightweight-charts';
import { queryHost } from './dom.helper';
import { toUtcTimestamp } from './chart-time.helper';

@Directive()
export abstract class BaseChartComponent implements OnDestroy {
  protected el = inject(ElementRef);
  protected platformId = inject(PLATFORM_ID);
  protected chart: IChartApi | null = null;
  protected resizeObserver: ResizeObserver | null = null;

  // effect() called in a field initializer (valid injection context) instead of the
  // constructor. In lazy-loaded @Component subclasses of a @Directive-decorated base,
  // the constructor runs before Angular sets up the directive-level injection context,
  // so effect() → inject(DestroyRef) fails with NG0203. Field initializers run later in
  // the init sequence, after the injector is ready.
  private _updateEffect = effect(() => this.updateChart());

  constructor() {
    afterNextRender(() => {
      if (!isPlatformBrowser(this.platformId)) return;
      this.initChart();
      // Initial paint: the field-initializer effect already ran once (while the series was still
      // null → no-op). When the chart is created AFTER its data input is set (e.g. a report whose
      // chart is gated behind `@if (data.length > 1)`), the data signal never changes again, so the
      // effect never re-fires. Render the current data once here, now that the series exists.
      this.updateChart();
    });
  }

  protected initChartBase(
    containerSelector: string,
    width: number,
    height: number,
    options?: Record<string, unknown>,
  ): HTMLDivElement | null {
    const container = queryHost(this.el, containerSelector) as HTMLDivElement;
    if (!container || this.chart) return null;

    this.chart = createChart(container, {
      width,
      height,
      layout: { background: { type: ColorType.Solid, color: 'transparent' }, textColor: '#9ca3af' },
      grid: { vertLines: { color: '#1f2937' }, horzLines: { color: '#1f2937' } },
      ...options,
    });

    this.resizeObserver = new ResizeObserver(() => {
      if (!this.chart || !container) return;
      this.chart.resize(container.clientWidth, container.clientHeight);
    });
    this.resizeObserver.observe(container);
    return container;
  }

  protected abstract initChart(): void;
  protected abstract updateChart(): void;

  ngOnDestroy(): void {
    this.resizeObserver?.disconnect();
    this.chart?.remove();
  }
}
