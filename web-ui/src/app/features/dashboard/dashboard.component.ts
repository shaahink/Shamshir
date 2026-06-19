import { Component, inject, OnInit, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { StatTileComponent } from '../../shared/stat-tile.component';
import { EquityChartComponent, type ChartPoint } from '../../shared/equity-chart.component';
import type { GovernorState } from '../../models/api.types';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [StatTileComponent, EquityChartComponent],
  template: `
    <div class="space-y-6">
      <h1 class="text-xl font-semibold">Dashboard</h1>

      <div class="grid grid-cols-2 gap-3 md:grid-cols-4">
        <app-stat-tile label="Status" [value]="status()" [positive]="status()==='Active'" [negative]="status()!=='Active'" />
        <app-stat-tile label="Daily DD" [value]="(dailyDdPct() * 100).toFixed(2) + '%'" [negative]="dailyDdPct() > 0.01" />
        <app-stat-tile label="Max DD" [value]="(maxDdPct() * 100).toFixed(2) + '%'" [negative]="maxDdPct() > 0.01" />
        <app-stat-tile label="Trades Today" [value]="tradesToday()" />
        <app-stat-tile label="Equity" [value]="equity().toFixed(0)" />
        <app-stat-tile label="Open Positions" [value]="openPositions()" />
        <app-stat-tile label="Governor" [value]="governor().state" [negative]="governor().state!=='Normal'" />
        <app-stat-tile label="Daily Limit Left" [value]="(distanceToLimit() * 100).toFixed(1) + '%'" [positive]="distanceToLimit() > 0.5" />
      </div>

      @if (governor().reason) {
        <div class="rounded-lg border border-yellow-800 bg-yellow-900/10 p-3 text-sm text-yellow-400">{{ governor().reason }}</div>
      }

      @if (equityPoints().length > 2) {
        <app-equity-chart title="Equity Curve" [data]="equityPoints()" [showDrawdown]="true" />
      } @else {
        <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-12 text-center"><p class="text-sm text-gray-500">No equity data yet. Start a run to see performance.</p></div>
      }
    </div>
  `,
})
export class DashboardComponent implements OnInit {
  private http = inject(HttpClient);

  status = signal('--');
  dailyDdPct = signal(0);
  maxDdPct = signal(0);
  tradesToday = signal(0);
  equity = signal(0);
  openPositions = signal(0);
  distanceToLimit = signal(0);
  governor = signal<GovernorState>({ state: '--', sizeMultiplier: 1, consecutiveLosses: 0, dayNetPnLFraction: 0, distanceToDailyLimitFraction: 0, reason: null });
  equityPoints = signal<ChartPoint[]>([]);

  async ngOnInit(): Promise<void> {
    try {
      const [gov, equity] = await Promise.all([
        firstValueFrom(this.http.get<GovernorState>('/api/governor/state')),
        firstValueFrom(this.http.get<{ timestampUtc: string; equity: number }[]>('/api/equity')),
      ]);
      this.governor.set(gov);
      this.dailyDdPct.set(gov.dayNetPnLFraction < 0 ? Math.abs(gov.dayNetPnLFraction) : 0);
      this.distanceToLimit.set(gov.distanceToDailyLimitFraction);
      this.status.set(gov.state === 'HardStop' ? 'Halted' : 'Active');
      this.equityPoints.set(equity.map((p) => ({ time: new Date(p.timestampUtc).getTime(), value: p.equity })));
      if (equity.length > 0) this.equity.set(equity[equity.length - 1].equity);
    } catch {
      this.status.set('Offline');
    }
  }
}
