import { Component, computed, inject, OnInit, signal, ChangeDetectionStrategy } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import type { StrategySummary } from '../../models/api.types';
import { StrategiesApiService } from '../strategies/strategies.service';
import { ExperimentsApiService } from './experiments.service';

const ALL_SYMBOLS = [
  'EURUSD', 'GBPUSD', 'USDJPY', 'GBPJPY', 'XAUUSD', 'AUDUSD',
  'USDCHF', 'USDCAD', 'NZDUSD', 'EURGBP', 'EURJPY', 'XAGUSD',
];
const ALL_TIMEFRAMES = ['H1', 'H4', 'D1', 'M15', 'M5', 'M1'];

interface VariantForm {
  label: string;
  overridesText: string;
}

function toIsoDate(d: Date): string {
  return d.toISOString().slice(0, 10);
}

@Component({
  selector: 'app-experiment-new',
  standalone: true,
  imports: [FormsModule, RouterLink],
  template: `
    <div class="max-w-4xl space-y-6">
      <div class="flex items-center gap-3">
        <a routerLink="/experiments" class="text-sm text-gray-500 hover:text-gray-300">&larr; Experiments</a>
      </div>
      <h1 class="text-xl font-semibold">New Experiment</h1>

      <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-5 space-y-5">
        <fieldset class="grid grid-cols-1 gap-3 md:grid-cols-2">
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Name</label>
            <input [(ngModel)]="name" type="text" placeholder="trailing-method-comparison"
              class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none" />
          </div>
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Hypothesis</label>
            <input [(ngModel)]="hypothesis" type="text" placeholder="What are you trying to learn?"
              class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none" />
          </div>
        </fieldset>

        <fieldset>
          <legend class="mb-2 text-xs font-medium uppercase tracking-wide text-gray-500">Strategies</legend>
          @if (strategies().length === 0) {
            <p class="text-xs text-gray-600">No strategies configured.</p>
          } @else {
            <div class="flex flex-wrap gap-1.5">
              @for (s of strategies(); track s.id) {
                <label [attr.class]="pillClass(selectedStrategyIds().has(s.id))" (click)="toggleStrategy(s.id)">{{ s.id }}</label>
              }
            </div>
          }
        </fieldset>

        <fieldset class="grid grid-cols-1 gap-4 md:grid-cols-2">
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-2">Symbols</label>
            <div class="flex flex-wrap gap-1.5">
              @for (s of allSymbols; track s) {
                <label [attr.class]="pillClass(selectedSymbols().has(s))" (click)="toggleSymbol(s)">{{ s }}</label>
              }
            </div>
          </div>
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-2">Timeframes</label>
            <div class="flex flex-wrap gap-1.5">
              @for (tf of allTimeframes; track tf) {
                <label [attr.class]="pillClass(selectedTimeframes().has(tf))" (click)="toggleTimeframe(tf)">{{ tf }}</label>
              }
            </div>
          </div>
        </fieldset>

        <fieldset class="grid grid-cols-2 gap-3">
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">From</label>
            <input [(ngModel)]="from" type="date"
              class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none" />
          </div>
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">To</label>
            <input [(ngModel)]="to" type="date"
              class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none" />
          </div>
        </fieldset>

        <fieldset>
          <label class="flex items-center gap-2 text-xs text-gray-400">
            <input type="checkbox" [(ngModel)]="useWalkForward"
              class="h-3.5 w-3.5 rounded border-gray-600 bg-gray-800 text-emerald-500 focus:ring-emerald-500" />
            Walk-forward folds
          </label>
          @if (useWalkForward) {
            <div class="mt-2 grid grid-cols-2 gap-3">
              <div>
                <label class="block text-xs font-medium text-gray-400 mb-1">Folds</label>
                <input [(ngModel)]="folds" type="number" min="1" max="20"
                  class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none" />
              </div>
              <div>
                <label class="block text-xs font-medium text-gray-400 mb-1">Train fraction</label>
                <input [(ngModel)]="trainFraction" type="number" min="0.1" max="0.9" step="0.05"
                  class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none" />
              </div>
            </div>
          }
        </fieldset>

        <fieldset>
          <div class="mb-2 flex items-center justify-between">
            <legend class="text-xs font-medium uppercase tracking-wide text-gray-500">Variants — {{ variants().length }}</legend>
            <button (click)="addVariant()" class="text-xs text-emerald-400 hover:underline">+ add variant</button>
          </div>
          <div class="space-y-2">
            @for (v of variants(); track $index; let i = $index) {
              <div class="rounded-md border border-gray-800 p-3 space-y-2">
                <div class="flex items-center gap-2">
                  <input [ngModel]="v.label" (ngModelChange)="setVariantLabel(i, $event)" type="text" placeholder="label"
                    class="flex-1 rounded-md border border-gray-700 bg-gray-800 px-3 py-1.5 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none" />
                  @if (variants().length > 1) {
                    <button (click)="removeVariant(i)" class="rounded border border-red-800 px-2 py-1 text-xs text-red-400 hover:bg-red-900/20">Remove</button>
                  }
                </div>
                <textarea [ngModel]="v.overridesText" (ngModelChange)="setVariantOverrides(i, $event)" rows="2"
                  placeholder='Optional config overrides, e.g. {"positionManagement.trailing.method": "AtrMultiple"}'
                  class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-1.5 font-mono text-xs text-gray-300 focus:border-emerald-500 focus:outline-none"></textarea>
              </div>
            }
          </div>
        </fieldset>

        @if (error()) {
          <div class="rounded-md border border-red-800 bg-red-900/20 p-3 text-sm text-red-400">{{ error() }}</div>
        }

        <div class="flex items-center gap-3">
          <button (click)="submit()" [disabled]="!canSubmit() || submitting()"
            class="rounded-md bg-emerald-600 px-4 py-2 text-sm font-medium text-white hover:bg-emerald-500 disabled:opacity-50">
            {{ submitting() ? 'Running…' : 'Run Experiment' }}
          </button>
          @if (submitting()) {
            <span class="text-xs text-gray-500">This runs every variant/fold synchronously — can take a while for large sweeps.</span>
          }
        </div>
      </div>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ExperimentNewComponent implements OnInit {
  private strategiesApi = inject(StrategiesApiService);
  private experimentsApi = inject(ExperimentsApiService);
  private router = inject(Router);

  allSymbols = ALL_SYMBOLS;
  allTimeframes = ALL_TIMEFRAMES;
  strategies = signal<StrategySummary[]>([]);

  name = '';
  hypothesis = '';
  selectedSymbols = signal<Set<string>>(new Set(['EURUSD']));
  selectedTimeframes = signal<Set<string>>(new Set(['H1']));
  selectedStrategyIds = signal<Set<string>>(new Set());
  from = toIsoDate(new Date(Date.now() - 30 * 86_400_000));
  to = toIsoDate(new Date());
  useWalkForward = false;
  folds = 4;
  trainFraction = 0.7;
  variants = signal<VariantForm[]>([{ label: 'baseline', overridesText: '' }]);

  submitting = signal(false);
  error = signal<string | null>(null);

  canSubmit = computed(
    () =>
      this.name.trim().length > 0 &&
      this.selectedSymbols().size > 0 &&
      this.selectedTimeframes().size > 0 &&
      this.selectedStrategyIds().size > 0 &&
      this.variants().every((v) => v.label.trim().length > 0) &&
      this.from <= this.to,
  );

  async ngOnInit(): Promise<void> {
    try {
      const strategies = await this.strategiesApi.getAll();
      this.strategies.set(strategies);
      this.selectedStrategyIds.set(new Set(strategies.filter((s) => s.isEnabled).map((s) => s.id).slice(0, 1)));
    } catch {
      /* strategy picker stays empty; user can still type a name/date range and fix later */
    }
  }

  pillClass(active: boolean): string {
    const base = 'cursor-pointer rounded-full border px-2.5 py-1 text-xs transition';
    return active
      ? `${base} border-emerald-600 bg-emerald-900/40 text-emerald-300`
      : `${base} border-gray-700 text-gray-400 hover:border-gray-500`;
  }

  toggleSymbol(s: string): void {
    this.selectedSymbols.update((set) => toggleSet(set, s));
  }
  toggleTimeframe(tf: string): void {
    this.selectedTimeframes.update((set) => toggleSet(set, tf));
  }
  toggleStrategy(id: string): void {
    this.selectedStrategyIds.update((set) => toggleSet(set, id));
  }

  addVariant(): void {
    this.variants.update((v) => [...v, { label: `variant-${v.length + 1}`, overridesText: '' }]);
  }
  removeVariant(i: number): void {
    this.variants.update((v) => v.filter((_, idx) => idx !== i));
  }
  setVariantLabel(i: number, label: string): void {
    this.variants.update((v) => v.map((x, idx) => (idx === i ? { ...x, label } : x)));
  }
  setVariantOverrides(i: number, overridesText: string): void {
    this.variants.update((v) => v.map((x, idx) => (idx === i ? { ...x, overridesText } : x)));
  }

  async submit(): Promise<void> {
    if (!this.canSubmit() || this.submitting()) return;
    this.error.set(null);

    let variantsPayload: { label: string; overrides?: Record<string, unknown> }[];
    try {
      variantsPayload = this.variants().map((v) => ({
        label: v.label.trim(),
        overrides: v.overridesText.trim() ? JSON.parse(v.overridesText) : undefined,
      }));
    } catch {
      this.error.set('One or more variant overrides is not valid JSON.');
      return;
    }

    const totalRuns = variantsPayload.length * (this.useWalkForward ? this.folds * 2 : 1);

    this.submitting.set(true);
    try {
      const result = await this.experimentsApi.create({
        name: this.name.trim(),
        hypothesis: this.hypothesis.trim(),
        symbols: [...this.selectedSymbols()],
        timeframes: [...this.selectedTimeframes()],
        strategies: [...this.selectedStrategyIds()],
        from: this.from,
        to: this.to,
        walkForward: this.useWalkForward ? { folds: this.folds, trainFraction: this.trainFraction } : null,
        variants: variantsPayload,
        maxRuns: Math.max(64, totalRuns + 8),
      });
      this.router.navigate(['/experiments', result.experimentId]);
    } catch (err: unknown) {
      const message = (err as { error?: { error?: string } })?.error?.error;
      this.error.set(message ?? 'Failed to run experiment.');
    } finally {
      this.submitting.set(false);
    }
  }
}

function toggleSet<T>(set: Set<T>, value: T): Set<T> {
  const next = new Set(set);
  if (next.has(value)) next.delete(value);
  else next.add(value);
  return next;
}
