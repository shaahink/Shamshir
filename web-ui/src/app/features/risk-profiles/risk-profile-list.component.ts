import { Component, inject, OnInit, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { RouterLink, Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { FormsModule } from '@angular/forms';

@Component({
  selector: 'app-risk-profile-list',
  standalone: true,
  imports: [RouterLink, FormsModule],
  template: `
    <div class="space-y-6">
      <div class="flex items-center justify-between">
        <h1 class="text-xl font-semibold">Risk Profiles</h1>
        <button
          (click)="openCreate()"
          class="rounded-md bg-emerald-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-emerald-500"
        >
          New Profile
        </button>
      </div>

      @if (showCreate()) {
        <div class="fixed inset-0 z-50 flex items-center justify-center bg-black/60" (click)="cancelCreate()">
          <div class="w-96 rounded-lg border border-gray-700 bg-gray-900 p-6 shadow-xl" (click)="$event.stopPropagation()">
            <h2 class="mb-4 text-sm font-medium text-gray-200">New Risk Profile</h2>
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
      <div class="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
        @for (p of profiles(); track p.id) {
          <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4 transition hover:border-gray-600">
            <div class="flex items-center justify-between mb-2">
              <a
                [routerLink]="['/risk-profiles', p.id]"
                class="text-sm font-medium text-gray-100 hover:text-emerald-400"
                >{{ p.displayName }}</a
              >
              <button
                (click)="duplicate(p)"
                class="rounded border border-gray-700 px-2 py-0.5 text-xs text-gray-400 hover:bg-gray-800"
              >
                Copy
              </button>
            </div>
            <p class="font-mono text-xs text-gray-500 mb-2">{{ p.id }}</p>
            <div class="grid grid-cols-3 gap-2 text-xs text-gray-400">
              <div>
                <span class="text-gray-500">Risk/Trade</span><br />{{ (p.riskPerTradePercent * 100).toFixed(2) }}%
              </div>
              <div>
                <span class="text-gray-500">Daily DD</span><br />{{ (p.maxDailyDrawdownPercent * 100).toFixed(1) }}%
              </div>
              <div>
                <span class="text-gray-500">Max DD</span><br />{{ (p.maxTotalDrawdownPercent * 100).toFixed(1) }}%
              </div>
              <div><span class="text-gray-500">Positions</span><br />{{ p.maxConcurrentPositions }}</div>
              <div><span class="text-gray-500">Hedging</span><br />{{ p.allowHedging ? 'Yes' : 'No' }}</div>
              <div><span class="text-gray-500">Sizing</span><br />{{ p.lotSizingMethod }}</div>
            </div>
          </div>
        }
      </div>
      @if (profiles().length === 0) {
        <div class="py-12 text-center text-sm text-gray-500">No risk profiles loaded.</div>
      }
    </div>
  `,
})
export class RiskProfileListComponent implements OnInit {
  private http = inject(HttpClient);
  private router = inject(Router);
  profiles = signal<any[]>([]);
  // iter-38 S9 W-D2: replace prompt() with a real dialog.
  showCreate = signal(false);
  newId = '';
  newName = '';

  async ngOnInit(): Promise<void> {
    const rp: any = await firstValueFrom(this.http.get('/api/risk-profiles'));
    this.profiles.set(Array.isArray(rp) ? rp : rp.profiles || []);
  }

  async duplicate(p: any): Promise<void> {
    const res: any = await firstValueFrom(this.http.post(`/api/risk-profiles/${p.id}/duplicate`, {}));
    this.router.navigate(['/risk-profiles', res.id]);
  }

  async create(profileId: string, displayName: string): Promise<void> {
    await firstValueFrom(
      this.http.post('/api/risk-profiles', {
        id: profileId,
        displayName,
        riskPerTradePercent: 0.005,
        maxDailyDrawdownPercent: 0.04,
        maxTotalDrawdownPercent: 0.08,
        maxSlPips: 100,
        maxExposurePercent: 0.05,
        drawdownScaleThreshold: 0.5,
        drawdownScaleFloor: 0.5,
        maxConcurrentPositions: 3,
        allowHedging: false,
        propFirmRuleSetId: 'ftmo-standard',
        lotSizingMethod: 'PercentRisk',
        fixedLots: 0.1,
        fixedDollarRisk: 0,
        kellyFraction: 0.25,
        antiMartingaleMultiplier: 1.5,
        antiMartingaleMaxSteps: 3,
      }),
    );
    this.router.navigate(['/risk-profiles', profileId]);
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
