import { Component, input, ChangeDetectionStrategy } from '@angular/core';

@Component({
  selector: 'app-stat-tile',
  standalone: true,
  template: `
    <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
      <div class="text-xs font-medium uppercase tracking-wide text-gray-500">{{ label() }}</div>
      <div
        class="mt-1 text-2xl font-semibold tabular-nums"
        [class.text-emerald-400]="positive()"
        [class.text-red-400]="negative()"
        [class.text-gray-100]="!positive() && !negative()"
      >
        {{ value() ?? '-' }}
      </div>
      @if (subtitle()) {
        <div class="mt-0.5 text-xs text-gray-500">{{ subtitle() }}</div>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class StatTileComponent {
  readonly label = input.required<string>();
  readonly value = input.required<string | number | null>();
  readonly subtitle = input<string>();
  readonly positive = input(false);
  readonly negative = input(false);
}
