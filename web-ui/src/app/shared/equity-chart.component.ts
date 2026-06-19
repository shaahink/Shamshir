import { Component, input } from '@angular/core';

@Component({
  selector: 'app-equity-chart',
  standalone: true,
  template: `
    <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
      <h3 class="mb-2 text-sm font-medium text-gray-400">{{ title() }}</h3>
      <div class="flex h-64 items-center justify-center">
        <p class="text-sm text-gray-500">
          {{ data().length }} equity points
        </p>
      </div>
    </div>
  `,
})
export class EquityChartComponent {
  readonly title = input('Equity Curve');
  readonly data = input<{ time: number; value: number }[]>([]);
  readonly lineColor = input('#10b981');
}
