import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <div class="min-h-screen bg-gray-950 text-gray-100">
      <nav class="border-b border-gray-800 bg-gray-900/50 backdrop-blur">
        <div class="mx-auto flex max-w-7xl items-center gap-6 px-6 py-3">
          <a routerLink="/runs" class="text-lg font-bold tracking-tight text-emerald-400">Shamshir</a>
          <div class="flex gap-1">
            <a routerLink="/" routerLinkActive="bg-gray-800 text-white"
              class="rounded-md px-3 py-1.5 text-sm text-gray-400 transition hover:text-white">Live</a>
            <a routerLink="/runs" routerLinkActive="bg-gray-800 text-white"
              class="rounded-md px-3 py-1.5 text-sm text-gray-400 transition hover:text-white">Runs</a>
            <a
              routerLink="/trades"
              routerLinkActive="bg-gray-800 text-white"
              class="rounded-md px-3 py-1.5 text-sm text-gray-400 transition hover:text-white"
            >
              Trades
            </a>
            <a
              routerLink="/strategies"
              routerLinkActive="bg-gray-800 text-white"
              class="rounded-md px-3 py-1.5 text-sm text-gray-400 transition hover:text-white"
            >
              Strategies
            </a>
            <a
              routerLink="/compliance"
              routerLinkActive="bg-gray-800 text-white"
              class="rounded-md px-3 py-1.5 text-sm text-gray-400 transition hover:text-white"
            >
              Compliance
            </a>
            <a routerLink="/events" routerLinkActive="bg-gray-800 text-white"
              class="rounded-md px-3 py-1.5 text-sm text-gray-400 transition hover:text-white">Events</a>
            <a routerLink="/risk-profiles" routerLinkActive="bg-gray-800 text-white"
              class="rounded-md px-3 py-1.5 text-sm text-gray-400 transition hover:text-white">Risk</a>
            <a routerLink="/prop-firm-rules" routerLinkActive="bg-gray-800 text-white"
              class="rounded-md px-3 py-1.5 text-sm text-gray-400 transition hover:text-white">FTMO</a>
            <a routerLink="/governor-options" routerLinkActive="bg-gray-800 text-white"
              class="rounded-md px-3 py-1.5 text-sm text-gray-400 transition hover:text-white">Governor</a>
            <a routerLink="/settings" routerLinkActive="bg-gray-800 text-white"
              class="rounded-md px-3 py-1.5 text-sm text-gray-400 transition hover:text-white">Settings</a>
            <a
              href="/runs/new"
              class="ml-4 rounded-md bg-emerald-600 px-3 py-1.5 text-sm font-medium text-white transition hover:bg-emerald-500"
            >
              + New Backtest
            </a>
          </div>
          <div class="ml-auto flex items-center gap-3">
            <a href="/scalar/v1" target="_blank" class="text-xs text-gray-500 hover:text-gray-300">API Docs</a>
          </div>
        </div>
      </nav>
      <main class="mx-auto max-w-7xl px-6 py-6">
        <router-outlet />
      </main>
    </div>
  `,
})
export class AppComponent {}
