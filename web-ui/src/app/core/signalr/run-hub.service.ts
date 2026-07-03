import { Injectable, OnDestroy, signal } from '@angular/core';
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
  speed: number;
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

export type ConnectionState = 'disconnected' | 'connecting' | 'connected' | 'reconnecting';

export interface ConnectionDiagnostic {
  state: ConnectionState;
  error: string | null;
  lastAttempt: Date | null;
}

@Injectable({ providedIn: 'root' })
export class RunHubService implements OnDestroy {
  private hub: signalR.HubConnection | null = null;

  readonly progress$ = new Subject<RunProgressEnvelope>();
  readonly completed$ = new Subject<RunCompletedEnvelope>();

  readonly state = signal<ConnectionDiagnostic>({
    state: 'disconnected',
    error: null,
    lastAttempt: null,
  });

  async start(): Promise<void> {
    if (this.hub) return;

    this.state.set({ state: 'connecting', error: null, lastAttempt: new Date() });

    this.hub = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/run')
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (retryContext) => {
          this.state.set({
            state: 'reconnecting',
            error: retryContext.previousRetryCount > 3
              ? `Reconnect attempt ${retryContext.previousRetryCount}. Server may be unreachable. Is the app running on HTTPS? Try restarting with 'dotnet run'.`
              : `Reconnecting… (attempt ${retryContext.previousRetryCount})`,
            lastAttempt: new Date(),
          });
          // Backoff: 500ms, 1s, 2s, 4s, 10s, 10s...
          const delays = [0, 500, 1000, 2000, 4000, 10000];
          return delays[Math.min(retryContext.previousRetryCount, delays.length - 1)];
        },
      })
      .build();

    this.hub.on('RunProgress', (e: RunProgressEnvelope) => this.progress$.next(e));
    this.hub.on('RunCompleted', (e: RunCompletedEnvelope) => this.completed$.next(e));

    // Surface SignalR lifecycle events as diagnostic state changes.
    this.hub.onreconnected(() => {
      console.info('SignalR: reconnected');
      this.state.set({ state: 'connected', error: null, lastAttempt: null });
    });
    this.hub.onreconnecting(() => {
      console.warn('SignalR: reconnecting...');
    });
    this.hub.onclose((err) => {
      // Only surface an error when the close was unexpected (not from stop() or page unload).
      if (err) {
        const msg = err.message || 'Connection lost.';
        console.error('SignalR: closed with error', err);
        this.state.set({ state: 'disconnected', error: msg, lastAttempt: new Date() });
      }
    });

    try {
      await this.hub.start();
      this.state.set({ state: 'connected', error: null, lastAttempt: null });
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : 'Could not connect to SignalR hub.';
      console.error('SignalR: start failed', err);
      this.state.set({ state: 'disconnected', error: msg, lastAttempt: new Date() });
      throw err;
    }
  }

  get connectionState(): signalR.HubConnectionState | null {
    return this.hub?.state ?? null;
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
