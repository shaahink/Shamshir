import { Component, computed, inject, OnInit, signal, ChangeDetectionStrategy } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { DatePipe } from '@angular/common';
import type { ExperimentDetail, FoldScore } from '../../models/api.types';
import { ExperimentsApiService } from './experiments.service';
import { BadgeComponent } from '../../shared/badge.component';

interface ScoringWeights {
  passProbability: number;
  expectancyR: number;
  maxDrawdown: number;
  foldConsistency: number;
}
const DEFAULT_WEIGHTS: ScoringWeights = { passProbability: 0.4, expectancyR: 0.3, maxDrawdown: 0.2, foldConsistency: 0.1 };

// specJson/scoreJson are written with plain JsonSerializer.Serialize(...) inside ExperimentRunner (no
// ASP.NET Core naming policy applied), so they come back PascalCase — unlike every other API response body,
// which goes through MVC's camelCase JSON formatter. Recursively lowercase the first letter of every key so
// the rest of this component can treat both blobs like normal camelCase API data.
function camelizeKeys(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(camelizeKeys);
  if (value !== null && typeof value === 'object') {
    const out: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(value as Record<string, unknown>)) {
      out[k.length > 0 ? k[0].toLowerCase() + k.slice(1) : k] = camelizeKeys(v);
    }
    return out;
  }
  return value;
}

interface ScoredRun extends FoldScore {
  variantLabel: string;
}

interface VariantSummary {
  label: string;
  composite: number;
  passProbability: number;
  expectancyR: number;
  maxDrawdownPercent: number;
  totalTrades: number;
  foldCount: number;
}

function mean(xs: number[]): number {
  return xs.length === 0 ? 0 : xs.reduce((a, b) => a + b, 0) / xs.length;
}
function normalizeExpectancy(r: number): number {
  return Math.min(Math.max((r + 1) / 3, 0), 1);
}
function foldConsistency(composites: number[]): number {
  if (composites.length < 2) return 1;
  const m = mean(composites);
  if (m === 0) return 1;
  const variance = mean(composites.map((c) => (c - m) * (c - m)));
  return Math.min(Math.max(1 - Math.sqrt(variance) / m, 0), 1);
}

// Mirrors VariantScorer.ScoreVariant (TradingEngine.Experiments) so the persisted per-fold rows can be
// re-aggregated into the same variant composite the backend would compute — the API only persists fold-
// level ExperimentRunEntity rows, not the aggregate VariantScore, once the page is reloaded.
function summarizeVariant(label: string, folds: ScoredRun[], weights: ScoringWeights): VariantSummary {
  const testFolds = folds.filter((f) => f.foldRole === 'Test');
  const useFolds = testFolds.length > 0 ? testFolds : folds;
  const avgPassProb = mean(useFolds.map((f) => f.passProbability));
  const avgExpectancyR = mean(useFolds.map((f) => f.expectancyR));
  const avgMaxDD = mean(useFolds.map((f) => f.maxDrawdownPercent));
  const totalTrades = useFolds.reduce((a, f) => a + f.totalTrades, 0);
  const consistency = foldConsistency(folds.map((f) => f.composite));
  const composite =
    avgPassProb * weights.passProbability +
    normalizeExpectancy(avgExpectancyR) * weights.expectancyR +
    (1 - Math.min(avgMaxDD / 100, 1)) * weights.maxDrawdown +
    consistency * weights.foldConsistency;
  return {
    label,
    composite,
    passProbability: avgPassProb,
    expectancyR: avgExpectancyR,
    maxDrawdownPercent: avgMaxDD,
    totalTrades,
    foldCount: folds.length,
  };
}

