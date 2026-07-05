import { Component, inject, OnInit, signal, ChangeDetectionStrategy } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { DatePipe } from '@angular/common';
import { StatTileComponent } from '../../../shared/stat-tile.component';
import { TradeChartCardComponent } from '../../../shared/trade-chart-card.component';
import type { TradeDetail, TradeExcursionResponse } from '../../../models/api.types';
import { TradesApiService } from '../trades.service';
import { formatDuration } from '../../../shared/format.helper';

@Component({
  selector: 'app-trade-detail',
  standalone: true,
  imports: [DatePipe, StatTileComponent, TradeChartCardComponent],
  template: `
    <div class="space-y-6">
      <h1 class="text-xl font-semibold">Trade Detail</h1>
      @if (trade(); as t) {
        <div class="grid grid-cols-2 gap-3 md:grid-cols-4">
          <app-stat-tile label="Symbol" [value]="t.symbol" />
          <app-stat-tile label="Direction" [value]="t.direction" [positive]="t.direction === 'Long'" />
          <app-stat-tile label="Lots" [value]="t.lots" />
          <app-stat-tile
            label="Net P/L"
            [value]="t.netPnLAmount.toFixed(2)"
            [positive]="t.netPnLAmount > 0"
            [negative]="t.netPnLAmount < 0"
          />
          <app-stat-tile label="Entry" [value]="t.entryPrice" />
          <app-stat-tile label="Exit" [value]="t.exitPrice" />
          <app-stat-tile label="Pips" [value]="t.pnLPips.toFixed(1)" />
          <app-stat-tile label="R" [value]="t.rMultiple.toFixed(2)" [positive]="t.rMultiple > 1" />
          <app-stat-tile label="Commission" [value]="t.commissionAmount.toFixed(2)" />
          <app-stat-tile label="Swap" [value]="t.swapAmount.toFixed(2)" />
          <app-stat-tile
            label="Gross P/L"
            [value]="t.grossPnLAmount.toFixed(2)"
            [positive]="t.grossPnLAmount > 0"
            [negative]="t.grossPnLAmount < 0"
          />
          <app-stat-tile label="MAE" [value]="t.maxAdverseExcursion.toFixed(1)" />
          <app-stat-tile label="MFE" [value]="t.maxFavorableExcursion.toFixed(1)" />
          <app-stat-tile label="Hold" [value]="fmtDuration(t.durationSeconds)" />
          <app-stat-tile label="Opened" [value]="t.openedAtUtc | date: 'MM-dd HH:mm'" />
          <app-stat-tile label="Closed" [value]="t.closedAtUtc | date: 'MM-dd HH:mm'" />
        </div>
        <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
          <h2 class="mb-1 text-sm font-medium text-gray-400">Exit: {{ t.exitReason }}</h2>
          <p class="text-xs text-gray-500">Strategy: {{ t.strategyId }}</p>
        </div>

        <app-trade-chart-card [tradeId]="t.id" />

        @if (excursionPoints().length > 0) {
          <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
            <h2 class="mb-2 text-sm font-medium text-gray-400">MAE/MFE Path</h2>
            <div class="flex h-32 items-end gap-px">
              @for (pt of excursionPoints(); track pt.t) {
                @if (pt.hi !== 0 || pt.lo !== 0) {
                  <div class="flex flex-1 flex-col justify-end" [title]="pt.t + 'm: hi=' + pt.hi.toFixed(1) + ' lo=' + pt.lo.toFixed(1)">
                    <div
                      class="w-full"
                      [style.height]="(Math.abs(pt.hi) / range() * 100) + '%'"
                      [style.background]="pt.hi > 0 ? '#22c55e' : '#ef4444'"
                    ></div>
                    <div
                      class="w-full"
                      [style.height]="(Math.abs(pt.lo) / range() * 100) + '%'"
                      [style.background]="pt.lo < 0 ? '#ef4444' : '#22c55e'"
                    ></div>
                  </div>
                }
              }
            </div>
            <div class="mt-1 flex justify-between text-xs text-gray-500">
              <span>&#8593; Green = favorable</span>
              <span>Red = adverse &#8595;</span>
            </div>
          </div>
        }
      } @else {
        <div class="py-12 text-center text-sm text-gray-500">Trade not found.</div>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TradeDetailComponent implements OnInit {
  private route = inject(ActivatedRoute);
  private api = inject(TradesApiService);
  trade = signal<TradeDetail | null>(null);
  excursionPoints = signal<{ t: number; hi: number; lo: number }[]>([]);
  range = signal<number>(0.01);

  async ngOnInit(): Promise<void> {
    const id = this.route.snapshot.paramMap.get('id');
    if (!id) return;
    try {
      const t = await this.api.getById(id);
      this.trade.set(t);
      // Load excursion path for MAE/MFE chart
      this.loadExcursions(id);
    } catch {
      this.trade.set(null);
    }
  }

  private async loadExcursions(id: string): Promise<void> {
    try {
      const r = await this.api.getExcursions(id);
      const parsed: { t: number; hi: number; lo: number }[] = JSON.parse(r.pathJson || '[]');
      this.excursionPoints.set(parsed);
      if (parsed.length > 0) {
        const max = Math.max(
          ...parsed.map((p) => Math.abs(p.hi)),
          ...parsed.map((p) => Math.abs(p.lo)),
          1,
        );
        this.range.set(max);
      }
    } catch {
      this.excursionPoints.set([]);
    }
  }

  fmtDuration = formatDuration;
  Math = Math;
}
