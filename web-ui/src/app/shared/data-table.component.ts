import { Component, input, output } from '@angular/core';

export interface ColumnDef {
  key: string;
  label: string;
  format?: 'text' | 'number' | 'currency' | 'percent' | 'pips' | 'datetime' | 'duration';
  colorFn?: (val: number) => string;
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
            <tr class="transition hover:bg-gray-800/30 cursor-pointer" (click)="rowClick.emit(row)">
              @for (col of columns(); track col.key) {
                <td
                  class="whitespace-nowrap px-4 py-2 font-mono text-xs tabular-nums"
                  [style.color]="cellColor($any(row)[col.key], col)"
                >
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
  readonly rowClick = output<any>();

  cellColor(value: unknown, col: ColumnDef): string | null {
    if (!col.colorFn || value == null) return null;
    const n = Number(value);
    if (Number.isNaN(n)) return null;
    return col.colorFn(n);
  }

  formatValue(value: unknown, format?: string): string {
    if (value == null) return '-';
    const n = Number(value);
    if (Number.isNaN(n) && format !== 'datetime' && format !== 'text') return String(value);
    switch (format) {
      case 'currency':
        return n.toFixed(2);
      case 'percent':
        return (n * 100).toFixed(2) + '%';
      case 'pips':
        return n.toFixed(1);
      case 'datetime':
        return new Date(value as string).toLocaleString();
      case 'duration': {
        const h = Math.floor(n / 3600);
        const m = Math.floor((n % 3600) / 60);
        return h > 0 ? `${h}h ${m}m` : `${m}m`;
      }
      default:
        return String(value);
    }
  }
}
