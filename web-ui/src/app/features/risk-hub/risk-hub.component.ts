import { Component, ChangeDetectionStrategy } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-risk-hub',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  template: `
    <div class="space-y-4">
      <nav class="flex gap-1 border-b border-gray-800 pb-2">
        <a
          routerLink="/risk/profiles"
          routerLinkActive="bg-gray-800 text-white"
          class="rounded-md px-3 py-1.5 text-sm text-gray-400 transition hover:text-white"
          >Profiles</a
        >
        <a
          routerLink="/risk/ftmo"
          routerLinkActive="bg-gray-800 text-white"
          class="rounded-md px-3 py-1.5 text-sm text-gray-400 transition hover:text-white"
          >FTMO</a
        >
        <a
          routerLink="/risk/governor"
          routerLinkActive="bg-gray-800 text-white"
          class="rounded-md px-3 py-1.5 text-sm text-gray-400 transition hover:text-white"
          >Governor</a
        >
        <a
          routerLink="/risk/packs"
          routerLinkActive="bg-gray-800 text-white"
          class="rounded-md px-3 py-1.5 text-sm text-gray-400 transition hover:text-white"
          >Packs</a
        >
      </nav>
      <router-outlet />
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RiskHubComponent {}
