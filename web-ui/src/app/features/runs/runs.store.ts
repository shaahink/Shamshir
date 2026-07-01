import { computed, inject, Injectable, signal } from '@angular/core';
import { RunsApiService } from './runs.service';
import type { RunSummary, RunDetail, StartRunRequest } from '../../models/api.types';

@Injectable({ providedIn: 'root' })
export class RunsStore {
  private api = inject(RunsApiService);

  readonly runs = signal<RunSummary[]>([]);
  readonly selectedRun = signal<RunDetail | null>(null);
  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);

  readonly activeRuns = computed(() => this.runs().filter((r) => r.status === 'running'));
  readonly completedRuns = computed(() => this.runs().filter((r) => r.status !== 'running'));
  readonly totalNetProfit = computed(() => this.runs().reduce((s, r) => s + r.netProfit, 0));

  async loadRuns(): Promise<void> {
    this.isLoading.set(true);
    this.error.set(null);
    try {
      this.runs.set(await this.api.getRuns());
    } catch {
      this.error.set('Failed to load runs');
    } finally {
      this.isLoading.set(false);
    }
  }

  async loadRun(runId: string): Promise<void> {
    this.isLoading.set(true);
    try {
      this.selectedRun.set(await this.api.getRun(runId));
    } catch {
      this.error.set('Failed to load run detail');
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
