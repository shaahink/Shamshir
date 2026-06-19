import { Component, inject, OnInit } from '@angular/core';
import { RouterLink } from '@angular/router';
import { RunsStore } from '../runs.store';
import { BadgeComponent } from '../../../shared/badge.component';

@Component({
  selector: 'app-run-list',
  standalone: true,
  imports: [RouterLink, BadgeComponent],
  template: `
    <div class="space-y-6">
      <div class="flex items-center justify-between">
        <h1 class="text-xl font-semibold">Backtest Runs</h1>
        <a routerLink="/runs/new" class="rounded-md bg-emerald-600 px-4 py-2 text-sm font-medium text-white hover:bg-emerald-500">
          + New Backtest
        </a>
      </div>

      @if (store.isLoading()) {
        <div class="py-12 text-center text-sm text-gray-500">Loading...</div>
      } @else if (store.error()) {
        <div class="rounded-lg border border-red-800 bg-red-900/20 p-4 text-sm text-red-400">
          {{ store.error() }}
        </div>
      } @else if (store.runs().length === 0) {
        <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-12 text-center">
          <p class="text-sm text-gray-500">No backtest runs yet.</p>
          <a routerLink="/runs/new" class="mt-3 inline-block text-sm text-emerald-400 hover:underline">
            Start your first backtest
          </a>
        </div>
      } @else {
        <div class="overflow-x-auto rounded-lg border border-gray-800">
          <table class="min-w-full text-sm">
            <thead class="bg-gray-900/50">
              <tr>
                <th class="px-4 py-2 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Run ID</th>
                <th class="px-4 py-2 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Status</th>
                <th class="px-4 py-2 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Symbol</th>
                <th class="px-4 py-2 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Period</th>
                <th class="px-4 py-2 text-right text-xs font-medium uppercase tracking-wide text-gray-500">Net P/L</th>
                <th class="px-4 py-2 text-right text-xs font-medium uppercase tracking-wide text-gray-500">Max DD</th>
                <th class="px-4 py-2 text-right text-xs font-medium uppercase tracking-wide text-gray-500">Trades</th>
                <th class="px-4 py-2 text-right text-xs font-medium uppercase tracking-wide text-gray-500">Win Rate</th>
              </tr>
            </thead>
            <tbody class="divide-y divide-gray-800">
              @for (run of store.runs(); track run.runId) {
                <tr
                  class="cursor-pointer transition hover:bg-gray-800/30"
                  [routerLink]="['/runs', run.runId]"
                >
                  <td class="whitespace-nowrap px-4 py-2 font-mono text-xs text-gray-400">
                    {{ run.runId.slice(0, 8) }}
                  </td>
                  <td class="whitespace-nowrap px-4 py-2">
                    <app-badge
                      [label]="run.status"
                      [variant]="run.status === 'completed' ? 'success' : run.status === 'failed' ? 'error' : 'warning'"
                    />
                  </td>
                  <td class="whitespace-nowrap px-4 py-2 font-mono text-xs">{{ run.symbol }}</td>
                  <td class="whitespace-nowrap px-4 py-2 font-mono text-xs text-gray-400">{{ run.period }}</td>
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
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    </div>
  `,
})
export class RunListComponent implements OnInit {
  readonly store = inject(RunsStore);

  ngOnInit(): void {
    this.store.loadRuns();
  }
}
