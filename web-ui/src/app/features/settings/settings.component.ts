import { Component } from '@angular/core';

@Component({
  selector: 'app-settings',
  standalone: true,
  template: `
    <div class="space-y-6">
      <h1 class="text-xl font-semibold">Settings</h1>
      <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-6">
        <dl class="space-y-3 text-sm">
          <div class="flex justify-between py-1 border-b border-gray-800"><dt class="text-gray-400">Database Path</dt><dd class="font-mono text-xs text-gray-500">data/trading.db</dd></div>
          <div class="flex justify-between py-1 border-b border-gray-800"><dt class="text-gray-400">Default Symbol</dt><dd class="text-gray-200">EURUSD</dd></div>
          <div class="flex justify-between py-1 border-b border-gray-800"><dt class="text-gray-400">Default Timeframe</dt><dd class="text-gray-200">H1</dd></div>
          <div class="flex justify-between py-1 border-b border-gray-800"><dt class="text-gray-400">Default Balance</dt><dd class="text-gray-200">100,000</dd></div>
          <div class="flex justify-between py-1 border-b border-gray-800"><dt class="text-gray-400">Default Commission</dt><dd class="text-gray-200">30 per million</dd></div>
          <div class="flex justify-between py-1 border-b border-gray-800"><dt class="text-gray-400">Default Spread</dt><dd class="text-gray-200">1 pip</dd></div>
          <div class="flex justify-between py-1 border-b border-gray-800"><dt class="text-gray-400">Venue</dt><dd class="text-gray-200">Replay (credential-free)</dd></div>
          <div class="flex justify-between py-1"><dt class="text-gray-400">Version</dt><dd class="font-mono text-xs text-gray-500">2.0.0 · iter/33-angular-spa</dd></div>
        </dl>
      </div>
    </div>
  `,
})
export class SettingsComponent {}
