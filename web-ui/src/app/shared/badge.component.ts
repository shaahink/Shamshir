import { Component, input } from '@angular/core';

@Component({
  selector: 'app-badge',
  standalone: true,
  template: `<span [class]="badgeClasses()">{{ label() }}</span>`,
})
export class BadgeComponent {
  readonly label = input.required<string>();
  readonly variant = input<'success' | 'error' | 'warning' | 'neutral'>('neutral');

  badgeClasses(): string {
    const base = 'inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium';
    switch (this.variant()) {
      case 'success':
        return base + ' bg-emerald-900/50 text-emerald-400';
      case 'error':
        return base + ' bg-red-900/50 text-red-400';
      case 'warning':
        return base + ' bg-yellow-900/50 text-yellow-400';
      default:
        return base + ' bg-gray-800 text-gray-400';
    }
  }
}
