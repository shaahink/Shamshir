import { Component, inject, OnInit, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { RouterLink } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { DataTableComponent, type ColumnDef } from '../../../shared/data-table.component';
import type { TradeSummary } from '../../../models/api.types';

@Component({
  selector: 'app-trade-list',
  standalone: true,
  imports: [RouterLink, FormsModule, DataTableComponent],
  template: `
    <div class="space-y-6">
      <h1 class="text-xl font-semibold">All Trades</h1>

      <div class="flex flex-wrap gap-2">
        <input [(ngModel)]="filterSymbol" placeholder="Symbol" class="w-24 rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none" />
        <input [(ngModel)]="filterStrategy" placeholder="Strategy" class="w-32 rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none" />
        <select [(ngModel)]="filterDirection" class="rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"><option value="">All</option><option value="Long">Long</option><option value="Short">Short</option></select>
        <input type="date" [(ngModel)]="filterFrom" class="rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none w-36" />
        <input type="date" [(ngModel)]="filterTo" class="rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none w-36" />
        <button (click)="load()" class="rounded-md bg-emerald-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-emerald-500">Apply</button>
        <button (click)="clear()" class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800">Clear</button>
      </div>

      <div class="flex items-center gap-2 text-xs text-gray-500">
        <span>Page {{ page() }} · {{ filtered().length }} results</span>
        <button (click)="prevPage()" [disabled]="page() <= 1" class="rounded border border-gray-700 px-2 py-0.5 disabled:opacity-30">Prev</button>
        <button (click)="nextPage()" [disabled]="page() * pageSize >= filtered().length" class="rounded border border-gray-700 px-2 py-0.5 disabled:opacity-30">Next</button>
      </div>

      <app-data-table [columns]="columns" [data]="$any(pagedTrades())" />
    </div>
  `,
})
export class TradeListComponent implements OnInit {
  private http = inject(HttpClient);
  allTrades = signal<TradeSummary[]>([]);
  filterSymbol = ''; filterStrategy = ''; filterDirection = ''; filterFrom = ''; filterTo = '';
  page = signal(1); pageSize = 50;

  filtered = () => {
    let t = this.allTrades();
    if (this.filterSymbol) t = t.filter(x => x.symbol.toUpperCase().includes(this.filterSymbol.toUpperCase()));
    if (this.filterStrategy) t = t.filter(x => x.strategyId.toLowerCase().includes(this.filterStrategy.toLowerCase()));
    if (this.filterDirection) t = t.filter(x => x.direction === this.filterDirection);
    if (this.filterFrom) t = t.filter(x => x.closedAtUtc >= this.filterFrom);
    if (this.filterTo) t = t.filter(x => x.closedAtUtc <= this.filterTo);
    return t;
  };

  pagedTrades = () => {
    const start = (this.page() - 1) * this.pageSize;
    return this.filtered().slice(start, start + this.pageSize);
  };

  columns: ColumnDef[] = [
    { key: 'symbol', label: 'Sym' }, { key: 'direction', label: 'Dir' }, { key: 'lots', label: 'Lots', format: 'number' },
    { key: 'entryPrice', label: 'Entry', format: 'number' }, { key: 'exitPrice', label: 'Exit', format: 'number' },
    { key: 'netPnLAmount', label: 'Net P/L', format: 'currency', colorFn: (v: number) => v >= 0 ? '#34d399' : '#f87171' },
    { key: 'pnLPips', label: 'Pips', format: 'pips' }, { key: 'rMultiple', label: 'R', format: 'number' },
    { key: 'exitReason', label: 'Exit' }, { key: 'strategyId', label: 'Strategy' }, { key: 'closedAtUtc', label: 'Closed', format: 'datetime' },
  ];

  async ngOnInit(): Promise<void> { await this.load(); }
  async load(): Promise<void> { const data = await firstValueFrom(this.http.get<TradeSummary[]>('/api/trades')); this.allTrades.set(data); this.page.set(1); }
  clear(): void { this.filterSymbol = ''; this.filterStrategy = ''; this.filterDirection = ''; this.filterFrom = ''; this.filterTo = ''; this.page.set(1); }
  prevPage(): void { this.page.set(Math.max(1, this.page() - 1)); }
  nextPage(): void { this.page.set(this.page() + 1); }
}
