import { Component, inject, OnInit, signal, ChangeDetectionStrategy } from '@angular/core';
import { RouterLink, Router } from '@angular/router';
import { DatePipe } from '@angular/common';
import { RunsStore } from '../runs.store';
import { RunsApiService } from '../runs.service';
import { BadgeComponent } from '../../../shared/badge.component';
import { formatSymbols } from '../../../shared/symbols.helper';

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
          @if (selectedRuns().length >= 1) {
            <button
              (click)="deleteSelected()"
              [disabled]="deleting()"
              class="rounded-md border border-red-800 px-3 py-1.5 text-xs text-red-400 hover:bg-red-900/20 disabled:opacity-50"
            >
              {{ deleting() ? 'Deleting…' : 'Delete (' + selectedRuns().length + ')' }}
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
                  <td class="whitespace-nowrap px-4 py-2 font-mono text-xs text-gray-500">
                    {{ venueLabel(run.venue) }}
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
  private router = inject(Router);
  private api = inject(RunsApiService);
  selectedRuns = signal<string[]>([]);
  deleting = signal(false);
  symbolsDisplay = formatSymbols;

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
    if (ids.length === 0 || this.deleting()) return;
    if (!confirm(`Delete ${ids.length} run(s) and all their trades, journal, equity and recorded bars? This cannot be undone.`)) return;
    this.deleting.set(true);
    try {
      await this.api.deleteRuns(ids);
      this.selectedRuns.set([]);
      this.store.loadRuns();
    } catch {
      alert('Delete failed. A selected run may still be running — cancel it first.');
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
}
