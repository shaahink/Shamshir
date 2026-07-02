import { Component, inject, OnInit, signal, ChangeDetectionStrategy } from '@angular/core';
import { RouterLink } from '@angular/router';
import { DatePipe } from '@angular/common';
import type { ExperimentSummary } from '../../models/api.types';
import { ExperimentsApiService } from './experiments.service';
import { BadgeComponent } from '../../shared/badge.component';

@Component({
  selector: 'app-experiment-list',
  standalone: true,
  imports: [RouterLink, DatePipe, BadgeComponent],
  template: `
    <div class="space-y-6">
      <div class="flex items-center justify-between">
        <h1 class="text-xl font-semibold">Experiments</h1>
        <a
          routerLink="/experiments/new"
          class="rounded-md bg-emerald-600 px-4 py-2 text-sm font-medium text-white hover:bg-emerald-500"
          >+ New Experiment</a
        >
      </div>

      @if (loading()) {
        <div class="py-12 text-center text-sm text-gray-500">Loading...</div>
      } @else if (error()) {
        <div class="rounded-lg border border-red-800 bg-red-900/20 p-4 text-sm text-red-400">{{ error() }}</div>
      } @else if (experiments().length === 0) {
        <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-12 text-center">
          <p class="text-sm text-gray-500">No experiments yet.</p>
          <a routerLink="/experiments/new" class="mt-3 inline-block text-sm text-emerald-400 hover:underline"
            >Run your first experiment</a
          >
        </div>
      } @else {
        <div class="overflow-x-auto rounded-lg border border-gray-800">
          <table class="min-w-full text-sm">
            <thead class="bg-gray-900/50">
              <tr>
                <th class="px-4 py-2 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Name</th>
                <th class="px-4 py-2 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Status</th>
                <th class="px-4 py-2 text-right text-xs font-medium uppercase tracking-wide text-gray-500">Runs</th>
                <th class="px-4 py-2 text-right text-xs font-medium uppercase tracking-wide text-gray-500">Created</th>
              </tr>
            </thead>
            <tbody class="divide-y divide-gray-800">
              @for (e of experiments(); track e.id) {
                <tr class="cursor-pointer transition hover:bg-gray-800/30" [routerLink]="['/experiments', e.id]">
                  <td class="px-4 py-2 text-gray-100">{{ e.name }}</td>
                  <td class="whitespace-nowrap px-4 py-2">
                    <app-badge [label]="e.status" [variant]="statusVariant(e.status)" />
                  </td>
                  <td class="whitespace-nowrap px-4 py-2 text-right font-mono text-xs tabular-nums">{{ e.runCount }}</td>
                  <td class="whitespace-nowrap px-4 py-2 text-right font-mono text-xs tabular-nums text-gray-500">
                    {{ e.createdUtc | date: 'MM-dd HH:mm' }}
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ExperimentListComponent implements OnInit {
  private api = inject(ExperimentsApiService);
  experiments = signal<ExperimentSummary[]>([]);
  loading = signal(true);
  error = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    try {
      this.experiments.set(await this.api.getAll());
    } catch {
      this.error.set('Failed to load experiments.');
    } finally {
      this.loading.set(false);
    }
  }

  statusVariant(status: string): 'success' | 'error' | 'warning' | 'neutral' {
    if (status === 'Completed') return 'success';
    if (status.startsWith('Failed')) return 'error';
    return 'warning';
  }
}
