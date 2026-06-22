import { Component, inject, OnInit, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { DatePipe } from '@angular/common';
import type { AddOnPack } from '../../models/api.types';
import { AddOnPacksApiService } from './addon-packs.service';

@Component({
  selector: 'app-addon-pack-list',
  standalone: true,
  imports: [RouterLink, DatePipe],
  template: `
    <div class="space-y-6">
      <div class="flex items-center justify-between">
        <h1 class="text-xl font-semibold">Add-on Packs</h1>
        <button
          (click)="createStarter()"
          class="rounded-md bg-emerald-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-emerald-500"
        >
          New Pack
        </button>
      </div>
      <div class="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
        @for (p of packs(); track p.id) {
          <div class="rounded-lg border border-gray-800 bg-gray-900/50 p-4 transition hover:border-gray-600">
            <div class="flex items-center justify-between mb-2">
              <a
                [routerLink]="['/addon-packs', p.id]"
                class="text-sm font-medium text-gray-100 hover:text-emerald-400"
              >{{ p.name }}</a>
              <button (click)="remove(p)" class="rounded border border-red-800 px-2 py-0.5 text-xs text-red-400 hover:bg-red-900/20">
                Delete
              </button>
            </div>
            @if (p.description) {
              <p class="text-xs text-gray-500 mb-2">{{ p.description }}</p>
            }
            <p class="font-mono text-xs text-gray-600 mb-2">{{ p.id }}</p>
            <div class="flex items-center gap-2 text-xs">
              <span
                [class.text-emerald-400]="p.regimeDetectionEnabled"
                [class.text-gray-500]="!p.regimeDetectionEnabled"
              >Regime: {{ p.regimeDetectionEnabled ? 'On' : 'Off' }}</span>
              @if (p.createdAtUtc) {
                <span class="text-gray-600">Created {{ p.createdAtUtc | date: 'yyyy-MM-dd' }}</span>
              }
            </div>
          </div>
        }
      </div>
      @if (packs().length === 0) {
        <div class="py-12 text-center text-sm text-gray-500">No packs. Click "New Pack" to create a starter pack.</div>
      }
    </div>
  `,
})
export class AddOnPackListComponent implements OnInit {
  private api = inject(AddOnPacksApiService);
  packs = signal<AddOnPack[]>([]);

  async ngOnInit(): Promise<void> {
    try { this.packs.set(await this.api.getAll()); } catch { /* */ }
  }

  async createStarter(): Promise<void> {
    const id = 'pack-' + Date.now();
    await this.api.save(id, {
      id, name: id, description: 'Custom add-on pack',
      addOns: {
        trailing: { method: 'AtrMultiple', atrMultiple: 2.5 },
        breakeven: { enabled: false, triggerRMultiple: 1.0, offsetPips: 1.0 },
      },
      regimeDetectionEnabled: true,
    });
    this.packs.set(await this.api.getAll());
  }

  async remove(p: AddOnPack): Promise<void> {
    await this.api.delete(p.id);
    this.packs.update((arr) => arr.filter((x) => x.id !== p.id));
  }
}
