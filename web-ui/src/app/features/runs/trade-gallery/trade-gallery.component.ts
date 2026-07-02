import { Component, inject, OnInit, OnDestroy, signal, ChangeDetectionStrategy } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { TradeChartCardComponent } from '../../../shared/trade-chart-card.component';
import { RunsApiService } from '../runs.service';

@Component({
  selector: 'app-trade-gallery',
  standalone: true,
  imports: [RouterLink, TradeChartCardComponent],
  template: `
    <div class="space-y-6">
      <div class="flex items-center justify-between">
        <div>
          <h1 class="text-xl font-semibold">Trade Gallery</h1>
          <p class="text-xs text-gray-500">Run {{ runId() }}</p>
        </div>
        <a [routerLink]="['/runs', runId()]" class="rounded-md border border-gray-700 px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800">
          &larr; Report
        </a>
      </div>

      <div class="text-xs text-gray-500">
        {{ loadedCount() }} / {{ tradeCount() }} trades loaded
      </div>

      <div class="grid gap-4 sm:grid-cols-1 lg:grid-cols-2 xl:grid-cols-3">
        @for (trade of tradeIds(); track trade; let i = $index) {
          <div #card [attr.data-index]="i" class="gallery-card">
            @if (visibleIndices().has(i)) {
              <app-trade-chart-card [tradeId]="trade" />
            } @else {
              <div class="flex h-24 items-center justify-center rounded-lg border border-gray-800 bg-gray-900/20 text-xs text-gray-600">
                Scroll to load...
              </div>
            }
          </div>
        }
      </div>

      @if (tradeIds().length === 0) {
        <div class="py-12 text-center text-sm text-gray-500">No trades in this run.</div>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TradeGalleryComponent implements OnInit, OnDestroy {
  private route = inject(ActivatedRoute);
  private api = inject(RunsApiService);

  runId = signal('');
  tradeIds = signal<string[]>([]);
  tradeCount = signal(0);
  loadedCount = signal(0);
  visibleIndices = signal(new Set<number>());

  // P5.2: lazy-load charts via IntersectionObserver.
  private observer: IntersectionObserver | null = null;

  async ngOnInit(): Promise<void> {
    const rid = this.route.snapshot.paramMap.get('runId');
    if (!rid) return;
    this.runId.set(rid);

    try {
      const trades = await this.api.getRunTrades(rid);
      this.tradeIds.set(trades.map((t) => t.id));
      this.tradeCount.set(trades.length);
    } catch {
      /* no trades */
    }

    // Observe cards for lazy loading.
    this.observer = new IntersectionObserver(
      (entries) => {
        const newIndices = new Set(this.visibleIndices());
        for (const e of entries) {
          if (e.isIntersecting) {
            const idx = Number((e.target as HTMLElement).dataset['index']);
            if (!isNaN(idx)) {
              if (!newIndices.has(idx)) this.loadedCount.update((c) => c + 1);
              newIndices.add(idx);
            }
          }
        }
        this.visibleIndices.set(newIndices);
      },
      { rootMargin: '200px' },
    );

    // Observe after render.
    setTimeout(() => {
      document.querySelectorAll('.gallery-card').forEach((el) => {
        this.observer?.observe(el);
      });
    }, 100);
  }

  ngOnDestroy(): void {
    this.observer?.disconnect();
  }
}
