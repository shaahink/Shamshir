import { Component, inject, ChangeDetectionStrategy } from '@angular/core';
import { ToastService } from './toast.service';

@Component({
  selector: 'app-toast',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @for (t of svc.toasts(); track t.id) {
      <div
        class="pointer-events-auto flex items-center gap-2 rounded-md px-4 py-2 text-sm shadow-lg border"
        [class.bg-red-950]="t.kind === 'error'"
        [class.border-red-800]="t.kind === 'error'"
        [class.text-red-300]="t.kind === 'error'"
        [class.bg-amber-950]="t.kind === 'warning'"
        [class.border-amber-800]="t.kind === 'warning'"
        [class.text-amber-300]="t.kind === 'warning'"
        [class.bg-emerald-950]="t.kind === 'info'"
        [class.border-emerald-800]="t.kind === 'info'"
        [class.text-emerald-300]="t.kind === 'info'"
      >
        <span class="flex-1">{{ t.message }}</span>
        <button (click)="svc.dismiss(t.id)" class="text-gray-400 hover:text-white text-lg leading-none">&times;</button>
      </div>
    }
  `,
  host: {
    class: 'fixed top-4 right-4 z-50 flex flex-col gap-2 pointer-events-none',
  },
})
export class ToastComponent {
  readonly svc = inject(ToastService);
}
