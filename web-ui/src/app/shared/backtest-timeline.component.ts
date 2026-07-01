import { Component, input, computed, ChangeDetectionStrategy } from '@angular/core';

export interface TimelineEvent {
  simTime: string;
  label: string;
  kind: 'entry' | 'exit' | 'breach' | 'roll';
}

@Component({
  selector: 'app-backtest-timeline',
  standalone: true,
  template: `
    <div class="rounded-lg border border-gray-800 bg-gray-900/30 p-4">
      <div class="mb-2 flex items-center justify-between text-xs text-gray-500">
        <span>{{ fromDisplay() }}</span>
        <span class="text-gray-400">{{ simTimeDisplay() }}</span>
        <span>{{ toDisplay() }}</span>
      </div>
      <div class="relative h-8 rounded bg-gray-800/50">
        <!-- Pass segments -->
        @for (seg of passSegments(); track seg.index) {
          <div
            class="absolute top-0 h-full bg-blue-900/30"
            [style.left]="seg.leftPct + '%'"
            [style.width]="seg.widthPct + '%'"
          ></div>
        }
        <!-- Event ticks -->
        @for (evt of visibleEvents(); track $index) {
          <div
            class="absolute top-0 h-full w-px"
            [style.left]="evtPos(evt) + '%'"
            [class.bg-green-500]="evt.kind === 'entry'"
            [class.bg-red-500]="evt.kind === 'exit'"
            [class.bg-yellow-500]="evt.kind === 'breach'"
            [class.bg-gray-500]="evt.kind === 'roll'"
            [title]="evt.label"
          ></div>
        }
        <!-- Playhead -->
        @if (playheadPct() >= 0 && playheadPct() <= 100) {
          <div
            class="absolute top-0 h-full w-0.5 bg-white shadow-[0_0_6px_rgba(255,255,255,0.5)] transition-all duration-500"
            [style.left]="playheadPct() + '%'"
          >
            <div class="absolute -top-1 left-1/2 h-3 w-3 -translate-x-1/2 rounded-full bg-white shadow-[0_0_6px_rgba(255,255,255,0.5)]"></div>
          </div>
        }
      </div>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BacktestTimelineComponent {
  readonly from = input.required<string>();
  readonly to = input.required<string>();
  readonly simTime = input<string | null>(null);
  readonly events = input<TimelineEvent[]>([]);
  readonly passCount = input(1);
  readonly currentPass = input(1);

  private ticks(key: string): number {
    return new Date(key + 'Z').getTime();
  }

  protected fromDisplay = computed(() => this.formatShort(this.from()));
  protected toDisplay = computed(() => this.formatShort(this.to()));
  protected simTimeDisplay = computed(() => {
    const st = this.simTime();
    return st ? this.formatShort(st) : '--';
  });

  protected playheadPct = computed(() => {
    const st = this.simTime();
    if (!st) return -1;
    const range = this.ticks(this.to()) - this.ticks(this.from());
    if (range <= 0) return -1;
    return ((this.ticks(st) - this.ticks(this.from())) / range) * 100;
  });

  protected passSegments = computed(() => {
    const n = this.passCount();
    if (n <= 1) return [];
    return Array.from({ length: n }, (_, i) => ({
      index: i,
      leftPct: (i / n) * 100,
      widthPct: 100 / n,
    }));
  });

  protected visibleEvents = this.events;

  protected evtPos(evt: TimelineEvent): number {
    const range = this.ticks(this.to()) - this.ticks(this.from());
    if (range <= 0) return 0;
    return ((this.ticks(evt.simTime) - this.ticks(this.from())) / range) * 100;
  }

  private formatShort(iso: string): string {
    try {
      const d = new Date(iso + 'Z');
      return d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short' });
    } catch {
      return iso.slice(0, 10);
    }
  }
}
