import { Component, inject, OnDestroy, OnInit, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { DatePipe } from '@angular/common';
import { Subscription } from 'rxjs';
import { RunHubService, type RunProgressEnvelope, type JournalEnvelope } from '../../../core/signalr/run-hub.service';
import { RunsStore } from '../runs.store';
import { StatTileComponent } from '../../../shared/stat-tile.component';

@Component({
  selector: 'app-run-monitor',
  standalone: true,
  imports: [DatePipe, RouterLink, StatTileComponent],
  template: `
    <div class="space-y-6">
      <div class="flex items-center justify-between">
        <div>
          <h1 class="text-xl font-semibold">Live Monitor</h1>
          <p class="font-mono text-xs text-gray-500">Run ID: {{ runId() }}</p>
        </div>
        <div class="flex gap-2">
          <button (click)="cancel()" [disabled]="cancelling()"
            class="rounded-md border border-red-800 px-3 py-1.5 text-xs text-red-400 hover:bg-red-900/20 disabled:opacity-50">
            {{ cancelling() ? 'Cancelling...' : 'Cancel Run' }}
          </button>
          <a [routerLink]="['/runs', runId()]"
             class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800">View Report</a>
        </div>
      </div>

      <div class="grid grid-cols-2 gap-3 md:grid-cols-4">
        <app-stat-tile label="Status" [value]="status()"
          [positive]="status() === 'completed'"
          [negative]="status() === 'failed'" />
        <app-stat-tile label="Bars" [value]="barCount()"
          [subtitle]="totalBars() ? ((barCount() / totalBars() * 100).toFixed(1) + '% of ' + totalBars()) : undefined" />
        <app-stat-tile label="Progress" [value]="totalBars() ? (barCount() / totalBars() * 100).toFixed(0) + '%' : '--'" />
        <app-stat-tile label="Elapsed" [value]="elapsed()" />
      </div>

      <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
        <h2 class="mb-2 text-sm font-medium text-gray-400">Event Log ({{ journalEntries().length }})</h2>
        <div class="max-h-80 overflow-y-auto space-y-0.5">
          @for (entry of journalEntries(); track entry.seq) {
            <div class="border-b border-gray-800 py-1 text-xs last:border-0">
              <span class="text-gray-500">{{ entry.simTimeUtc | date:'HH:mm:ss' }}</span>
              <span class="ml-2 font-medium text-gray-300">{{ entry.kind }}</span>
              @if (entry.symbol) { <span class="ml-1 text-gray-600">{{ entry.symbol }}</span> }
              @if (entry.reason) { <span class="ml-2 text-gray-600">— {{ entry.reason }}</span> }
            </div>
          }
          @if (journalEntries().length === 0) {
            <div class="py-4 text-center text-xs text-gray-500">Waiting for events...</div>
          }
        </div>
      </div>
    </div>
  `,
})
export class RunMonitorComponent implements OnInit, OnDestroy {
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private hub = inject(RunHubService);
  private store = inject(RunsStore);

  runId = signal('');
  status = signal('connecting');
  barCount = signal(0);
  totalBars = signal(0);
  lastMessage = signal('');
  elapsed = signal('--');
  cancelling = signal(false);
  journalEntries = signal<JournalEnvelope[]>([]);

  private subs = new Subscription();
  private startTime = 0;

  async ngOnInit(): Promise<void> {
    const runId = this.route.snapshot.paramMap.get('runId');
    if (!runId) return;
    this.runId.set(runId);
    this.startTime = Date.now();

    setInterval(() => {
      const s = Math.floor((Date.now() - this.startTime) / 1000);
      const m = Math.floor(s / 60);
      this.elapsed.set(m > 0 ? `${m}m ${s % 60}s` : `${s}s`);
    }, 1000);

    await this.hub.start();
    await this.hub.joinRun(runId);

    this.subs.add(
      this.hub.progress$.subscribe((e: RunProgressEnvelope) => {
        this.status.set('running');
        this.lastMessage.set(e.message);
        if (e.barCount != null) this.barCount.set(e.barCount);
        if (e.totalBars != null) this.totalBars.set(e.totalBars);
      })
    );
    this.subs.add(
      this.hub.journal$.subscribe((e: JournalEnvelope) => {
        this.journalEntries.update((entries) => [...entries.slice(-199), e]);
      })
    );
    this.subs.add(
      this.hub.completed$.subscribe((e) => {
        this.status.set(e.status);
        if (e.error) this.lastMessage.set('Error: ' + e.error);
      })
    );
  }

  async cancel(): Promise<void> {
    this.cancelling.set(true);
    await this.store.cancelRun(this.runId());
    this.cancelling.set(false);
  }

  ngOnDestroy(): void {
    this.subs.unsubscribe();
    this.hub.leaveRun(this.runId());
  }
}
