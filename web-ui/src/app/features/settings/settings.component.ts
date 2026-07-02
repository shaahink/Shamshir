import { Component, inject, OnInit, signal, ChangeDetectionStrategy } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { StrategiesApiService } from '../strategies/strategies.service';
import { RunsApiService } from '../runs/runs.service';
import { RiskProfilesApiService } from '../risk-profiles/risk-profiles.service';
import { SystemApiService, type SystemInfo, type ResetScope } from './system.service';

@Component({
  selector: 'app-settings',
  standalone: true,
  imports: [DatePipe, FormsModule],
  template: `
    <div class="space-y-6">
      <h1 class="text-xl font-semibold">Settings</h1>

      <!-- System info -->
      <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-6">
        <h2 class="mb-3 text-sm font-medium uppercase tracking-wide text-gray-500">System</h2>
        <dl class="space-y-3 text-sm">
          <div class="flex justify-between py-1 border-b border-gray-800">
            <dt class="text-gray-400">Version</dt>
            <dd class="text-gray-200">{{ info()?.version || '—' }}</dd>
          </div>
          <div class="flex justify-between py-1 border-b border-gray-800">
            <dt class="text-gray-400">Branch</dt>
            <dd class="font-mono text-xs text-gray-300">{{ info()?.branch || '—' }}</dd>
          </div>
          <div class="flex justify-between py-1 border-b border-gray-800">
            <dt class="text-gray-400">Build</dt>
            <dd class="font-mono text-xs text-gray-500">{{ info()?.buildDate ? (info()!.buildDate | date: 'yyyy-MM-dd HH:mm') : '—' }}</dd>
          </div>
          <div class="flex justify-between py-1 border-b border-gray-800">
            <dt class="text-gray-400">Trading DB</dt>
            <dd class="font-mono text-xs text-gray-500 truncate max-w-[60%]" [title]="info()?.dataPaths?.tradingDb || ''">{{ info()?.dataPaths?.tradingDb || '—' }}</dd>
          </div>
          <div class="flex justify-between py-1 border-b border-gray-800">
            <dt class="text-gray-400">Strategies</dt>
            <dd class="text-gray-200">{{ stratCount() }} seeded</dd>
          </div>
          <div class="flex justify-between py-1 border-b border-gray-800">
            <dt class="text-gray-400">Risk Profiles</dt>
            <dd class="text-gray-200">{{ profileCount() }} seeded</dd>
          </div>
          <div class="flex justify-between py-1">
            <dt class="text-gray-400">Completed Runs</dt>
            <dd class="text-gray-200">{{ runCount() }} <span class="text-gray-500">({{ info()?.runningRuns ?? 0 }} running)</span></dd>
          </div>
        </dl>
      </div>

      <!-- Housekeeping: prune -->
      <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-6">
        <h2 class="mb-1 text-sm font-medium uppercase tracking-wide text-gray-500">Housekeeping</h2>
        <p class="mb-3 text-xs text-gray-500">Keep the newest runs, delete older ones. Running runs are always kept.</p>
        <div class="flex items-center gap-3">
          <label class="text-sm text-gray-400">Keep newest</label>
          <input type="number" min="0" [(ngModel)]="keepN" class="w-24 rounded-md border border-gray-700 bg-gray-800 px-3 py-1.5 text-sm text-gray-100 focus:border-emerald-500 focus:outline-none" />
          <button (click)="prune()" [disabled]="busy()" class="rounded-md border border-amber-700 px-3 py-1.5 text-xs text-amber-400 hover:bg-amber-900/20 disabled:opacity-50">
            {{ busy() ? 'Working…' : 'Prune runs' }}
          </button>
          @if (pruneMsg()) { <span class="text-xs text-gray-400">{{ pruneMsg() }}</span> }
        </div>
      </div>

      <!-- Danger zone: DB reset -->
      <div class="rounded-lg border border-red-900/60 bg-red-950/10 p-6">
        <h2 class="mb-1 text-sm font-medium uppercase tracking-wide text-red-400">Danger Zone</h2>
        <p class="mb-4 text-xs text-gray-500">Irreversible. Market data (marketdata.db) is never touched by any reset.</p>
        <div class="space-y-3">
          <div class="flex items-center justify-between gap-4">
            <div>
              <div class="text-sm text-gray-200">Delete all runs</div>
              <div class="text-xs text-gray-500">Clears every backtest run, its trades, journal, equity and recorded bars.</div>
            </div>
            <button (click)="askReset('runs')" [disabled]="busy()" class="shrink-0 rounded-md border border-red-800 px-3 py-1.5 text-xs text-red-400 hover:bg-red-900/20 disabled:opacity-50">Reset runs</button>
          </div>
          <div class="flex items-center justify-between gap-4 border-t border-gray-800 pt-3">
            <div>
              <div class="text-sm text-gray-200">Reset config</div>
              <div class="text-xs text-gray-500">Re-seeds strategies, risk profiles, prop-firm rules, governor and add-on packs from JSON.</div>
            </div>
            <button (click)="askReset('config')" [disabled]="busy()" class="shrink-0 rounded-md border border-red-800 px-3 py-1.5 text-xs text-red-400 hover:bg-red-900/20 disabled:opacity-50">Reset config</button>
          </div>
          <div class="flex items-center justify-between gap-4 border-t border-gray-800 pt-3">
            <div>
              <div class="text-sm text-gray-200">Wipe everything</div>
              <div class="text-xs text-gray-500">Backs up and recreates the trading DB, then re-seeds config.</div>
            </div>
            <button (click)="askReset('all')" [disabled]="busy()" class="shrink-0 rounded-md border border-red-800 px-3 py-1.5 text-xs text-red-400 hover:bg-red-900/20 disabled:opacity-50">Wipe all</button>
          </div>
        </div>
        @if (resetMsg()) { <div class="mt-3 text-xs text-gray-400">{{ resetMsg() }}</div> }
      </div>
    </div>

    <!-- Confirm modal: type the word -->
    @if (pendingScope()) {
      <div class="fixed inset-0 z-50 flex items-center justify-center bg-black/60" (click)="cancelReset()">
        <div class="w-full max-w-md rounded-lg border border-red-800 bg-gray-900 p-5 shadow-xl" (click)="$event.stopPropagation()">
          <h3 class="mb-2 text-sm font-medium text-red-400">Confirm {{ scopeLabel(pendingScope()!) }}</h3>
          <p class="mb-3 text-xs text-gray-400">This cannot be undone. Type <span class="font-mono text-gray-200">delete-everything</span> to proceed.</p>
          <input type="text" [(ngModel)]="confirmText" placeholder="delete-everything" class="w-full rounded-md border border-gray-700 bg-gray-800 px-3 py-2 text-sm text-gray-100 focus:border-red-500 focus:outline-none" />
          <div class="mt-4 flex justify-end gap-2">
            <button (click)="cancelReset()" class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-400 hover:bg-gray-800">Cancel</button>
            <button (click)="confirmReset()" [disabled]="confirmText !== 'delete-everything' || busy()" class="rounded-md bg-red-700 px-4 py-1.5 text-xs font-medium text-white hover:bg-red-600 disabled:opacity-40">
              {{ busy() ? 'Working…' : 'Confirm' }}
            </button>
          </div>
        </div>
      </div>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SettingsComponent implements OnInit {
  private strategies = inject(StrategiesApiService);
  private runs = inject(RunsApiService);
  private profiles = inject(RiskProfilesApiService);
  private system = inject(SystemApiService);

  info = signal<SystemInfo | null>(null);
  stratCount = signal(0);
  runCount = signal(0);
  profileCount = signal(0);
  busy = signal(false);

  keepN = 20;
  pruneMsg = signal('');
  resetMsg = signal('');
  pendingScope = signal<ResetScope | null>(null);
  confirmText = '';

  async ngOnInit(): Promise<void> {
    await this.refresh();
  }

  private async refresh(): Promise<void> {
    try { this.info.set(await this.system.getInfo()); } catch { /* */ }
    try { this.stratCount.set((await this.strategies.getAll()).length); } catch { /* */ }
    try { this.runCount.set((await this.runs.getRuns()).length); } catch { /* */ }
    try { this.profileCount.set((await this.profiles.getAll()).length); } catch { /* */ }
  }

  async prune(): Promise<void> {
    if (this.busy()) return;
    const keep = Math.max(0, Math.floor(this.keepN || 0));
    if (!confirm(`Delete all but the newest ${keep} run(s)?`)) return;
    this.busy.set(true);
    this.pruneMsg.set('');
    try {
      const res = await this.runs.pruneRuns(keep);
      this.pruneMsg.set(`Deleted ${res.deleted}, kept ${res.kept}.`);
      await this.refresh();
    } catch {
      this.pruneMsg.set('Prune failed.');
    } finally {
      this.busy.set(false);
    }
  }

  askReset(scope: ResetScope): void {
    this.confirmText = '';
    this.resetMsg.set('');
    this.pendingScope.set(scope);
  }
  cancelReset(): void { this.pendingScope.set(null); }

  scopeLabel(s: ResetScope): string {
    return s === 'runs' ? 'delete all runs' : s === 'config' ? 'reset config' : 'wipe everything';
  }

  async confirmReset(): Promise<void> {
    const scope = this.pendingScope();
    if (!scope || this.confirmText !== 'delete-everything' || this.busy()) return;
    this.busy.set(true);
    try {
      await this.system.reset(scope);
      this.resetMsg.set(`${scopeLabelDone(scope)} — done.`);
      this.pendingScope.set(null);
      await this.refresh();
    } catch {
      this.resetMsg.set('Reset failed — a run may still be active. Cancel it and retry.');
    } finally {
      this.busy.set(false);
    }
  }
}

function scopeLabelDone(s: ResetScope): string {
  return s === 'runs' ? 'Runs deleted' : s === 'config' ? 'Config re-seeded' : 'Database wiped';
}
