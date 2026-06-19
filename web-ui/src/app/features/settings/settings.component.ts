import { Component, inject, OnInit, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

@Component({
  selector: 'app-settings',
  standalone: true,
  template: `
    <div class="space-y-6">
      <h1 class="text-xl font-semibold">Settings</h1>
      <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-6">
        <dl class="space-y-3 text-sm">
          <div class="flex justify-between py-1 border-b border-gray-800"><dt class="text-gray-400">Strategies</dt><dd class="text-gray-200">{{ stratCount() }} seeded</dd></div>
          <div class="flex justify-between py-1 border-b border-gray-800"><dt class="text-gray-400">Completed Runs</dt><dd class="text-gray-200">{{ runCount() }}</dd></div>
          <div class="flex justify-between py-1 border-b border-gray-800"><dt class="text-gray-400">Risk Profiles</dt><dd class="text-gray-200">{{ profileCount() }} seeded</dd></div>
          <div class="flex justify-between py-1 border-b border-gray-800"><dt class="text-gray-400">Default Symbol</dt><dd class="text-gray-200">EURUSD</dd></div>
          <div class="flex justify-between py-1 border-b border-gray-800"><dt class="text-gray-400">Default Timeframe</dt><dd class="text-gray-200">H1</dd></div>
          <div class="flex justify-between py-1 border-b border-gray-800"><dt class="text-gray-400">Default Balance</dt><dd class="text-gray-200">100,000</dd></div>
          <div class="flex justify-between py-1 border-b border-gray-800"><dt class="text-gray-400">Venue</dt><dd class="text-gray-200">Replay (credential-free) + cTrader (NetMQ)</dd></div>
          <div class="flex justify-between py-1"><dt class="text-gray-400">Branch</dt><dd class="font-mono text-xs text-gray-500">iter/35-kernel-finish-ab</dd></div>
        </dl>
      </div>
    </div>
  `,
})
export class SettingsComponent implements OnInit {
  private http = inject(HttpClient);
  stratCount = signal(0); runCount = signal(0); profileCount = signal(0);

  async ngOnInit(): Promise<void> {
    try { const s: any = await firstValueFrom(this.http.get('/api/strategies')); this.stratCount.set(Array.isArray(s) ? s.length : (s.strategies?.length || 0)); } catch {}
    try { const r: any = await firstValueFrom(this.http.get('/api/runs')); this.runCount.set(Array.isArray(r) ? r.length : 0); } catch {}
    try { const p: any = await firstValueFrom(this.http.get('/api/risk-profiles')); this.profileCount.set(Array.isArray(p) ? p.length : (p.profiles?.length || 0)); } catch {}
  }
}
