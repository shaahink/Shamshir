import { Component, inject, OnInit, signal, ChangeDetectionStrategy, computed } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { HttpClient } from '@angular/common/http';
import { DatePipe, DecimalPipe } from '@angular/common';
import { forkJoin } from 'rxjs';
import type { RunDetail, TradeSummary } from '../../models/api.types';

@Component({
  selector: 'app-compare',
  standalone: true,
  imports: [RouterLink, DatePipe, DecimalPipe],
  template: `
    <div class="space-y-6">
      <div class="flex items-center justify-between">
        <h1 class="text-xl font-semibold">Compare Runs</h1>
        <a routerLink="/runs/new" class="rounded-md bg-emerald-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-emerald-500">+ New</a>
      </div>

      @if (loading()) {
        <div class="py-12 text-center text-sm text-gray-500">Loading...</div>
      } @else if (error()) {
        <div class="rounded-lg border border-red-800 bg-red-900/20 p-4 text-sm text-red-400">{{ error() }}</div>
      } @else if (left() && right()) {
        <div class="grid grid-cols-2 gap-4">
          <div class="rounded-lg border border-blue-800">
            <div class="rounded-t-lg bg-blue-900/40 px-4 py-2 text-xs font-medium">
              Left &#64; {{ left()?.runId?.slice(0, 8) }} &mdash; {{ venueLabel(left()?.venue) }}
              <span class="ml-2 text-gray-500">{{ left()?.startedAtUtc | date:'MM-dd HH:mm' }}</span>
            </div>
            <div class="p-4 space-y-3">
              <div class="grid grid-cols-2 gap-2 text-xs">
                <div class="text-gray-500">Status</div><div class="text-gray-300">{{ left()?.status }}</div>
                <div class="text-gray-500">Symbol</div><div class="font-mono text-gray-300">{{ left()?.symbol }}</div>
                <div class="text-gray-500">Period</div><div class="font-mono text-gray-300">{{ left()?.period }}</div>
                <div class="text-gray-500">Venue</div><div class="font-mono text-gray-300">{{ venueLabel(left()?.venue) }}</div>
              </div>
              <hr class="border-gray-800" />
              <div class="grid grid-cols-2 gap-2 text-xs">
                <div class="text-gray-500">Net P/L</div>
                <div class="font-mono tabular-nums" [class.text-emerald-400]="(left()?.netProfit ?? 0) > 0" [class.text-red-400]="(left()?.netProfit ?? 0) < 0">{{ (left()?.netProfit ?? 0) | number:'1.2-2' }}</div>
                <div class="text-gray-500">Max DD</div><div class="font-mono tabular-nums text-red-400">{{ ((left()?.maxDrawdownPct ?? 0) * 100) | number:'1.2-2' }}%</div>
                <div class="text-gray-500">Trades</div><div class="font-mono tabular-nums text-gray-300">{{ leftTrades().length }}</div>
                <div class="text-gray-500">Win Rate</div><div class="font-mono tabular-nums text-gray-300">{{ ((left()?.winRatePct ?? 0) * 100) | number:'1.1-1' }}%</div>
                <div class="text-gray-500">Gross PnL</div><div class="font-mono tabular-nums text-gray-300">{{ (left()?.grossPnL ?? 0) | number:'1.2-2' }}</div>
                <div class="text-gray-500">Commission</div><div class="font-mono tabular-nums text-gray-300">{{ (left()?.commissionTotal ?? 0) | number:'1.2-2' }}</div>
                <div class="text-gray-500">Swap</div><div class="font-mono tabular-nums text-gray-300">{{ (left()?.swapTotal ?? 0) | number:'1.2-2' }}</div>
              </div>
            </div>
          </div>
          <div class="rounded-lg border border-emerald-800">
            <div class="rounded-t-lg bg-emerald-900/40 px-4 py-2 text-xs font-medium">
              Right &#64; {{ right()?.runId?.slice(0, 8) }} &mdash; {{ venueLabel(right()?.venue) }}
              <span class="ml-2 text-gray-500">{{ right()?.startedAtUtc | date:'MM-dd HH:mm' }}</span>
            </div>
            <div class="p-4 space-y-3">
              <div class="grid grid-cols-2 gap-2 text-xs">
                <div class="text-gray-500">Status</div><div class="text-gray-300">{{ right()?.status }}</div>
                <div class="text-gray-500">Symbol</div><div class="font-mono text-gray-300">{{ right()?.symbol }}</div>
                <div class="text-gray-500">Period</div><div class="font-mono text-gray-300">{{ right()?.period }}</div>
                <div class="text-gray-500">Venue</div><div class="font-mono text-gray-300">{{ venueLabel(right()?.venue) }}</div>
              </div>
              <hr class="border-gray-800" />
              <div class="grid grid-cols-2 gap-2 text-xs">
                <div class="text-gray-500">Net P/L</div>
                <div class="font-mono tabular-nums" [class.text-emerald-400]="(right()?.netProfit ?? 0) > 0" [class.text-red-400]="(right()?.netProfit ?? 0) < 0">{{ (right()?.netProfit ?? 0) | number:'1.2-2' }}</div>
                <div class="text-gray-500">Max DD</div><div class="font-mono tabular-nums text-red-400">{{ ((right()?.maxDrawdownPct ?? 0) * 100) | number:'1.2-2' }}%</div>
                <div class="text-gray-500">Trades</div><div class="font-mono tabular-nums text-gray-300">{{ rightTrades().length }}</div>
                <div class="text-gray-500">Win Rate</div><div class="font-mono tabular-nums text-gray-300">{{ ((right()?.winRatePct ?? 0) * 100) | number:'1.1-1' }}%</div>
                <div class="text-gray-500">Gross PnL</div><div class="font-mono tabular-nums text-gray-300">{{ (right()?.grossPnL ?? 0) | number:'1.2-2' }}</div>
                <div class="text-gray-500">Commission</div><div class="font-mono tabular-nums text-gray-300">{{ (right()?.commissionTotal ?? 0) | number:'1.2-2' }}</div>
                <div class="text-gray-500">Swap</div><div class="font-mono tabular-nums text-gray-300">{{ (right()?.swapTotal ?? 0) | number:'1.2-2' }}</div>
              </div>
            </div>
          </div>
        </div>

        @if (diff(); as d) {
        <div class="rounded-lg border border-amber-800 bg-gray-900/50 p-4">
          <h2 class="mb-3 text-sm font-medium text-amber-400">Reconciliation</h2>
          <div class="grid grid-cols-2 gap-x-4 gap-y-1 text-xs">
            <div class="text-gray-500">Net P/L diff</div>
            <div class="font-mono tabular-nums" [class.text-red-400]="d.netPlDiff !== 0">{{ d.netPlDiff | number:'1.2-2' }}</div>
            <div class="text-gray-500">Trade count diff</div>
            <div class="font-mono tabular-nums" [class.text-red-400]="d.tradeCountDiff !== 0">{{ d.tradeCountDiff }}</div>
            <div class="text-gray-500">Max DD diff</div>
            <div class="font-mono tabular-nums" [class.text-red-400]="d.maxDdDiff !== 0">{{ (d.maxDdDiff * 100) | number:'1.2-2' }}%</div>
            <div class="text-gray-500">Gross PnL diff</div>
            <div class="font-mono tabular-nums" [class.text-red-400]="d.grossPlDiff !== 0">{{ d.grossPlDiff | number:'1.2-2' }}</div>
            <div class="text-gray-500">Commission diff</div>
            <div class="font-mono tabular-nums" [class.text-red-400]="d.commissionDiff !== 0">{{ d.commissionDiff | number:'1.2-2' }}</div>
            <div class="text-gray-500">Swap diff</div>
            <div class="font-mono tabular-nums" [class.text-red-400]="d.swapDiff !== 0">{{ d.swapDiff | number:'1.2-2' }}</div>
          </div>
        </div>
        }
      } @else {
        <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4 text-sm text-gray-400">
          Select two runs to compare. Go to <a routerLink="/runs" class="text-emerald-400 hover:underline">Runs</a>, check two runs, and click Compare.
        </div>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CompareComponent implements OnInit {
  private http = inject(HttpClient);
  private route = inject(ActivatedRoute);

  left = signal<RunDetail | null>(null);
  right = signal<RunDetail | null>(null);
  leftTrades = signal<TradeSummary[]>([]);
  rightTrades = signal<TradeSummary[]>([]);
  loading = signal(true);
  error = signal<string | null>(null);

  diff = computed(() => {
    const l = this.left();
    const r = this.right();
    if (!l || !r) return null;
    return {
      netPlDiff: (l.netProfit ?? 0) - (r.netProfit ?? 0),
      tradeCountDiff: (this.leftTrades().length) - (this.rightTrades().length),
      maxDdDiff: (l.maxDrawdownPct ?? 0) - (r.maxDrawdownPct ?? 0),
      grossPlDiff: (l.grossPnL ?? 0) - (r.grossPnL ?? 0),
      commissionDiff: (l.commissionTotal ?? 0) - (r.commissionTotal ?? 0),
      swapDiff: (l.swapTotal ?? 0) - (r.swapTotal ?? 0),
    };
  });

  ngOnInit(): void {
    const leftId = this.route.snapshot.queryParamMap.get('left');
    const rightId = this.route.snapshot.queryParamMap.get('right');
    if (leftId && rightId) {
      forkJoin({
        left: this.http.get<RunDetail>(`/api/runs/${leftId}`),
        right: this.http.get<RunDetail>(`/api/runs/${rightId}`),
        leftTrades: this.http.get<{ trades: TradeSummary[] }>(`/api/runs/${leftId}/trades`),
        rightTrades: this.http.get<{ trades: TradeSummary[] }>(`/api/runs/${rightId}/trades`),
      }).subscribe({
        next: (r: any) => {
          this.left.set(r.left);
          this.right.set(r.right);
          this.leftTrades.set(r.leftTrades?.trades ?? []);
          this.rightTrades.set(r.rightTrades?.trades ?? []);
          this.loading.set(false);
        },
        error: (err: any) => {
          this.error.set(err?.message ?? 'Failed to load runs');
          this.loading.set(false);
        },
      });
    } else {
      this.loading.set(false);
    }
  }

  venueLabel(v: string | null | undefined): string {
    if (!v || v === 'replay') return 'replay';
    if (v === 'tape') return 'tape';
    if (v === 'ctrader' || v === 'ctrader-desktop') return 'cTrader';
    return v ?? '?';
  }
}
