import { Component, inject, OnInit, OnDestroy, signal, computed, ChangeDetectionStrategy } from '@angular/core';
import { RouterLink, Router } from '@angular/router';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import type { Subscription } from 'rxjs';
import { RunsStore } from '../runs.store';
import { RunsApiService } from '../runs.service';
import { RunHubService } from '../../../core/signalr/run-hub.service';
import { BadgeComponent } from '../../../shared/badge.component';
import { formatSymbols } from '../../../shared/symbols.helper';
import type { RunSummary } from '../../../models/api.types';

interface RunListItem {
  run: RunSummary;
  indent: boolean;
}

const ACTIVE_STATUSES = new Set(['running', 'starting', 'queued']);

@Component({
  selector: 'app-run-list',
  standalone: true,
  imports: [RouterLink, BadgeComponent, DatePipe, FormsModule],
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

      @if (store.isLoading() && store.runs().length === 0) {
        <div class="py-12 text-center text-sm text-gray-500">Loading...</div>
      } @else if (store.error() && store.runs().length === 0) {
        <div class="rounded-lg border border-red-800 bg-red-900/20 p-4 text-sm text-red-400">{{ store.error() }}</div>
      } @else if (store.runs().length === 0) {
        <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-12 text-center">
          <p class="text-sm text-gray-500">No backtest runs yet.</p>
          <a routerLink="/runs/new" class="mt-3 inline-block text-sm text-emerald-400 hover:underline"
            >Start your first backtest</a
          >
        </div>
      } @else {
        <input
          type="text"
          placeholder="Filter by id, strategy, symbol, venue, status or note..."
          [ngModel]="filter()"
          (ngModelChange)="filter.set($event)"
          class="w-full max-w-md rounded border border-gray-700 bg-gray-800 px-3 py-1.5 text-xs text-gray-100 placeholder-gray-500 focus:border-emerald-500 focus:outline-none"
        />
        <div class="overflow-x-auto rounded-lg border border-gray-800">
          <table class="min-w-full text-sm">
            <thead class="bg-gray-900/50">
              <tr>
                <th class="px-3 py-2 text-left text-xs font-medium uppercase tracking-wide text-gray-500 w-8">#</th>
                <th class="px-3 py-2 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Run</th>
                <th class="px-3 py-2 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Status</th>
                <th class="px-3 py-2 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Venue</th>
                <th class="px-3 py-2 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Strategy</th>
                <th class="px-3 py-2 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Symbol</th>
                <th class="px-3 py-2 text-left text-xs font-medium uppercase tracking-wide text-gray-500">TF</th>
                <th class="px-3 py-2 text-right text-xs font-medium uppercase tracking-wide text-gray-500">Net P/L</th>
                <th class="px-3 py-2 text-right text-xs font-medium uppercase tracking-wide text-gray-500">Max DD</th>
                <th class="px-3 py-2 text-right text-xs font-medium uppercase tracking-wide text-gray-500">Trades</th>
                <th class="px-3 py-2 text-right text-xs font-medium uppercase tracking-wide text-gray-500">Win%</th>
                <th class="px-3 py-2 text-right text-xs font-medium uppercase tracking-wide text-gray-500" title="Latest SetupScore composite (sv1)">Score</th>
                <th class="px-3 py-2 text-right text-xs font-medium uppercase tracking-wide text-gray-500">Duration</th>
                <th class="px-3 py-2 text-right text-xs font-medium uppercase tracking-wide text-gray-500">Created</th>
                <th class="px-3 py-2 text-left text-xs font-medium uppercase tracking-wide text-gray-500 min-w-40">Notes</th>
                <th class="px-3 py-2 w-10"></th>
              </tr>
            </thead>
            <tbody class="divide-y divide-gray-800">
              @for (item of groupedRuns(); track item.run.runId) {
                <tr class="cursor-pointer transition hover:bg-gray-800/30" [routerLink]="['/runs', item.run.runId]">
                  <td class="px-3 py-2">
                    <input
                      type="checkbox"
                      [checked]="isSelected(item.run.runId)"
                      (click)="toggleSelect($event, item.run.runId)"
                      class="h-3.5 w-3.5 rounded border-gray-600 bg-gray-800 text-emerald-500 focus:ring-emerald-500"
                    />
                  </td>
                  <td class="whitespace-nowrap px-3 py-2 font-mono text-xs" [class.pl-7]="item.indent" [class.text-gray-400]="!item.indent" [class.text-gray-500]="item.indent">
                    @if (item.indent) { ↳ } {{ item.run.runId.slice(0, 8) }}
                  </td>
                  <td class="whitespace-nowrap px-3 py-2">
                    <app-badge
                      [label]="statusLabel(item.run.status)"
                      [variant]="statusVariant(item.run.status)"
                    />
                    @if (item.run.status === 'queued' && item.run.queuePosition) {
                      <span class="ml-1 text-xs text-gray-500">#{{ item.run.queuePosition }}</span>
                    }
                    @if (item.run.status === 'running' && progressPct(item.run.runId) != null) {
                      <span class="ml-1 text-xs tabular-nums text-emerald-500">{{ progressPct(item.run.runId)!.toFixed(0) }}%</span>
                    }
                  </td>
                  <td class="whitespace-nowrap px-3 py-2 font-mono text-xs text-gray-500">
                    {{ venueLabel(item.run.venue) }}
                  </td>
                  <td class="max-w-40 truncate px-3 py-2 font-mono text-xs text-gray-400" [title]="item.run.strategies || ''">
                    {{ item.run.strategies || '—' }}
                  </td>
                  <td class="whitespace-nowrap px-3 py-2 font-mono text-xs">{{ symbolsDisplay(item.run) }}</td>
                  <td class="whitespace-nowrap px-3 py-2 font-mono text-xs uppercase text-gray-400">{{ timeframes(item.run) }}</td>
                  <td
                    class="whitespace-nowrap px-3 py-2 text-right font-mono text-xs tabular-nums"
                    [class.text-emerald-400]="item.run.netProfit > 0"
                    [class.text-red-400]="item.run.netProfit < 0"
                  >
                    {{ item.run.netProfit.toFixed(2) }}
                  </td>
                  <td class="whitespace-nowrap px-3 py-2 text-right font-mono text-xs tabular-nums text-red-400">
                    {{ (item.run.maxDrawdownPct * 100).toFixed(2) }}%
                  </td>
                  <td class="whitespace-nowrap px-3 py-2 text-right font-mono text-xs tabular-nums">
                    {{ item.run.totalTrades }}
                  </td>
                  <td class="whitespace-nowrap px-3 py-2 text-right font-mono text-xs tabular-nums text-gray-400">
                    {{ (item.run.winRatePct * 100).toFixed(1) }}%
                  </td>
                  <td class="whitespace-nowrap px-3 py-2 text-right font-mono text-xs tabular-nums"
                    [class.text-emerald-400]="(item.run.score ?? 0) >= 70"
                    [class.text-gray-400]="(item.run.score ?? 0) < 70">
                    {{ item.run.score != null ? item.run.score.toFixed(1) : '—' }}
                  </td>
                  <td class="whitespace-nowrap px-3 py-2 text-right font-mono text-xs tabular-nums text-gray-500">
                    {{ duration(item.run) }}
                  </td>
                  <td class="whitespace-nowrap px-3 py-2 text-right font-mono text-xs tabular-nums text-gray-500">
                    {{ item.run.createdAtUtc ? (item.run.createdAtUtc | date: 'MM-dd HH:mm') : '—' }}
                  </td>
                  <td class="px-3 py-2 text-xs" (click)="$event.stopPropagation()">
                    @if (editingNotes() === item.run.runId) {
                      <div class="flex items-start gap-1">
                        <textarea
                          [ngModel]="notesDraft()"
                          (ngModelChange)="notesDraft.set($event)"
                          (keydown.enter)="$event.preventDefault(); saveNotes(item.run.runId)"
                          (keydown.escape)="editingNotes.set(null)"
                          rows="2"
                          class="w-44 rounded border border-emerald-700 bg-gray-800 px-2 py-1 text-xs text-gray-100 focus:outline-none"
                        ></textarea>
                        <button (click)="saveNotes(item.run.runId)" [disabled]="savingNotes()"
                          class="rounded border border-emerald-700 px-1.5 py-0.5 text-xs text-emerald-400 hover:bg-emerald-900/20 disabled:opacity-50">✓</button>
                        <button (click)="editingNotes.set(null)"
                          class="rounded border border-gray-700 px-1.5 py-0.5 text-xs text-gray-500 hover:bg-gray-800">✕</button>
                      </div>
                    } @else {
                      <button (click)="startEditNotes(item.run)"
                        class="group flex max-w-44 items-center gap-1 text-left"
                        [title]="item.run.notes || 'Add note'">
                        <span class="truncate" [class.text-gray-300]="item.run.notes" [class.text-gray-600]="!item.run.notes">
                          {{ item.run.notes || '＋ note' }}
                        </span>
                      </button>
                    }
                  </td>
                  <td class="px-3 py-2" (click)="$event.stopPropagation()">
                    <button (click)="copyRun(item.run.runId)"
                      class="rounded border border-gray-700 px-1.5 py-0.5 text-xs text-gray-400 hover:bg-gray-800 hover:text-emerald-400"
                      title="Copy run — prefill the builder with this run's parameters">⧉</button>
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
export class RunListComponent implements OnInit, OnDestroy {
  readonly store = inject(RunsStore);
  private router = inject(Router);
  private api = inject(RunsApiService);
  private hub = inject(RunHubService);
  selectedRuns = signal<string[]>([]);
  deleting = signal(false);
  filter = signal('');
  editingNotes = signal<string | null>(null);
  notesDraft = signal('');
  savingNotes = signal(false);
  symbolsDisplay = formatSymbols;

  // X2 liveness: percent per running run, fed by the SignalR envelopes of joined runs.
  private progress = signal<Map<string, number>>(new Map());
  private joined = new Set<string>();
  private subs: Subscription[] = [];
  private pollTimer: ReturnType<typeof setInterval> | null = null;
  private reloadPending = false;

  filteredRuns = computed<RunSummary[]>(() => {
    const q = this.filter().trim().toLowerCase();
    const runs = this.store.runs();
    if (!q) return runs;
    return runs.filter((r) =>
      r.runId.toLowerCase().includes(q) ||
      (r.strategies ?? '').toLowerCase().includes(q) ||
      (r.symbols ?? '').toLowerCase().includes(q) ||
      r.symbol.toLowerCase().includes(q) ||
      (r.venue ?? '').toLowerCase().includes(q) ||
      r.status.toLowerCase().includes(q) ||
      (r.notes ?? '').toLowerCase().includes(q),
    );
  });

  // X2 grouping fix: the old version suppressed EVERY run with a parentRunId, which made duplicated
  // runs (parent lineage, no compare pair) and orphaned compare children vanish from the list
  // entirely. Now: emit in order; a compare-pair's later siblings render indented under the first
  // member; a run whose pair/parent isn't visible still renders as a normal top-level row.
  groupedRuns = computed<RunListItem[]>(() => {
    const runs = this.filteredRuns();
    const byComparePair = new Map<string, RunSummary[]>();
    for (const r of runs) {
      if (r.comparePairId) {
        const list = byComparePair.get(r.comparePairId) ?? [];
        list.push(r);
        byComparePair.set(r.comparePairId, list);
      }
    }

    const emitted = new Set<string>();
    const result: RunListItem[] = [];
    for (const r of runs) {
      if (emitted.has(r.runId)) continue;
      // Render the pair under its parent when both are visible: if this run is a child whose
      // parent shares the pair and is also in the list, let the parent's turn emit the group.
      if (r.comparePairId && r.parentRunId) {
        const pair = byComparePair.get(r.comparePairId) ?? [];
        if (pair.some((s) => s.runId === r.parentRunId)) continue;
      }
      emitted.add(r.runId);
      result.push({ run: r, indent: false });

      if (r.comparePairId) {
        for (const s of byComparePair.get(r.comparePairId) ?? []) {
          if (emitted.has(s.runId)) continue;
          emitted.add(s.runId);
          result.push({ run: s, indent: true });
        }
      }
    }
    return result;
  });

  ngOnInit(): void {
    void this.reload();

    // Live: join the groups of visible active runs; refresh the list when anything completes.
    this.store.startProgress();
    this.subs.push(
      this.hub.progress$.subscribe((env) => {
        this.progress.update((m) => {
          const next = new Map(m);
          next.set(env.runId, env.percent);
          return next;
        });
      }),
      this.hub.completed$.subscribe(() => void this.debouncedReload()),
    );

    // Slow poll as the fallback for runs started outside this page (CLI, research verbs) — the
    // server list itself is cached at 2s, so this is cheap.
    this.pollTimer = setInterval(() => void this.reload(), 15_000);
  }

  ngOnDestroy(): void {
    this.subs.forEach((s) => s.unsubscribe());
    this.subs = [];
    if (this.pollTimer) clearInterval(this.pollTimer);
    for (const id of this.joined) void this.store.unwatchRun(id);
    this.joined.clear();
  }

  private async reload(): Promise<void> {
    await this.store.loadRuns();
    for (const r of this.store.runs()) {
      if (ACTIVE_STATUSES.has(r.status) && !this.joined.has(r.runId)) {
        this.joined.add(r.runId);
        void this.store.watchRun(r.runId);
      }
    }
  }

  private async debouncedReload(): Promise<void> {
    if (this.reloadPending) return;
    this.reloadPending = true;
    setTimeout(() => {
      this.reloadPending = false;
      void this.reload();
    }, 1_000);
  }

  progressPct(runId: string): number | null {
    return this.progress().get(runId) ?? null;
  }

  timeframes(run: RunSummary): string {
    try {
      const arr = JSON.parse(run.periods || '[]');
      if (Array.isArray(arr) && arr.length > 0) return arr.join(', ');
    } catch { /* fall through */ }
    return run.period || '—';
  }

  duration(run: RunSummary): string {
    const ms = run.wallElapsedMs ?? 0;
    if (ms <= 0) return '—';
    const s = Math.round(ms / 1000);
    if (s < 60) return s + 's';
    const m = Math.floor(s / 60);
    if (m < 60) return m + 'm ' + (s % 60) + 's';
    return Math.floor(m / 60) + 'h ' + (m % 60) + 'm';
  }

  startEditNotes(run: RunSummary): void {
    this.editingNotes.set(run.runId);
    this.notesDraft.set(run.notes ?? '');
  }

  async saveNotes(runId: string): Promise<void> {
    if (this.savingNotes()) return;
    this.savingNotes.set(true);
    try {
      const text = this.notesDraft();
      await this.api.updateNotes(runId, text);
      this.store.runs.update((rs) => rs.map((r) => (r.runId === runId ? { ...r, notes: text || null } : r)));
      this.editingNotes.set(null);
    } finally {
      this.savingNotes.set(false);
    }
  }

  copyRun(runId: string): void {
    void this.router.navigate(['/runs/new'], { queryParams: { copyFrom: runId } });
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
