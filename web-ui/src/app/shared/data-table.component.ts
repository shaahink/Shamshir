import { Component, input, output, signal, computed, ChangeDetectionStrategy } from '@angular/core';

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
    <div class="space-y-2">
      @if (searchable()) {
        <input
          type="text"
          [value]="searchTerm()"
          (input)="onSearch($event)"
          placeholder="Search..."
          class="w-full rounded-md border border-gray-700 bg-gray-900/50 px-3 py-1.5 text-xs text-gray-300 placeholder-gray-600 outline-none focus:border-gray-500"
        />
      }
      <div class="overflow-x-auto rounded-lg border border-gray-800">
        <table class="min-w-full text-sm">
          <thead class="bg-gray-900/50">
            <tr>
              @for (col of columns(); track col.key) {
                <th
                  class="cursor-pointer select-none px-4 py-2 text-left text-xs font-medium uppercase tracking-wide text-gray-500 hover:text-gray-300"
                  (click)="toggleSort(col)"
                >
                  <span class="inline-flex items-center gap-1">
                    {{ col.label }}
                    @if (sortColumn() === col.key) {
                      <span class="text-gray-400">{{ sortDir() === 'asc' ? '\u25B2' : '\u25BC' }}</span>
                    }
                  </span>
                </th>
              }
            </tr>
          </thead>
          <tbody class="divide-y divide-gray-800">
            @for (row of displayData(); track (trackKey() ? $any(row)[trackKey()!] : $index)) {
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
        @if (displayData().length === 0) {
          <div class="px-4 py-8 text-center text-sm text-gray-500">
            {{ data().length === 0 ? 'No data' : 'No matches' }}
          </div>
        }
      </div>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DataTableComponent {
  readonly columns = input.required<ColumnDef[]>();
  readonly data = input.required<unknown[]>();
  readonly rowClick = output<any>();
  readonly trackKey = input<string>('');
  readonly searchable = input(true);

  // P4: sort state
  readonly sortColumn = signal<string | null>(null);
  readonly sortDir = signal<'asc' | 'desc' | null>(null);
  readonly searchTerm = signal('');

  // P4: debounced search
  private searchTimeout: ReturnType<typeof setTimeout> | null = null;

  onSearch(event: Event): void {
    const val = (event.target as HTMLInputElement).value;
    if (this.searchTimeout) clearTimeout(this.searchTimeout);
    this.searchTimeout = setTimeout(() => this.searchTerm.set(val), 200);
  }

  toggleSort(col: ColumnDef): void {
    if (this.sortColumn() === col.key) {
      if (this.sortDir() === 'asc') {
        this.sortDir.set('desc');
      } else if (this.sortDir() === 'desc') {
        this.sortColumn.set(null);
        this.sortDir.set(null);
      } else {
        this.sortDir.set('asc');
      }
    } else {
      this.sortColumn.set(col.key);
      this.sortDir.set('asc');
    }
  }

  readonly displayData = computed(() => {
    let rows = [...this.data()];
    const sc = this.sortColumn();
    const sd = this.sortDir();
    const st = this.searchTerm().toLowerCase().trim();
    const cols = this.columns();

    // Filter
    if (st) {
      rows = rows.filter((row) =>
        cols.some((col) => {
          const val = this.rawCell(row, col);
          return String(val ?? '').toLowerCase().includes(st);
        }),
      );
    }

    // Sort
    if (sc && sd) {
      const col = cols.find((c) => c.key === sc);
      rows.sort((a, b) => this.compare(a, b, sc, col, sd));
    }

    return rows;
  });

  private compare(a: unknown, b: unknown, key: string, col?: ColumnDef, dir: 'asc' | 'desc' = 'asc'): number {
    const va = (a as any)?.[key];
    const vb = (b as any)?.[key];
    if (va == null && vb == null) return 0;
    if (va == null) return 1;
    if (vb == null) return -1;

    const fmt = col?.format;
    let cmp: number;

    if (fmt === 'number' || fmt === 'currency' || fmt === 'percent' || fmt === 'pips') {
      cmp = Number(va) - Number(vb);
    } else if (fmt === 'datetime') {
      cmp = new Date(va as string).getTime() - new Date(vb as string).getTime();
    } else if (fmt === 'duration') {
      cmp = Number(va) - Number(vb);
    } else {
      cmp = String(va).localeCompare(String(vb));
    }

    return dir === 'asc' ? cmp : -cmp;
  }

  private rawCell(row: unknown, col: ColumnDef): unknown {
    return (row as any)?.[col.key];
  }

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
      case 'number':
        return n.toFixed(2);
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
        return h > 0 ? h + 'h ' + m + 'm' : m + 'm';
      }
      default:
        return String(value);
    }
  }
}
