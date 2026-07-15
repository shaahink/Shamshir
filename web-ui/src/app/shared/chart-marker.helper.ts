import type { PriceMarker } from './candle-chart.component';

// X3: `time` (ms) makes Entry/Exit time-anchored candle markers; without it a marker renders as a
// horizontal level line. The old helper dropped the API's time, which is why every trade chart
// showed meaningless full-width lines instead of entry/exit arrows.
export function markerFor(kind: string, price: number, timeMs?: number): PriceMarker {
  switch (kind) {
    case 'Entry': return { price, label: 'Entry', color: '#60a5fa', time: timeMs };
    case 'Exit': return { price, label: 'Exit', color: '#fb923c', time: timeMs };
    case 'StopLoss': return { price, label: 'SL', color: '#ef4444' };
    case 'TakeProfit': return { price, label: 'TP', color: '#10b981' };
    default: return { price, label: kind, color: '#9ca3af' };
  }
}
