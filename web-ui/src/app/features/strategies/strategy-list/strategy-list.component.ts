import { Component, inject, OnInit, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { RouterLink } from '@angular/router';
import { NgClass } from '@angular/common';
import { firstValueFrom } from 'rxjs';

@Component({
  selector: 'app-strategy-list',
  standalone: true,
  imports: [RouterLink, NgClass],
  template: `
    <div class="space-y-6">
      <h1 class="text-xl font-semibold">Strategies</h1>

      <div class="grid gap-4 md:grid-cols-2">
        @for (s of strategies(); track s.id) {
          <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4 transition hover:border-gray-700">
            <div class="flex items-center justify-between">
              <a [routerLink]="['/strategies', s.id]" class="text-sm font-medium text-gray-100 hover:text-emerald-400">
                {{ s.displayName }}
              </a>
              <span
                class="inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium"
                [ngClass]="statusClasses(s.isEnabled)"
              >{{ s.isEnabled ? 'Enabled' : 'Disabled' }}</span>
            </div>
            <p class="mt-1 font-mono text-xs text-gray-500">{{ s.id }}</p>
          </div>
        }
      </div>
    </div>
  `,
})
export class StrategyListComponent implements OnInit {
  private http = inject(HttpClient);
  strategies = signal<any[]>([]);

  statusClasses(enabled: boolean): Record<string, boolean> {
    return {
      'bg-emerald-900/50': enabled,
      'text-emerald-400': enabled,
      'bg-gray-800': !enabled,
      'text-gray-500': !enabled,
    };
  }

  async ngOnInit(): Promise<void> {
    const data = await firstValueFrom(this.http.get<any[]>('/api/strategies'));
    this.strategies.set(data);
  }
}
