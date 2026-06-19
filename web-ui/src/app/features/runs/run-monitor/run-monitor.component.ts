import { Component, inject, OnDestroy, OnInit, signal } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { DatePipe } from '@angular/common';
import { Subscription } from 'rxjs';
import { RunHubService, type RunProgressEnvelope, type JournalEnvelope } from '../../../core/signalr/run-hub.service';
import { RunsApiService } from '../runs.service';
import { StatTileComponent } from '../../../shared/stat-tile.component';

@Component({
  selector: 'app-run-monitor',
  standalone: true,
  imports: [DatePipe, StatTileComponent],
  template: `
    <div class="space-y-6">
      <h1 class="text-xl font-semibold">Live Monitor</h1>
      <p class="font-mono text-xs text-gray-500">Run ID: {{ runId() }}</p>

      <div class="grid grid-cols-3 gap-3">
        <app-stat-tile label="Status" [value]="status()" [positive]="status() === 'completed'" />
        <app-stat-tile label="Bars" [value]="barCount()" />
        <app-stat-tile label="Last Event" [value]="lastMessage()" />
      </div>

      <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
        <h2 class="mb-2 text-sm font-medium text-gray-400">Event Log</h2>
        <div class="max-h-80 overflow-y-auto space-y-1">
          @for (entry of journalEntries(); track entry.seq) {
            <div class="border-b border-gray-800 py-1 text-xs last:border-0">
              <span class="text-gray-500">{{ entry.simTimeUtc | date:'HH:mm:ss' }}</span>
              <span class="ml-2 font-medium text-gray-300">{{ entry.kind }}</span>
              @if (entry.reason) {
                <span class="ml-2 text-gray-500">— {{ entry.reason }}</span>
              }
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
  private hub = inject(RunHubService);

  runId = signal('');
  status = signal('connecting');
  barCount = signal(0);
  lastMessage = signal('');
  journalEntries = signal<JournalEnvelope[]>([]);

  private subs = new Subscription();

  async ngOnInit(): Promise<void> {
    const runId = this.route.snapshot.paramMap.get('runId');
    if (!runId) return;
    this.runId.set(runId);

    await this.hub.start();
    await this.hub.joinRun(runId);

    this.subs.add(
      this.hub.progress$.subscribe((e: RunProgressEnvelope) => {
        this.status.set('running');
        this.lastMessage.set(e.message);
        if (e.barCount != null) this.barCount.set(e.barCount);
      })
    );
    this.subs.add(
      this.hub.journal$.subscribe((e: JournalEnvelope) => {
        this.journalEntries.update((entries) => [...entries.slice(-99), e]);
      })
    );
    this.subs.add(
      this.hub.completed$.subscribe((e) => {
        this.status.set(e.status);
        if (e.error) this.lastMessage.set('Error: ' + e.error);
      })
    );
  }

  ngOnDestroy(): void {
    this.subs.unsubscribe();
    this.hub.leaveRun(this.runId());
  }
}
