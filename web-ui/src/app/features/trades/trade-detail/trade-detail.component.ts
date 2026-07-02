import { Component, inject, OnInit, signal, computed, ChangeDetectionStrategy } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { DatePipe } from '@angular/common';
import { StatTileComponent } from '../../../shared/stat-tile.component';
import { TradeChartCardComponent } from '../../../shared/trade-chart-card.component';
import type { TradeDetail } from '../../../models/api.types';
import { TradesApiService } from '../trades.service';
import { formatDuration } from '../../../shared/format.helper';

function safeParse(raw: string): Record<string, unknown> | null {
  try {
    const v = JSON.parse(raw);
    if (v !== null && typeof v === 'object' && !Array.isArray(v)) return v as Record<string, unknown>;
    return null;
  } catch { return null; }
}

interface EntrySnapshot { reason: string | null | undefined; regime: string | null | undefined; snapshot: Record<string, unknown> | null; }

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
        <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4 space-y-3">
          @if (entryNarrative(); as en) {
            <div>
              <h2 class="mb-1 text-sm font-medium text-gray-400">Why entered</h2>
              <p class="text-xs text-gray-300">{{ en.reason || '—' }}</p>
              @if (en.regime) { <p class="text-xs text-gray-500">Regime: {{ en.regime }}</p> }
              @if (en.snapshot) {
                <div class="mt-1 flex flex-wrap gap-1.5">
                  <span class="rounded bg-gray-800 px-1.5 py-0.5 text-xs text-gray-400">Entry {{ $any(en.snapshot).entryPrice ?? '—' }}</span>
                  <span class="rounded bg-gray-800 px-1.5 py-0.5 text-xs text-gray-400">SL {{ $any(en.snapshot).stopLoss ?? '—' }}</span>
                  @if ($any(en.snapshot).takeProfit) { <span class="rounded bg-gray-800 px-1.5 py-0.5 text-xs text-gray-400">TP {{ $any(en.snapshot).takeProfit }}</span> }
                  @if ($any(en.snapshot).lots) { <span class="rounded bg-gray-800 px-1.5 py-0.5 text-xs text-gray-400">{{ $any(en.snapshot).lots }} lots</span> }
                </div>
              }
            </div>
          }
          <div>
            <h2 class="mb-1 text-sm font-medium text-gray-400">Exit: {{ t.exitReason }}</h2>
            @if (exitNarrative(); as xn) {
              @if ($any(xn).exitPrice) { <p class="text-xs text-gray-500">Exit at {{ $any(xn).exitPrice }}, net {{ $any(xn).netAmt?.toFixed(2) }}, R {{ $any(xn).rMultiple?.toFixed(2) }}</p> }
            }
          </div>
          <p class="text-xs text-gray-500">Strategy: {{ t.strategyId }}</p>
        </div>

        <app-trade-chart-card [tradeId]="t.id" />
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

  async ngOnInit(): Promise<void> {
    const id = this.route.snapshot.paramMap.get('id');
    if (!id) return;
    try {
      const t = await this.api.getById(id);
      this.trade.set(t);
    } catch {
      // trade signal stays null → template shows "Trade not found."
    }
  }

  fmtDuration = formatDuration;

  entryNarrative = computed((): EntrySnapshot | null => {
    const t = this.trade();
    if (!t) return null;
    const snapshot = t.entrySnapshotJson ? safeParse(t.entrySnapshotJson) : null;
    return { reason: t.entryReason, regime: t.entryRegime, snapshot };
  });

  exitNarrative = computed(() => {
    const t = this.trade();
    if (!t) return null;
    if (!t.exitDetailJson) return null;
    return safeParse(t.exitDetailJson);
  });
}
