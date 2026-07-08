import { Component, inject, signal, ChangeDetectionStrategy, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import type { ExitLabEvaluateRequest, ExitLabEvaluateResponse, ExitLabCellResponse, SaveCalibrationRequest } from '../../models/api.types';

@Component({
  selector: 'app-exit-lab',
  standalone: true,
  imports: [FormsModule, RouterLink],
  template: `
    <div class="space-y-6">
      <h1 class="text-xl font-semibold">Exit Lab</h1>
      <p class="text-xs text-gray-500">Calibrate exit rules (SL/TP/BE/Trail) from recorded MAE/MFE paths.</p>

      <!-- P4.1 (F11): empty-state when no excursions are available -->
      @if (emptyReason()) {
        <div class="rounded-lg border border-amber-800 bg-amber-900/20 p-4 text-xs">
          <p class="text-amber-300 font-medium mb-1">{{ emptyReason() }}</p>
          <p class="text-gray-400">
            The Exit Lab replays excursion paths recorded during a backtest. To generate them,
            enable <code class="text-amber-300">RecordExcursions</code> in the backtest form, or use the
            <a routerLink="/runs/new" [queryParams]="{ preset: 'exploration' }"
               class="text-emerald-400 hover:underline">Exploration Mode preset</a>
            (SL=ATR×4, no TP, no add-ons). Then run the
            <a routerLink="/research" class="text-emerald-400 hover:underline">explore-exit playbook</a>
            to go from data to calibrated exits.
          </p>
        </div>
      }

      <!-- Controls -->
      <div class="grid grid-cols-2 gap-3 rounded-lg border border-gray-800 bg-gray-900/50 p-4 md:grid-cols-5">
        <label class="text-xs text-gray-400">
          Run IDs <span class="text-gray-600">(comma-separated)</span>
          <input [(ngModel)]="runIds" class="mt-1 w-full rounded border border-gray-700 bg-gray-800 px-2 py-1 text-xs text-white" placeholder="run-abc, run-def" />
        </label>
        <label class="text-xs text-gray-400">
          Position IDs <span class="text-gray-600">(comma-separated)</span>
          <input [(ngModel)]="positionIds" class="mt-1 w-full rounded border border-gray-700 bg-gray-800 px-2 py-1 text-xs text-white" placeholder="guid-1, guid-2" />
        </label>
        <label class="text-xs text-gray-400">
          Reference ATR (pips)
          <input [(ngModel)]="refAtr" type="number" step="1" class="mt-1 w-full rounded border border-gray-700 bg-gray-800 px-2 py-1 text-xs text-white" />
        </label>
        <label class="text-xs text-gray-400">
          Regime <span class="text-gray-600">(optional)</span>
          <input [(ngModel)]="regimeFilter" class="mt-1 w-full rounded border border-gray-700 bg-gray-800 px-2 py-1 text-xs text-white" placeholder="e.g. London-NY" />
        </label>
        <div class="flex items-end">
          <button (click)="evaluate()" [disabled]="loading()"
            class="w-full rounded bg-emerald-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-emerald-500 disabled:opacity-40">
            {{ loading() ? 'Running...' : 'Evaluate Grid' }}
          </button>
        </div>
      </div>

      <!-- Calibration save -->
      @if (selectedCell()) {
        <div class="grid grid-cols-3 gap-3 rounded-lg border border-green-800 bg-green-900/20 p-4 md:grid-cols-6">
          <input [(ngModel)]="calStrategyId" placeholder="Strategy ID" class="w-full rounded border border-green-700 bg-gray-800 px-2 py-1 text-xs text-white" />
          <input [(ngModel)]="calSymbol" placeholder="Symbol" class="w-full rounded border border-green-700 bg-gray-800 px-2 py-1 text-xs text-white" />
          <input [(ngModel)]="calTf" placeholder="Timeframe" class="w-full rounded border border-green-700 bg-gray-800 px-2 py-1 text-xs text-white" />
          <input [(ngModel)]="calRegime" placeholder="Regime (opt)" class="w-full rounded border border-green-700 bg-gray-800 px-2 py-1 text-xs text-white" />
          <input [(ngModel)]="calDataset" placeholder="Dataset ID" class="w-full rounded border border-green-700 bg-gray-800 px-2 py-1 text-xs text-white" />
          <button (click)="saveCalibration()" class="w-full rounded bg-green-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-green-500">
            Save Calibration
          </button>
        </div>
      }

      <!-- Results -->
      @if (result()) {
        <div class="text-xs text-gray-500">
          {{ result()!.totalTrades }} trades · {{ result()!.totalCells }} cells
          @if (result()!.regime) { · regime={{ result()!.regime }} }
        </div>
        @if (result()!.regimeBreakdown) {
          <div class="flex flex-wrap gap-2 text-xs text-gray-500">
            @for (kv of regimeEntries(); track kv[0]) {
              <span class="rounded bg-gray-800 px-1.5 py-0.5">{{ kv[0] }}: {{ kv[1] }}</span>
            }
          </div>
        }
        <div class="overflow-x-auto rounded-lg border border-gray-800">
          <table class="w-full text-xs">
            <thead class="bg-gray-900 text-gray-400">
              <tr>
                <th class="px-2 py-1 text-left">SL×</th>
                <th class="px-2 py-1 text-left">TP×</th>
                <th class="px-2 py-1 text-left">BE×</th>
                <th class="px-2 py-1 text-left">Trail×</th>
                <th class="px-2 py-1 text-right">AvgR</th>
                <th class="px-2 py-1 text-right">Win%</th>
                <th class="px-2 py-1 text-right">MedR</th>
                <th class="px-2 py-1 text-right">Hold</th>
                <th class="px-2 py-1 text-right">MaxDD</th>
                <th class="px-2 py-1 text-right">P(pass)</th>
              </tr>
            </thead>
            <tbody class="divide-y divide-gray-800">
              @for (cell of result()!.cells; track cell) {
                 <tr (click)="selectCell(cell)"
                    [style.background]="selectedCell() === cell ? 'rgba(22,163,74,0.15)' : (cell.isPlateauCenter ? 'rgba(34,197,94,0.08)' : '')"
                    class="cursor-pointer hover:bg-gray-800/50">
                   <td class="px-2 py-1" [style.color]="cell.isPlateauCenter ? '#4ade80' : '#d1d5db'">{{ cell.rule.slAtrMultiple }}</td>
                  <td class="px-2 py-1 text-gray-300">{{ cell.rule.tpRrMultiple ?? '--' }}</td>
                  <td class="px-2 py-1 text-gray-300">{{ cell.rule.beTriggerR ?? '--' }}</td>
                  <td class="px-2 py-1 text-gray-300">{{ cell.rule.trailAtrMultiple ?? '--' }}</td>
                  <td class="px-2 py-1 text-right" [style.color]="cell.avgR > 0 ? '#4ade80' : cell.avgR < 0 ? '#f87171' : '#9ca3af'">{{ cell.avgR.toFixed(3) }}</td>
                  <td class="px-2 py-1 text-right text-gray-400">{{ (cell.winRate * 100).toFixed(1) }}%</td>
                  <td class="px-2 py-1 text-right text-gray-500">{{ cell.medianR.toFixed(3) }}</td>
                  <td class="px-2 py-1 text-right text-gray-500">{{ cell.avgHoldBars.toFixed(0) }}</td>
                  <td class="px-2 py-1 text-right text-gray-500">{{ cell.maxDdContributionR.toFixed(2) }}</td>
                  <td class="px-2 py-1 text-right" [style.color]="cell.passProbability >= 0.7 ? '#4ade80' : cell.passProbability >= 0.4 ? '#eab308' : '#f87171'">{{ (cell.passProbability * 100).toFixed(1) }}%</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }

      @if (error()) {
        <div class="rounded bg-red-900/20 px-3 py-2 text-xs text-red-400">{{ error() }}</div>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ExitLabComponent implements OnInit {
  private http = inject(HttpClient);
  private route = inject(ActivatedRoute);

  runIds = '';
  positionIds = '';
  refAtr = 20;
  regimeFilter = '';
  loading = signal(false);
  result = signal<ExitLabEvaluateResponse | null>(null);
  error = signal<string | null>(null);
  emptyReason = signal<string | null>(null);
  selectedCell = signal<ExitLabCellResponse | null>(null);

  calStrategyId = '';
  calSymbol = 'EURUSD';
  calTf = 'H1';
  calRegime = '';
  calDataset = 'default';

  ngOnInit(): void {
    this.route.queryParams.subscribe(params => {
      const ids = params['runIds'] as string;
      if (ids && !this.runIds) {
        this.runIds = ids;
      }
    });
  }

  async evaluate(): Promise<void> {
    this.error.set(null);
    this.emptyReason.set(null);
    this.loading.set(true);
    try {
      const req: ExitLabEvaluateRequest = {
        runIds: this.runIds.split(',').map((s) => s.trim()).filter(Boolean),
        positionIds: this.positionIds.split(',').map((s) => s.trim()).filter(Boolean),
        referenceAtrPips: this.refAtr,
        regime: this.regimeFilter.trim() || null,
      };
      const r = await firstValueFrom(this.http.post<ExitLabEvaluateResponse>('/api/exit-lab/evaluate', req));
      this.result.set(r);
      if (r.totalTrades === 0 && req.runIds.length > 0) {
        this.emptyReason.set('No excursion paths found for the selected run(s). RecordExcursions must be enabled in the backtest to record per-bar MAE/MFE paths.');
      }
    } catch (e: any) {
      this.error.set(e?.message ?? 'Evaluation failed');
    } finally {
      this.loading.set(false);
    }
  }

  selectCell(cell: ExitLabCellResponse): void {
    this.selectedCell.set(cell);
  }

  regimeEntries(): [string, number][] {
    const bd = this.result()?.regimeBreakdown;
    if (!bd) return [];
    return Object.entries(bd).sort((a, b) => a[0].localeCompare(b[0]));
  }

  async saveCalibration(): Promise<void> {
    if (!this.selectedCell()) return;
    const c = this.selectedCell()!;
    try {
      const req: SaveCalibrationRequest = {
        strategyId: this.calStrategyId || 'unknown',
        symbol: this.calSymbol,
        entryTimeframe: this.calTf,
        regime: this.calRegime.trim() || null,
        rule: c.rule,
        datasetId: this.calDataset,
        isStartUtc: new Date().toISOString(),
        isEndUtc: new Date().toISOString(),
      };
      await firstValueFrom(this.http.post('/api/exit-lab/calibrations', req));
      this.error.set(null);
    } catch (e: any) {
      this.error.set(e?.message ?? 'Save failed');
    }
  }
}
