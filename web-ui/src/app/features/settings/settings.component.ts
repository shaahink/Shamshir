import { Component, inject, OnInit, signal, ChangeDetectionStrategy } from '@angular/core';
import { StrategiesApiService } from '../strategies/strategies.service';
import { RunsApiService } from '../runs/runs.service';
import { RiskProfilesApiService } from '../risk-profiles/risk-profiles.service';

@Component({
  selector: 'app-settings',
  standalone: true,
  template: `
    <div class="space-y-6">
      <h1 class="text-xl font-semibold">Settings</h1>
      <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-6">
        <dl class="space-y-3 text-sm">
          <div class="flex justify-between py-1 border-b border-gray-800">
            <dt class="text-gray-400">Strategies</dt>
            <dd class="text-gray-200">{{ stratCount() }} seeded</dd>
          </div>
          <div class="flex justify-between py-1 border-b border-gray-800">
            <dt class="text-gray-400">Completed Runs</dt>
            <dd class="text-gray-200">{{ runCount() }}</dd>
          </div>
          <div class="flex justify-between py-1 border-b border-gray-800">
            <dt class="text-gray-400">Risk Profiles</dt>
            <dd class="text-gray-200">{{ profileCount() }} seeded</dd>
          </div>
          <div class="flex justify-between py-1 border-b border-gray-800">
            <dt class="text-gray-400">Default Symbol</dt>
            <dd class="text-gray-200">EURUSD</dd>
          </div>
          <div class="flex justify-between py-1 border-b border-gray-800">
            <dt class="text-gray-400">Default Timeframe</dt>
            <dd class="text-gray-200">H1</dd>
          </div>
          <div class="flex justify-between py-1 border-b border-gray-800">
            <dt class="text-gray-400">Default Balance</dt>
            <dd class="text-gray-200">100,000</dd>
          </div>
          <div class="flex justify-between py-1 border-b border-gray-800">
            <dt class="text-gray-400">Venue</dt>
            <dd class="text-gray-200">Replay (credential-free) + cTrader (NetMQ)</dd>
          </div>
          <div class="flex justify-between py-1">
            <dt class="text-gray-400">Branch</dt>
            <dd class="font-mono text-xs text-gray-500">iter/35-kernel-finish-ab</dd>
          </div>
        </dl>
      </div>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SettingsComponent implements OnInit {
  private strategies = inject(StrategiesApiService);
  private runs = inject(RunsApiService);
  private profiles = inject(RiskProfilesApiService);
  stratCount = signal(0);
  runCount = signal(0);
  profileCount = signal(0);

  async ngOnInit(): Promise<void> {
    try {
      const s = await this.strategies.getAll();
      this.stratCount.set(s.length);
    } catch {}
    try {
      const r = await this.runs.getRuns();
      this.runCount.set(r.length);
    } catch {}
    try {
      const p = await this.profiles.getAll();
      this.profileCount.set(p.length);
    } catch {}
  }
}
