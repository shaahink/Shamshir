import { computed, inject, Injectable, signal } from '@angular/core';
import { RunsApiService } from './runs.service';
import { ToastService } from '../../core/toast/toast.service';
import { RunHubService, type RunProgressEnvelope } from '../../core/signalr/run-hub.service';
import type { RunSummary, RunDetail, StartRunRequest } from '../../models/api.types';

export interface EquityPoint {
  simTimeUtc: string;
  equity: number;
}

export interface RunProgress {
  latest: RunProgressEnvelope | null;
  equityHistory: EquityPoint[];
}

@Injectable({ providedIn: 'root' })
export class RunsStore {
  private api = inject(RunsApiService);
  private toast = inject(ToastService);
  private hub = inject(RunHubService);

  readonly runs = signal<RunSummary[]>([]);
  readonly selectedRun = signal<RunDetail | null>(null);
  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);

  private readonly _progress = signal<Map<string, RunProgress>>(new Map());

  readonly activeRuns = computed(() => this.runs().filter((r) => r.status === 'running'));
  readonly completedRuns = computed(() => this.runs().filter((r) => r.status !== 'running'));
  readonly totalNetProfit = computed(() => this.runs().reduce((s, r) => s + r.netProfit, 0));

  progressFor(runId: string): RunProgress {
    return this._progress().get(runId) ?? { latest: null, equityHistory: [] };
  }

  latestProgress(runId: string): RunProgressEnvelope | null {
    return this._progress().get(runId)?.latest ?? null;
  }

  equityPoints(runId: string): readonly EquityPoint[] {
    return this._progress().get(runId)?.equityHistory ?? [];
  }

  private _hubSub: { unsubscribe(): void } | null = null;

  startProgress(): void {
    if (this._hubSub) return;
    this.hub.start().catch(() => {});
    this._hubSub = this.hub.progress$.subscribe((env) => {
      this._onProgress(env);
    });
  }

  stopProgress(): void {
    this._hubSub?.unsubscribe();
    this._hubSub = null;
  }

  async watchRun(runId: string): Promise<void> {
    this.startProgress();
    await this.hub.joinRun(runId);
  }

  async unwatchRun(runId: string): Promise<void> {
    await this.hub.leaveRun(runId);
  }

  private _onProgress(env: RunProgressEnvelope): void {
    const cur = this._progress().get(env.runId);
    const points = cur?.equityHistory ?? [];
    if (env.equity != null) {
      points.push({ simTimeUtc: env.simTimeUtc, equity: env.equity });
    }
    this._progress.update((m) => {
      const next = new Map(m);
      next.set(env.runId, { latest: env, equityHistory: points });
      return next;
    });
  }

  async loadRuns(): Promise<void> {
    this.isLoading.set(true);
    this.error.set(null);
    try {
      this.runs.set(await this.api.getRuns());
    } catch {
      const msg = 'Failed to load runs';
      this.error.set(msg);
      this.toast.show(msg, 'error');
    } finally {
      this.isLoading.set(false);
    }
  }

  async loadRun(runId: string): Promise<void> {
    this.isLoading.set(true);
    try {
      this.selectedRun.set(await this.api.getRun(runId));
    } catch {
      const msg = 'Failed to load run detail';
      this.error.set(msg);
      this.toast.show(msg, 'error');
    } finally {
      this.isLoading.set(false);
    }
  }

  async startBacktest(req: StartRunRequest): Promise<string> {
    const res = await this.api.startRun(req);
    await this.loadRuns();
    return res.runId;
  }

  async cancelRun(runId: string): Promise<void> {
    await this.api.cancelRun(runId);
    await this.loadRuns();
  }
}
