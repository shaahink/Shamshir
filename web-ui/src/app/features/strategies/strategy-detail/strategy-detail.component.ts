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
        <h1 class="text-xl font-semibold">{{ strategy()?.displayName || 'Strategy' }}</h1>
        <a routerLink="/strategies" class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800">All Strategies</a>
      </div>

      @if (strategy(); as s) {
        <div class="flex gap-2">
          <button (click)="toggle()" [disabled]="toggling()" [attr.class]="toggleBtnClass(s.isEnabled)">{{ toggling() ? '...' : s.isEnabled ? 'Disable' : 'Enable' }}</button>
          <button (click)="editing.set(!editing())" class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800">{{ editing() ? 'Cancel' : 'Edit' }}</button>
        </div>

        @if (editing()) {
          <div class="space-y-4 rounded-lg border border-gray-800 bg-gray-900/50 p-4">
            <div><label class="block text-xs font-medium text-gray-400 mb-1">Parameters JSON</label><textarea [(ngModel)]="paramsJson" rows="6" class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-xs font-mono text-gray-100 focus:border-emerald-500 focus:outline-none"></textarea></div>
            <div><label class="block text-xs font-medium text-gray-400 mb-1">Position Management JSON</label><textarea [(ngModel)]="posMgmtJson" rows="4" class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-xs font-mono text-gray-100 focus:border-emerald-500 focus:outline-none"></textarea></div>
            <div><label class="block text-xs font-medium text-gray-400 mb-1">Order Entry JSON</label><textarea [(ngModel)]="orderEntryJson" rows="4" class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-xs font-mono text-gray-100 focus:border-emerald-500 focus:outline-none"></textarea></div>
            <div><label class="block text-xs font-medium text-gray-400 mb-1">Regime Filter JSON</label><textarea [(ngModel)]="regimeJson" rows="4" class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-xs font-mono text-gray-100 focus:border-emerald-500 focus:outline-none"></textarea></div>
            @if (validationError()) { <div class="rounded-md bg-red-900/20 p-3 text-xs text-red-400">{{ validationError() }}</div> }
            <button (click)="save()" [disabled]="saving()" class="rounded-md bg-emerald-600 px-4 py-2 text-xs font-medium text-white hover:bg-emerald-500 disabled:opacity-50">{{ saving() ? 'Saving...' : 'Save' }}</button>
          </div>
        } @else {
          @if (s.parametersJson) { <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4"><h2 class="mb-2 text-xs font-medium uppercase tracking-wide text-gray-500">Parameters</h2><pre class="overflow-auto text-xs text-gray-400">{{ s.parametersJson }}</pre></div> }
          @if (s.positionManagementJson) { <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4"><h2 class="mb-2 text-xs font-medium uppercase tracking-wide text-gray-500">Position Management</h2><pre class="overflow-auto text-xs text-gray-400">{{ s.positionManagementJson }}</pre></div> }
          @if (s.orderEntryJson) { <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4"><h2 class="mb-2 text-xs font-medium uppercase tracking-wide text-gray-500">Order Entry</h2><pre class="overflow-auto text-xs text-gray-400">{{ s.orderEntryJson }}</pre></div> }
          @if (s.regimeFilterJson) { <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4"><h2 class="mb-2 text-xs font-medium uppercase tracking-wide text-gray-500">Regime Filter</h2><pre class="overflow-auto text-xs text-gray-400">{{ s.regimeFilterJson }}</pre></div> }
        }
      } @else { <div class="py-12 text-center text-sm text-gray-500">Strategy not found.</div> }
    </div>
  `,
})
export class StrategyDetailComponent implements OnInit {
  private route = inject(ActivatedRoute);
  private http = inject(HttpClient);
  strategy = signal<any>(null);
  toggling = signal(false); editing = signal(false); saving = signal(false);
  paramsJson = ''; posMgmtJson = ''; orderEntryJson = ''; regimeJson = '';
  validationError = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    const id = this.route.snapshot.paramMap.get('id'); if (!id) return;
    try {
      const data = await firstValueFrom(this.http.get<any>(`/api/strategies/${id}`));
      this.strategy.set(data);
      this.paramsJson = data.parametersJson ?? ''; this.posMgmtJson = data.positionManagementJson ?? '';
      this.orderEntryJson = data.orderEntryJson ?? ''; this.regimeJson = data.regimeFilterJson ?? '';
    } catch { /* */ }
  }

  toggleBtnClass(enabled: boolean): string { return `rounded-md px-3 py-1.5 text-xs font-medium ${enabled ? 'bg-red-900/50 text-red-400' : 'bg-emerald-900/50 text-emerald-400'}`; }

  async toggle(): Promise<void> {
    const s = this.strategy(); if (!s) return; this.toggling.set(true);
    const ep = s.isEnabled ? 'disable' : 'enable';
    await firstValueFrom(this.http.put(`/api/strategies/${s.id}/${ep}`, {}));
    this.strategy.set({ ...s, isEnabled: !s.isEnabled }); this.toggling.set(false);
  }

  async save(): Promise<void> {
    this.validationError.set(null);
    for (const [label, json] of [['Parameters', this.paramsJson], ['Position Management', this.posMgmtJson], ['Order Entry', this.orderEntryJson], ['Regime Filter', this.regimeJson]] as const) {
      if (json) { try { JSON.parse(json); } catch { this.validationError.set(`${label}: invalid JSON`); return; } }
    }
    this.saving.set(true);
    const body: any = {};
    if (this.paramsJson) body.parametersJson = this.paramsJson;
    if (this.posMgmtJson) body.positionManagementJson = this.posMgmtJson;
    if (this.orderEntryJson) body.orderEntryJson = this.orderEntryJson;
    if (this.regimeJson) body.regimeFilterJson = this.regimeJson;
    await firstValueFrom(this.http.put(`/api/strategies/${this.strategy()!.id}/config`, body));
    const s = this.strategy()!;
    this.strategy.set({ ...s, parametersJson: this.paramsJson || null, positionManagementJson: this.posMgmtJson || null, orderEntryJson: this.orderEntryJson || null, regimeFilterJson: this.regimeJson || null });
    this.editing.set(false); this.saving.set(false);
  }
}
