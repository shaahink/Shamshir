import { Component, inject, OnInit, OnDestroy, signal, computed, ChangeDetectionStrategy } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { DataTableComponent, type ColumnDef } from '../../shared/data-table.component';
import { RouterLink } from '@angular/router';

interface VenueSession {
  id: string;
  runId: string;
  venue: string;
  event: string;
  detail: string | null;
  occurredAtUtc: string;
}

interface ListenStatus {
  state: string;
  isListening: boolean;
  activeRunId: string | null;
  dataPort: number;
  commandPort: number;
}

interface ListenStartResponse {
  status: string;
  dataPort: number;
  commandPort: number;
  message: string;
}

@Component({
  selector: 'app-ctrader-sessions',
  standalone: true,
  imports: [DataTableComponent, RouterLink],
  template: `
    <div class="space-y-6">
      <h1 class="text-xl font-semibold">cTrader Desktop Capture</h1>

      <!-- Status + Controls -->
      <div class="rounded-lg border border-gray-700 bg-gray-900 p-5 space-y-4">
        <div class="flex items-center gap-3">
          <div
            class="h-3 w-3 rounded-full"
            [class.bg-gray-600]="status().state === 'idle'"
            [class.bg-yellow-500]="status().state === 'listening'"
            [class.bg-emerald-500]="status().state === 'active'"
          ></div>
          <span class="text-sm font-medium capitalize">{{ status().state }}</span>
          <span class="text-xs text-gray-500">
            {{ statusLabel() }}
          </span>
        </div>

        <!-- Desktop instructions when listening -->
        @if (status().isListening) {
          <div class="rounded bg-gray-800 p-4 text-sm space-y-2">
            <p class="font-medium text-emerald-400">Engine listening on ports {{ status().dataPort }}/{{ status().commandPort }}</p>
            <ol class="list-decimal list-inside space-y-1 text-gray-300">
              <li>In <strong>cTrader Desktop</strong>, add <strong>TradingEngineCBot</strong> to an EURUSD H1 chart</li>
              <li>Set cBot parameters:
                <code class="ml-1 rounded bg-gray-700 px-1.5 py-0.5 text-xs">DataPort={{ status().dataPort }} CommandPort={{ status().commandPort }}</code>
              </li>
              <li><strong>Grant Full Access</strong> when cTrader prompts</li>
              <li>Run a <strong>Backtest</strong> (right-click chart &rarr; Backtesting)</li>
              <li>Progress will appear below when the session starts</li>
            </ol>
          </div>
        }

        <!-- Active session link -->
        @if (status().state === 'active' && status().activeRunId) {
          <div class="rounded bg-emerald-900/30 border border-emerald-800 p-3 text-sm">
            <span class="text-emerald-400 font-medium">Session active: </span>
            <a
              [routerLink]="['/runs', status().activeRunId, 'monitor']"
              class="text-emerald-300 underline hover:text-emerald-200"
            >Open Live Monitor &rarr;</a>
            <a
              [routerLink]="['/runs', status().activeRunId]"
              class="ml-3 text-emerald-400 underline hover:text-emerald-300"
            >View Report &rarr;</a>
          </div>
        }

        <!-- Buttons -->
        <div class="flex gap-3">
          @if (!status().isListening) {
            <button
              (click)="startListening()"
              [disabled]="loading()"
              class="rounded bg-emerald-600 px-4 py-2 text-sm font-medium hover:bg-emerald-500 disabled:opacity-50"
            >
              @if (loading()) { Starting... } @else { Start Listening }
            </button>
          } @else {
            <button
              (click)="stopListening()"
              [disabled]="loading()"
              class="rounded bg-red-600 px-4 py-2 text-sm font-medium hover:bg-red-500 disabled:opacity-50"
            >
              @if (loading()) { Stopping... } @else { Stop Listening }
            </button>
          }
          <span class="text-xs self-center text-gray-500">Ports: {{ status().dataPort }} (data) / {{ status().commandPort }} (cmd)</span>
        </div>

        <!-- Error -->
        @if (error()) {
          <p class="text-xs text-red-400">{{ error() }}</p>
        }
      </div>

      <!-- Session History -->
      <div>
        <h2 class="mb-3 text-lg font-medium">Session History</h2>
        <app-data-table [columns]="columns" [data]="sessions()" trackKey="id" />
      </div>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CtraderSessionsComponent implements OnInit, OnDestroy {
  private http = inject(HttpClient);

  sessions = signal<VenueSession[]>([]);
  status = signal<ListenStatus>({ state: 'idle', isListening: false, activeRunId: null, dataPort: 15555, commandPort: 15556 });
  loading = signal(false);
  error = signal<string | null>(null);

  private pollTimer?: ReturnType<typeof setInterval>;

  statusLabel = computed(() => {
    const s = this.status();
    if (s.state === 'idle') return 'Not listening. Click Start to begin.';
    if (s.state === 'listening') return 'Waiting for cTrader Desktop to connect...';
    if (s.state === 'active') return 'cBot connected, running backtest.';
    return '';
  });

  columns: ColumnDef[] = [
    { key: 'occurredAtUtc', label: 'Time', format: 'datetime' },
    { key: 'runId', label: 'Run', format: 'text' },
    { key: 'event', label: 'Event', format: 'text' },
    { key: 'detail', label: 'Detail', format: 'text' },
  ];

  async ngOnInit(): Promise<void> {
    await this.loadSessions();
    await this.loadStatus();
    this.pollTimer = setInterval(() => this.loadStatus(), 2000);
  }

  ngOnDestroy(): void {
    if (this.pollTimer) clearInterval(this.pollTimer);
  }

  async loadSessions(): Promise<void> {
    try {
      const resp = await firstValueFrom(this.http.get<{ sessions: VenueSession[] }>('/api/ctrader/sessions'));
      if (resp?.sessions) this.sessions.set(resp.sessions);
    } catch { /* */ }
  }

  async loadStatus(): Promise<void> {
    try {
      const resp = await firstValueFrom(this.http.get<ListenStatus>('/api/ctrader/listen/status'));
      this.status.set(resp);
    } catch { /* */ }
  }

  async startListening(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const resp = await firstValueFrom(this.http.post<ListenStartResponse>('/api/ctrader/listen/start', {
        governorEnabled: true,
        dailyDdEnabled: true,
        maxDdEnabled: true,
        commissionPerMillion: 50,
        spreadPips: 1,
      }));
      await this.loadStatus();
      await this.loadSessions();
    } catch (e: any) {
      const msg = e?.error?.error ?? e?.message ?? 'Failed to start listener';
      this.error.set(msg);
    } finally {
      this.loading.set(false);
    }
  }

  async stopListening(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      await firstValueFrom(this.http.post('/api/ctrader/listen/stop', {}));
      await this.loadStatus();
      await this.loadSessions();
    } catch (e: any) {
      const msg = e?.error?.error ?? e?.message ?? 'Failed to stop listener';
      this.error.set(msg);
    } finally {
      this.loading.set(false);
    }
  }
}
