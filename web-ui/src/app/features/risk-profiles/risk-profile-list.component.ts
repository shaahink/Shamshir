import { Component, inject, OnInit, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { RouterLink, Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';

@Component({
  selector: 'app-risk-profile-list',
  standalone: true,
  imports: [RouterLink],
  template: `
    <div class="space-y-6">
      <div class="flex items-center justify-between">
        <h1 class="text-xl font-semibold">Risk Profiles</h1>
        <button (click)="create()" class="rounded-md bg-emerald-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-emerald-500">New Profile</button>
      </div>
      <div class="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
        @for (p of profiles(); track p.id) {
          <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4 transition hover:border-gray-600">
            <div class="flex items-center justify-between mb-2">
              <a [routerLink]="['/risk-profiles', p.id]" class="text-sm font-medium text-gray-100 hover:text-emerald-400">{{ p.displayName }}</a>
              <button (click)="duplicate(p)" class="rounded border border-gray-700 px-2 py-0.5 text-xs text-gray-400 hover:bg-gray-800">Copy</button>
            </div>
            <p class="font-mono text-xs text-gray-500 mb-2">{{ p.id }}</p>
            <div class="grid grid-cols-3 gap-2 text-xs text-gray-400">
              <div><span class="text-gray-500">Risk/Trade</span><br>{{ (p.riskPerTradePercent * 100).toFixed(2) }}%</div>
              <div><span class="text-gray-500">Daily DD</span><br>{{ (p.maxDailyDrawdownPercent * 100).toFixed(1) }}%</div>
              <div><span class="text-gray-500">Max DD</span><br>{{ (p.maxTotalDrawdownPercent * 100).toFixed(1) }}%</div>
              <div><span class="text-gray-500">Positions</span><br>{{ p.maxConcurrentPositions }}</div>
              <div><span class="text-gray-500">Hedging</span><br>{{ p.allowHedging ? 'Yes' : 'No' }}</div>
              <div><span class="text-gray-500">Sizing</span><br>{{ p.lotSizingMethod }}</div>
            </div>
          </div>
        }
      </div>
      @if (profiles().length === 0) { <div class="py-12 text-center text-sm text-gray-500">No risk profiles loaded.</div> }
    </div>
  `,
})
export class RiskProfileListComponent implements OnInit {
  private http = inject(HttpClient);
  private router = inject(Router);
  profiles = signal<any[]>([]);

  async ngOnInit(): Promise<void> {
    const rp: any = await firstValueFrom(this.http.get('/api/risk-profiles'));
    this.profiles.set(Array.isArray(rp) ? rp : (rp.profiles || []));
  }

  async duplicate(p: any): Promise<void> {
    const res: any = await firstValueFrom(this.http.post(`/api/risk-profiles/${p.id}/duplicate`, {}));
    this.router.navigate(['/risk-profiles', res.id]);
  }

  async create(): Promise<void> {
    const id = prompt('Enter new profile ID (e.g. my-profile):');
    if (!id) return;
    const displayName = prompt('Display name:', id);
    if (!displayName) return;
    await firstValueFrom(this.http.post('/api/risk-profiles', {
      id, displayName, riskPerTradePercent: 0.005, maxDailyDrawdownPercent: 0.04, maxTotalDrawdownPercent: 0.08,
      maxSlPips: 100, maxExposurePercent: 0.05, drawdownScaleThreshold: 0.5, drawdownScaleFloor: 0.5,
      maxConcurrentPositions: 3, allowHedging: false, propFirmRuleSetId: 'ftmo-standard',
      lotSizingMethod: 'PercentRisk', fixedLots: 0.1, fixedDollarRisk: 0, kellyFraction: 0.25,
      antiMartingaleMultiplier: 1.5, antiMartingaleMaxSteps: 3,
    }));
    this.router.navigate(['/risk-profiles', id]);
  }
}
