import { Component, inject, OnInit, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { NgClass } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import type { StrategyDetail } from '../../../models/api.types';
import { StatTileComponent } from '../../../shared/stat-tile.component';

@Component({
  selector: 'app-strategy-detail',
  standalone: true,
  imports: [RouterLink, NgClass, FormsModule, StatTileComponent],
  template: `
    <div class="space-y-6">
      <div class="flex items-center justify-between">
        <h1 class="text-xl font-semibold">{{ strategy()?.displayName || 'Strategy' }}</h1>
        <a routerLink="/strategies" class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800">All Strategies</a>
      </div>

      @if (strategy(); as s) {
        <div class="grid gap-4 md:grid-cols-2">
          <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
            <h2 class="mb-3 text-xs font-medium uppercase tracking-wide text-gray-500">Identity</h2>
            <dl class="space-y-2 text-sm">
              <div class="flex justify-between"><dt class="text-gray-400">ID</dt><dd class="font-mono text-gray-200">{{ s.id }}</dd></div>
              <div class="flex justify-between"><dt class="text-gray-400">Status</dt><dd [class.text-emerald-400]="s.isEnabled" [class.text-gray-500]="!s.isEnabled">{{ s.isEnabled ? 'Enabled' : 'Disabled' }}</dd></div>
            </dl>
          </div>
          <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
            <h2 class="mb-3 text-xs font-medium uppercase tracking-wide text-gray-500">Actions</h2>
            <div class="flex gap-2">
              <button (click)="toggle()" [disabled]="toggling()" class="rounded-md px-3 py-1.5 text-xs font-medium"
                [ngClass]="s.isEnabled ? 'bg-red-900/50 text-red-400 hover:bg-red-800/50' : 'bg-emerald-900/50 text-emerald-400 hover:bg-emerald-800/50'">{{ toggling() ? '...' : s.isEnabled ? 'Disable' : 'Enable' }}</button>
            </div>
          </div>
        </div>

        @if (s.parametersJson) {
          <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
            <h2 class="mb-2 text-xs font-medium uppercase tracking-wide text-gray-500">Parameters</h2>
            <pre class="overflow-auto text-xs text-gray-400">{{ s.parametersJson }}</pre>
          </div>
        }
        @if (s.positionManagementJson) {
          <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
            <h2 class="mb-2 text-xs font-medium uppercase tracking-wide text-gray-500">Position Management</h2>
            <pre class="overflow-auto text-xs text-gray-400">{{ s.positionManagementJson }}</pre>
          </div>
        }
        @if (s.orderEntryJson) {
          <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
            <h2 class="mb-2 text-xs font-medium uppercase tracking-wide text-gray-500">Order Entry</h2>
            <pre class="overflow-auto text-xs text-gray-400">{{ s.orderEntryJson }}</pre>
          </div>
        }
        @if (s.regimeFilterJson) {
          <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
            <h2 class="mb-2 text-xs font-medium uppercase tracking-wide text-gray-500">Regime Filter</h2>
            <pre class="overflow-auto text-xs text-gray-400">{{ s.regimeFilterJson }}</pre>
          </div>
        }
      } @else {
        <div class="py-12 text-center text-sm text-gray-500">Strategy not found.</div>
      }
    </div>
  `,
})
export class StrategyDetailComponent implements OnInit {
  private route = inject(ActivatedRoute);
  private http = inject(HttpClient);

  strategy = signal<StrategyDetail | null>(null);
  toggling = signal(false);

  async ngOnInit(): Promise<void> {
    const id = this.route.snapshot.paramMap.get('id');
    if (!id) return;
    try {
      const data = await firstValueFrom(this.http.get<StrategyDetail>(`/api/strategies/${id}`));
      this.strategy.set(data);
    } catch { /* not found */ }
  }

  async toggle(): Promise<void> {
    const s = this.strategy();
    if (!s) return;
    this.toggling.set(true);
    const endpoint = s.isEnabled ? 'disable' : 'enable';
    await firstValueFrom(this.http.put(`/api/strategies/${s.id}/${endpoint}`, {}));
    this.strategy.set({ ...s, isEnabled: !s.isEnabled });
    this.toggling.set(false);
  }
}
