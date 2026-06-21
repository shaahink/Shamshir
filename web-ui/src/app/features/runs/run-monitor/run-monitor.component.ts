import { Component, ElementRef, inject, OnDestroy, OnInit, signal, ViewChild, ChangeDetectionStrategy } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { DatePipe } from '@angular/common';
import { Subscription } from 'rxjs';
import { RunHubService, type RunProgressEnvelope, type JournalEnvelope } from '../../../core/signalr/run-hub.service';
import { RunsStore } from '../runs.store';
import { StatTileComponent } from '../../../shared/stat-tile.component';
import { EquityChartComponent, type ChartPoint } from '../../../shared/equity-chart.component';

@Component({
  selector: 'app-run-monitor',
  standalone: true,
  imports: [DatePipe, RouterLink, StatTileComponent, EquityChartComponent],
  template: `
    <div class="space-y-6">
      <div class="flex items-center justify-between">
        <div>
          <h1 class="text-xl font-semibold">Live Monitor</h1>
          <p class="font-mono text-xs text-gray-500">Run {{ runId() }}</p>
        </div>
        <div class="flex gap-2">
          <button
            (click)="cancel()"
            [disabled]="cancelling()"
            class="rounded-md border border-red-800 px-3 py-1.5 text-xs text-red-400 hover:bg-red-900/20 disabled:opacity-50"
          >
            {{ cancelling() ? 'Cancelling...' : 'Cancel Run' }}
          </button>
          <a
            [routerLink]="['/runs', runId()]"
            class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800"
            >Report</a
          >
        </div>
      </div>

      @if (breachBanner()) {
        <div class="rounded-lg border border-red-800 bg-red-900/20 p-3 text-sm text-red-400">
          BREACH: {{ breachBanner() }}
        </div>
      }

      <div class="rounded-lg border border-gray-800 bg-gray-800/20 p-4">
        <div class="mb-2 flex justify-between text-xs text-gray-400">
          <span>Progress</span><span>{{ percent().toFixed(1) }}% ({{ barCount() }} / {{ totalBars() || '?' }})</span>
        </div>
        <div class="h-2 overflow-hidden rounded-full bg-gray-700">
          <div
            class="h-full rounded-full bg-emerald-500 transition-all duration-500"
            [style.width]="percent() + '%'"
          ></div>
        </div>
        <div class="mt-2 grid grid-cols-4 gap-4 text-xs">
          <span class="text-gray-500">Speed: {{ barsPerSec().toFixed(1) }} bars/s</span
          ><span class="text-gray-500">ETA: {{ eta() }}</span
          ><span class="text-gray-500">Elapsed: {{ elapsed() }}</span
          ><span class="text-gray-500">Sim: {{ simTime() | date: 'yyyy-MM-dd HH:mm' }}</span>
        </div>
      </div>

      <div class="grid grid-cols-2 gap-3 md:grid-cols-4">
        <app-stat-tile
          label="Status"
          [value]="status()"
          [positive]="status() === 'completed'"
          [negative]="status() === 'failed'"
        />
        <app-stat-tile label="Equity" [value]="equity().toFixed(0)" />
        <app-stat-tile label="Balance" [value]="balance().toFixed(0)" />
        <app-stat-tile label="Open Positions" [value]="openPositions()" />
        <app-stat-tile
          label="Daily DD %"
          [value]="(dailyDdPct() * 100).toFixed(2) + '%'"
          [negative]="dailyDdPct() > 0.005"
        />
        <app-stat-tile label="Max DD %" [value]="(maxDdPct() * 100).toFixed(2) + '%'" [negative]="maxDdPct() > 0.005" />
        <app-stat-tile label="Governor" [value]="governorState() || '--'" [negative]="governorState() !== 'Normal'" />
        <app-stat-tile
          label="Distance to Limit"
          [value]="(distanceToLimit() * 100).toFixed(1) + '%'"
          [positive]="distanceToLimit() > 0.5"
        />
      </div>

      <div class="grid grid-cols-6 gap-2 text-center">
        @for (c of counterDefs; track c.key) {
          <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-2">
            <div class="text-lg font-mono tabular-nums">{{ counters()[c.key] }}</div>
            <div class="text-xs text-gray-500">{{ c.label }}</div>
          </div>
        }
      </div>

      @if (equityData().length > 2) {
        <app-equity-chart title="Live Equity" [data]="equityData()" [showDrawdown]="false" [showBalance]="true" />
      }

      <div class="relative rounded-lg border border-gray-800 bg-gray-900/50 p-4">
        <h2 class="mb-2 text-sm font-medium text-gray-400">Journal ({{ journalEntries().length }})</h2>
        <div #journalScroll (scroll)="onJournalScroll()" class="max-h-80 overflow-y-auto space-y-0.5">
          @for (entry of journalEntries(); track entry.seq) {
            <div class="border-b border-gray-800 py-1 text-xs last:border-0">
              <span class="text-gray-500">{{ entry.simTimeUtc | date: 'HH:mm:ss' }}</span
              ><span class="ml-2 font-medium text-gray-300">{{ entry.kind }}</span>
              @if (entry.symbol) {
                <span class="ml-1 text-gray-600">{{ entry.symbol }}</span>
              }
              @if (entry.reason) {
                <span class="ml-2 text-gray-600">- {{ entry.reason }}</span>
              }
            </div>
          }
          @if (journalEntries().length === 0) {
            <div class="py-4 text-center text-xs text-gray-500">Waiting...</div>
          }
        </div>
        @if (!stick()) {
          <button
            (click)="jumpToLatest()"
            class="absolute bottom-3 right-3 rounded-full bg-emerald-700 px-3 py-1 text-xs text-white shadow hover:bg-emerald-600"
          >
            ↓ jump to latest
          </button>
        }
      </div>
    </div>
  \`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RunMonitorComponent implements OnInit, OnDestroy {
  private route = inject(ActivatedRoute);
  private hub = inject(RunHubService);
  private store = inject(RunsStore);

  runId = signal('');
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
  openPositions = signal(0);
  dailyDdPct = signal(0);
  maxDdPct = signal(0);
  distanceToLimit = signal(0);
  governorState = signal<string | null>(null);
  breachBanner = signal<string | null>(null);
  counters = signal<Record<string, number>>({ signals: 0, orders: 0, fills: 0, closes: 0, rejections: 0, breaches: 0 });
  journalEntries = signal<JournalEnvelope[]>([]);
  equityData = signal<ChartPoint[]>([]);
  cancelling = signal(false);
  stick = signal(true);
  @ViewChild('journalScroll') private journalScroll?: ElementRef<HTMLDivElement>;
  counterDefs = [
    { key: 'signals', label: 'Signals' },
    { key: 'orders', label: 'Orders' },
    { key: 'fills', label: 'Fills' },
    { key: 'closes', label: 'Closes' },
    { key: 'rejections', label: 'Rejections' },
    { key: 'breaches', label: 'Breaches' },
  ];

  // F5 — stick-to-bottom: auto-scroll only when the user is already near the bottom; otherwise hold
  // position and show a "jump to latest" affordance (set via the scroll handler).
  onJournalScroll(): void {
    const el = this.journalScroll?.nativeElement;
    if (!el) return;
    this.stick.set(el.scrollTop + el.clientHeight >= el.scrollHeight - 40);
  }

  jumpToLatest(): void {
    this.stick.set(true);
    this.scrollJournalToBottom();
  }

  private scrollJournalToBottom(): void {
    queueMicrotask(() => {
      const el = this.journalScroll?.nativeElement;
      if (el) el.scrollTop = el.scrollHeight;
    });
  }

  private subs = new Subscription();
  private startTime = 0;
  private elapsedTimer: any = null;

  async ngOnInit(): Promise<void> {
    const rid = this.route.snapshot.paramMap.get('runId');
    if (!rid) return;
    this.runId.set(rid);
    this.startTime = Date.now();
    this.elapsedTimer = setInterval(() => {
      const s = Math.floor((Date.now() - this.startTime) / 1000);
      this.elapsed.set(
        s > 3600 ? `${Math.floor(s / 3600)}h ${Math.floor((s % 3600) / 60)}m` : `${Math.floor(s / 60)}m ${s % 60}s`,
      );
    }, 1000);

    await this.hub.start();
    await this.hub.joinRun(rid);

    this.subs.add(
      this.hub.progress$.subscribe((e: any) => {
        this.status.set('running');
        this.barCount.set(e.barsProcessed ?? e.barCount ?? 0);
        this.totalBars.set(e.barsTotal ?? 0);
        this.percent.set(e.percent ?? 0);
        this.barsPerSec.set(e.barsPerSec ?? 0);
        if (e.etaSeconds > 0) {
          const m = Math.floor(e.etaSeconds / 60);
          this.eta.set(m > 60 ? `${Math.floor(m / 60)}h ${m % 60}m` : `${m}m`);
        }
        if (e.simTimeUtc) this.simTime.set(e.simTimeUtc);
        if (e.equity != null) {
          this.equity.set(e.equity);
          const t = e.simTimeUtc ? new Date(e.simTimeUtc).getTime() : Date.now();
          this.equityData.update((d) => [
            ...d.slice(-499),
            { time: t, value: e.equity, balance: e.balance ?? e.equity },
          ]);
        }
        if (e.balance != null) this.balance.set(e.balance);
        if (e.openPositions != null) this.openPositions.set(e.openPositions);
        if (e.dailyDdPct != null) this.dailyDdPct.set(e.dailyDdPct);
        if (e.maxDdPct != null) this.maxDdPct.set(e.maxDdPct);
        if (e.distanceToDailyLimit != null) this.distanceToLimit.set(e.distanceToDailyLimit);
        // Clear breach banner when DD recovers below threshold during a run
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
        // L2: append-only journal — merge by seq, never replace the whole array. Keep last 500 entries.
        if (Array.isArray(e.recentJournal) && e.recentJournal.length > 0) {
          const mapped: JournalEnvelope[] = e.recentJournal.map((r: any) => ({
            runId: this.runId(),
            seq: r.seq,
            simTimeUtc: r.simTimeUtc,
            kind: r.kind ?? r.event,
            symbol: r.symbol,
            strategyId: r.strategyId,
            reason: r.reason,
            detail: r.detail ?? r.detailJson,
          }));
          const existing = this.journalEntries();
          const existingSeqs = new Set(existing.map((x) => x.seq));
          const fresh = mapped.filter((m) => !existingSeqs.has(m.seq));
          if (fresh.length > 0) {
            this.journalEntries.set([...existing, ...fresh].slice(-500));
            if (this.stick()) this.scrollJournalToBottom();
          }
        }
      }),
    );
    this.subs.add(
      this.hub.completed$.subscribe((e: any) => {
        this.status.set(e.status || 'completed');
        if (e.error) {
          this.breachBanner.set(e.error);
        } else {
          this.breachBanner.set(null);
        }
      }),
    );
  }

  async cancel(): Promise<void> {
    this.cancelling.set(true);
    await this.store.cancelRun(this.runId());
    this.cancelling.set(false);
  }

  ngOnDestroy(): void {
    if (this.elapsedTimer) clearInterval(this.elapsedTimer);
    this.subs.unsubscribe();
    this.hub.leaveRun(this.runId());
  }
}
