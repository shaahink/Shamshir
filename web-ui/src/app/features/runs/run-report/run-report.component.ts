import { Component, inject, OnInit, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { DatePipe, JsonPipe } from '@angular/common';
import { RunsStore } from '../runs.store';
import { RunsApiService, type TradeSummary, type JournalEntry, type EquityPoint } from '../runs.service';
import { StatTileComponent } from '../../../shared/stat-tile.component';
import { DataTableComponent, type ColumnDef } from '../../../shared/data-table.component';
import { EquityChartComponent } from '../../../shared/equity-chart.component';

@Component({
  selector: 'app-run-report',
  standalone: true,
  imports: [RouterLink, DatePipe, JsonPipe, StatTileComponent, DataTableComponent, EquityChartComponent],
  template: `
    @if (store.isLoading()) {
      <div class="py-12 text-center text-sm text-gray-500">Loading...</div>
    } @else {
      @if (store.selectedRun(); as detail) {
        <div class="space-y-6">
          <div class="flex items-center justify-between">
            <div>
              <h1 class="text-xl font-semibold">
                Run <span class="font-mono text-sm text-gray-400">{{ detail.runId.slice(0, 8) }}</span>
              </h1>
              <p class="text-sm text-gray-500">
                {{ detail.symbol }} · {{ detail.period }} · {{ detail.backtestFrom | date }} – {{ detail.backtestTo | date }}
              </p>
            </div>
            <div class="flex gap-2">
              <a [routerLink]="['/runs', detail.runId, 'monitor']"
                 class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800">
                Monitor
              </a>
              <a routerLink="/runs"
                 class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800">
                All Runs
              </a>
            </div>
          </div>

          <div class="grid grid-cols-2 gap-3 md:grid-cols-4">
            <app-stat-tile label="Net P/L" [value]="detail.netProfit.toFixed(2)"
              [positive]="detail.netProfit > 0" [negative]="detail.netProfit < 0" />
            <app-stat-tile label="Max DD"
              [value]="(detail.maxDrawdownPct * 100).toFixed(2) + '%'" [negative]="true" />
            <app-stat-tile label="Trades" [value]="detail.totalTrades" />
            <app-stat-tile label="Win Rate"
              [value]="(detail.winRatePct * 100).toFixed(1) + '%'"
              [positive]="detail.winRatePct > 0.5" />
          </div>

          @if (equityData().length > 0) {
            <app-equity-chart title="Equity Curve" [data]="equityData()" />
          }

          @if (trades().length > 0) {
            <div>
              <h2 class="mb-3 text-sm font-medium text-gray-400">Trades</h2>
              <app-data-table [columns]="tradeColumns" [data]="$any(trades())" />
            </div>
          }

          @if (journal().length > 0) {
            <div>
              <h2 class="mb-3 text-sm font-medium text-gray-400">Journal</h2>
              <div class="max-h-96 overflow-y-auto rounded-lg border border-gray-800">
                @for (entry of journal(); track entry.seq) {
                  <div class="border-b border-gray-800 px-4 py-2 text-xs last:border-0">
                    <span class="text-gray-500">{{ entry.simTimeUtc | date:'HH:mm:ss' }}</span>
                    <span class="ml-2 font-medium text-gray-300">{{ entry.kind }}</span>
                    @if (entry.reason) {
                      <span class="ml-2 text-gray-500">— {{ entry.reason }}</span>
                    }
                  </div>
                }
              </div>
            </div>
          }
        </div>
      } @else {
        <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-12 text-center">
          <p class="text-sm text-gray-500">Run not found.</p>
        </div>
      }
    }
  `,
})
export class RunReportComponent implements OnInit {
  private route = inject(ActivatedRoute);
  private api = inject(RunsApiService);
  readonly store = inject(RunsStore);

  trades = signal<TradeSummary[]>([]);
  journal = signal<JournalEntry[]>([]);
  equityData = signal<{ time: number; value: number }[]>([]);

  tradeColumns: ColumnDef[] = [
    { key: 'symbol', label: 'Symbol' },
    { key: 'direction', label: 'Dir' },
    { key: 'lots', label: 'Lots', format: 'number' },
    { key: 'entryPrice', label: 'Entry', format: 'number' },
    { key: 'exitPrice', label: 'Exit', format: 'number' },
    { key: 'netPnLAmount', label: 'Net P/L', format: 'currency' },
    { key: 'commissionAmount', label: 'Comm', format: 'currency' },
    { key: 'swapAmount', label: 'Swap', format: 'currency' },
    { key: 'pnLPips', label: 'Pips', format: 'pips' },
    { key: 'rMultiple', label: 'R', format: 'number' },
    { key: 'exitReason', label: 'Exit Reason' },
    { key: 'strategyId', label: 'Strategy' },
  ];

  async ngOnInit(): Promise<void> {
    const runId = this.route.snapshot.paramMap.get('runId');
    if (!runId) return;
    await this.store.loadRun(runId);
    const [trades, journal, equity] = await Promise.all([
      this.api.getRunTrades(runId),
      this.api.getRunJournal(runId, undefined, undefined, 100),
      this.api.getRunEquity(runId),
    ]);
    this.trades.set(trades);
    this.journal.set(journal);
    this.equityData.set(equity.map((p: EquityPoint) => ({
      time: new Date(p.timestampUtc).getTime(),
      value: p.equity,
    })));
  }
}
