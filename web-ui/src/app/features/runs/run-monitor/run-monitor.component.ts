import { Component, DestroyRef, inject, OnDestroy, OnInit, signal, ChangeDetectionStrategy } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { DatePipe, NgClass } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { interval, firstValueFrom } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RunHubService, type RunProgressEnvelope, type RunCompletedEnvelope } from '../../../core/signalr/run-hub.service';
import { RunsStore } from '../runs.store';
import { RunsApiService } from '../runs.service';
import type { EquityPoint } from '../../../models/api.types';
import { StatTileComponent } from '../../../shared/stat-tile.component';
import { EquityChartComponent, type ChartPoint } from '../../../shared/equity-chart.component';

interface NarrativeEvent {
  seq: number;
  simTime: string;
  severity: string;
  category: string;
  headline: string;
  detail: string;
}

interface NarrativeResponse {
  events: NarrativeEvent[];
  latestSeq: number;
  hasMore: boolean;
}

@Component({
  selector: 'app-run-monitor',
  standalone: true,
  imports: [DatePipe, NgClass, RouterLink, StatTileComponent, EquityChartComponent],
  template: `
    <div class="space-y-6">
      <div class="flex items-center justify-between">
        <div>
          <h1 class="text-xl font-semibold">Live Monitor</h1>
          <p class="font-mono text-xs text-gray-500">Run {{ runId() }}</p>
        </div>
        <div class="flex gap-2">
          <button
            (click)="cancel()" [disabled]="cancelling()"
            class="rounded-md border border-red-800 px-3 py-1.5 text-xs text-red-400 hover:bg-red-900/20 disabled:opacity-50"
          >{{ cancelling() ? 'Cancelling...' : 'Cancel Run' }}</button>
          <a [routerLink]="['/runs', runId()]"
            class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800">Report</a>
        </div>
      </div>

      @if (breachBanner()) {
        <div class="rounded-lg border border-red-800 bg-red-900/20 p-3 text-sm text-red-400">{{ breachBanner() }}</div>
      }

      <!-- Progress bar -->
      <div class="rounded-lg border border-gray-800 bg-gray-800/20 p-4">
        <div class="mb-2 flex justify-between text-xs text-gray-400">
          <span>Progress @if (passTotal() > 1) { <span class="ml-2 rounded bg-gray-700/60 px-1.5 py-0.5 text-gray-300">Pass {{ passIndex() }}/{{ passTotal() }}</span> }</span>
          <span>{{ percent().toFixed(1) }}% ({{ barCount() }} / {{ totalBars() || '?' }})</span>
        </div>
        <div class="h-2 overflow-hidden rounded-full bg-gray-700">
          <div class="h-full rounded-full bg-emerald-500 transition-all duration-500" [style.width]="percent() + '%'"></div>
        </div>
        <div class="mt-2 grid grid-cols-4 gap-4 text-xs">
          <span class="text-gray-500">Speed: {{ barsPerSec().toFixed(1) }} bars/s</span>
          <span class="text-gray-500">ETA: {{ eta() }}</span>
          <span class="text-gray-500">Elapsed: {{ elapsed() }}</span>
          <span class="text-gray-500">Sim: {{ simTime() | date: 'yyyy-MM-dd HH:mm' }}</span>
        </div>
      </div>

      <!-- Terminal state -->
      @if (status() === 'completed' || status() === 'failed' || status() === 'cancelled') {
        <div class="rounded-lg border p-4" [class.border-emerald-800]="status() === 'completed'" [ngClass]="status() === 'completed' ? 'bg-emerald-900/20' : 'bg-red-900/20'" [class.border-red-800]="status() !== 'completed'">
          <div class="flex items-center justify-between">
            <div>
              <span class="text-sm font-medium capitalize" [class.text-emerald-400]="status() === 'completed'" [class.text-red-400]="status() !== 'completed'">{{ status() }}</span>
              @if (finalEquity() > 0) { <span class="ml-3 text-xs text-gray-400">Final equity: {{ finalEquity().toFixed(2) }}</span> }
            </div>
            <a [routerLink]="['/runs', runId()]" class="rounded-md bg-emerald-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-emerald-500">View Report</a>
          </div>
          <div class="mt-2 flex gap-4 text-xs text-gray-500">
            <span>Trades: {{ tradeCount() }}</span>
            <span>Net P/L: <span [class.text-emerald-400]="netPnL() > 0" [class.text-red-400]="netPnL() < 0">{{ netPnL().toFixed(2) }}</span></span>
            <span>Max DD: {{ (maxDdPct() * 100).toFixed(2) }}%</span>
          </div>
        </div>
      }

      <!-- 2×2 grid -->
      <div class="grid grid-cols-1 gap-4 lg:grid-cols-2">
        <!-- Top-left: Equity + DD chart -->
        @if (equityData().length > 2) {
          <app-equity-chart title="Live Equity" [data]="equityData()" [showDrawdown]="false" [showBalance]="true" />
        }

        <!-- Top-right: Risk tiles -->
        <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
          <h3 class="mb-3 text-sm font-medium text-gray-400">Risk</h3>
          <div class="grid grid-cols-2 gap-3">
            <app-stat-tile label="Equity" [value]="equity().toFixed(0)" />
            <app-stat-tile label="Balance" [value]="balance().toFixed(0)" />
            <app-stat-tile label="Open Pos" [value]="openPositions()" />
            <app-stat-tile label="Daily DD" [value]="(dailyDdPct() * 100).toFixed(2) + '%'" [negative]="dailyDdPct() > 0.005" />
            <app-stat-tile label="Max DD" [value]="(maxDdPct() * 100).toFixed(2) + '%'" [negative]="maxDdPct() > 0.005" />
            <app-stat-tile label="Governor" [value]="governorState() || '--'" [negative]="governorState() !== 'Normal'" />
            <app-stat-tile label="Dist to Limit" [value]="(distanceToLimit() * 100).toFixed(1) + '%'" [positive]="distanceToLimit() > 0.5" />
            <app-stat-tile label="Status" [value]="status()" [positive]="status() === 'completed'" [negative]="status() === 'failed'" />
          </div>
        </div>

        <!-- Bottom-left: Narrative journal -->
        <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
          <h3 class="mb-2 text-sm font-medium text-gray-400">Journal ({{ narrative().length }})</h3>
          <div class="max-h-80 overflow-y-auto space-y-0.5">
            @for (e of narrative(); track e.seq) {
              <div class="border-b border-gray-800 py-1 text-xs last:border-0"
                [class.text-amber-400]="e.severity === 'warning'"
                [class.text-red-400]="e.severity === 'critical'"
                [class.text-green-400]="e.category === 'Entry'"
                [class.text-blue-400]="e.category === 'Exit'">
                <span class="text-gray-500">{{ e.simTime | date: 'HH:mm:ss' }}</span>
                <span class="ml-2 font-medium">{{ e.headline }}</span>
                @if (e.detail) { <span class="ml-1 text-gray-600">— {{ e.detail }}</span> }
              </div>
            }
            @if (narrative().length === 0 && status() === 'running') {
              <div class="py-4 text-center text-xs text-gray-500">Waiting for events...</div>
            }
            @if (narrative().length === 0 && status() !== 'running') {
              <div class="py-4 text-center text-xs text-gray-500">No events recorded</div>
            }
          </div>
        </div>

        <!-- Bottom-right: Counters + open positions placeholder -->
        <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4 space-y-4">
          <div>
            <h3 class="mb-2 text-sm font-medium text-gray-400">Counters</h3>
            <div class="grid grid-cols-3 gap-2 text-center">
              @for (c of counterDefs; track c.key) {
                <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-2">
                  <div class="text-lg font-mono tabular-nums">{{ counters()[c.key] }}</div>
                  <div class="text-xs text-gray-500">{{ c.label }}</div>
                </div>
              }
            </div>
          </div>
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
  private http = inject(HttpClient);
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
  eta = signal('--');
  elapsed = signal('--');
  simTime = signal('');
  equity = signal(0);
  balance = signal(0);
  finalEquity = signal(0);
  netPnL = signal(0);
  tradeCount = signal(0);
  openPositions = signal(0);
  dailyDdPct = signal(0);
  maxDdPct = signal(0);
  distanceToLimit = signal(0);
  governorState = signal<string | null>(null);
  breachBanner = signal<string | null>(null);
  counters = signal<Record<string, number>>({ signals: 0, orders: 0, fills: 0, closes: 0, rejections: 0, breaches: 0 });
  narrative = signal<NarrativeEvent[]>([]);
  equityData = signal<ChartPoint[]>([]);
  cancelling = signal(false);
  private lastNarrSeq = 0;
  private narrTimer?: ReturnType<typeof setInterval>;

  counterDefs = [
    { key: 'signals', label: 'Signals' }, { key: 'orders', label: 'Orders' }, { key: 'fills', label: 'Fills' },
    { key: 'closes', label: 'Closes' }, { key: 'rejections', label: 'Rejected' }, { key: 'breaches', label: 'Breaches' },
  ];

  async ngOnInit(): Promise<void> {
    const rid = this.route.snapshot.paramMap.get('runId');
    if (!rid) return;
    this.runId.set(rid);
    const startTime = Date.now();

    interval(1000).pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => {
      const s = Math.floor((Date.now() - startTime) / 1000);
      this.elapsed.set(s > 3600 ? Math.floor(s / 3600) + 'h ' + Math.floor((s % 3600) / 60) + 'm' : Math.floor(s / 60) + 'm ' + (s % 60) + 's');
    });

    await this.hub.start();
    await this.hub.joinRun(rid);

    try {
      const detail = await this.api.getRun(rid);
      if (detail.backtestFrom) { /* metadata */ }
    } catch { /* */ }

    try {
      const eq = await this.api.getRunEquity(rid);
      if (eq.length > 0) {
        this.equityData.set(eq.map((p: EquityPoint) => ({ time: new Date(p.timestampUtc).getTime(), value: p.equity, balance: p.balance })));
      }
    } catch { /* */ }

    // Narrative polling every 2s
    this.narrTimer = setInterval(() => void this.pollNarrative(rid), 2000);

    this.hub.progress$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((e: RunProgressEnvelope) => {
      this.status.set('running');
      if (e.currentPass) this.currentPass.set(e.currentPass);
      if (e.passTotal) { this.passIndex.set(e.passIndex ?? 0); this.passTotal.set(e.passTotal); }
      this.barCount.set(e.barsProcessed ?? 0);
      this.totalBars.set(e.barsTotal ?? 0);
      this.percent.set(e.percent ?? 0);
      this.barsPerSec.set(e.barsPerSec ?? 0);
      if (e.etaSeconds > 0) { const m = Math.floor(e.etaSeconds / 60); this.eta.set(m > 60 ? Math.floor(m / 60) + 'h ' + (m % 60) + 'm' : m + 'm'); }
      if (e.simTimeUtc) this.simTime.set(e.simTimeUtc);
      if (e.equity != null) {
        this.equity.set(e.equity);
        this.finalEquity.set(e.equity);
        const t = e.simTimeUtc ? new Date(e.simTimeUtc).getTime() : (this.equityData().length > 0 ? this.equityData()[this.equityData().length - 1].time : Date.now());
        this.equityData.update(d => [...d.slice(-499), { time: t, value: e.equity, balance: e.balance ?? e.equity }]);
      }
      if (e.balance != null) this.balance.set(e.balance);
      if (e.openPositions != null) this.openPositions.set(e.openPositions);
      if (e.dailyDdPct != null) this.dailyDdPct.set(e.dailyDdPct);
      if (e.maxDdPct != null) this.maxDdPct.set(e.maxDdPct);
      if (e.distanceToDailyLimit != null) this.distanceToLimit.set(e.distanceToDailyLimit);
      if (e.governorState) this.governorState.set(e.governorState);
      if (e.counters) {
        this.counters.set({
          signals: e.counters.signals ?? 0, orders: e.counters.orders ?? 0, fills: e.counters.fills ?? 0,
          closes: e.counters.closes ?? 0, rejections: e.counters.rejections ?? 0, breaches: e.counters.breaches ?? 0,
        });
      }
    });

    this.hub.completed$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(async (e: RunCompletedEnvelope) => {
      this.status.set(e.status || 'completed');
      this.breachBanner.set(e.error || null);
      // Run is terminal: catch up on any trailing events once, then stop polling a finished run.
      await this.pollNarrative(this.runId());
      if (this.narrTimer) { clearInterval(this.narrTimer); this.narrTimer = undefined; }
    });
  }

  private async pollNarrative(rid: string): Promise<void> {
    try {
      const resp = await firstValueFrom(this.http.get<NarrativeResponse>(
        `/api/runs/${rid}/narrative?afterSeq=${this.lastNarrSeq}&limit=200`
      ));
      if (resp?.events?.length) {
        this.narrative.update(existing => {
          const existingSeqs = new Set(existing.map(e => e.seq));
          const fresh = resp.events.filter(e => !existingSeqs.has(e.seq));
          return [...existing, ...fresh].slice(-300);
        });
        this.lastNarrSeq = resp.latestSeq;
      }
    } catch { /* */ }
  }

  async cancel(): Promise<void> {
    this.cancelling.set(true);
    await this.store.cancelRun(this.runId());
    this.cancelling.set(false);
  }

  ngOnDestroy(): void {
    if (this.narrTimer) clearInterval(this.narrTimer);
    this.hub.leaveRun(this.runId());
  }
}
