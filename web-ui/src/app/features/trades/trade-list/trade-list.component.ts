import { Component, inject, OnInit, signal, ChangeDetectionStrategy } from '@angular/core';
import { RouterLink } from '@angular/router';
import { FormsModule } from '@angular/forms';
import type { TradeSummary } from '../../../models/api.types';
import { TradesApiService } from '../trades.service';
import { TRADE_COLUMNS, type ColumnDef } from '../../../shared/trade-columns';

@Component({
  selector: 'app-trade-list',
  standalone: true,
  imports: [RouterLink, FormsModule],
  template: `
    <div class="space-y-6">
      <h1 class="text-xl font-semibold">All Trades</h1>

      <div class="flex flex-wrap gap-2">
        <input
          [(ngModel)]="filterSymbol"
          placeholder="Symbol"
          class="w-24 rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
        />
        <input
          [(ngModel)]="filterStrategy"
          placeholder="Strategy"
          class="w-32 rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
        />
        <select
          [(ngModel)]="filterDirection"
          class="rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
        >
          <option value="">All</option>
          <option value="Long">Long</option>
          <option value="Short">Short</option>
        </select>
        <input
          type="date"
          [(ngModel)]="filterFrom"
          class="rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none w-36"
        />
        <input
          type="date"
          [(ngModel)]="filterTo"
          class="rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none w-36"
        />
        <button
          (click)="load()"
          class="rounded-md bg-emerald-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-emerald-500"
        >
          Apply
        </button>
        <button
          (click)="clear()"
          class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800"
        >
          Clear
        </button>
      </div>

      <div class="flex items-center gap-2 text-xs text-gray-500">
        <span>Page {{ page() }} · {{ filtered().length }} results</span>
        <button
          (click)="prevPage()"
          [disabled]="page() <= 1"
          class="rounded border border-gray-700 px-2 py-0.5 disabled:opacity-30"
        >
          Prev
        </button>
        <button
          (click)="nextPage()"
          [disabled]="page() * pageSize >= filtered().length"
          class="rounded border border-gray-700 px-2 py-0.5 disabled:opacity-30"
        >
          Next
        </button>
      </div>

      <div class="overflow-x-auto rounded-lg border border-gray-800">
        <table class="min-w-full text-sm">
          <thead class="bg-gray-900/50">
            <tr>
              @for (col of columns; track col.key) {
                <th class="px-4 py-2 text-left text-xs font-medium uppercase tracking-wide text-gray-500">
                  {{ col.label }}
                </th>
              }
            </tr>
          </thead>
          <tbody class="divide-y divide-gray-800">
            @for (row of pagedTrades(); track row.id) {
              <tr class="cursor-pointer transition hover:bg-gray-800/30" [routerLink]="['/trades', row.id]">
                @for (col of columns; track col.key) {
                  <td class="whitespace-nowrap px-4 py-2 font-mono text-xs tabular-nums">{{ fmtVal(row, col) }}</td>
                }
              </tr>
            }
          </tbody>
        </table>
        @if (allTrades().length === 0) {
          <div class="px-4 py-8 text-center text-sm text-gray-500">No trades</div>
        }
      </div>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TradeListComponent implements OnInit {
  private api = inject(TradesApiService);
  allTrades = signal<TradeSummary[]>([]);
  filterSymbol = '';
  filterStrategy = '';
  filterDirection = '';
  filterFrom = '';
  filterTo = '';
  page = signal(1);
  pageSize = 50;

  columns: ColumnDef[] = [
    { key: 'timeframe', label: 'TF' },
    ...TRADE_COLUMNS,
    { key: 'entryType', label: 'Type' },
    { key: 'durationSeconds', label: 'Hold', format: 'duration' },
    { key: 'maxAdverseExcursion', label: 'MAE', format: 'number' },
    { key: 'maxFavorableExcursion', label: 'MFE', format: 'number' },
  ];

  filtered(): TradeSummary[] {
    let t = this.allTrades();
    if (this.filterSymbol) t = t.filter((x) => x.symbol.toUpperCase().includes(this.filterSymbol.toUpperCase()));
    if (this.filterStrategy)
      t = t.filter((x) => x.strategyId.toLowerCase().includes(this.filterStrategy.toLowerCase()));
    if (this.filterDirection) t = t.filter((x) => x.direction === this.filterDirection);
    if (this.filterFrom) t = t.filter((x) => x.closedAtUtc >= this.filterFrom);
    if (this.filterTo) t = t.filter((x) => x.closedAtUtc <= this.filterTo);
    return t;
  }

  pagedTrades(): TradeSummary[] {
    const start = (this.page() - 1) * this.pageSize;
    return this.filtered().slice(start, start + this.pageSize);
  }

  fmtVal(
    row: TradeSummary,
    col: { key: string; label: string; format?: string; colorFn?: (v: number) => string },
  ): string {
    const v = (row as any)[col.key];
    if (v == null) return '-';
    const n = Number(v);
    switch (col.format) {
      case 'currency':
        return Number.isNaN(n) ? String(v) : n.toFixed(2);
      case 'pips':
        return Number.isNaN(n) ? String(v) : n.toFixed(1);
      case 'number':
        return Number.isNaN(n) ? String(v) : n.toString();
      default:
        return String(v);
    }
  }

  async ngOnInit(): Promise<void> {
    await this.load();
  }
  async load(): Promise<void> {
    const data = await this.api.getAll();
    this.allTrades.set(data);
    this.page.set(1);
  }
  clear(): void {
    this.filterSymbol = '';
    this.filterStrategy = '';
    this.filterDirection = '';
    this.filterFrom = '';
    this.filterTo = '';
  }
  prevPage(): void {
    this.page.set(Math.max(1, this.page() - 1));
  }
  nextPage(): void {
    this.page.set(this.page() + 1);
  }
}
