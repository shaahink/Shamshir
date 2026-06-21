import { Component, inject, OnInit, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { RouterLink, Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';

@Component({
  selector: 'app-prop-firm-rule-list',
  standalone: true,
  imports: [RouterLink],
  template: `
    <div class="space-y-6">
      <div class="flex items-center justify-between">
        <h1 class="text-xl font-semibold">Prop Firm Rules</h1>
        <button
          (click)="create()"
          class="rounded-md bg-emerald-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-emerald-500"
        >
          New Rule Set
        </button>
      </div>
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
  private http = inject(HttpClient);
  private router = inject(Router);
  rules = signal<any[]>([]);

  async ngOnInit(): Promise<void> {
    const res: any = await firstValueFrom(this.http.get('/api/prop-firm-rules'));
    this.rules.set(Array.isArray(res) ? res : res.rules || []);
  }

  async duplicate(r: any): Promise<void> {
    const res: any = await firstValueFrom(this.http.post(`/api/prop-firm-rules/${r.id}/duplicate`, {}));
    this.router.navigate(['/prop-firm-rules', res.id]);
  }

  async create(): Promise<void> {
    const id = prompt('Enter new rule set ID:');
    if (!id) return;
    const displayName = prompt('Display name:', id);
    if (!displayName) return;
    await firstValueFrom(
      this.http.post('/api/prop-firm-rules', {
        id,
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
      }),
    );
    this.router.navigate(['/prop-firm-rules', id]);
  }
}
