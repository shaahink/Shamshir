import { Component, input, output, signal, ChangeDetectionStrategy } from '@angular/core';
import { FormsModule } from '@angular/forms';

@Component({
  selector: 'app-create-modal',
  standalone: true,
  imports: [FormsModule],
  template: `
    @if (open()) {
      <div class="fixed inset-0 z-50 flex items-center justify-center bg-black/60" (click)="cancel()">
        <div class="w-96 rounded-lg border border-gray-700 bg-gray-900 p-6 shadow-xl" (click)="$event.stopPropagation()">
          <h2 class="mb-4 text-sm font-medium text-gray-200">{{ title() }}</h2>
          <label class="mb-1 block text-xs text-gray-400">{{ idLabel() }}</label>
          <input [(ngModel)]="newId" class="mb-3 w-full rounded border border-gray-700 bg-gray-800 px-3 py-1.5 text-xs text-gray-200" />
          <label class="mb-1 block text-xs text-gray-400">{{ nameLabel() }}</label>
          <input [(ngModel)]="newName" class="mb-4 w-full rounded border border-gray-700 bg-gray-800 px-3 py-1.5 text-xs text-gray-200" />
          <div class="flex justify-end gap-2">
            <button (click)="cancel()" class="rounded border border-gray-700 px-3 py-1 text-xs text-gray-400 hover:bg-gray-800">Cancel</button>
            <button (click)="confirm()" [disabled]="!newId || !newName" class="rounded bg-emerald-600 px-3 py-1 text-xs text-white hover:bg-emerald-500 disabled:opacity-50">Create</button>
          </div>
        </div>
      </div>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CreateModalComponent {
  readonly open = input(false);
  readonly title = input('New Entity');
  readonly idLabel = input('ID');
  readonly nameLabel = input('Display Name');
  readonly confirmed = output<{ id: string; displayName: string }>();
  readonly cancelled = output<void>();

  newId = '';
  newName = '';

  cancel(): void {
    this.newId = '';
    this.newName = '';
    this.cancelled.emit();
  }

  confirm(): void {
    if (!this.newId || !this.newName) return;
    this.confirmed.emit({ id: this.newId.trim(), displayName: this.newName.trim() });
    this.newId = '';
    this.newName = '';
  }
}
