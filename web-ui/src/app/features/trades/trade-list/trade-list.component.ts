import { Component, inject, OnInit, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { DataTableComponent, type ColumnDef } from '../../../shared/data-table.component';

@Component({
  selector: 'app-trade-list',
  standalone: true,
  imports: [RouterLink, DataTableComponent],
  template: `
    <div class="space-y-6">
      <h1 class="text-xl font-semibold">All Trades</h1>
      <app-data-table
        [columns]="columns"
        [data]="$any(trades())"
      />
    </div>
  `,
})
export class TradeListComponent implements OnInit {
  private http = inject(HttpClient);
  trades = signal<unknown[]>([]);

  columns: ColumnDef[] = [
    { key: 'symbol', label: 'Symbol' },
    { key: 'direction', label: 'Dir' },
    { key: 'lots', label: 'Lots', format: 'number' },
    { key: 'entryPrice', label: 'Entry', format: 'number' },
    { key: 'exitPrice', label: 'Exit', format: 'number' },
    { key: 'netPnLAmount', label: 'Net P/L', format: 'currency' },
    { key: 'pnLPips', label: 'Pips', format: 'pips' },
    { key: 'rMultiple', label: 'R', format: 'number' },
    { key: 'exitReason', label: 'Exit' },
    { key: 'strategyId', label: 'Strategy' },
  ];

  async ngOnInit(): Promise<void> {
    const data = await firstValueFrom(this.http.get<any[]>('/api/trades'));
    this.trades.set(data);
  }
}
