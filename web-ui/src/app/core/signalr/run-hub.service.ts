import { Injectable, OnDestroy } from '@angular/core';
import { Subject } from 'rxjs';
import * as signalR from '@microsoft/signalr';

export interface RunProgressEnvelope {
  runId: string;
  eventType: string;
  message: string;
  simTimeUtc: string;
  barCount?: number;
  totalBars?: number;
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
  readonly journal$ = new Subject<JournalEnvelope>();
  readonly completed$ = new Subject<RunCompletedEnvelope>();

  async start(): Promise<void> {
    if (this.hub) return;
    this.hub = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/run')
      .withAutomaticReconnect()
      .build();

    this.hub.on('RunProgress', (e: RunProgressEnvelope) => this.progress$.next(e));
    this.hub.on('JournalAppend', (e: JournalEnvelope) => this.journal$.next(e));
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
