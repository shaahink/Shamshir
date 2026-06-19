import { Component, inject, OnInit, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { ActivatedRoute } from '@angular/router';
import { DatePipe } from '@angular/common';
import { firstValueFrom } from 'rxjs';
import { StatTileComponent } from '../../../shared/stat-tile.component';
import type { TradeSummary } from '../../../models/api.types';

@Component({
  selector: 'app-trade-detail',
  standalone: true,
  imports: [DatePipe, StatTileComponent],
  template: `
    <div class="space-y-6">
      <h1 class="text-xl font-semibold">Trade Detail</h1>
      @if (trade(); as t) {
        <div class="grid grid-cols-2 gap-3 md:grid-cols-4">
          <app-stat-tile label="Symbol" [value]="t.symbol" />
          <app-stat-tile label="Direction" [value]="t.direction" [positive]="t.direction==='Long'" />
          <app-stat-tile label="Lots" [value]="t.lots" />
          <app-stat-tile label="Net P/L" [value]="t.netPnLAmount.toFixed(2)" [positive]="t.netPnLAmount>0" [negative]="t.netPnLAmount<0" />
          <app-stat-tile label="Entry" [value]="t.entryPrice" />
          <app-stat-tile label="Exit" [value]="t.exitPrice" />
          <app-stat-tile label="Pips" [value]="t.pnLPips.toFixed(1)" />
          <app-stat-tile label="R" [value]="t.rMultiple.toFixed(2)" [positive]="t.rMultiple>1" />
          <app-stat-tile label="Commission" [value]="t.commissionAmount.toFixed(2)" [negative]="t.commissionAmount<0" />
          <app-stat-tile label="Swap" [value]="t.swapAmount.toFixed(2)" [negative]="t.swapAmount<0" />
          <app-stat-tile label="Gross P/L" [value]="t.grossPnLAmount.toFixed(2)" [positive]="t.grossPnLAmount>0" [negative]="t.grossPnLAmount<0" />
          <app-stat-tile label="MAE" [value]="t.maxAdverseExcursion.toFixed(1)" />
          <app-stat-tile label="MFE" [value]="t.maxFavorableExcursion.toFixed(1)" />
          <app-stat-tile label="Hold Time" [value]="formatDuration(t.durationSeconds)" />
          <app-stat-tile label="Opened" [value]="t.openedAtUtc | date:'MM-dd HH:mm'" />
          <app-stat-tile label="Closed" [value]="t.closedAtUtc | date:'MM-dd HH:mm'" />
        </div>
        <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
          <h2 class="mb-1 text-sm font-medium text-gray-400">Exit: {{ t.exitReason }}</h2>
          <p class="text-xs text-gray-500">Strategy: {{ t.strategyId }}</p>
        </div>
      } @else {
        <div class="py-12 text-center text-sm text-gray-500">Trade not found.</div>
      }
    </div>
  `,
})
export class TradeDetailComponent implements OnInit {
  private route = inject(ActivatedRoute);
  private http = inject(HttpClient);
  trade = signal<TradeSummary | null>(null);

  async ngOnInit(): Promise<void> {
    const id = this.route.snapshot.paramMap.get('id');
    if (!id) return;
    const data = await firstValueFrom(this.http.get<TradeSummary>(`/api/trades/${id}`));
    this.trade.set(data);
  }

  formatDuration(seconds: number): string {
    const h = Math.floor(seconds / 3600);
    const m = Math.floor((seconds % 3600) / 60);
    return h > 0 ? `${h}h ${m}m` : `${m}m`;
  }
}
