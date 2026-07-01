import type { PriceMarker } from './candle-chart.component';

export function markerFor(kind: string, price: number): PriceMarker {
  switch (kind) {
    case 'Entry': return { price, label: 'Entry', color: '#60a5fa' };
    case 'Exit': return { price, label: 'Exit', color: '#fb923c' };
    case 'StopLoss': return { price, label: 'SL', color: '#ef4444' };
    case 'TakeProfit': return { price, label: 'TP', color: '#10b981' };
    default: return { price, label: kind, color: '#9ca3af' };
  }
}
