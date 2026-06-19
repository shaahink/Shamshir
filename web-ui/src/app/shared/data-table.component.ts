import { Component, input } from '@angular/core';

export interface ColumnDef {
  key: string;
  label: string;
  format?: 'text' | 'number' | 'currency' | 'percent' | 'pips' | 'datetime' | 'duration';
}

@Component({
  selector: 'app-data-table',
  standalone: true,
  template: `
    <div class="overflow-x-auto rounded-lg border border-gray-800">
      <table class="min-w-full text-sm">
        <thead class="bg-gray-900/50">
          <tr>
            @for (col of columns(); track col.key) {
              <th class="px-4 py-2 text-left text-xs font-medium uppercase tracking-wide text-gray-500">
                {{ col.label }}
              </th>
            }
          </tr>
        </thead>
        <tbody class="divide-y divide-gray-800">
          @for (row of data(); track $index) {
            <tr class="transition hover:bg-gray-800/30">
              @for (col of columns(); track col.key) {
                <td class="whitespace-nowrap px-4 py-2 font-mono text-xs tabular-nums">
                  {{ formatValue($any(row)[col.key], col.format) }}
                </td>
              }
            </tr>
          }
        </tbody>
      </table>
      @if (data().length === 0) {
        <div class="px-4 py-8 text-center text-sm text-gray-500">No data</div>
      }
    </div>
  `,
})
export class DataTableComponent {
  readonly columns = input.required<ColumnDef[]>();
  readonly data = input.required<unknown[]>();

  formatValue(value: unknown, format?: string): string {
    if (value == null) return '-';
    const n = Number(value);
    switch (format) {
      case 'currency':
        return n.toFixed(2);
      case 'percent':
        return (n * 100).toFixed(2) + '%';
      case 'pips':
        return n.toFixed(1);
      case 'datetime':
        return new Date(value as string).toLocaleString();
      case 'duration':
        return this.formatDuration(n);
      default:
        return String(value);
    }
  }

  private formatDuration(seconds: number): string {
    const h = Math.floor(seconds / 3600);
    const m = Math.floor((seconds % 3600) / 60);
    return h > 0 ? `${h}h ${m}m` : `${m}m`;
  }
}
