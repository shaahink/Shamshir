import { Component, ChangeDetectionStrategy } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-risk-hub',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <div class="space-y-4">
      <div class="flex items-center gap-1">
        <a
          routerLink="/risk/profiles"
          routerLinkActive="bg-gray-800 text-white border-emerald-500"
          [routerLinkActiveOptions]="{ exact: true }"
          class="rounded-t-md border-b-2 border-transparent px-4 py-2 text-sm text-gray-400 transition hover:text-white"
        >Profiles</a>
        <a
          routerLink="/risk/ftmo"
          routerLinkActive="bg-gray-800 text-white border-emerald-500"
          [routerLinkActiveOptions]="{ exact: true }"
          class="rounded-t-md border-b-2 border-transparent px-4 py-2 text-sm text-gray-400 transition hover:text-white"
        >FTMO Rules</a>
        <a
          routerLink="/risk/governor"
          routerLinkActive="bg-gray-800 text-white border-emerald-500"
          [routerLinkActiveOptions]="{ exact: true }"
          class="rounded-t-md border-b-2 border-transparent px-4 py-2 text-sm text-gray-400 transition hover:text-white"
        >Governor</a>
        <a
          routerLink="/risk/packs"
          routerLinkActive="bg-gray-800 text-white border-emerald-500"
          [routerLinkActiveOptions]="{ exact: true }"
          class="rounded-t-md border-b-2 border-transparent px-4 py-2 text-sm text-gray-400 transition hover:text-white"
        >Packs</a>
      </div>
      <router-outlet />
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RiskHubComponent {}
