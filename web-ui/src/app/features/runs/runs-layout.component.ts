import { Component, ChangeDetectionStrategy } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-runs-layout',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  template: `
    <div class="space-y-4">
      <nav class="flex gap-1 border-b border-gray-800 pb-2">
        <a
          routerLink="/runs"
          routerLinkActive="bg-gray-800 text-white"
          [routerLinkActiveOptions]="{ exact: true }"
          class="rounded-md px-3 py-1.5 text-sm text-gray-400 transition hover:text-white"
          >All runs</a
        >
        <a
          routerLink="/runs/compare"
          routerLinkActive="bg-gray-800 text-white"
          class="rounded-md px-3 py-1.5 text-sm text-gray-400 transition hover:text-white"
          >Compare</a
        >
        <a
          routerLink="/runs/trades"
          routerLinkActive="bg-gray-800 text-white"
          class="rounded-md px-3 py-1.5 text-sm text-gray-400 transition hover:text-white"
          >Trades</a
        >
        <a
          routerLink="/runs/ctrader"
          routerLinkActive="bg-gray-800 text-white"
          class="rounded-md px-3 py-1.5 text-sm text-gray-400 transition hover:text-white"
          >cTrader</a
        >
      </nav>
      <router-outlet />
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RunsLayoutComponent {}
