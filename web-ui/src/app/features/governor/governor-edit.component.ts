import { Component, inject, OnInit, signal, ChangeDetectionStrategy } from '@angular/core';
import { FormsModule } from '@angular/forms';
import type { GovernorOptions, GovernorOptionsEdit } from '../../models/api.types';
import { GovernorApiService } from './governor.service';

@Component({
  selector: 'app-governor-edit',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="space-y-6">
      <h1 class="text-xl font-semibold">Governor Options</h1>
      @if (data()) {
        <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4 space-y-4">
          <label class="flex items-center gap-1.5 cursor-pointer"
            ><input
              type="checkbox"
              [checked]="edit.enabled"
              (change)="edit.enabled = !edit.enabled"
              class="h-4 w-4 rounded border-gray-600 bg-gray-800 text-emerald-500 focus:ring-emerald-500"
            /><span class="text-xs text-gray-300">Enabled</span></label
          >

          <div class="grid grid-cols-2 gap-3">
            <div>
              <label class="block text-xs text-gray-500 mb-1">Loss Band Fractions (JSON array)</label
              ><input
                [(ngModel)]="edit.lossBandFractions"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 font-mono focus:border-emerald-500 focus:outline-none"
              />
            </div>
            <div>
              <label class="block text-xs text-gray-500 mb-1">Loss Band Multipliers (JSON array)</label
              ><input
                [(ngModel)]="edit.lossBandMultipliers"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 font-mono focus:border-emerald-500 focus:outline-none"
              />
            </div>
            <div>
              <label class="block text-xs text-gray-500 mb-1">Streak Reduce At</label
              ><input
                type="number"
                [(ngModel)]="edit.streakReduceAt"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              />
            </div>
            <div>
              <label class="block text-xs text-gray-500 mb-1">Streak Multiplier</label
              ><input
                type="number"
                [(ngModel)]="edit.streakMultiplier"
                step="0.1"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              />
            </div>
            <div>
              <label class="block text-xs text-gray-500 mb-1">Streak Pause At</label
              ><input
                type="number"
                [(ngModel)]="edit.streakPauseAt"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              />
            </div>
            <div>
              <label class="block text-xs text-gray-500 mb-1">Cooling Off Bars</label
              ><input
                type="number"
                [(ngModel)]="edit.coolingOffBars"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              />
            </div>
            <div>
              <label class="block text-xs text-gray-500 mb-1">Profit Lock Fraction</label
              ><input
                type="number"
                [(ngModel)]="edit.profitLockFraction"
                step="0.05"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              />
            </div>
          </div>

          <label class="flex items-center gap-1.5 cursor-pointer"
            ><input
              type="checkbox"
              [checked]="edit.profitLockEnabled"
              (change)="edit.profitLockEnabled = !edit.profitLockEnabled"
              class="h-4 w-4 rounded border-gray-600 bg-gray-800 text-emerald-500 focus:ring-emerald-500"
            /><span class="text-xs text-gray-300">Profit Lock Enabled</span></label
          >

          @if (savedOk()) {
            <div class="rounded-md bg-emerald-900/20 p-3 text-xs text-emerald-400">Saved</div>
          }
          <button
            (click)="save()"
            [disabled]="saving()"
            class="rounded-md bg-emerald-600 px-4 py-2 text-xs font-medium text-white hover:bg-emerald-500 disabled:opacity-50"
          >
            {{ saving() ? 'Saving...' : 'Save' }}
          </button>
        </div>
      } @else {
        <div class="py-12 text-center text-sm text-gray-500">Loading...</div>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class GovernorEditComponent implements OnInit {
  private api = inject(GovernorApiService);
  data = signal<GovernorOptions | null>(null);
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  edit: Record<string, any> = {};
  saving = signal(false);
  savedOk = signal(false);

  async ngOnInit(): Promise<void> {
    const g = await this.api.get();
    this.data.set(g);
    this.edit = {
      ...g,
      lossBandFractions: JSON.stringify(g.lossBandFractions || [0.4, 0.6]),
      lossBandMultipliers: JSON.stringify(g.lossBandMultipliers || [0.5, 0.0]),
    };
  }

  async save(): Promise<void> {
    this.saving.set(true);
    this.savedOk.set(false);
    const body: any = { ...this.edit };
    try {
      body.lossBandFractions = JSON.parse(this.edit.lossBandFractions);
    } catch {}
    try {
      body.lossBandMultipliers = JSON.parse(this.edit.lossBandMultipliers);
    } catch {}
    await this.api.save(body);
    this.savedOk.set(true);
    this.saving.set(false);
  }
}
