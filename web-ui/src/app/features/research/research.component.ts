import { Component, inject, signal, computed, ChangeDetectionStrategy, OnInit } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { PipelineSummary, PipelineDetail, PipelineStep } from '../../models/api.types';

type View = 'list' | 'detail';

@Component({
  selector: 'app-research',
  standalone: true,
  imports: [],
  template: `
    <div class="space-y-6">
      <div class="flex items-center justify-between">
        <div>
          <h1 class="text-xl font-semibold">Research Pipelines</h1>
          <p class="mt-1 text-xs text-gray-500">Automated research playbook runs — review results, approve owner gates.</p>
        </div>
        @if (view() === 'detail') {
          <button
            (click)="backToList()"
            class="rounded border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800 transition"
          >Back</button>
        }
      </div>

      @if (loading()) {
        <div class="py-12 text-center text-sm text-gray-500">Loading...</div>
      } @else if (error()) {
        <div class="rounded bg-red-900/20 px-3 py-2 text-xs text-red-400">{{ error() }}</div>
      } @else if (view() === 'list') {
        <!-- LIST VIEW -->
        @if (pipelines().length === 0) {
          <div class="rounded-lg border border-dashed border-gray-800 py-16 text-center">
            <p class="text-sm text-gray-500">No pipelines yet.</p>
            <p class="mt-1 text-xs text-gray-600">Run a playbook via the CLI to see pipelines here — e.g. <code class="rounded bg-gray-900 px-1.5 py-0.5">research pipeline run playbooks/venue-parity.json</code></p>
          </div>
        } @else {
          <div class="overflow-x-auto rounded-lg border border-gray-800">
            <table class="w-full text-xs">
              <thead class="bg-gray-900 text-gray-400">
                <tr>
                  <th class="px-3 py-2 text-left">Name</th>
                  <th class="px-3 py-2 text-left">Status</th>
                  <th class="px-3 py-2 text-center">Progress</th>
                  <th class="px-3 py-2 text-left">Started</th>
                  <th class="px-3 py-2 text-center">Steps</th>
                </tr>
              </thead>
              <tbody class="divide-y divide-gray-800">
                @for (p of pipelines(); track p.id) {
                  <tr class="hover:bg-gray-800/30 cursor-pointer transition" (click)="selectPipeline(p.id)">
                    <td class="px-3 py-2 font-medium text-gray-300">{{ p.name }}</td>
                    <td class="px-3 py-2">
                      <span [class]="statusBadge(p.status)">{{ p.status }}</span>
                    </td>
                    <td class="px-3 py-2 text-center text-gray-400">
                      {{ progressLabel(p) }}
                    </td>
                    <td class="px-3 py-2 text-gray-500">{{ formatDate(p.startedAtUtc) }}</td>
                    <td class="px-3 py-2 text-center text-gray-400">{{ p.stepCount }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      } @else {
        <!-- DETAIL VIEW -->
        @if (detail(); as d) {
          <div class="space-y-6">
            <div class="grid grid-cols-2 gap-4 rounded-lg border border-gray-800 bg-gray-900/30 p-4 md:grid-cols-4">
              <div>
                <div class="text-[10px] uppercase tracking-wide text-gray-600">Status</div>
                <div class="mt-1"><span [class]="statusBadge(d.status)">{{ d.status }}</span></div>
              </div>
              <div>
                <div class="text-[10px] uppercase tracking-wide text-gray-600">Step</div>
                <div class="mt-1 text-sm text-gray-300">{{ d.currentStepIndex + 1 }} of {{ d.steps.length }}</div>
              </div>
              <div>
                <div class="text-[10px] uppercase tracking-wide text-gray-600">Started</div>
                <div class="mt-1 text-sm text-gray-400">{{ formatDate(d.startedAtUtc) }}</div>
              </div>
              <div>
                <div class="text-[10px] uppercase tracking-wide text-gray-600">Completed</div>
                <div class="mt-1 text-sm text-gray-400">{{ d.completedAtUtc ? formatDate(d.completedAtUtc) : '—' }}</div>
              </div>
            </div>

            @if (d.artifactDir) {
              <div class="rounded bg-gray-900/50 px-3 py-1.5 text-xs text-gray-500">
                Artifacts: <span class="font-mono text-gray-400">{{ d.artifactDir }}</span>
              </div>
            }

            <h2 class="text-sm font-semibold text-gray-300">Steps</h2>
            <div class="space-y-2">
              @for (step of d.steps; track step.stepIndex) {
                <div class="rounded-lg border border-gray-800 bg-gray-900/20 p-4" [class.border-yellow-700]="step.status === 'awaiting-owner'">
                  <div class="flex items-start justify-between gap-4">
                    <div class="flex-1">
                      <div class="flex items-center gap-2">
                        <span class="rounded bg-gray-800 px-2 py-0.5 text-xs font-mono text-gray-500">{{ step.stepIndex + 1 }}</span>
                        <span class="text-sm font-medium text-gray-300">{{ formatKind(step.kind) }}</span>
                        <span [class]="stepStatusBadge(step.status)">{{ step.status }}</span>
                        @if (step.paramHash) {
                          <span class="text-[10px] font-mono text-gray-600" title="Param hash: {{ step.paramHash }}">{{ step.paramHash.slice(0, 7) }}</span>
                        }
                      </div>
                      @if (step.verdictJson) {
                        <div class="mt-2 rounded bg-gray-950 px-2.5 py-1.5 font-mono text-[11px] text-gray-400 whitespace-pre-wrap break-all">{{ truncate(step.verdictJson, 300) }}</div>
                      }
                      @if (step.artifactPath) {
                        <div class="mt-1 text-[11px] text-gray-600">Artifact: <span class="text-gray-500">{{ step.artifactPath }}</span></div>
                      }
                      <div class="mt-1 flex gap-4 text-[10px] text-gray-600">
                        @if (step.startedAtUtc) { <span>Started: {{ formatDate(step.startedAtUtc) }}</span> }
                        @if (step.completedAtUtc) { <span>Completed: {{ formatDate(step.completedAtUtc) }}</span> }
                      </div>
                    </div>
                    @if (step.status === 'awaiting-owner') {
                      <div class="flex gap-2 shrink-0">
                        <button
                          (click)="approveGate(d.id)"
                          [disabled]="actionLoading()"
                          class="rounded bg-emerald-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-emerald-500 disabled:opacity-50 transition"
                        >Approve</button>
                        <button
                          (click)="rejectGate(d.id)"
                          [disabled]="actionLoading()"
                          class="rounded bg-red-600/70 px-3 py-1.5 text-xs font-medium text-white hover:bg-red-600 disabled:opacity-50 transition"
                        >Reject</button>
                      </div>
                    }
                  </div>
                </div>
              }
            </div>
          </div>
        }
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ResearchComponent implements OnInit {
  private http = inject(HttpClient);

  readonly view = signal<View>('list');
  readonly pipelines = signal<PipelineSummary[]>([]);
  readonly detail = signal<PipelineDetail | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly actionLoading = signal(false);

  readonly pendingGate = computed(() => {
    const d = this.detail();
    if (!d) return false;
    return d.steps.some(s => s.status === 'awaiting-owner');
  });

  async ngOnInit() {
    await this.loadPipelines();
  }

  async loadPipelines() {
    this.loading.set(true);
    this.error.set(null);
    try {
      const data = await firstValueFrom(this.http.get<PipelineSummary[]>('/api/research/pipelines'));
      this.pipelines.set(data);
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : 'Failed to load pipelines.';
      this.error.set(msg);
    } finally {
      this.loading.set(false);
    }
  }

  async selectPipeline(id: string) {
    this.loading.set(true);
    this.error.set(null);
    try {
      const data = await firstValueFrom(this.http.get<PipelineDetail>(`/api/research/pipelines/${id}`));
      this.detail.set(data);
      this.view.set('detail');
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : 'Failed to load pipeline detail.';
      this.error.set(msg);
    } finally {
      this.loading.set(false);
    }
  }

  backToList() {
    this.view.set('list');
    this.detail.set(null);
  }

  async approveGate(id: string) {
    this.actionLoading.set(true);
    this.error.set(null);
    try {
      const data = await firstValueFrom(this.http.post<PipelineDetail>(`/api/research/pipelines/${id}/approve`, {}));
      this.detail.set(data);
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : 'Failed to approve.';
      this.error.set(msg);
    } finally {
      this.actionLoading.set(false);
    }
  }

  async rejectGate(id: string) {
    this.actionLoading.set(true);
    this.error.set(null);
    try {
      const data = await firstValueFrom(this.http.post<PipelineDetail>(`/api/research/pipelines/${id}/reject`, {}));
      this.detail.set(data);
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : 'Failed to reject.';
      this.error.set(msg);
    } finally {
      this.actionLoading.set(false);
    }
  }

  statusBadge(status: string): string {
    const base = 'inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium';
    switch (status) {
      case 'completed': return `${base} bg-emerald-900/50 text-emerald-400`;
      case 'running': return `${base} bg-blue-900/50 text-blue-400`;
      case 'awaiting-owner': return `${base} bg-yellow-900/50 text-yellow-400`;
      case 'failed': return `${base} bg-red-900/50 text-red-400`;
      case 'cancelled': return `${base} bg-gray-800 text-gray-400`;
      default: return `${base} bg-gray-800 text-gray-400`;
    }
  }

  stepStatusBadge(status: string): string {
    const base = 'inline-flex items-center rounded-full px-2 py-0.5 text-[10px] font-medium';
    switch (status) {
      case 'passed': return `${base} bg-emerald-900/50 text-emerald-400`;
      case 'approved': return `${base} bg-emerald-900/50 text-emerald-400`;
      case 'failed': return `${base} bg-red-900/50 text-red-400`;
      case 'rejected': return `${base} bg-red-900/50 text-red-400`;
      case 'running': return `${base} bg-blue-900/50 text-blue-400`;
      case 'awaiting-owner': return `${base} bg-yellow-900/50 text-yellow-400`;
      case 'pending': return `${base} bg-gray-800 text-gray-400`;
      case 'skipped': return `${base} bg-gray-800 text-gray-500`;
      default: return `${base} bg-gray-800 text-gray-400`;
    }
  }

  progressLabel(p: PipelineSummary): string {
    if (p.status === 'completed' || p.status === 'failed' || p.status === 'cancelled') return p.stepCount.toString();
    return `${p.currentStepIndex + 1} / ${p.stepCount}`;
  }

  formatKind(kind: string): string {
    const map: Record<string, string> = {
      'ensure-data': 'Ensure Data',
      'start-run': 'Start Run',
      'await-run': 'Await Run',
      'assert-gates': 'Assert Gates',
      'reconcile': 'Reconcile',
      'exitlab-eval': 'Exit Lab',
      'walk-forward': 'Walk Forward',
      'apply-calibration': 'Apply Calibration',
      'owner-gate': 'Owner Gate',
      'report': 'Report',
    };
    return map[kind] ?? kind;
  }

  formatDate(iso: string): string {
    try {
      const d = new Date(iso);
      return d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' }) +
        ' ' + d.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
    } catch {
      return iso;
    }
  }

  truncate(s: string, max: number): string {
    return s.length > max ? s.slice(0, max) + '...' : s;
  }
}
