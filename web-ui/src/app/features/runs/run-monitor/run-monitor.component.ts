import { Component, DestroyRef, inject, OnDestroy, OnInit, signal, ChangeDetectionStrategy } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { interval } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RunHubService, type RunProgressEnvelope, type RunCompletedEnvelope } from '../../../core/signalr/run-hub.service';
import { RunsStore } from '../runs.store';
import { RunsApiService } from '../runs.service';
import type { EquityPoint, NarrativeEvent, NarrativeResponse } from '../../../models/api.types';
import { StatTileComponent } from '../../../shared/stat-tile.component';
import { EquityChartComponent, type ChartPoint } from '../../../shared/equity-chart.component';
import { BacktestTimelineComponent, type TimelineEvent } from '../../../shared/backtest-timeline.component';

const NARRATIVE_POLL_MS = 2000;
const NARRATIVE_LIMIT = 100;

@Component({
  selector: 'app-run-monitor',
  standalone: true,
  imports: [DatePipe, RouterLink, FormsModule, StatTileComponent, EquityChartComponent, BacktestTimelineComponent],
  template: `
    <div class="space-y-4">
      <!-- Header -->
      <div class="flex items-center justify-between">
        <div>
          <h1 class="text-xl font-semibold">Live Monitor</h1>
          <p class="font-mono text-xs text-gray-500">Run {{ runId() }}</p>
        </div>
        <div class="flex gap-2">
          @if (!terminal()) {
            <button
              (click)="cancel()"
              [disabled]="cancelling()"
              class="rounded-md border border-red-800 px-3 py-1.5 text-xs text-red-400 hover:bg-red-900/20 disabled:opacity-50"
            >
              {{ cancelling() ? 'Cancelling...' : 'Cancel Run' }}
            </button>
          }
          <a
            [routerLink]="['/runs', runId()]"
            class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800"
            >View Report</a
          >
        </div>
      </div>

      @if (breachBanner()) {
        <div class="rounded-lg border border-red-800 bg-red-900/20 p-3 text-sm text-red-400">
          {{ breachBanner() }}
        </div>
      }

      @if (terminal() && (status() === 'completed' || status() === 'completed-with-warnings')) {
        <div class="rounded-lg border border-emerald-800 bg-emerald-900/20 p-4 flex items-center justify-between">
          <div>
            <div class="text-sm text-emerald-400">Run completed</div>
            <div class="text-xs text-gray-500 mt-1">
              {{ equity()?.toFixed(0) ?? '--' }} equity &middot; {{ barCount() }} bars &middot; {{ elapsed() }}
            </div>
          </div>
          <a [routerLink]="['/runs', runId()]"
            class="rounded-md bg-emerald-600 px-4 py-2 text-sm font-medium text-white hover:bg-emerald-500">
            View Full Report
          </a>
        </div>
      }

      @if (terminal() && status() === 'failed') {
        <div class="rounded-lg border border-red-800 bg-red-900/20 p-3 text-sm text-red-400">
          Run failed. <a [routerLink]="['/runs', runId()]" class="underline">View details</a>
        </div>
      }

      <!-- Progress bar (kept) -->
      <div class="rounded-lg border border-gray-800 bg-gray-800/20 p-4">
        <div class="mb-2 flex justify-between text-xs text-gray-400">
          <span>
            Progress
            @if (passTotal() > 1) {
              <span class="ml-2 rounded bg-gray-700/60 px-1.5 py-0.5 text-gray-300">Pass {{ passIndex() }}/{{ passTotal() }} &middot; {{ currentPass() }}</span>
            } @else if (currentPass()) {
              <span class="ml-2 text-gray-500">{{ currentPass() }}</span>
            }
          </span>
          <span>{{ percent().toFixed(1) }}% ({{ barCount() }} / {{ totalBars() || '?' }} &#64; {{ barsPerSec().toFixed(0) }} bar/s)</span>
        </div>
        @if (venue() === 'tape' && !terminal()) {
          <div class="flex items-center gap-2 mt-2">
            <button (click)="togglePause()" class="rounded border border-gray-600 px-2 py-0.5 text-xs text-gray-300 hover:bg-gray-700">
              {{ speed() === 0 ? '▶ Resume' : '⏸ Pause' }}
            </button>
            <input type="range" [(ngModel)]="speedDisplay" (input)="onSpeedChange($event)" min="0" max="10" step="0.5" class="flex-1 accent-emerald-500 h-5" />
            <span class="text-xs font-mono text-gray-300 w-10 text-right">{{ speed() === 0 ? 'Paused' : speedDisplay + '×' }}</span>
          </div>
        }
        <div class="h-2 overflow-hidden rounded-full bg-gray-700">
          <div class="h-full rounded-full bg-emerald-500 transition-all duration-500" [style.width]="percent() + '%'"></div>
        </div>
        <div class="mt-2 flex justify-between text-xs text-gray-500">
          <span>ETA: {{ eta() }}</span>
          <span>Elapsed: {{ elapsed() }}</span>
          <span>Sim: {{ simTime() | date: 'yyyy-MM-dd HH:mm' }}</span>
        </div>
      </div>

      @if (backtestFrom() && backtestTo()) {
        <div class="rounded-lg border border-gray-800 bg-gray-800/20 p-4">
          <h3 class="mb-2 text-xs font-medium text-gray-500 uppercase tracking-wider">Simulation Timeline</h3>
          <app-backtest-timeline
            [from]="backtestFrom()!"
            [to]="backtestTo()!"
            [simTime]="simTime() || null"
            [events]="timelineEvents()"
            [passCount]="passTotal() || 1"
            [currentPass]="passIndex() || 1"
          />
        </div>
      }

      <!-- 2x2 grid -->
      <div class="grid grid-cols-2 gap-4">
        <!-- Top-left: Equity + DD chart -->
        <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4 min-h-[300px]">
          <app-equity-chart title="Equity &amp; Drawdown" [data]="equityData()" [showDrawdown]="true" [showBalance]="true" />
        </div>

        <!-- Top-right: Risk tiles -->
        <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4 space-y-3">
          <h3 class="text-xs font-medium text-gray-400 uppercase tracking-wider">Risk &amp; Account</h3>
          <div class="grid grid-cols-2 gap-2">
            <app-stat-tile label="Equity" [value]="equity()?.toFixed(0) ?? '--'" />
            <app-stat-tile label="Balance" [value]="balance().toFixed(0)" />
            <app-stat-tile
              label="Daily DD"
              [value]="(dailyDdPct() * 100).toFixed(2) + '%'"
              [negative]="dailyDdPct() > 0.005"
            />
            <app-stat-tile
              label="Max DD"
              [value]="(maxDdPct() * 100).toFixed(2) + '%'"
              [negative]="maxDdPct() > 0.005"
            />
            <app-stat-tile
              label="Governor"
              [value]="governorState() || 'Normal'"
              [negative]="governorState() !== 'Normal'"
            />
            <app-stat-tile
              label="Dist. to Limit"
              [value]="(distanceToLimit() * 100).toFixed(1) + '%'"
              [positive]="distanceToLimit() > 0.5"
            />
            <app-stat-tile label="Signals" [value]="counters()['signals']" />
            <app-stat-tile label="Fills" [value]="counters()['fills']" />
            <app-stat-tile label="Closes" [value]="counters()['closes']" />
            <app-stat-tile label="Rejections" [value]="counters()['rejections']" [negative]="counters()['rejections'] > 0" />
          </div>
        </div>

        <!-- Bottom-left: Live narrative journal -->
        <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4 flex flex-col min-h-[260px]">
          <div class="mb-2 flex items-center justify-between">
            <h3 class="text-xs font-medium text-gray-400 uppercase tracking-wider">Narrative</h3>
            <span class="text-xs text-gray-600">{{ narrativeEntries().length }} events</span>
          </div>
          <div class="flex-1 overflow-y-auto space-y-0.5 max-h-[220px]">
            @for (entry of narrativeEntries(); track entry.seq) {
              <div class="border-b border-gray-800 py-1 text-xs last:border-0"
                [class.text-red-400]="entry.severity === 'critical'"
                [class.text-amber-400]="entry.severity === 'warning'">
                <span class="text-gray-500">{{ entry.simTime | date: 'HH:mm:ss' }}</span>
                <span class="ml-1 text-gray-600">{{ entry.category }}</span>
                <span class="ml-2 font-medium text-gray-300">{{ entry.headline }}</span>
                @if (entry.detail) {
                  <span class="ml-1 text-gray-600">- {{ entry.detail }}</span>
                }
              </div>
            }
            @if (narrativeEntries().length === 0) {
              <div class="py-4 text-center text-xs text-gray-500">Waiting for events...</div>
            }
          </div>
        </div>

        <!-- Bottom-right: Open positions + counters -->
        <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4 flex flex-col min-h-[260px]">
          <h3 class="text-xs font-medium text-gray-400 uppercase tracking-wider mb-3">Positions</h3>
          @if (openPositions() > 0) {
            <div class="flex items-center gap-2 mb-3">
              <span class="rounded-full bg-emerald-900/60 px-2 py-0.5 text-xs text-emerald-400">{{ openPositions() }} open</span>
              <span class="text-xs text-gray-500">positions active</span>
            </div>
          } @else {
            <div class="text-xs text-gray-500 mb-3">No open positions</div>
          }
          @if (terminal()) {
            <a [routerLink]="['/runs', runId()]"
              class="mt-auto rounded-md border border-emerald-800 bg-emerald-900/20 px-4 py-2 text-center text-sm text-emerald-400 hover:bg-emerald-900/40">
              View all trades in report
            </a>
          } @else {
            <div class="mt-auto space-y-1.5 text-xs">
              <div class="flex justify-between text-gray-500">
                <span>Breaches</span>
                <span class="text-gray-300">{{ counters()['breaches'] || 0 }}</span>
              </div>
              <div class="flex justify-between text-gray-500">
                <span>Orders</span>
                <span class="text-gray-300">{{ counters()['orders'] || 0 }}</span>
              </div>
              <div class="flex justify-between text-gray-500">
                <span>Status</span>
                <span [class.text-emerald-400]="status() === 'running'" [class.text-gray-300]="status() !== 'running'">{{ status() }}</span>
              </div>
            </div>
          }
        </div>
      </div>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RunMonitorComponent implements OnInit, OnDestroy {
  private route = inject(ActivatedRoute);
  private hub = inject(RunHubService);
  private store = inject(RunsStore);
  private api = inject(RunsApiService);
  private destroyRef = inject(DestroyRef);

  runId = signal('');
  currentPass = signal<string | null>(null);
  passIndex = signal(0);
  passTotal = signal(0);
  status = signal('connecting');
  barCount = signal(0);
  totalBars = signal(0);
  percent = signal(0);
  barsPerSec = signal(0);
  speed = signal(10);
  speedDisplay = signal(10);
  venue = signal('');
  eta = signal('--');
  elapsed = signal('--');
  simTime = signal('');
  equity = signal<number | null>(null);
  balance = signal(0);
  openPositions = signal(0);
  dailyDdPct = signal(0);
  maxDdPct = signal(0);
  distanceToLimit = signal(0);
  governorState = signal<string | null>(null);
  breachBanner = signal<string | null>(null);
  counters = signal<Record<string, number>>({ signals: 0, orders: 0, fills: 0, closes: 0, rejections: 0, breaches: 0 });
  equityData = signal<ChartPoint[]>([]);
  backtestFrom = signal<string | null>(null);
  backtestTo = signal<string | null>(null);
  timelineEvents = signal<TimelineEvent[]>([]);
  cancelling = signal(false);
  narrativeEntries = signal<NarrativeEvent[]>([]);
  private narrativeLatestSeq = 0;
  terminal = signal(false);

  async ngOnInit(): Promise<void> {
    const rid = this.route.snapshot.paramMap.get('runId');
    if (!rid) return;
    this.runId.set(rid);
    const startTime = Date.now();

    interval(1000)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        const s = Math.floor((Date.now() - startTime) / 1000);
        this.elapsed.set(s > 3600 ? Math.floor(s / 3600) + 'h ' + Math.floor((s % 3600) / 60) + 'm' : Math.floor(s / 60) + 'm ' + (s % 60) + 's');
      });

    await this.hub.start();
    await this.hub.joinRun(rid);

    try {
      const detail = await this.api.getRun(rid);
      if (detail.backtestFrom) this.backtestFrom.set(detail.backtestFrom);
      if (detail.backtestTo) this.backtestTo.set(detail.backtestTo);
      this.venue.set((detail as any).venue ?? '');
    } catch { /* */ }

    try {
      const eq = await this.api.getRunEquity(rid);
      if (eq.length > 0) {
        this.equityData.set(eq.map((p: EquityPoint) => ({
          time: new Date(p.timestampUtc).getTime(), value: p.equity, balance: p.balance,
        })));
      }
    } catch { /* */ }

    // Narrative polling (replaces SignalR recentJournal ring)
    const pollNarrative = async () => {
      if (this.terminal()) return;
      try {
        const res = await this.api.getRunNarrative(rid, this.narrativeLatestSeq);
        if (res.events.length > 0) {
          const existingSeqs = new Set(this.narrativeEntries().map(e => e.seq));
          const fresh = res.events.filter(e => !existingSeqs.has(e.seq));
          if (fresh.length > 0) {
            this.narrativeEntries.update(prev => [...prev, ...fresh].slice(-200));
            this.narrativeLatestSeq = res.latestSeq;

            // Map narrative to timeline events
            const newTicks: TimelineEvent[] = fresh
              .filter(e => e.category === 'Entry' || e.category === 'Exit' || e.severity === 'critical')
              .map(e => ({
                simTime: e.simTime,
                label: `${e.category}: ${e.headline}`.trim(),
                kind: e.category === 'Entry' ? 'entry' as const
                  : e.severity === 'critical' ? 'breach' as const
                  : 'exit' as const,
              }));
            if (newTicks.length > 0) {
              this.timelineEvents.update(prev => [...prev, ...newTicks].slice(-200));
            }
          }
        }
      } catch { /* */ }
    };

    interval(NARRATIVE_POLL_MS)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => pollNarrative());

    // Initial poll
    pollNarrative();

    this.hub.progress$
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((e: RunProgressEnvelope) => {
        this.status.set('running');
        if (e.currentPass) this.currentPass.set(e.currentPass);
        if (e.passTotal) { this.passIndex.set(e.passIndex ?? 0); this.passTotal.set(e.passTotal); }
        this.barCount.set(e.barsProcessed ?? 0);
        this.totalBars.set(e.barsTotal ?? 0);
        this.percent.set(e.percent ?? 0);
        this.barsPerSec.set(e.barsPerSec ?? 0);
        if (e.speed != null) { this.speed.set(e.speed); this.speedDisplay.set(e.speed); }
        if (e.etaSeconds > 0) {
          const m = Math.floor(e.etaSeconds / 60);
          this.eta.set(m > 60 ? Math.floor(m / 60) + 'h ' + (m % 60) + 'm' : m + 'm');
        }
        if (e.simTimeUtc) this.simTime.set(e.simTimeUtc);
        if (e.equity != null) {
          const eq = e.equity;
          this.equity.set(eq);
          const t = e.simTimeUtc ? new Date(e.simTimeUtc).getTime()
            : this.equityData().length > 0 ? this.equityData()[this.equityData().length - 1].time : Date.now();
          this.equityData.update((d) => [...d.slice(-499), { time: t, value: eq, balance: e.balance ?? eq }]);
        }
        if (e.balance != null) this.balance.set(e.balance);
        if (e.openPositions != null) this.openPositions.set(e.openPositions);
        if (e.dailyDdPct != null) this.dailyDdPct.set(e.dailyDdPct);
        if (e.maxDdPct != null) this.maxDdPct.set(e.maxDdPct);
        if (e.distanceToDailyLimit != null) this.distanceToLimit.set(e.distanceToDailyLimit);
        if (e.dailyDdPct != null && e.dailyDdPct < 0.02 && this.breachBanner()) this.breachBanner.set(null);
        if (e.governorState) this.governorState.set(e.governorState);
        if (e.counters) {
          this.counters.set({
            signals: e.counters.signals ?? 0,
            orders: e.counters.orders ?? 0,
            fills: e.counters.fills ?? 0,
            closes: e.counters.closes ?? 0,
            rejections: e.counters.rejections ?? 0,
            breaches: e.counters.breaches ?? 0,
          });
        }
      });

    this.hub.completed$
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((e: RunCompletedEnvelope) => {
        this.status.set(e.status || 'completed');
        this.terminal.set(true);
        pollNarrative(); // final poll
        if (e.error) {
          this.breachBanner.set(e.error);
        } else {
          this.breachBanner.set(null);
        }
      });
  }

  async cancel(): Promise<void> {
    this.cancelling.set(true);
    await this.store.cancelRun(this.runId());
    this.cancelling.set(false);
  }

  togglePause(): void {
    const newSpeed = this.speed() === 0 ? this.speedDisplay() || 1 : 0;
    this.api.setSpeed(this.runId(), newSpeed);
  }

  onSpeedChange(event: Event): void {
    const val = parseFloat((event.target as HTMLInputElement).value);
    this.speedDisplay.set(val);
    this.api.setSpeed(this.runId(), val);
  }

  ngOnDestroy(): void {
    this.hub.leaveRun(this.runId());
  }
}
