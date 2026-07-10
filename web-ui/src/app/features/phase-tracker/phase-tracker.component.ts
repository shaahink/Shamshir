import { Component, inject, signal, ChangeDetectionStrategy } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

interface PassProbResult {
  passProbability: number;
  expectedDaysToTarget: number | null;
  dailyBreachRate: number;
  maxBreachRate: number;
  projectedEquity: number;
  recommendation: string;
}

@Component({
  selector: 'app-phase-tracker',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="mx-auto max-w-2xl space-y-6 p-4">
      <h1 class="text-xl font-semibold">Phase Tracker</h1>
      <p class="text-sm text-gray-500">Estimate FTMO phase pass probability from a completed backtest run.</p>

      <div class="grid grid-cols-2 gap-4">
        <label class="flex flex-col gap-1 text-sm">
          Run ID
          <input [(ngModel)]="runId" class="rounded border border-gray-700 bg-gray-900 px-3 py-1.5 text-white" placeholder="e.g. abc123" />
        </label>
        <label class="flex flex-col gap-1 text-sm">
          Days Remaining
          <input type="number" [(ngModel)]="daysRemaining" class="rounded border border-gray-700 bg-gray-900 px-3 py-1.5 text-white" />
        </label>
      </div>

      <p class="text-xs text-gray-500">Supply a completed backtest run ID. The estimator samples from that run&#39;s actual daily P&amp;L under FTMO challenge rules.</p>

      <button class="rounded bg-blue-700 px-4 py-1.5 text-sm text-white hover:bg-blue-600 disabled:opacity-50"
        [disabled]="computing() || !runId() || daysRemaining() <= 0" (click)="compute()">
        {{ computing() ? 'Computing...' : 'Estimate P(pass)' }}
      </button>

      @if (error()) {
        <p class="text-sm text-red-400">{{ error() }}</p>
      }

      @if (passResult()) {
        <div class="space-y-2 rounded border border-gray-700 bg-gray-800/50 p-3">
          <div class="flex justify-between text-sm">
            <span class="text-gray-400">P(pass)</span>
            <span [class.text-green-400]="passResult()!.passProbability > 50"
              [class.text-yellow-400]="passResult()!.passProbability <= 50"
              class="font-semibold">{{ (passResult()!.passProbability).toFixed(1) }}%</span>
          </div>
          <div class="flex justify-between text-sm">
            <span class="text-gray-400">Daily Breach Rate</span>
            <span class="text-red-400">{{ (passResult()!.dailyBreachRate * 100).toFixed(1) }}%</span>
          </div>
          <div class="flex justify-between text-sm">
            <span class="text-gray-400">Projected Equity</span>
            <span>&#36;{{ passResult()!.projectedEquity.toFixed(0) }}</span>
          </div>
          <p class="text-xs text-gray-500">{{ passResult()!.recommendation }}</p>
        </div>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PhaseTrackerComponent {
  private http = inject(HttpClient);

  runId = signal('');
  daysRemaining = signal(30);
  computing = signal(false);
  passResult = signal<PassProbResult | null>(null);
  error = signal<string | null>(null);

  async compute(): Promise<void> {
    this.error.set(null);
    this.computing.set(true);
    try {
      const body = {
        initialBalance: 0,
        daysRemaining: this.daysRemaining(),
        profitTargetPercent: 0,
        maxDailyLossPercent: 0,
        maxTotalLossPercent: 0,
        runId: this.runId() || undefined,
      };
      const r = await firstValueFrom(
        this.http.post<PassProbResult>('/api/phase-tracker/evaluate', body)
      );
      this.passResult.set(r);
    } catch (err: any) {
      this.error.set(err?.error ?? 'Request failed. Check the Run ID and try again.');
    } finally {
      this.computing.set(false);
    }
  }
}
