import { Injectable, OnDestroy } from '@angular/core';
import { Subject } from 'rxjs';
import * as signalR from '@microsoft/signalr';

export interface RunProgressEnvelope {
  runId: string;
  status: string;
  simTimeUtc: string;
  barsProcessed: number;
  barsTotal: number;
  percent: number;
  etaSeconds: number;
  wallElapsedMs: number;
  barsPerSec: number;
  equity: number;
  balance: number;
  openPositions: number;
  dailyDdPct: number;
  maxDdPct: number;
  distanceToDailyLimit: number;
  governorState: string;
  governorReason: string;
  counters: { signals: number; orders: number; fills: number; closes: number; rejections: number; breaches: number };
  recentJournal: JournalEnvelope[];
  // iter-strategy-system P3: multi-pass context.
  currentPass?: string | null;
  passIndex?: number;
  passTotal?: number;
}

export interface JournalEnvelope {
  runId: string;
  seq: number;
  simTimeUtc: string;
  kind: string;
  symbol?: string;
  strategyId?: string;
  reason?: string;
  detail?: string;
}

export interface RunCompletedEnvelope {
  runId: string;
  status: string;
  error?: string;
}

@Injectable({ providedIn: 'root' })
export class RunHubService implements OnDestroy {
  private hub: signalR.HubConnection | null = null;

  readonly progress$ = new Subject<RunProgressEnvelope>();
  readonly completed$ = new Subject<RunCompletedEnvelope>();

  async start(): Promise<void> {
    if (this.hub) return;
    this.hub = new signalR.HubConnectionBuilder().withUrl('/hubs/run').withAutomaticReconnect().build();

    this.hub.on('RunProgress', (e: RunProgressEnvelope) => this.progress$.next(e));
    this.hub.on('RunCompleted', (e: RunCompletedEnvelope) => this.completed$.next(e));

    await this.hub.start();
  }

  async joinRun(runId: string): Promise<void> {
    await this.hub?.invoke('JoinRun', runId);
  }

  async leaveRun(runId: string): Promise<void> {
    await this.hub?.invoke('LeaveRun', runId);
  }

  ngOnDestroy(): void {
    this.hub?.stop();
  }
}
