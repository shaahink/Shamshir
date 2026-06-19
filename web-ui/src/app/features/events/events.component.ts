import { Component, inject, OnInit, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { DataTableComponent, type ColumnDef } from '../../shared/data-table.component';
import type { PipelineEvent } from '../../models/api.types';

@Component({
  selector: 'app-events',
  standalone: true,
  imports: [FormsModule, DataTableComponent],
  template: `
    <div class="space-y-6">
      <h1 class="text-xl font-semibold">Pipeline Events</h1>
      <div class="flex gap-3">
        <input [(ngModel)]="runIdFilter"
          placeholder="Run ID"
          class="w-64 rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 placeholder-gray-500 focus:border-emerald-500 focus:outline-none" />
        <button (click)="load()"
          class="rounded-md bg-emerald-600 px-4 py-2 text-sm font-medium text-white hover:bg-emerald-500">Load</button>
      </div>
      <app-data-table [columns]="columns" [data]="$any(events())" />
    </div>
  `,
})
export class EventsComponent implements OnInit {
  private http = inject(HttpClient);
  runIdFilter = '';
  events = signal<PipelineEvent[]>([]);

  columns: ColumnDef[] = [
    { key: 'seq', label: 'Seq' },
    { key: 'simTimeUtc', label: 'Time', format: 'datetime' },
    { key: 'kind', label: 'Kind' },
    { key: 'symbol', label: 'Symbol' },
    { key: 'strategyId', label: 'Strategy' },
    { key: 'reason', label: 'Reason' },
  ];

  async ngOnInit(): Promise<void> { await this.load(); }

  async load(): Promise<void> {
    const url = this.runIdFilter
      ? `/api/events?runId=${this.runIdFilter}&tail=500`
      : '/api/events?tail=200';
    const data = await firstValueFrom(this.http.get<PipelineEvent[]>(url));
    this.events.set(data);
  }
}
