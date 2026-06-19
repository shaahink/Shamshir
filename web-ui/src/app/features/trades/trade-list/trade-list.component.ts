import { Component, inject, OnInit, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { DataTableComponent, type ColumnDef } from '../../../shared/data-table.component';
import type { TradeSummary } from '../../../models/api.types';

@Component({
  selector: 'app-trade-list',
  standalone: true,
  imports: [RouterLink, DataTableComponent],
  template: `
    <div class="space-y-6">
      <h1 class="text-xl font-semibold">All Trades</h1>
      <app-data-table [columns]="columns" [data]="$any(trades())" />
    </div>
  `,
})
export class TradeListComponent implements OnInit {
  private http = inject(HttpClient);
  trades = signal<TradeSummary[]>([]);

  columns: ColumnDef[] = [
    { key: 'symbol', label: 'Sym' },
    { key: 'direction', label: 'Dir' },
    { key: 'lots', label: 'Lots', format: 'number' },
    { key: 'entryPrice', label: 'Entry', format: 'number' },
    { key: 'exitPrice', label: 'Exit', format: 'number' },
    { key: 'netPnLAmount', label: 'Net P/L', format: 'currency', colorFn: (v: number) => v >= 0 ? '#34d399' : '#f87171' },
    { key: 'pnLPips', label: 'Pips', format: 'pips' },
    { key: 'rMultiple', label: 'R', format: 'number' },
    { key: 'exitReason', label: 'Exit' },
    { key: 'strategyId', label: 'Strategy' },
    { key: 'closedAtUtc', label: 'Closed', format: 'datetime' },
  ];

  async ngOnInit(): Promise<void> {
    const data = await firstValueFrom(this.http.get<TradeSummary[]>('/api/trades'));
    this.trades.set(data);
  }
}
