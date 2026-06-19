import { Component, inject, OnInit, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { StatTileComponent } from '../../shared/stat-tile.component';
import { DataTableComponent, type ColumnDef } from '../../shared/data-table.component';
import type { GovernorState, ProtectionDay } from '../../models/api.types';

@Component({
  selector: 'app-compliance',
  standalone: true,
  imports: [StatTileComponent, DataTableComponent],
  template: `
    <div class="space-y-6">
      <h1 class="text-xl font-semibold">Compliance</h1>

      @if (governor(); as g) {
        <div class="grid gap-3 md:grid-cols-4">
          <app-stat-tile label="State" [value]="g.state"
            [positive]="g.state==='Normal'" [negative]="g.state!=='Normal'" />
          <app-stat-tile label="Consecutive Losses" [value]="g.consecutiveLosses" />
          <app-stat-tile label="Size Multiplier" [value]="g.sizeMultiplier.toFixed(2)" />
          <app-stat-tile label="Daily PnL %"
            [value]="(g.dayNetPnLFraction * 100).toFixed(2) + '%'"
            [negative]="g.dayNetPnLFraction < 0" />
          <app-stat-tile label="Distance to Limit"
            [value]="(g.distanceToDailyLimitFraction * 100).toFixed(1) + '%'"
            [positive]="g.distanceToDailyLimitFraction > 0.5" />
          @if (g.reason) {
            <app-stat-tile label="Reason" [value]="g.reason" />
          }
        </div>
      }

      <div>
        <h2 class="mb-3 text-sm font-medium text-gray-400">Daily Protection Ledger</h2>
        <app-data-table [columns]="dayColumns" [data]="$any(days())" />
      </div>
    </div>
  `,
})
export class ComplianceComponent implements OnInit {
  private http = inject(HttpClient);
  governor = signal<GovernorState | null>(null);
  days = signal<ProtectionDay[]>([]);

  dayColumns: ColumnDef[] = [
    { key: 'date', label: 'Date' },
    { key: 'startEquity', label: 'Start', format: 'currency' },
    { key: 'minEquity', label: 'Min', format: 'currency' },
    { key: 'endEquity', label: 'End', format: 'currency' },
    { key: 'maxDailyDdUsedFraction', label: 'Max DD Used', format: 'percent' },
    { key: 'tradesOpened', label: 'Opened' },
    { key: 'tradesClosed', label: 'Closed' },
    { key: 'signalsBlocked', label: 'Blocked' },
    { key: 'breachOccurred', label: 'Breach' },
  ];

  async ngOnInit(): Promise<void> {
    const [gov, protDays] = await Promise.all([
      firstValueFrom(this.http.get<GovernorState>('/api/governor/state')),
      firstValueFrom(this.http.get<ProtectionDay[]>('/api/protection/days')),
    ]);
    this.governor.set(gov);
    this.days.set(protDays);
  }
}
