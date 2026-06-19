import { Component, inject, OnInit, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';

@Component({
  selector: 'app-strategy-detail',
  standalone: true,
  imports: [RouterLink, FormsModule],
  template: `
    <div class="space-y-6">
      <div class="flex items-center justify-between">
        <h1 class="text-xl font-semibold">{{ strategy()?.displayName || strategy()?.id || 'Strategy' }}</h1>
        <a routerLink="/strategies" class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800">All Strategies</a>
      </div>

      @if (strategy(); as s) {
        <div class="grid gap-4 md:grid-cols-2">
          <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
            <h2 class="mb-3 text-xs font-medium uppercase tracking-wide text-gray-500">Identity</h2>
            <dl class="space-y-2 text-sm">
              <div class="flex justify-between"><dt class="text-gray-400">ID</dt><dd class="font-mono text-gray-200">{{ s.id }}</dd></div>
              <div class="flex justify-between"><dt class="text-gray-400">Name</dt><dd class="text-gray-200">{{ s.displayName || s.id }}</dd></div>
              <div class="flex justify-between"><dt class="text-gray-400">Status</dt><dd [class.text-emerald-400]="s.enabled || s.isEnabled" [class.text-gray-500]="!(s.enabled || s.isEnabled)">{{ (s.enabled || s.isEnabled) ? 'Enabled' : 'Disabled' }}</dd></div>
            </dl>
          </div>
          <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
            <h2 class="mb-3 text-xs font-medium uppercase tracking-wide text-gray-500">Actions</h2>
            <div class="flex gap-2">
              <button (click)="toggle()" [disabled]="toggling()" [attr.class]="toggleBtnClass(s)">{{ toggling() ? '...' : (s.enabled || s.isEnabled) ? 'Disable' : 'Enable' }}</button>
              <button (click)="editing.set(!editing())" class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800">{{ editing() ? 'Cancel' : 'Edit Config' }}</button>
            </div>
          </div>
        </div>

        @if (editing()) {
          <div class="space-y-4 rounded-lg border border-gray-800 bg-gray-900/50 p-4">
            <div><label class="block text-xs font-medium text-gray-400 mb-1">Config JSON</label><textarea [(ngModel)]="configJson" rows="10" class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-xs font-mono text-gray-100 focus:border-emerald-500 focus:outline-none"></textarea></div>
            @if (validationError()) { <div class="rounded-md bg-red-900/20 p-3 text-xs text-red-400">{{ validationError() }}</div> }
            @if (savedOk()) { <div class="rounded-md bg-emerald-900/20 p-3 text-xs text-emerald-400">Saved</div> }
            <button (click)="save()" [disabled]="saving()" class="rounded-md bg-emerald-600 px-4 py-2 text-xs font-medium text-white hover:bg-emerald-500 disabled:opacity-50">{{ saving() ? 'Saving...' : 'Save Config' }}</button>
          </div>
        } @else {
          @if (configJson) {
            <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
              <h2 class="mb-2 text-xs font-medium uppercase tracking-wide text-gray-500">Config</h2>
              <pre class="overflow-auto text-xs text-gray-400">{{ configJson }}</pre>
            </div>
          } @else {
            <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-12 text-center"><p class="text-sm text-gray-500">No config data available. Click Edit Config to add.</p></div>
          }
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
  strategy = signal<any>(null);
  toggling = signal(false); editing = signal(false); saving = signal(false);
  configJson = ''; savedOk = signal(false);
  validationError = signal<string | null>(null);

  toggleBtnClass(s: any): string { const en = s.enabled ?? s.isEnabled; return `rounded-md px-3 py-1.5 text-xs font-medium ${en ? 'bg-red-900/50 text-red-400' : 'bg-emerald-900/50 text-emerald-400'}`; }

  async ngOnInit(): Promise<void> {
    const id = this.route.snapshot.paramMap.get('id'); if (!id) return;
    try {
      const data = await firstValueFrom(this.http.get<any>(`/api/strategies/${id}`));
      this.strategy.set(data);
      this.configJson = data.parametersJson ?? data.positionManagementJson ? JSON.stringify(data, null, 2) : '';
    } catch { /* */ }
  }

  async toggle(): Promise<void> {
    const s = this.strategy(); if (!s) return; this.toggling.set(true);
    const ep = (s.enabled ?? s.isEnabled) ? 'disable' : 'enable';
    await firstValueFrom(this.http.put(`/api/strategies/${s.id}/${ep}`, {}));
    this.strategy.set({ ...s, enabled: !(s.enabled ?? s.isEnabled), isEnabled: !(s.enabled ?? s.isEnabled) });
    this.toggling.set(false);
  }

  async save(): Promise<void> {
    this.validationError.set(null); this.savedOk.set(false);
    if (this.configJson) { try { JSON.parse(this.configJson); } catch { this.validationError.set('Invalid JSON'); return; } }
    this.saving.set(true);
    await firstValueFrom(this.http.put(`/api/strategies/${this.strategy()!.id}/config`, this.configJson));
    this.saving.set(false); this.savedOk.set(true); this.editing.set(false);
  }
}
