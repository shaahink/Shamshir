import { inject, OnDestroy, OnChanges, SimpleChanges, PLATFORM_ID, afterNextRender, effect, ElementRef, Directive, signal, Input } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { ColorType, createChart, LineType, type IChartApi } from 'lightweight-charts';
import { queryHost } from './dom.helper';
import { toUtcTimestamp } from './chart-time.helper';

export interface LegendEntry {
  name: string;
  color: string;
}

@Directive()
export abstract class BaseChartComponent implements OnDestroy, OnChanges {
  protected el = inject(ElementRef);
  protected platformId = inject(PLATFORM_ID);
  protected chart: IChartApi | null = null;
  protected resizeObserver: ResizeObserver | null = null;

  // P3.1: shared legend — subclasses push entries during init/update.
  protected legendEntries = signal<LegendEntry[]>([]);
  // P3.3: smooth line rendering (curved vs. straight). @Input so subclasses can bind.
  @Input() smooth = true;
  private _updateEffect = effect(() => this.updateChart());

  constructor() {
    afterNextRender(() => {
      if (!isPlatformBrowser(this.platformId)) return;
      this.initChart();
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

  // P3.2: auto-fit time scale to visible data after update.
  protected fitContent(): void {
    this.chart?.timeScale().fitContent();
  }

  // P3.3: return LineType.Curved when smooth is true, Simple otherwise.
  protected lineStyle(): number {
    return this.smooth ? LineType.Curved : LineType.Simple;
  }

  protected abstract initChart(): void;
  protected abstract updateChart(): void;

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['smooth']) this.updateChart();
  }

  ngOnDestroy(): void {
    this.resizeObserver?.disconnect();
    this.chart?.remove();
  }
}
