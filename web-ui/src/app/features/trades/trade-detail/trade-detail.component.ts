import { Component, inject, OnInit, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { ActivatedRoute } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { StatTileComponent } from '../../../shared/stat-tile.component';

@Component({
  selector: 'app-trade-detail',
  standalone: true,
  imports: [StatTileComponent],
  template: `
    <div class="space-y-6">
      <h1 class="text-xl font-semibold">Trade Detail</h1>

      @if (trade(); as t) {
        <div class="grid grid-cols-2 gap-3 md:grid-cols-4">
          <app-stat-tile label="Symbol" [value]="t.symbol" />
          <app-stat-tile label="Direction" [value]="t.direction" />
          <app-stat-tile label="Lots" [value]="t.lots" />
          <app-stat-tile label="Net P/L" [value]="t.netPnLAmount.toFixed(2)"
            [positive]="t.netPnLAmount > 0" [negative]="t.netPnLAmount < 0" />
          <app-stat-tile label="Entry" [value]="t.entryPrice" />
          <app-stat-tile label="Exit" [value]="t.exitPrice" />
          <app-stat-tile label="Pips" [value]="t.pnLPips.toFixed(1)" />
          <app-stat-tile label="R" [value]="t.rMultiple.toFixed(2)" />
          <app-stat-tile label="Commission" [value]="t.commissionAmount.toFixed(2)" />
          <app-stat-tile label="Swap" [value]="t.swapAmount.toFixed(2)" />
          <app-stat-tile label="MAE" [value]="t.maxAdverseExcursion.toFixed(1)" />
          <app-stat-tile label="MFE" [value]="t.maxFavorableExcursion.toFixed(1)" />
        </div>

        <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
          <h2 class="mb-2 text-sm font-medium text-gray-400">Exit Reason</h2>
          <p class="text-sm text-gray-300">{{ t.exitReason }}</p>
          <p class="mt-1 text-xs text-gray-500">Strategy: {{ t.strategyId }}</p>
        </div>
      }
    </div>
  `,
})
export class TradeDetailComponent implements OnInit {
  private route = inject(ActivatedRoute);
  private http = inject(HttpClient);
  trade = signal<any>(null);

  async ngOnInit(): Promise<void> {
    const id = this.route.snapshot.paramMap.get('id');
    if (!id) return;
    const data = await firstValueFrom(this.http.get<any>(`/api/trades/${id}`));
    this.trade.set(data);
  }
}
