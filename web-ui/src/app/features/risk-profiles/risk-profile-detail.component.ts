import { Component, inject, OnInit, signal, ChangeDetectionStrategy } from '@angular/core';
import { ActivatedRoute, RouterLink, Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import type { RiskProfile, RiskProfileEdit } from '../../models/api.types';
import { RiskProfilesApiService } from './risk-profiles.service';
import { DetailFormBase } from '../../shared/detail-form-base';

@Component({
  selector: 'app-risk-profile-detail',
  standalone: true,
  imports: [RouterLink, FormsModule],
  template: `
    <div class="space-y-6">
      <div class="flex items-center justify-between">
        <h1 class="text-xl font-semibold">{{ data()?.displayName || data()?.id || 'Risk Profile' }}</h1>
        <div class="flex gap-2">
          <button
            (click)="duplicate()"
            [disabled]="saving()"
            class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800"
          >
            Duplicate
          </button>
          <a
            routerLink="/risk/profiles"
            class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800"
            >All Profiles</a
          >
        </div>
      </div>
      @if (data(); as p) {
        <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4 space-y-4">
          <div>
            <label class="block text-xs font-medium text-gray-400 mb-1">Display Name</label
            ><input
              [(ngModel)]="edit.displayName"
              class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none"
            />
          </div>

          <div class="grid grid-cols-2 gap-3">
            <div>
              <label class="block text-xs text-gray-500 mb-1">Risk Per Trade (%)</label
              ><input
                type="number"
                [(ngModel)]="edit.riskPerTradePercent"
                step="0.001"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              />
            </div>
            <div>
              <label class="block text-xs text-gray-500 mb-1">Max Daily DD (%)</label
              ><input
                type="number"
                [(ngModel)]="edit.maxDailyDrawdownPercent"
                step="0.01"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              />
            </div>
            <div>
              <label class="block text-xs text-gray-500 mb-1">Max Total DD (%)</label
              ><input
                type="number"
                [(ngModel)]="edit.maxTotalDrawdownPercent"
                step="0.01"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              />
            </div>
            <div>
              <label class="block text-xs text-gray-500 mb-1">Max SL (pips)</label
              ><input
                type="number"
                [(ngModel)]="edit.maxSlPips"
                step="1"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              />
            </div>
            <div>
              <label class="block text-xs text-gray-500 mb-1">Max Exposure (%)</label
              ><input
                type="number"
                [(ngModel)]="edit.maxExposurePercent"
                step="0.01"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              />
            </div>
            <div>
              <label class="block text-xs text-gray-500 mb-1">Max Exposure/Currency (%)</label
              ><input
                type="number"
                [(ngModel)]="edit.maxExposurePerCurrencyPercent"
                step="0.01"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              />
            </div>
            <div>
              <label class="block text-xs text-gray-500 mb-1">DD Scale Threshold</label
              ><input
                type="number"
                [(ngModel)]="edit.drawdownScaleThreshold"
                step="0.1"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              />
            </div>
            <div>
              <label class="block text-xs text-gray-500 mb-1">DD Scale Floor</label
              ><input
                type="number"
                [(ngModel)]="edit.drawdownScaleFloor"
                step="0.1"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              />
            </div>
            <div>
              <label class="block text-xs text-gray-500 mb-1">Max Positions</label
              ><input
                type="number"
                [(ngModel)]="edit.maxConcurrentPositions"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              />
            </div>
            <div>
              <label class="block text-xs text-gray-500 mb-1">Prop Firm Rule Set</label
              ><input
                [(ngModel)]="edit.propFirmRuleSetId"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              />
            </div>
          </div>

          <div class="grid grid-cols-2 gap-3">
            <label class="flex items-center gap-1.5 cursor-pointer"
              ><input
                type="checkbox"
                [checked]="edit.allowHedging"
                (change)="edit.allowHedging = !edit.allowHedging"
                class="h-4 w-4 rounded border-gray-600 bg-gray-800 text-emerald-500 focus:ring-emerald-500"
              /><span class="text-xs text-gray-300">Allow Hedging</span></label
            >
          </div>

          <div>
            <label class="block text-xs text-gray-500 mb-1">Lot Sizing Method</label
            ><select
              [(ngModel)]="edit.lotSizingMethod"
              class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
            >
              <option value="PercentRisk">Percent Risk</option>
              <option value="FixedLots">Fixed Lots</option>
              <option value="FixedDollarRisk">Fixed Dollar Risk</option>
              <option value="KellyFraction">Kelly Fraction</option>
              <option value="AntiMartingale">Anti-Martingale</option>
            </select>
          </div>

          @if (edit.lotSizingMethod === 'FixedLots') {
            <div>
              <label class="block text-xs text-gray-500 mb-1">Fixed Lots</label
              ><input
                type="number"
                [(ngModel)]="edit.fixedLots"
                step="0.01"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              />
            </div>
          }
          @if (edit.lotSizingMethod === 'FixedDollarRisk') {
            <div>
              <label class="block text-xs text-gray-500 mb-1">Fixed Dollar Risk</label
              ><input
                type="number"
                [(ngModel)]="edit.fixedDollarRisk"
                step="1"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              />
            </div>
          }
          @if (edit.lotSizingMethod === 'KellyFraction') {
            <div>
              <label class="block text-xs text-gray-500 mb-1">Kelly Fraction</label
              ><input
                type="number"
                [(ngModel)]="edit.kellyFraction"
                step="0.05"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              />
            </div>
          }
          @if (edit.lotSizingMethod === 'AntiMartingale') {
            <div class="grid grid-cols-2 gap-3">
              <div>
                <label class="block text-xs text-gray-500 mb-1">Multiplier</label
                ><input
                  type="number"
                  [(ngModel)]="edit.antiMartingaleMultiplier"
                  step="0.1"
                  class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
                />
              </div>
              <div>
                <label class="block text-xs text-gray-500 mb-1">Max Steps</label
                ><input
                  type="number"
                  [(ngModel)]="edit.antiMartingaleMaxSteps"
                  class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
                />
              </div>
            </div>
          }

          @if (errors().length > 0) {
            <div class="rounded-md border border-red-800 bg-red-900/20 p-3 text-xs text-red-400">
              <ul class="list-disc pl-4">
                @for (e of errors(); track e) {
                  <li>{{ e }}</li>
                }
              </ul>
            </div>
          }
          @if (savedOk()) {
            <div class="rounded-md bg-emerald-900/20 p-3 text-xs text-emerald-400">Saved</div>
          }
          <div class="flex gap-2">
            <button
              (click)="save()"
              [disabled]="saving()"
              class="rounded-md bg-emerald-600 px-4 py-2 text-xs font-medium text-white hover:bg-emerald-500 disabled:opacity-50"
            >
              {{ saving() ? 'Saving...' : 'Save' }}
            </button>
            <button
              (click)="del()"
              [disabled]="saving()"
              class="rounded-md border border-red-800 px-4 py-2 text-xs text-red-400 hover:bg-red-900/20 disabled:opacity-50"
            >
              Delete
            </button>
          </div>
        </div>
      } @else {
        <div class="py-12 text-center text-sm text-gray-500">Profile not found.</div>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RiskProfileDetailComponent extends DetailFormBase implements OnInit {
  private route = inject(ActivatedRoute);
  private api = inject(RiskProfilesApiService);
  private router = inject(Router);
  data = signal<RiskProfile | null>(null);
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  edit: Record<string, any> = {};
  errors = signal<string[]>([]);

  // F7 — validate before save; block the PUT and surface field errors when invalid.
  private validate(): string[] {
    const e: string[] = [];
    const frac = (v: any) => Number(v) > 0 && Number(v) <= 1;
    if (!this.edit.displayName?.trim()) e.push('Display name is required.');
    if (!frac(this.edit.riskPerTradePercent)) e.push('Risk per trade must be a fraction in (0, 1] (e.g. 0.01 = 1%).');
    if (!frac(this.edit.maxDailyDrawdownPercent)) e.push('Max daily DD must be a fraction in (0, 1].');
    if (!frac(this.edit.maxTotalDrawdownPercent)) e.push('Max total DD must be a fraction in (0, 1].');
    if (Number(this.edit.maxTotalDrawdownPercent) < Number(this.edit.maxDailyDrawdownPercent))
      e.push('Max total DD must be >= max daily DD.');
    if (!(Number(this.edit.maxConcurrentPositions) >= 1)) e.push('Max positions must be at least 1.');
    return e;
  }

  async ngOnInit(): Promise<void> {
    const id = this.route.snapshot.paramMap.get('id');
    if (!id) return;
      const p = await this.api.getById(id);
    this.data.set(p);
    this.edit = { ...p, lotSizingMethod: p.lotSizingMethod || 'PercentRisk' };
  }

  async save(): Promise<void> {
    const errs = this.validate();
    this.errors.set(errs);
    if (errs.length > 0) return;
    this.saving.set(true);
    this.savedOk.set(false);
      await this.api.save(this.edit.id, this.edit);
    this.savedOk.set(true);
    this.saving.set(false);
  }

  async duplicate(): Promise<void> {
    const d = this.data();
    if (!d) return;
    const res = await this.api.duplicate(d.id);
    this.router.navigate(['/risk-profiles', res.id]);
  }

  async del(): Promise<void> {
    const d = this.data();
    if (!d || !confirm('Delete this risk profile?')) return;
    await this.api.delete(d.id);
    this.router.navigate(['/risk-profiles']);
  }
}
