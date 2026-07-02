import { Component, ChangeDetectionStrategy } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-runs-hub',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <div class="space-y-4">
      <div class="flex items-center justify-between">
        <div class="flex items-center gap-1">
          <a
            routerLink="/runs/all"
            routerLinkActive="bg-gray-800 text-white border-emerald-500"
            [routerLinkActiveOptions]="{ exact: true }"
            class="rounded-t-md border-b-2 border-transparent px-4 py-2 text-sm text-gray-400 transition hover:text-white"
          >All Runs</a>
          <a
            routerLink="/runs/compare"
            routerLinkActive="bg-gray-800 text-white border-emerald-500"
            [routerLinkActiveOptions]="{ exact: true }"
            class="rounded-t-md border-b-2 border-transparent px-4 py-2 text-sm text-gray-400 transition hover:text-white"
          >Compare</a>
          <a
            routerLink="/runs/trades"
            routerLinkActive="bg-gray-800 text-white border-emerald-500"
            [routerLinkActiveOptions]="{ exact: true }"
            class="rounded-t-md border-b-2 border-transparent px-4 py-2 text-sm text-gray-400 transition hover:text-white"
          >Trades</a>
          <a
            routerLink="/runs/ctrader"
            routerLinkActive="bg-gray-800 text-white border-emerald-500"
            [routerLinkActiveOptions]="{ exact: true }"
            class="rounded-t-md border-b-2 border-transparent px-4 py-2 text-sm text-gray-400 transition hover:text-white"
          >cTrader</a>
        </div>
        <a
          routerLink="/runs/new"
          class="rounded-md bg-emerald-600 px-4 py-2 text-sm font-medium text-white hover:bg-emerald-500"
        >+ New Backtest</a>
      </div>
      <router-outlet />
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RunsHubComponent {}
