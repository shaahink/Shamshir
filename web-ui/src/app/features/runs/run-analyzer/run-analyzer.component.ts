import { Component, inject, OnInit, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { RunsApiService } from '../runs.service';
import { HistogramChartComponent, type HistogramBin } from '../../../shared/histogram-chart.component';
import { ScatterChartComponent, type ScatterPoint } from '../../../shared/scatter-chart.component';
import type { RunAnalytics } from '../../../models/api.types';

@Component({
  selector: 'app-run-analyzer',
  standalone: true,
  imports: [RouterLink, HistogramChartComponent, ScatterChartComponent],
  template: `
    <div class="space-y-6">
      <div class="flex items-center justify-between">
        <h1 class="text-xl font-semibold">Analyzer</h1>
        <a
          [routerLink]="['/runs', runId()]"
          class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800"
          >Back to Report</a
        >
      </div>

      @if (analytics()) {
        <div class="grid gap-4 md:grid-cols-2">
          <app-histogram-chart title="R-Multiple Distribution" [data]="rData()" color="#34d399" />
          <app-histogram-chart title="Holding Time (s)" [data]="holdData()" color="#60a5fa" />
          <app-histogram-chart title="PnL by Hour (UTC)" [data]="hourData()" color="#fbbf24" />
          <app-histogram-chart title="PnL by Day of Week" [data]="dayData()" color="#a78bfa" />
        </div>
        <app-scatter-chart title="MAE vs MFE" [data]="maeMfeData()" xLabel="MAE" yLabel="MFE" color="#f472b6" />
      } @else {
        <div class="py-12 text-center text-sm text-gray-500">Loading analytics...</div>
      }
    </div>
  `,
})
export class RunAnalyzerComponent implements OnInit {
  private route = inject(ActivatedRoute);
  private api = inject(RunsApiService);
  runId = signal('');
  analytics = signal<RunAnalytics | null>(null);

  rData = () => this.bucket(this.analytics()?.rMultiples ?? [], 20, 0);
  holdData = () => this.bucket(this.analytics()?.holdingTimes ?? [], 20, 0);
  hourData = () => (this.analytics()?.pnlByHour ?? []).map((d, i) => ({ time: i, value: d.value }));
  dayData = () => (this.analytics()?.pnlByDay ?? []).map((d, i) => ({ time: i, value: d.value }));
  maeMfeData = () => (this.analytics()?.maeMfe ?? []).map((d) => ({ x: d.x, y: d.y }));

  private bucket(values: number[], bins: number, _: number): HistogramBin[] {
    if (!values.length) return [];
    const min = Math.min(...values);
    const max = Math.max(...values);
    const range = max - min || 1;
    const width = range / bins;
    const counts = new Array(bins).fill(0);
    for (const v of values) {
      const i = Math.min(bins - 1, Math.floor((v - min) / width));
      counts[i]++;
    }
    return counts.map((c, i) => ({ time: i, value: c }));
  }

  async ngOnInit(): Promise<void> {
    const runId = this.route.snapshot.paramMap.get('runId');
    if (!runId) return;
    this.runId.set(runId);
    try {
      this.analytics.set(await this.api.getRunAnalytics(runId));
    } catch {
      /* */
    }
  }
}
