import { Component, computed, inject, OnInit, signal, ChangeDetectionStrategy } from '@angular/core';
import { ActivatedRoute, RouterLink, Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import type { StrategyDetail, RiskProfile } from '../../../models/api.types';
import { StrategiesApiService } from '../strategies.service';
import { RiskProfilesApiService } from '../../risk-profiles/risk-profiles.service';
import { DetailFormBase } from '../../../shared/detail-form-base';

@Component({
  selector: 'app-strategy-detail',
  standalone: true,
  imports: [RouterLink, FormsModule],
  template: `
    <div class="space-y-6">
      <div class="flex items-center justify-between">
        <h1 class="text-xl font-semibold">{{ data()?.displayName || data()?.id || 'Strategy' }}</h1>
        <div class="flex gap-2">
          <button
            (click)="duplicate()"
            [disabled]="saving()"
            class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800"
          >
            Duplicate
          </button>
          <a
            routerLink="/strategies"
            class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800"
            >All Strategies</a
          >
        </div>
      </div>

      @if (isCreate) {
      <div class="mx-auto max-w-lg rounded-lg border border-gray-800 bg-gray-900/50 p-6 space-y-4">
        <h2 class="text-sm font-medium text-gray-200">New Strategy</h2>
        <div><label class="block text-xs text-gray-400 mb-1">ID *</label>
          <input [(ngModel)]="createForm.id" class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none" /></div>
        <div><label class="block text-xs text-gray-400 mb-1">Display Name *</label>
          <input [(ngModel)]="createForm.displayName" class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none" /></div>
        <div><label class="block text-xs text-gray-400 mb-1">Risk Profile</label>
          <select [(ngModel)]="createForm.riskProfileId" class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100">
            @for (rp of riskProfiles(); track rp.id) { <option [value]="rp.id">{{ rp.displayName }}</option> }
          </select></div>
        @if (createError()) { <div class="rounded-md bg-red-900/20 p-2 text-xs text-red-400">{{ createError() }}</div> }
        <div class="flex gap-2">
          <a routerLink="/strategies" class="rounded-md border border-gray-700 px-4 py-2 text-sm text-gray-400 hover:bg-gray-800">Cancel</a>
          <button (click)="doCreate()" [disabled]="saving()" class="flex-1 rounded-md bg-emerald-600 px-4 py-2 text-sm font-medium text-white hover:bg-emerald-500 disabled:opacity-50">{{ saving() ? 'Creating...' : 'Create Strategy' }}</button>
        </div>
      </div>
      }

      @if (data(); as s) {
        <div class="grid gap-4 md:grid-cols-2">
          <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
            <h2 class="mb-3 text-xs font-medium uppercase tracking-wide text-gray-500">Identity</h2>
            <dl class="space-y-2 text-sm">
              <div class="flex justify-between">
                <dt class="text-gray-400">ID</dt>
                <dd class="font-mono text-gray-200">{{ s.id }}</dd>
              </div>
            </dl>
          </div>
          <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4">
            <h2 class="mb-3 text-xs font-medium uppercase tracking-wide text-gray-500">Actions</h2>
            <div class="flex gap-2">
              <button
                (click)="toggleEnabled()"
                [disabled]="saving()"
                [attr.class]="
                  s.enabled
                    ? 'rounded-md bg-red-900/50 px-3 py-1.5 text-xs font-medium text-red-400'
                    : 'rounded-md bg-emerald-900/50 px-3 py-1.5 text-xs font-medium text-emerald-400'
                "
              >
                {{ s.enabled ? 'Disable' : 'Enable' }}
              </button>
              <button
                (click)="editing.set(!editing())"
                class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800"
              >
                {{ editing() ? 'Cancel' : 'Edit Fields' }}
              </button>
              <button
                (click)="deleteStrategy()"
                [disabled]="saving()"
                class="rounded-md border border-red-800 px-3 py-1.5 text-xs text-red-400 hover:bg-red-900/20"
              >Delete</button>
            </div>
          </div>
        </div>

        @if (editing()) {
          <div class="space-y-4 rounded-lg border border-gray-800 bg-gray-900/50 p-4">
            <div>
              <label class="block text-xs font-medium text-gray-400 mb-1">Display Name</label
              ><input
                [(ngModel)]="edit.displayName"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none"
              />
            </div>
            <div>
              <label class="block text-xs font-medium text-gray-400 mb-1">Risk Profile</label
              ><select
                [(ngModel)]="edit.riskProfileId"
                class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none"
              >
                @for (rp of riskProfiles(); track rp.id) {
                  <option [value]="rp.id">{{ rp.displayName }}</option>
                }
              </select>
            </div>

            <div>
              <h3 class="mb-2 text-xs font-medium uppercase tracking-wide text-gray-500">Regime Filter</h3>
              <div class="flex flex-wrap gap-3">
                @for (f of regimeFields; track f.key) {
                  <label class="flex items-center gap-1.5 cursor-pointer"
                    ><input
                      type="checkbox"
                      [checked]="getRegime(f.key)"
                      (change)="setRegime(f.key, !getRegime(f.key))"
                      class="h-4 w-4 rounded border-gray-600 bg-gray-800 text-emerald-500 focus:ring-emerald-500"
                    /><span class="text-xs text-gray-300">{{ f.label }}</span></label
                  >
                }
              </div>
            </div>

            <div>
              <h3 class="mb-2 text-xs font-medium uppercase tracking-wide text-gray-500">Order Entry</h3>
              <div class="grid grid-cols-2 gap-3">
                <div>
                  <label class="block text-xs text-gray-500 mb-1">Method</label
                  ><select
                    [(ngModel)]="edit.orderEntry.method"
                    class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
                  >
                    <option value="Market">Market</option>
                    <option value="LimitOffset">Limit Offset</option>
                    <option value="MarketWithSlippage">Market w/ Slippage</option>
                  </select>
                </div>
                <div>
                  <label class="block text-xs text-gray-500 mb-1">Max Slippage (pips)</label
                  ><input
                    type="number"
                    [(ngModel)]="edit.orderEntry.maxSlippagePips"
                    step="0.1"
                    class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
                  />
                </div>
                <div>
                  <label class="block text-xs text-gray-500 mb-1">Limit Offset (pips)</label
                  ><input
                    type="number"
                    [(ngModel)]="edit.orderEntry.limitOffsetPips"
                    step="0.1"
                    class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
                  />
                </div>
                <div>
                  <label class="block text-xs text-gray-500 mb-1">Limit Expiry (bars)</label
                  ><input
                    type="number"
                    [(ngModel)]="edit.orderEntry.limitOrderExpiryBars"
                    class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
                  />
                </div>
                <div>
                  <label class="block text-xs text-gray-500 mb-1">Max Retries</label
                  ><input
                    type="number"
                    [(ngModel)]="edit.orderEntry.maxMarketRetries"
                    class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
                  />
                </div>
              </div>
            </div>

            <div>
              <h3 class="mb-2 text-xs font-medium uppercase tracking-wide text-gray-500">Reentry</h3>
              <div class="grid grid-cols-2 gap-3">
                <label class="flex items-center gap-1.5 cursor-pointer col-span-2"
                  ><input
                    type="checkbox"
                    [checked]="edit.reentry.blockWhileSameDirectionOpen"
                    (change)="edit.reentry.blockWhileSameDirectionOpen = !edit.reentry.blockWhileSameDirectionOpen"
                    class="h-4 w-4 rounded border-gray-600 bg-gray-800 text-emerald-500 focus:ring-emerald-500"
                  /><span class="text-xs text-gray-300">Block While Same Direction Open</span></label
                >
                <div>
                  <label class="block text-xs text-gray-500 mb-1">Cooldown After SL</label
                  ><input
                    type="number"
                    [(ngModel)]="edit.reentry.cooldownBarsAfterSl"
                    class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
                  />
                </div>
                <div>
                  <label class="block text-xs text-gray-500 mb-1">Cooldown After TP</label
                  ><input
                    type="number"
                    [(ngModel)]="edit.reentry.cooldownBarsAfterTp"
                    class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
                  />
                </div>
                <div>
                  <label class="block text-xs text-gray-500 mb-1">Cooldown After Entry</label
                  ><input
                    type="number"
                    [(ngModel)]="edit.reentry.cooldownBarsAfterEntry"
                    class="w-full rounded-md border border-gray-700 bg-gray-800 px-2 py-1.5 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
                  />
                </div>
              </div>
            </div>

            <div>
              <h3 class="mb-2 text-xs font-medium uppercase tracking-wide text-gray-500">Baseline & Add-ons</h3>
              @for (sec of pmSectionDefs; track sec.section) {
                <div class="mb-2">
                  <button
                    type="button"
                    (click)="togglePmSection(sec.section)"
                    class="flex items-center gap-1 text-xs font-medium text-gray-400 hover:text-gray-300 mb-1"
                  >
                    {{ pmOpen(sec.section) ? '▼' : '▶' }} {{ sec.label }}
                  </button>
                  @if (pmOpen(sec.section)) {
                    <div class="ml-4 space-y-1 border-l border-gray-700 pl-3">
                      @for (f of sec.fields; track f.key) {
                        @if (f.type === 'toggle') {
                          <label class="flex items-center gap-1.5 cursor-pointer"
                            ><input
                              type="checkbox"
                              [checked]="getPmField(sec.section, f.key)"
                              (change)="setPmField(sec.section, f.key, !getPmField(sec.section, f.key))"
                              class="h-3.5 w-3.5 rounded border-gray-600 bg-gray-800 text-emerald-500 focus:ring-emerald-500"
                            /><span class="text-xs text-gray-300">{{ f.label }}</span></label
                          >
                        } @else if (f.type === 'select') {
                          <div class="flex items-center gap-2">
                            <span class="text-xs text-gray-500 w-24">{{ f.label }}</span
                            ><select
                              [ngModel]="getPmField(sec.section, f.key)"
                              (ngModelChange)="setPmField(sec.section, f.key, $event)"
                              class="flex-1 rounded-md border border-gray-700 bg-gray-800 px-2 py-1 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
                            >
                              @for (o of f.options; track o) {
                                <option [value]="o">{{ o }}</option>
                              }
                            </select>
                          </div>
                        } @else {
                          <div class="flex items-center gap-2">
                            <span class="text-xs text-gray-500 w-24">{{ f.label }}</span
                            ><input
                              type="number"
                              [ngModel]="getPmField(sec.section, f.key)"
                              (ngModelChange)="setPmField(sec.section, f.key, $event)"
                              [step]="f.step || 0.1"
                              class="flex-1 rounded-md border border-gray-700 bg-gray-800 px-2 py-1 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
                            />
                          </div>
                        }
                      }
                    </div>
                  }
                </div>
              }
            </div>

            @if (paramKeys().length > 0) {
              <div>
                <h3 class="mb-2 text-xs font-medium uppercase tracking-wide text-gray-500">Parameters</h3>
                <div class="grid grid-cols-2 gap-2">
                  @for (k of paramKeys(); track k) {
                    <div class="flex items-center gap-2">
                      <span class="text-xs text-gray-500 w-28">{{ humanize(k) }}</span
                      ><input
                        [type]="paramType(k)"
                        [ngModel]="edit.parameters[k]"
                        (ngModelChange)="edit.parameters[k] = $event"
                        [step]="paramType(k) === 'number' ? '0.1' : undefined"
                        class="flex-1 rounded-md border border-gray-700 bg-gray-800 px-2 py-1 text-xs text-gray-100 focus:border-emerald-500 focus:outline-none"
                      />
                    </div>
                  }
                </div>
              </div>
            }

            @if (validationError()) {
              <div class="rounded-md bg-red-900/20 p-3 text-xs text-red-400">{{ validationError() }}</div>
            }
            @if (savedOk()) {
              <div class="rounded-md bg-emerald-900/20 p-3 text-xs text-emerald-400">Saved</div>
            }
            <button
              (click)="save()"
              [disabled]="saving()"
              class="w-full rounded-md bg-emerald-600 px-4 py-2.5 text-sm font-medium text-white hover:bg-emerald-500 disabled:opacity-50"
            >
              {{ saving() ? 'Saving...' : 'Save Config' }}
            </button>
          </div>
        } @else {
          <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4 space-y-3">
            <h2 class="text-xs font-medium uppercase tracking-wide text-gray-500">Config</h2>
            @if (data(); as d) {
              <div class="grid grid-cols-2 gap-x-4 gap-y-1 text-xs">
                <div>
                  <span class="text-gray-500">Risk Profile:</span>
                  <span class="text-gray-300">{{ d.riskProfileId }}</span>
                </div>
                <div>
                  <span class="text-gray-500">Enabled:</span>
                  <span class="text-gray-300">{{ d.enabled ? 'Yes' : 'No' }}</span>
                </div>
              </div>
              @if (d.parametersJson) {
                <div>
                  <h3 class="mb-1 text-xs font-medium text-gray-500">Parameters</h3>
                  <div class="grid grid-cols-2 gap-x-4 gap-y-0.5 text-xs">
                    @for (k of paramKeys(); track k) {
                      <div>
                        <span class="text-gray-500">{{ humanize(k) }}:</span>
                        <span class="text-gray-300">{{ edit.parameters[k] ?? '-' }}</span>
                      </div>
                    }
                  </div>
                </div>
              }
            }
          </div>
        }
      } @else {
        <div class="py-12 text-center text-sm text-gray-500">Strategy not found.</div>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class StrategyDetailComponent extends DetailFormBase implements OnInit {
  private route = inject(ActivatedRoute);
  private api = inject(StrategiesApiService);
  private rpApi = inject(RiskProfilesApiService);
  private router = inject(Router);

  data = signal<StrategyDetail | null>(null);
  editing = signal(false);
  validationError = signal<string | null>(null);
  riskProfiles = signal<RiskProfile[]>([]);
  isCreate = false;
  createError = signal<string | null>(null);
  createForm = { id: '', displayName: '', riskProfileId: 'standard' };
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  edit: Record<string, any> = {};
  private pmOpenSections = new Set<string>();

  regimeFields = [
    { key: 'allowTrending', label: 'Trending' },
    { key: 'allowRanging', label: 'Ranging' },
    { key: 'allowHighVolatility', label: 'High Vol' },
    { key: 'allowLowVolatility', label: 'Low Vol' },
    { key: 'allowUnknown', label: 'Unknown' },
  ];

  pmSectionDefs: {
    section: string;
    label: string;
    fields: { key: string; label: string; type: string; step?: string; options?: string[] }[];
  }[] = [];

  paramKeys = computed(() => Object.keys(this.edit.parameters || {}));
  configPreview = computed(() => {
    const d = this.data();
    if (!d) return '';
    const obj: any = {
      id: d.id,
      displayName: d.displayName,
      enabled: d.enabled,
      riskProfileId: d.riskProfileId,
    };
    try {
      if (d.regimeFilterJson) obj.regimeFilter = JSON.parse(d.regimeFilterJson);
    } catch {}
    try {
      if (d.orderEntryJson) obj.orderEntry = JSON.parse(d.orderEntryJson);
    } catch {}
    try {
      if (d.positionManagementJson) obj.positionManagement = JSON.parse(d.positionManagementJson);
    } catch {}
    try {
      if (d.reentryJson) obj.reentry = JSON.parse(d.reentryJson);
    } catch {}
    try {
      if (d.parametersJson) obj.parameters = JSON.parse(d.parametersJson);
    } catch {}
    return JSON.stringify(obj, null, 2);
  });

  async ngOnInit(): Promise<void> {
    const id = this.route.snapshot.paramMap.get('id');
    this.isCreate = id === 'new' || !id;
    if (this.isCreate) {
      try { const rp = await this.rpApi.getAll(); this.riskProfiles.set(rp); } catch {}
      return;
    }
    if (!id) return;
    try {
      const data = await this.api.getById(id);
      this.data.set(data);
      this.buildEdit(data);
    } catch {}
    try {
      const rp = await this.rpApi.getAll();
      this.riskProfiles.set(rp);
    } catch {}
  }

  private buildEdit(d: any): void {
    const rf = this.pj(d.regimeFilterJson) || {
      allowTrending: true,
      allowRanging: true,
      allowHighVolatility: true,
      allowLowVolatility: true,
      allowUnknown: true,
    };
    const oe = this.pj(d.orderEntryJson) || {
      method: 'Market',
      maxSlippagePips: 2.0,
      limitOffsetPips: 0,
      limitOrderExpiryBars: 3,
      maxMarketRetries: 2,
    };
    const pm = this.pj(d.positionManagementJson) || {};
    const re = this.pj(d.reentryJson) || {
      blockWhileSameDirectionOpen: true,
      cooldownBarsAfterSl: 5,
      cooldownBarsAfterTp: 2,
      cooldownBarsAfterEntry: 3,
    };
    const params = this.pj(d.parametersJson) || {};
    this.edit = {
      displayName: d.displayName || d.id,
      riskProfileId: d.riskProfileId || 'standard',
      regimeFilter: rf,
      orderEntry: oe,
      positionManagement: {
        stopLoss: pm.stopLoss || { method: 'AtrMultiple', atrMultiple: 1.5 },
        takeProfit: pm.takeProfit || { method: 'RrMultiple', rrMultiple: 2.0 },
        breakeven: pm.breakeven || { enabled: false, triggerRMultiple: 1.0, offsetPips: 1.0 },
        trailing: pm.trailing || { method: 'AtrMultiple', atrMultiple: 2.5 },
        ride: pm.ride || { enabled: false, activationR: 1.0, lockRiseFraction: 0.5 },
        partialTp: pm.partialTp || { enabled: false, fraction: 0.5, offsetPips: 0 },
      },
      reentry: re,
      parameters: params,
    };
    this.pmSectionDefs = [
      {
        section: 'stopLoss',
        label: 'Stop Loss (Baseline)',
        fields: [
          {
            key: 'method',
            label: 'Method',
            type: 'select',
            options: ['AtrMultiple', 'SwingPoint', 'RrMultiple', 'FixedPips', 'Structure'],
          },
          { key: 'atrMultiple', label: 'ATR Multiple', type: 'number', step: '0.1' },
          { key: 'maxPips', label: 'Max Pips', type: 'number', step: '1' },
        ],
      },
      {
        section: 'takeProfit',
        label: 'Take Profit (Baseline)',
        fields: [
          {
            key: 'method',
            label: 'Method',
            type: 'select',
            options: ['RrMultiple', 'AtrMultiple', 'FixedPips', 'Structure'],
          },
          { key: 'rrMultiple', label: 'RR Multiple', type: 'number', step: '0.1' },
          { key: 'atrMultiple', label: 'ATR Multiple', type: 'number', step: '0.1' },
        ],
      },
      {
        section: 'breakeven',
        label: 'Breakeven (Add-on)',
        fields: [
          { key: 'enabled', label: 'Enabled', type: 'toggle' },
          { key: 'triggerRMultiple', label: 'Trigger R', type: 'number', step: '0.1' },
          { key: 'offsetPips', label: 'Offset Pips', type: 'number', step: '0.1' },
        ],
      },
      {
        section: 'trailing',
        label: 'Trailing (Add-on)',
        fields: [
          { key: 'method', label: 'Method', type: 'select', options: ['AtrMultiple', 'Structure'] },
          { key: 'atrMultiple', label: 'ATR Multiple', type: 'number', step: '0.1' },
        ],
      },
      {
        section: 'ride',
        label: 'Ride (Add-on)',
        fields: [
          { key: 'enabled', label: 'Enabled', type: 'toggle' },
          { key: 'activationR', label: 'Activation R', type: 'number', step: '0.1' },
          { key: 'lockRiseFraction', label: 'Lock Rise Fraction', type: 'number', step: '0.05' },
        ],
      },
      {
        section: 'partialTp',
        label: 'Partial TP (Add-on)',
        fields: [
          { key: 'enabled', label: 'Enabled', type: 'toggle' },
          { key: 'fraction', label: 'Fraction', type: 'number', step: '0.05' },
          { key: 'offsetPips', label: 'Offset Pips', type: 'number', step: '0.1' },
        ],
      },
    ];
  }

  getRegime(k: string): boolean {
    return this.edit.regimeFilter?.[k] ?? false;
  }
  setRegime(k: string, v: boolean): void {
    this.edit.regimeFilter[k] = v;
  }

  getPmField(section: string, field: string): any {
    return this.edit.positionManagement?.[section]?.[field];
  }
  setPmField(section: string, field: string, value: any): void {
    if (!this.edit.positionManagement[section]) this.edit.positionManagement[section] = {};
    this.edit.positionManagement[section][field] = value;
  }

  pmOpen(section: string): boolean {
    return this.pmOpenSections.has(section);
  }
  togglePmSection(section: string): void {
    if (this.pmOpenSections.has(section)) this.pmOpenSections.delete(section);
    else this.pmOpenSections.add(section);
  }

  async toggleEnabled(): Promise<void> {
    const s = this.data();
    if (!s) return;
    this.saving.set(true);
    await this.api.toggleEnabled(s.id, s.enabled);
    this.data.set({ ...s, enabled: !s.enabled });
    this.saving.set(false);
  }

  async save(): Promise<void> {
    this.validationError.set(null);
    this.savedOk.set(false);
    const body: any = {
      displayName: this.edit.displayName,
      riskProfileId: this.edit.riskProfileId,
      regimeFilter: this.edit.regimeFilter,
      orderEntry: this.edit.orderEntry,
      positionManagement: this.edit.positionManagement,
      reentry: this.edit.reentry,
      parameters: this.edit.parameters,
    };
    this.saving.set(true);
    try {
      await this.api.save(this.data()!.id, body);
      this.savedOk.set(true);
      this.editing.set(false);
    } catch (e: any) {
      this.validationError.set(e?.error?.error || e?.message || 'Save failed');
    } finally {
      this.saving.set(false);
    }
  }

  async duplicate(): Promise<void> {
    this.saving.set(true);
    try {
      const res = await this.api.duplicate(this.data()!.id);
      this.router.navigate(['/strategies', res.id]);
    } catch {
    } finally {
      this.saving.set(false);
    }
  }

  async doCreate(): Promise<void> {
    const f = this.createForm;
    const id = f.id.trim(), name = f.displayName.trim();
    if (!id) { this.createError.set('ID is required.'); return; }
    if (!name) { this.createError.set('Display name is required.'); return; }
    this.createError.set(null); this.saving.set(true);
    try {
      const res = await this.api.create({ id, displayName: name, riskProfileId: f.riskProfileId });
      this.router.navigate(['/strategies', res.id]);
    } catch (e: any) { this.createError.set(e?.error?.error || e?.message || 'Create failed'); }
    finally { this.saving.set(false); }
  }

  async deleteStrategy(): Promise<void> {
    const id = this.data()?.id;
    if (!id || !confirm(`Delete strategy "${id}"? This cannot be undone.`)) return;
    this.saving.set(true);
    try { await this.api.delete(id); this.router.navigate(['/strategies']); }
    catch {}
    finally { this.saving.set(false); }
  }

  humanize(key: string): string {
    return key.replace(/([A-Z])/g, ' $1').replace(/^./, (s) => s.toUpperCase());
  }
  paramType(key: string): string {
    const v = this.edit.parameters?.[key];
    return typeof v === 'number' ? 'number' : 'text';
  }

  private pj(json: string | null): any {
    if (!json) return null;
    try {
      return JSON.parse(json);
    } catch {
      return null;
    }
  }
}
