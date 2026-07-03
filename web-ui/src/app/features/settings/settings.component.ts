import { Component, inject, OnInit, signal, ChangeDetectionStrategy } from '@angular/core';
import { DatePipe } from '@angular/common';
import { SettingsService } from './settings.service';
import type { SystemInfo } from '../../models/api.types';

@Component({
  selector: 'app-settings',
  standalone: true,
  imports: [DatePipe],
  template: `
    <div class="space-y-6">
      <h1 class="text-xl font-semibold">Settings</h1>

      <section class="rounded-lg border border-gray-800 bg-gray-900/50 p-6">
        <h2 class="text-sm font-medium text-gray-300 mb-4">System Info</h2>
        @if (info(); as info) {
          <dl class="space-y-2 text-sm">
            <div class="flex justify-between py-1 border-b border-gray-800">
              <dt class="text-gray-400">Version</dt>
              <dd class="text-gray-200">{{ info.version }}</dd>
            </div>
            <div class="flex justify-between py-1 border-b border-gray-800">
              <dt class="text-gray-400">Branch</dt>
              <dd class="text-gray-200 font-mono text-xs">{{ info.branch }}</dd>
            </div>
            <div class="flex justify-between py-1 border-b border-gray-800">
              <dt class="text-gray-400">Build Date</dt>
              <dd class="text-gray-200">{{ info.buildDate | date:'medium' }}</dd>
            </div>
            <div class="flex justify-between py-1 border-b border-gray-800">
              <dt class="text-gray-400">Active Runs</dt>
              <dd class="text-gray-200">{{ info.activeRuns }} ({{ info.runningRuns }} running)</dd>
            </div>
          </dl>
        } @else {
          <p class="text-sm text-gray-500">Loading system info...</p>
        }
        @if (error(); as err) {
          <p class="text-xs text-red-400 mt-2">{{ err }}</p>
        }
      </section>

      <section class="rounded-lg border border-gray-800 bg-gray-900/50 p-6">
        <h2 class="text-sm font-medium text-gray-300 mb-4">Database Reset</h2>
        <p class="text-xs text-gray-500 mb-4">
          These actions permanently delete data. This cannot be undone.
        </p>

        <div class="grid gap-4 md:grid-cols-2">
          <div class="rounded border border-gray-700 p-4">
            <h3 class="text-sm font-medium text-gray-200 mb-2">Clear Runs</h3>
            <p class="text-xs text-gray-500 mb-3">
              Deletes all backtest runs, trades, journal entries, equity snapshots, and related data.
            </p>
            @if (confirmScope() === 'runs') {
              <div class="space-y-2">
                <input
                  #runsInput
                  type="text"
                  placeholder="Type delete-everything"
                  class="w-full rounded border border-gray-700 bg-gray-800 px-2 py-1 text-xs text-gray-100 focus:border-red-500 focus:outline-none"
                />
                <div class="flex gap-2">
                  <button
                    (click)="doReset('runs', runsInput.value); runsInput.value = ''"
                    class="rounded bg-red-600 px-2 py-1 text-xs text-white hover:bg-red-500"
                  >
                    Confirm Reset
                  </button>
                  <button
                    (click)="confirmScope.set(null)"
                    class="rounded border border-gray-600 px-2 py-1 text-xs text-gray-400 hover:text-white"
                  >
                    Cancel
                  </button>
                </div>
              </div>
            } @else {
              <button
                (click)="confirmScope.set('runs')"
                [disabled]="busy()"
                class="rounded border border-red-800 px-3 py-1.5 text-xs text-red-400 hover:bg-red-900/20 disabled:opacity-40"
              >
                Clear Runs
              </button>
            }
          </div>

          <div class="rounded border border-gray-700 p-4">
            <h3 class="text-sm font-medium text-gray-200 mb-2">Re-seed Config</h3>
            <p class="text-xs text-gray-500 mb-3">
              Resets all strategy configs, risk profiles, FTMO rules, governor options, and add-on packs to factory defaults.
            </p>
            @if (confirmScope() === 'config') {
              <div class="space-y-2">
                <input
                  #configInput
                  type="text"
                  placeholder="Type delete-everything"
                  class="w-full rounded border border-gray-700 bg-gray-800 px-2 py-1 text-xs text-gray-100 focus:border-red-500 focus:outline-none"
                />
                <div class="flex gap-2">
                  <button
                    (click)="doReset('config', configInput.value); configInput.value = ''"
                    class="rounded bg-red-600 px-2 py-1 text-xs text-white hover:bg-red-500"
                  >
                    Confirm Reset
                  </button>
                  <button
                    (click)="confirmScope.set(null)"
                    class="rounded border border-gray-600 px-2 py-1 text-xs text-gray-400 hover:text-white"
                  >
                    Cancel
                  </button>
                </div>
              </div>
            } @else {
              <button
                (click)="confirmScope.set('config')"
                [disabled]="busy()"
                class="rounded border border-orange-800 px-3 py-1.5 text-xs text-orange-400 hover:bg-orange-900/20 disabled:opacity-40"
              >
                Re-seed Config
              </button>
            }
          </div>

          <div class="rounded border border-gray-700 p-4">
            <h3 class="text-sm font-medium text-gray-200 mb-2">Wipe All</h3>
            <p class="text-xs text-gray-500 mb-3">
              Renames trading.db, runs EF migrations, and re-seeds config. The old database is kept as a .bak file.
            </p>
            @if (confirmScope() === 'all') {
              <div class="space-y-2">
                <input
                  #allInput
                  type="text"
                  placeholder="Type delete-everything"
                  class="w-full rounded border border-gray-700 bg-gray-800 px-2 py-1 text-xs text-gray-100 focus:border-red-500 focus:outline-none"
                />
                <div class="flex gap-2">
                  <button
                    (click)="doReset('all', allInput.value); allInput.value = ''"
                    class="rounded bg-red-600 px-2 py-1 text-xs text-white hover:bg-red-500"
                  >
                    Confirm Reset
                  </button>
                  <button
                    (click)="confirmScope.set(null)"
                    class="rounded border border-gray-600 px-2 py-1 text-xs text-gray-400 hover:text-white"
                  >
                    Cancel
                  </button>
                </div>
              </div>
            } @else {
              <button
                (click)="confirmScope.set('all')"
                [disabled]="busy()"
                class="rounded border border-red-800 px-3 py-1.5 text-xs text-red-400 hover:bg-red-900/20 disabled:opacity-40"
              >
                Wipe All
              </button>
            }
          </div>

          <div class="rounded border border-gray-700 p-4">
            <h3 class="text-sm font-medium text-gray-200 mb-2">Clear Market Data</h3>
            <p class="text-xs text-gray-500 mb-3">
              Deletes all downloaded bars from marketdata.db. Tape backtests require re-download afterward.
            </p>
            @if (confirmScope() === 'marketdata') {
              <div class="space-y-2">
                <input
                  #mdInput
                  type="text"
                  placeholder="Type delete-everything"
                  class="w-full rounded border border-gray-700 bg-gray-800 px-2 py-1 text-xs text-gray-100 focus:border-red-500 focus:outline-none"
                />
                <div class="flex gap-2">
                  <button
                    (click)="doReset('marketdata', mdInput.value); mdInput.value = ''"
                    class="rounded bg-red-600 px-2 py-1 text-xs text-white hover:bg-red-500"
                  >
                    Confirm Reset
                  </button>
                  <button
                    (click)="confirmScope.set(null)"
                    class="rounded border border-gray-600 px-2 py-1 text-xs text-gray-400 hover:text-white"
                  >
                    Cancel
                  </button>
                </div>
              </div>
            } @else {
              <button
                (click)="confirmScope.set('marketdata')"
                [disabled]="busy()"
                class="rounded border border-orange-800 px-3 py-1.5 text-xs text-orange-400 hover:bg-orange-900/20 disabled:opacity-40"
              >
                Clear Market Data
              </button>
            }
          </div>
        </div>

        @if (resetStatus(); as status) {
          <div [class]="statusClass(status)">
            {{ status }}
          </div>
        }
      </section>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SettingsComponent implements OnInit {
  private svc = inject(SettingsService);
  info = signal<SystemInfo | null>(null);
  error = signal<string | null>(null);
  busy = signal(false);
  confirmScope = signal<'runs' | 'config' | 'marketdata' | 'all' | null>(null);
  resetStatus = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    try {
      this.info.set(await this.svc.getSystemInfo());
    } catch {
      this.error.set('Failed to load system info.');
    }
  }

  statusClass(status: string): string {
    const base = 'mt-3 rounded border px-3 py-2 text-xs';
    const failed = status.includes('failed');
    return failed
      ? base + ' border-red-800 bg-red-900/20 text-red-400'
      : base + ' border-emerald-800 bg-emerald-900/20 text-emerald-400';
  }

  async doReset(scope: 'runs' | 'config' | 'marketdata' | 'all', confirmValue: string): Promise<void> {
    this.confirmScope.set(null);
    if (confirmValue !== 'delete-everything') {
      this.resetStatus.set('Confirm token incorrect. Type "delete-everything" exactly.');
      return;
    }
    this.busy.set(true);
    this.resetStatus.set(null);
    try {
      const res = await this.svc.reset({ scope, confirm: confirmValue });
      this.resetStatus.set(`Reset complete: scope=${res.scope}, status=${res.status}`);
      this.info.set(await this.svc.getSystemInfo());
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : 'Unknown error';
      this.resetStatus.set(`Reset failed: ${msg}`);
    } finally {
      this.busy.set(false);
    }
  }
}
