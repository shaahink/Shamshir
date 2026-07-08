import { Component, inject, OnInit, signal, computed, ChangeDetectionStrategy } from '@angular/core';
import { RouterLink, Router } from '@angular/router';
import { DatePipe } from '@angular/common';
import { RunsStore } from '../runs.store';
import { RunsApiService } from '../runs.service';
import { BadgeComponent } from '../../../shared/badge.component';
import { formatSymbols } from '../../../shared/symbols.helper';
import type { RunSummary } from '../../../models/api.types';

interface RunListItem {
  run: RunSummary;
  indent: boolean;
}

@Component({
  selector: 'app-run-list',
  standalone: true,
  imports: [RouterLink, BadgeComponent, DatePipe],
  template: `
    <div class="space-y-6">
      <div class="flex items-center justify-between">
        <h1 class="text-xl font-semibold">Backtest Runs</h1>
        <div class="flex gap-2">
          @if (selectedRuns().length >= 2) {
            <button
              (click)="openCompare()"
              class="rounded-md border border-emerald-700 px-3 py-1.5 text-xs text-emerald-400 hover:bg-emerald-900/20"
            >
              Compare ({{ selectedRuns().length }})
            </button>
          }
          @if (selectedRuns().length > 0) {
            <button
              (click)="deleteSelected()"
              [disabled]="deleting()"
              class="rounded-md border border-red-800 px-3 py-1.5 text-xs text-red-400 hover:bg-red-900/20 disabled:opacity-50"
            >
              {{ deleting() ? 'Deleting...' : 'Delete (' + selectedRuns().length + ')' }}
            </button>
          }
          <a
            routerLink="/runs/new"
            class="rounded-md bg-emerald-600 px-4 py-2 text-sm font-medium text-white hover:bg-emerald-500"
            >+ New Backtest</a
          >
        </div>
      </div>

      @if (store.isLoading()) {
        <div class="py-12 text-center text-sm text-gray-500">Loading...</div>
      } @else if (store.error()) {
        <div class="rounded-lg border border-red-800 bg-red-900/20 p-4 text-sm text-red-400">{{ store.error() }}</div>
      } @else if (store.runs().length === 0) {
        <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-12 text-center">
          <p class="text-sm text-gray-500">No backtest runs yet.</p>
          <a routerLink="/runs/new" class="mt-3 inline-block text-sm text-emerald-400 hover:underline"
            >Start your first backtest</a
          >
        </div>
      } @else {
        <div class="overflow-x-auto rounded-lg border border-gray-800">
          <table class="min-w-full text-sm">
            <thead class="bg-gray-900/50">
              <tr>
                <th class="px-4 py-2 text-left text-xs font-medium uppercase tracking-wide text-gray-500 w-8">#</th>
                <th class="px-4 py-2 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Run ID</th>
                <th class="px-4 py-2 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Status</th>
                <th class="px-4 py-2 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Method</th>
                <th class="px-4 py-2 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Symbol</th>
                <th class="px-4 py-2 text-right text-xs font-medium uppercase tracking-wide text-gray-500">Net P/L</th>
                <th class="px-4 py-2 text-right text-xs font-medium uppercase tracking-wide text-gray-500">Max DD</th>
                <th class="px-4 py-2 text-right text-xs font-medium uppercase tracking-wide text-gray-500">Trades</th>
                <th class="px-4 py-2 text-right text-xs font-medium uppercase tracking-wide text-gray-500">Win Rate</th>
                <th class="px-4 py-2 text-right text-xs font-medium uppercase tracking-wide text-gray-500">Created</th>
              </tr>
            </thead>
            <tbody class="divide-y divide-gray-800">
              @for (item of groupedRuns(); track item.run.runId) {
                <tr class="cursor-pointer transition hover:bg-gray-800/30" [routerLink]="['/runs', item.run.runId]">
                  <td class="px-4 py-2">
                    <input
                      type="checkbox"
                      [checked]="isSelected(item.run.runId)"
                      (click)="toggleSelect($event, item.run.runId)"
                      class="h-3.5 w-3.5 rounded border-gray-600 bg-gray-800 text-emerald-500 focus:ring-emerald-500"
                    />
                  </td>
                  <td class="whitespace-nowrap px-4 py-2 font-mono text-xs" [class.pl-8]="item.indent" [class.text-gray-400]="!item.indent" [class.text-gray-500]="item.indent">
                    @if (item.indent) { ↳ } {{ item.run.runId.slice(0, 8) }}
                  </td>
                  <td class="whitespace-nowrap px-4 py-2">
                    <app-badge
                      [label]="statusLabel(item.run.status)"
                      [variant]="statusVariant(item.run.status)"
                    />
                  </td>
                  <td class="whitespace-nowrap px-4 py-2 font-mono text-xs text-gray-500">
                    {{ venueLabel(item.run.venue) }}
                  </td>
                  <td class="whitespace-nowrap px-4 py-2 font-mono text-xs">{{ symbolsDisplay(item.run) }}</td>
                  <td
                    class="whitespace-nowrap px-4 py-2 text-right font-mono text-xs tabular-nums"
                    [class.text-emerald-400]="item.run.netProfit > 0"
                    [class.text-red-400]="item.run.netProfit < 0"
                  >
                    {{ item.run.netProfit.toFixed(2) }}
                  </td>
                  <td class="whitespace-nowrap px-4 py-2 text-right font-mono text-xs tabular-nums text-red-400">
                    {{ (item.run.maxDrawdownPct * 100).toFixed(2) }}%
                  </td>
                  <td class="whitespace-nowrap px-4 py-2 text-right font-mono text-xs tabular-nums">
                    {{ item.run.totalTrades }}
                  </td>
                  <td class="whitespace-nowrap px-4 py-2 text-right font-mono text-xs tabular-nums text-gray-400">
                    {{ (item.run.winRatePct * 100).toFixed(1) }}%
                  </td>
                  <td class="whitespace-nowrap px-4 py-2 text-right font-mono text-xs tabular-nums text-gray-500">
                    {{ item.run.createdAtUtc ? (item.run.createdAtUtc | date: 'MM-dd HH:mm') : '—' }}
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RunListComponent implements OnInit {
  readonly store = inject(RunsStore);
  private router = inject(Router);
  private api = inject(RunsApiService);
  selectedRuns = signal<string[]>([]);
  deleting = signal(false);
  symbolsDisplay = formatSymbols;

  groupedRuns = computed<RunListItem[]>(() => {
    const runs = this.store.runs();
    const result: RunListItem[] = [];
    const byComparePair = new Map<string, RunSummary[]>();
    const children = new Set<string>();

    for (const r of runs) {
      if (r.comparePairId) {
        const list = byComparePair.get(r.comparePairId) ?? [];
        list.push(r);
        byComparePair.set(r.comparePairId, list);
      }
      if (r.parentRunId) children.add(r.runId);
    }

    for (const r of runs) {
      if (children.has(r.runId)) continue; // child runs handled inside their group
      result.push({ run: r, indent: false });

      const pairId = r.comparePairId;
      if (pairId) {
        const siblings = (byComparePair.get(pairId) ?? [])
          .filter((s) => s.runId !== r.runId);
        for (const s of siblings) {
          result.push({ run: s, indent: true });
        }
      }
    }
    return result;
  });

  ngOnInit(): void {
    this.store.loadRuns();
  }

  openCompare(): void {
    const ids = this.selectedRuns();
    if (ids.length >= 2) {
      this.router.navigate(['/runs/compare'], {
        queryParams: { left: ids[0], right: ids[1] },
      });
    }
  }

  async deleteSelected(): Promise<void> {
    const ids = this.selectedRuns();
    if (ids.length === 0) return;
    this.deleting.set(true);
    try {
      await this.api.deleteRuns(ids);
      this.selectedRuns.set([]);
      this.store.loadRuns();
    } finally {
      this.deleting.set(false);
    }
  }

  isSelected(runId: string): boolean {
    return this.selectedRuns().includes(runId);
  }
  toggleSelect(event: Event, runId: string): void {
    event.stopPropagation();
    this.selectedRuns.update((a) => (a.includes(runId) ? a.filter((x) => x !== runId) : [...a, runId]));
  }

  venueLabel(v: string | null | undefined): string {
    if (!v) return 'replay';
    if (v === 'tape') return 'tape';
    if (v === 'ctrader' || v === 'ctrader-desktop') return 'cTrader';
    return v;
  }

  statusLabel(s: string): string {
    if (s === 'completed-with-warnings') return 'warnings';
    return s;
  }

  statusVariant(s: string): 'success' | 'error' | 'warning' | 'neutral' {
    if (s === 'completed') return 'success';
    if (s === 'failed') return 'error';
    if (s === 'completed-with-warnings' || s === 'cancelled' || s === 'queued') return 'warning';
    return 'neutral';
  }
}
