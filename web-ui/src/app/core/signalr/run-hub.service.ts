import { Injectable, OnDestroy } from '@angular/core';
import { Subject } from 'rxjs';
import * as signalR from '@microsoft/signalr';

export const HttpTransport = signalR.HttpTransportType;

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
      .withUrl('/hubs/run', { transport: HttpTransport.WebSockets | HttpTransport.LongPolling })
      .configureLogging(signalR.LogLevel.Information)
      .build();

    this.hub.onreconnecting(() => console.warn('[SignalR] reconnecting...'));
    this.hub.onreconnected(() => console.log('[SignalR] reconnected'));
    this.hub.onclose((e) => console.error('[SignalR] closed:', e));

    this.hub.on('RunProgress', (e: RunProgressEnvelope) => { console.log('[SignalR] RunProgress', e.runId, e.barsProcessed); this.progress$.next(e); });
    this.hub.on('JournalAppend', (e: JournalEnvelope) => this.journal$.next(e));
    this.hub.on('RunCompleted', (e: RunCompletedEnvelope) => { console.log('[SignalR] RunCompleted', e.runId, e.status); this.completed$.next(e); });

    try {
      await this.hub.start();
      console.log('[SignalR] connected');
    } catch (err) {
      console.error('[SignalR] start failed:', err);
    }
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
