import { Component, inject, OnInit, signal, ChangeDetectionStrategy } from '@angular/core';
import { ActivatedRoute, RouterLink, Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import type { PropFirmRule, PropFirmRuleEdit } from '../../models/api.types';
import { PropFirmRulesApiService } from './prop-firm-rules.service';
import { DetailFormBase } from '../../shared/detail-form-base';

@Component({
  selector: 'app-prop-firm-rule-detail',
  standalone: true,
  imports: [RouterLink, FormsModule],
  template: `
    <div class="space-y-6">
      <div class="flex items-center justify-between">
        <h1 class="text-xl font-semibold">{{ data()?.displayName || data()?.id || 'Rule Set' }}</h1>
        <div class="flex gap-2">
          <button
            (click)="duplicate()"
            [disabled]="saving()"
            class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800"
          >
            Duplicate
          </button>
          <a
            routerLink="/prop-firm-rules"
            class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800"
            >All Rules</a
          >
        </div>
      </div>
      @if (data(); as r) {
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
              <label class="block text-xs text-gray-500 mb-1">Drawdown Type</label
              ><select
                [(ngModel)]="edit.drawdownType"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              >
                <option value="Fixed">Fixed</option>
                <option value="Trailing">Trailing</option>
              </select>
            </div>
            <div>
              <label class="block text-xs text-gray-500 mb-1">Daily DD Base</label
              ><select
                [(ngModel)]="edit.dailyDdBase"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              >
                <option value="InitialBalance">Initial Balance</option>
                <option value="DailyStart">Daily Start</option>
              </select>
            </div>
            <div>
              <label class="block text-xs text-gray-500 mb-1">Max Daily Loss (%)</label
              ><input
                type="number"
                [(ngModel)]="edit.maxDailyLossPercent"
                step="0.01"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              />
            </div>
            <div>
              <label class="block text-xs text-gray-500 mb-1">Max Total Loss (%)</label
              ><input
                type="number"
                [(ngModel)]="edit.maxTotalLossPercent"
                step="0.01"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              />
            </div>
            <div>
              <label class="block text-xs text-gray-500 mb-1">Max Weekly Loss (%)</label
              ><input
                type="number"
                [(ngModel)]="edit.maxWeeklyLossPercent"
                step="0.01"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              />
            </div>
            <div>
              <label class="block text-xs text-gray-500 mb-1">Max Monthly Loss (%)</label
              ><input
                type="number"
                [(ngModel)]="edit.maxMonthlyLossPercent"
                step="0.01"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              />
            </div>
            <div>
              <label class="block text-xs text-gray-500 mb-1">Profit Target (%)</label
              ><input
                type="number"
                [(ngModel)]="edit.profitTargetPercent"
                step="0.01"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              />
            </div>
            <div>
              <label class="block text-xs text-gray-500 mb-1">Min Trading Days</label
              ><input
                type="number"
                [(ngModel)]="edit.minTradingDays"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              />
            </div>
            <div>
              <label class="block text-xs text-gray-500 mb-1">Daily Reset Time (UTC)</label
              ><input
                [(ngModel)]="edit.dailyResetTimeUtc"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              />
            </div>
            <div>
              <label class="block text-xs text-gray-500 mb-1">Daily Reset TZ</label
              ><input
                [(ngModel)]="edit.dailyResetTimezone"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              />
            </div>
            <div>
              <label class="block text-xs text-gray-500 mb-1">News Impact Filter</label
              ><select
                [(ngModel)]="edit.newsImpactFilter"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              >
                <option value="High">High</option>
                <option value="Medium">Medium</option>
                <option value="All">All</option>
              </select>
            </div>
            <div>
              <label class="block text-xs text-gray-500 mb-1">News Window Before (min)</label
              ><input
                type="number"
                [(ngModel)]="edit.newsWindowMinutesBefore"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              />
            </div>
            <div>
              <label class="block text-xs text-gray-500 mb-1">News Window After (min)</label
              ><input
                type="number"
                [(ngModel)]="edit.newsWindowMinutesAfter"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              />
            </div>
            <div>
              <label class="block text-xs text-gray-500 mb-1">Weekend Close (UTC)</label
              ><input
                [(ngModel)]="edit.weekendCloseUtc"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              />
            </div>
            <div>
              <label class="block text-xs text-gray-500 mb-1">Weekend No-Open (UTC)</label
              ><input
                [(ngModel)]="edit.weekendNoOpenUtc"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              />
            </div>
            <div>
              <label class="block text-xs text-gray-500 mb-1">Protection Reset Policy</label
              ><input
                [(ngModel)]="edit.protectionResetPolicy"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
              />
            </div>
          </div>

          <div class="flex flex-wrap gap-3">
            <label class="flex items-center gap-1.5 cursor-pointer"
              ><input
                type="checkbox"
                [checked]="edit.allowTradesDuringNews"
                (change)="edit.allowTradesDuringNews = !edit.allowTradesDuringNews"
                class="h-4 w-4 rounded border-gray-600 bg-gray-800 text-emerald-500 focus:ring-emerald-500"
              /><span class="text-xs text-gray-300">Allow Trades During News</span></label
            >
            <label class="flex items-center gap-1.5 cursor-pointer"
              ><input
                type="checkbox"
                [checked]="edit.allowWeekendHolding"
                (change)="edit.allowWeekendHolding = !edit.allowWeekendHolding"
                class="h-4 w-4 rounded border-gray-600 bg-gray-800 text-emerald-500 focus:ring-emerald-500"
              /><span class="text-xs text-gray-300">Allow Weekend Holding</span></label
            >
            <label class="flex items-center gap-1.5 cursor-pointer"
              ><input
                type="checkbox"
                [checked]="edit.forceCloseOnBreach"
                (change)="edit.forceCloseOnBreach = !edit.forceCloseOnBreach"
                class="h-4 w-4 rounded border-gray-600 bg-gray-800 text-emerald-500 focus:ring-emerald-500"
              /><span class="text-xs text-gray-300">Force Close On Breach</span></label
            >
            <label class="flex items-center gap-1.5 cursor-pointer"
              ><input
                type="checkbox"
                [checked]="edit.requireProfitTarget"
                (change)="edit.requireProfitTarget = !edit.requireProfitTarget"
                class="h-4 w-4 rounded border-gray-600 bg-gray-800 text-emerald-500 focus:ring-emerald-500"
              /><span class="text-xs text-gray-300">Require Profit Target</span></label
            >
          </div>

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
        <div class="py-12 text-center text-sm text-gray-500">Rule set not found.</div>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PropFirmRuleDetailComponent extends DetailFormBase implements OnInit {
  private route = inject(ActivatedRoute);
  private api = inject(PropFirmRulesApiService);
  private router = inject(Router);
  data = signal<PropFirmRule | null>(null);
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  edit: Record<string, any> = {};

  async ngOnInit(): Promise<void> {
    const id = this.route.snapshot.paramMap.get('id');
    if (!id) return;
      const r = await this.api.getById(id);
    this.data.set(r);
    this.edit = { ...r };
  }

  async save(): Promise<void> {
    this.saving.set(true);
    this.savedOk.set(false);
      await this.api.save(this.edit.id, this.edit);
    this.savedOk.set(true);
    this.saving.set(false);
  }

  async duplicate(): Promise<void> {
      const res = await this.api.duplicate(this.data()!.id);
    this.router.navigate(['/prop-firm-rules', res.id]);
  }

  async del(): Promise<void> {
    if (!confirm('Delete this rule set?')) return;
      await this.api.delete(this.data()!.id);
    this.router.navigate(['/prop-firm-rules']);
  }
}
