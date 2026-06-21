import { Component, inject, OnInit, signal } from '@angular/core';
import { RouterLink, Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import type { PropFirmRule } from '../../models/api.types';
import { PropFirmRulesApiService } from './prop-firm-rules.service';

@Component({
  selector: 'app-prop-firm-rule-list',
  standalone: true,
  imports: [RouterLink, FormsModule],
  template: `
    <div class="space-y-6">
      <div class="flex items-center justify-between">
        <h1 class="text-xl font-semibold">Prop Firm Rules</h1>
        <button
          (click)="openCreate()"
          class="rounded-md bg-emerald-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-emerald-500"
        >
          New Rule Set
        </button>
      </div>

      @if (showCreate()) {
        <div class="fixed inset-0 z-50 flex items-center justify-center bg-black/60" (click)="cancelCreate()">
          <div class="w-96 rounded-lg border border-gray-700 bg-gray-900 p-6 shadow-xl" (click)="$event.stopPropagation()">
            <h2 class="mb-4 text-sm font-medium text-gray-200">New Rule Set</h2>
            <label class="mb-1 block text-xs text-gray-400">ID</label>
            <input [(ngModel)]="newId" class="mb-3 w-full rounded border border-gray-700 bg-gray-800 px-3 py-1.5 text-xs text-gray-200" />
            <label class="mb-1 block text-xs text-gray-400">Display Name</label>
            <input [(ngModel)]="newName" class="mb-4 w-full rounded border border-gray-700 bg-gray-800 px-3 py-1.5 text-xs text-gray-200" />
            <div class="flex justify-end gap-2">
              <button (click)="cancelCreate()" class="rounded border border-gray-700 px-3 py-1 text-xs text-gray-400 hover:bg-gray-800">Cancel</button>
              <button (click)="submitCreate()" class="rounded bg-emerald-600 px-3 py-1 text-xs text-white hover:bg-emerald-500">Create</button>
            </div>
          </div>
        </div>
      }
      <div class="grid gap-4 md:grid-cols-2">
        @for (r of rules(); track r.id) {
          <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4 transition hover:border-gray-600">
            <div class="flex items-center justify-between mb-2">
              <a
                [routerLink]="['/prop-firm-rules', r.id]"
                class="text-sm font-medium text-gray-100 hover:text-emerald-400"
                >{{ r.displayName }}</a
              >
              <button
                (click)="duplicate(r)"
                class="rounded border border-gray-700 px-2 py-0.5 text-xs text-gray-400 hover:bg-gray-800"
              >
                Copy
              </button>
            </div>
            <p class="font-mono text-xs text-gray-500 mb-2">{{ r.id }}</p>
            <div class="grid grid-cols-3 gap-2 text-xs text-gray-400">
              <div>
                <span class="text-gray-500">Daily Loss</span><br />{{ (r.maxDailyLossPercent * 100).toFixed(1) }}%
              </div>
              <div>
                <span class="text-gray-500">Total Loss</span><br />{{ (r.maxTotalLossPercent * 100).toFixed(1) }}%
              </div>
              <div>
                <span class="text-gray-500">Profit Target</span><br />{{ (r.profitTargetPercent * 100).toFixed(1) }}%
              </div>
              <div><span class="text-gray-500">Min Days</span><br />{{ r.minTradingDays }}</div>
              <div><span class="text-gray-500">DD Type</span><br />{{ r.drawdownType }}</div>
              <div><span class="text-gray-500">Force Close</span><br />{{ r.forceCloseOnBreach ? 'Yes' : 'No' }}</div>
            </div>
          </div>
        }
      </div>
    </div>
  `,
})
export class PropFirmRuleListComponent implements OnInit {
  private api = inject(PropFirmRulesApiService);
  private router = inject(Router);
  rules = signal<PropFirmRule[]>([]);
  // iter-38 S9 W-D2: replace prompt() with a real dialog (native <dialog>-backdrop pattern).
  showCreate = signal(false);
  newId = '';
  newName = '';

  async ngOnInit(): Promise<void> {
    const res = await this.api.getAll();
    this.rules.set(res);
  }

  async duplicate(r: any): Promise<void> {
    const res = await this.api.duplicate(r.id);
    this.router.navigate(['/prop-firm-rules', res.id]);
  }

  async create(ruleId: string, displayName: string): Promise<void> {
    await this.api.create({
        id: ruleId,
        displayName,
        drawdownType: 'Fixed',
        maxDailyLossPercent: 0.05,
        maxTotalLossPercent: 0.1,
        profitTargetPercent: 0.1,
        minTradingDays: 4,
        equityDefinition: 'BalancePlusFloatingMinusFeesAndSwaps',
        dailyResetTimeUtc: '22:00:00',
        dailyResetTimezone: 'Europe/Prague',
        allowTradesDuringNews: false,
        newsImpactFilter: 'High',
        newsWindowMinutesBefore: 30,
        newsWindowMinutesAfter: 15,
        allowWeekendHolding: false,
        weekendCloseUtc: '21:00:00',
        weekendNoOpenUtc: '20:00:00',
        protectionResetPolicy: 'NextTradingDay',
        forceCloseOnBreach: false,
    });
    this.router.navigate(['/prop-firm-rules', ruleId]);
  }

  openCreate(): void {
    this.newId = '';
    this.newName = '';
    this.showCreate.set(true);
  }
  cancelCreate(): void {
    this.showCreate.set(false);
  }
  async submitCreate(): Promise<void> {
    const id = this.newId.trim();
    const name = this.newName.trim() || id;
    if (!id) return;
    this.showCreate.set(false);
    await this.create(id, name);
  }
}
