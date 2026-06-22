import { Component, inject, OnInit, signal, ChangeDetectionStrategy } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { FormsModule } from '@angular/forms';
import type { AddOnPack, AutoTunePreview } from '../../models/api.types';
import { AddOnPacksApiService } from './addon-packs.service';

@Component({
  selector: 'app-addon-pack-detail',
  standalone: true,
  imports: [RouterLink, FormsModule],
  template: `
    <div class="space-y-6">
      <a [routerLink]="['/addon-packs']" class="text-xs text-gray-500 hover:text-gray-300">← Packs</a>
      @if (pack(); as p) {
        <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-6">
          <h1 class="text-lg font-semibold mb-4">{{ p.name }}</h1>

          <label class="block text-xs text-gray-400 mb-1">Name</label>
          <input [(ngModel)]="editName" class="w-full mb-3 rounded border border-gray-700 bg-gray-800 px-3 py-1.5 text-xs text-gray-200" />

          <label class="block text-xs text-gray-400 mb-1">Description</label>
          <input [(ngModel)]="editDesc" class="w-full mb-4 rounded border border-gray-700 bg-gray-800 px-3 py-1.5 text-xs text-gray-200" />

          <label class="flex items-center gap-2 mb-4 text-xs text-gray-400">
            <input type="checkbox" [(ngModel)]="regimeEnabled" class="rounded" />
            Regime Detection Enabled
          </label>

          <button (click)="save()" [disabled]="saving()" class="rounded-md bg-emerald-600 px-4 py-1.5 text-xs font-medium text-white hover:bg-emerald-500 disabled:opacity-50">
            {{ saving() ? 'Saving...' : 'Save Pack' }}
          </button>
          @if (savedOk()) { <span class="ml-2 text-xs text-emerald-400">Saved</span> }
        </div>

        <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-6">
          <h2 class="text-sm font-medium mb-4">Add-on Values (editable per-strategy)</h2>
          <p class="text-xs text-gray-500 mb-4">These values are used as defaults. Enable Auto mode on a strategy to auto-tune them per symbol/timeframe at entry.</p>
          <div class="grid grid-cols-2 gap-4 text-xs">
            <!-- Trailing -->
            <div class="rounded border border-gray-800 bg-gray-800/30 p-3">
              <div class="flex items-center justify-between mb-2">
                <span class="font-medium text-gray-300">Trailing</span>
              </div>
              <label class="text-gray-500">ATR Multiple</label>
              <input type="number" step="0.1" [(ngModel)]="trailingAtr" class="w-full rounded border border-gray-700 bg-gray-800 px-2 py-1 text-gray-200" />
            </div>
            <!-- Breakeven -->
            <div class="rounded border border-gray-800 bg-gray-800/30 p-3">
              <div class="flex items-center justify-between mb-2">
                <span class="font-medium text-gray-300">Breakeven</span>
                <input type="checkbox" [(ngModel)]="beEnabled" class="rounded" />
              </div>
              <label class="text-gray-500">Trigger R</label>
              <input type="number" step="0.1" [(ngModel)]="beTriggerR" class="w-full rounded border border-gray-700 bg-gray-800 px-2 py-1 text-gray-200" [disabled]="!beEnabled" />
              <label class="text-gray-500 mt-1">Offset Pips</label>
              <input type="number" step="0.1" [(ngModel)]="beOffset" class="w-full rounded border border-gray-700 bg-gray-800 px-2 py-1 text-gray-200" [disabled]="!beEnabled" />
            </div>
          </div>
          <button (click)="save()" [disabled]="saving()" class="mt-4 rounded-md bg-emerald-600 px-4 py-1.5 text-xs font-medium text-white hover:bg-emerald-500 disabled:opacity-50">
            {{ saving() ? 'Saving...' : 'Save Pack' }}
          </button>
        </div>
      } @else {
        <div class="py-12 text-center text-sm text-gray-500">Loading...</div>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AddOnPackDetailComponent implements OnInit {
  private route = inject(ActivatedRoute);
  private api = inject(AddOnPacksApiService);
  pack = signal<AddOnPack | null>(null);
  editName = '';
  editDesc = '';
  regimeEnabled = true;
  trailingAtr = 2.5;
  beEnabled = false;
  beTriggerR = 1.0;
  beOffset = 1.0;
  saving = signal(false);
  savedOk = signal(false);

  async ngOnInit(): Promise<void> {
    const id = this.route.snapshot.paramMap.get('id');
    if (!id) return;
    try {
      const p = await this.api.getById(id);
      this.pack.set(p);
      this.editName = p.name;
      this.editDesc = p.description || '';
      this.regimeEnabled = p.regimeDetectionEnabled;
      this.trailingAtr = p.addOns?.trailing?.atrMultiple ?? 2.5;
      this.beEnabled = p.addOns?.breakeven?.enabled ?? false;
      this.beTriggerR = p.addOns?.breakeven?.triggerRMultiple ?? 1.0;
      this.beOffset = p.addOns?.breakeven?.offsetPips ?? 1.0;
    } catch { /* */ }
  }

  async save(): Promise<void> {
    const p = this.pack();
    if (!p) return;
    this.saving.set(true);
    try {
      await this.api.save(p.id, {
        ...p,
        name: this.editName,
        description: this.editDesc || null,
        regimeDetectionEnabled: this.regimeEnabled,
        addOns: {
          ...p.addOns,
          trailing: { ...p.addOns?.trailing, atrMultiple: this.trailingAtr },
          breakeven: { ...p.addOns?.breakeven, enabled: this.beEnabled, triggerRMultiple: this.beTriggerR, offsetPips: this.beOffset },
        },
      });
      this.savedOk.set(true);
    } finally { this.saving.set(false); }
  }
}