@Component({
  selector: 'app-experiment-detail',
  standalone: true,
  imports: [RouterLink, DatePipe, BadgeComponent],
  template: `
    <div class="space-y-6">
      <div class="flex items-center gap-3">
        <a routerLink="/experiments" class="text-sm text-gray-500 hover:text-gray-300">&larr; Experiments</a>
      </div>

      @if (experiment(); as exp) {
        <div class="flex items-start justify-between">
          <div>
            <h1 class="text-xl font-semibold">{{ exp.name }}</h1>
            <p class="mt-1 max-w-2xl text-sm text-gray-400">{{ exp.hypothesis }}</p>
          </div>
          <app-badge [label]="exp.status" [variant]="statusVariant(exp.status)" />
        </div>

        <div class="flex gap-6 text-xs text-gray-500">
          <span>Created {{ exp.createdUtc | date: 'yyyy-MM-dd HH:mm' }}</span>
          @if (exp.completedUtc) {
            <span>Completed {{ exp.completedUtc | date: 'yyyy-MM-dd HH:mm' }}</span>
          }
          @if (specSummary(); as s) {
            <span>{{ s.symbols }} · {{ s.timeframes }} · {{ s.strategies }} · {{ s.range }}</span>
          }
        </div>

        <div>
          <h2 class="mb-2 text-sm font-medium uppercase tracking-wide text-gray-500">Variants</h2>
          @if (variantSummaries().length === 0) {
            <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-6 text-center text-sm text-gray-500">
              No run rows yet{{ exp.status === 'Running' ? ' — still running.' : '.' }}
            </div>
          } @else {
            <div class="overflow-x-auto rounded-lg border border-gray-800">
              <table class="min-w-full text-sm">
                <thead class="bg-gray-900/50">
                  <tr>
                    <th class="px-4 py-2 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Variant</th>
                    <th class="px-4 py-2 text-right text-xs font-medium uppercase tracking-wide text-gray-500">Composite</th>
                    <th class="px-4 py-2 text-right text-xs font-medium uppercase tracking-wide text-gray-500">P(pass)</th>
                    <th class="px-4 py-2 text-right text-xs font-medium uppercase tracking-wide text-gray-500">Expectancy R</th>
                    <th class="px-4 py-2 text-right text-xs font-medium uppercase tracking-wide text-gray-500">Max DD%</th>
                    <th class="px-4 py-2 text-right text-xs font-medium uppercase tracking-wide text-gray-500">Trades</th>
                    <th class="px-4 py-2 text-right text-xs font-medium uppercase tracking-wide text-gray-500">Folds</th>
                  </tr>
                </thead>
                <tbody class="divide-y divide-gray-800">
                  @for (v of variantSummaries(); track v.label) {
                    <tr>
                      <td class="px-4 py-2 text-gray-100">{{ v.label }}</td>
                      <td class="whitespace-nowrap px-4 py-2 text-right font-mono text-xs tabular-nums">{{ v.composite.toFixed(3) }}</td>
                      <td class="whitespace-nowrap px-4 py-2 text-right font-mono text-xs tabular-nums">{{ (v.passProbability * 100).toFixed(1) }}%</td>
                      <td class="whitespace-nowrap px-4 py-2 text-right font-mono text-xs tabular-nums">{{ v.expectancyR.toFixed(2) }}R</td>
                      <td class="whitespace-nowrap px-4 py-2 text-right font-mono text-xs tabular-nums text-red-400">{{ v.maxDrawdownPercent.toFixed(1) }}%</td>
                      <td class="whitespace-nowrap px-4 py-2 text-right font-mono text-xs tabular-nums">{{ v.totalTrades }}</td>
                      <td class="whitespace-nowrap px-4 py-2 text-right font-mono text-xs tabular-nums text-gray-500">{{ v.foldCount }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        </div>

        <div>
          <button (click)="toggleReport()" class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800">
            {{ showReport() ? 'Hide report' : 'View full report' }}
          </button>
          @if (showReport()) {
            @if (reportLoading()) {
              <p class="mt-3 text-xs text-gray-500">Loading report...</p>
            } @else if (reportError()) {
              <p class="mt-3 text-xs text-red-400">{{ reportError() }}</p>
            } @else {
              <pre class="mt-3 max-h-[32rem] overflow-auto rounded-lg border border-gray-800 bg-gray-900/50 p-4 text-xs text-gray-300 whitespace-pre-wrap">{{ report() }}</pre>
            }
          }
        </div>
      } @else if (loading()) {
        <div class="py-12 text-center text-sm text-gray-500">Loading...</div>
      } @else if (error()) {
        <div class="rounded-lg border border-red-800 bg-red-900/20 p-4 text-sm text-red-400">{{ error() }}</div>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ExperimentDetailComponent implements OnInit {
  private route = inject(ActivatedRoute);
  private api = inject(ExperimentsApiService);

  experiment = signal<ExperimentDetail | null>(null);
  loading = signal(true);
  error = signal<string | null>(null);

  showReport = signal(false);
  report = signal('');
  reportLoading = signal(false);
  reportError = signal<string | null>(null);

  specSummary = computed(() => {
    const exp = this.experiment();
    if (!exp) return null;
    try {
      const spec = camelizeKeys(JSON.parse(exp.specJson)) as Record<string, unknown>;
      return {
        symbols: ((spec['symbols'] as string[]) ?? []).join(', '),
        timeframes: ((spec['timeframes'] as string[]) ?? []).join(', '),
        strategies: ((spec['strategies'] as string[]) ?? []).join(', '),
        range: `${spec['from']} → ${spec['to']}`,
      };
    } catch {
      return null;
    }
  });

  private weights = computed<ScoringWeights>(() => {
    const exp = this.experiment();
    if (!exp) return DEFAULT_WEIGHTS;
    try {
      const spec = camelizeKeys(JSON.parse(exp.specJson)) as Record<string, unknown>;
      return (spec['scoring'] as ScoringWeights) ?? DEFAULT_WEIGHTS;
    } catch {
      return DEFAULT_WEIGHTS;
    }
  });

  variantSummaries = computed<VariantSummary[]>(() => {
    const exp = this.experiment();
    if (!exp) return [];
    const scored: ScoredRun[] = [];
    for (const r of exp.runs) {
      try {
        const score = camelizeKeys(JSON.parse(r.scoreJson)) as FoldScore;
        scored.push({ ...score, variantLabel: r.variantLabel });
      } catch {
        /* skip rows with malformed scoreJson */
      }
    }
    const byLabel = new Map<string, ScoredRun[]>();
    for (const s of scored) {
      const arr = byLabel.get(s.variantLabel) ?? [];
      arr.push(s);
      byLabel.set(s.variantLabel, arr);
    }
    const weights = this.weights();
    return [...byLabel.entries()]
      .map(([label, folds]) => summarizeVariant(label, folds, weights))
      .sort((a, b) => b.composite - a.composite);
  });

  async ngOnInit(): Promise<void> {
    const id = this.route.snapshot.paramMap.get('id');
    if (!id) {
      this.error.set('Missing experiment id.');
      this.loading.set(false);
      return;
    }
    try {
      this.experiment.set(await this.api.getById(id));
    } catch {
      this.error.set('Failed to load experiment.');
    } finally {
      this.loading.set(false);
    }
  }

  statusVariant(status: string): 'success' | 'error' | 'warning' | 'neutral' {
    if (status === 'Completed') return 'success';
    if (status.startsWith('Failed')) return 'error';
    return 'warning';
  }

  async toggleReport(): Promise<void> {
    this.showReport.update((v) => !v);
    if (!this.showReport() || this.report()) return;
    const exp = this.experiment();
    if (!exp) return;
    this.reportLoading.set(true);
    this.reportError.set(null);
    try {
      this.report.set(await this.api.getReport(exp.id));
    } catch {
      this.reportError.set('Report not found — it may not have been written for this run.');
    } finally {
      this.reportLoading.set(false);
    }
  }
}
