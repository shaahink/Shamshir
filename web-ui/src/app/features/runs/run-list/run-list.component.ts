import { Component, inject, OnInit, signal, ChangeDetectionStrategy } from '@angular/core';
import { RouterLink } from '@angular/router';
import { DatePipe } from '@angular/common';
import { RunsStore } from '../runs.store';
import { BadgeComponent } from '../../../shared/badge.component';

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
              (click)="compareOpen.set(!compareOpen())"
              class="rounded-md border border-emerald-700 px-3 py-1.5 text-xs text-emerald-400 hover:bg-emerald-900/20"
            >
              Compare ({{ selectedRuns().length }})
            </button>
          }
          <a
            routerLink="/runs/new"
            class="rounded-md bg-emerald-600 px-4 py-2 text-sm font-medium text-white hover:bg-emerald-500"
            >+ New Backtest</a
          >
        </div>
      </div>

      @if (compareOpen() && selectedRuns().length >= 2) {
        <div class="rounded-lg border border-emerald-800 bg-gray-900/50 p-4">
          <h2 class="mb-2 text-sm font-medium text-emerald-400">Comparison</h2>
          <div class="overflow-x-auto">
            <table class="min-w-full text-xs">
              <thead class="text-gray-500">
                <tr>
                  <th class="px-3 py-1 text-left">Run</th>
                  <th class="px-3 py-1 text-right">Net P/L</th>
                  <th class="px-3 py-1 text-right">Max DD%</th>
                  <th class="px-3 py-1 text-right">Trades</th>
                  <th class="px-3 py-1 text-right">Win Rate</th>
                </tr>
              </thead>
              <tbody>
                @for (r of compareRuns(); track r.runId) {
                  <tr class="border-t border-gray-800">
                    <td class="px-3 py-1 font-mono text-gray-400">{{ r.runId.slice(0, 8) }}</td>
                    <td
                      class="px-3 py-1 text-right"
                      [class.text-emerald-400]="r.netProfit > 0"
                      [class.text-red-400]="r.netProfit < 0"
                    >
                      {{ r.netProfit.toFixed(2) }}
                    </td>
                    <td class="px-3 py-1 text-right">{{ (r.maxDrawdownPct * 100).toFixed(2) }}%</td>
                    <td class="px-3 py-1 text-right">{{ r.totalTrades }}</td>
                    <td class="px-3 py-1 text-right">{{ (r.winRatePct * 100).toFixed(1) }}%</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </div>
      }

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
                <th class="px-4 py-2 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Symbol</th>
                <th class="px-4 py-2 text-right text-xs font-medium uppercase tracking-wide text-gray-500">Net P/L</th>
                <th class="px-4 py-2 text-right text-xs font-medium uppercase tracking-wide text-gray-500">Max DD</th>
                <th class="px-4 py-2 text-right text-xs font-medium uppercase tracking-wide text-gray-500">Trades</th>
                <th class="px-4 py-2 text-right text-xs font-medium uppercase tracking-wide text-gray-500">Win Rate</th>
                <th class="px-4 py-2 text-right text-xs font-medium uppercase tracking-wide text-gray-500">Created</th>
              </tr>
            </thead>
            <tbody class="divide-y divide-gray-800">
              @for (run of store.runs(); track run.runId) {
                <tr class="cursor-pointer transition hover:bg-gray-800/30" [routerLink]="['/runs', run.runId]">
                  <td class="px-4 py-2">
                    <input
                      type="checkbox"
                      [checked]="isSelected(run.runId)"
                      (click)="toggleSelect($event, run.runId)"
                      class="h-3.5 w-3.5 rounded border-gray-600 bg-gray-800 text-emerald-500 focus:ring-emerald-500"
                    />
                  </td>
                  <td class="whitespace-nowrap px-4 py-2 font-mono text-xs text-gray-400">
                    {{ run.runId.slice(0, 8) }}
                  </td>
                  <td class="whitespace-nowrap px-4 py-2">
                    <app-badge
                      [label]="run.status"
                      [variant]="run.status === 'completed' ? 'success' : run.status === 'failed' ? 'error' : 'warning'"
                    />
                  </td>
                  <td class="whitespace-nowrap px-4 py-2 font-mono text-xs">{{ symbolsDisplay(run) }}</td>
                  <td
                    class="whitespace-nowrap px-4 py-2 text-right font-mono text-xs tabular-nums"
                    [class.text-emerald-400]="run.netProfit > 0"
                    [class.text-red-400]="run.netProfit < 0"
                  >
                    {{ run.netProfit.toFixed(2) }}
                  </td>
                  <td class="whitespace-nowrap px-4 py-2 text-right font-mono text-xs tabular-nums text-red-400">
                    {{ (run.maxDrawdownPct * 100).toFixed(2) }}%
                  </td>
                  <td class="whitespace-nowrap px-4 py-2 text-right font-mono text-xs tabular-nums">
                    {{ run.totalTrades }}
                  </td>
                  <td class="whitespace-nowrap px-4 py-2 text-right font-mono text-xs tabular-nums text-gray-400">
                    {{ (run.winRatePct * 100).toFixed(1) }}%
                  </td>
                  <td class="whitespace-nowrap px-4 py-2 text-right font-mono text-xs tabular-nums text-gray-500">
                    {{ run.createdAtUtc ? (run.createdAtUtc | date: 'MM-dd HH:mm') : '—' }}
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
  selectedRuns = signal<string[]>([]);
  compareOpen = signal(false);
  symbolsDisplay(run: any): string {
    try {
      const s = typeof run.symbols === 'string' ? JSON.parse(run.symbols) : run.symbols;
      if (Array.isArray(s) && s.length > 1) return s.join(', ');
    } catch {}
    return run.symbol;
  }

  ngOnInit(): void {
    this.store.loadRuns();
  }

  isSelected(runId: string): boolean {
    return this.selectedRuns().includes(runId);
  }
  toggleSelect(event: Event, runId: string): void {
    event.stopPropagation();
    this.selectedRuns.update((a) => (a.includes(runId) ? a.filter((x) => x !== runId) : [...a, runId]));
  }

  compareRuns = () => this.store.runs().filter((r) => this.selectedRuns().includes(r.runId));
}
