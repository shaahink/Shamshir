import type { RunDetail, StrategyPerformance } from '../../models/api.types';
import { downloadBlob } from '../../shared/download.helper';

export interface ExportStats {
  symbols: string;
  profitFactor: string;
  avgR: string;
}

export function buildMarkdownReport(
  d: RunDetail,
  stats: ExportStats,
  breakdown: StrategyPerformance[],
): string {
  return [
    '# Run ' + d.runId.slice(0, 8),
    '',
    '- Symbols: ' + stats.symbols + ' ' + d.period,
    '- Period: ' + d.backtestFrom + ' \u2192 ' + d.backtestTo,
    '- Balance: ' + d.initialBalance,
    '- Net P/L: ' + d.netProfit.toFixed(2) + ' (Gross ' + (d.grossPnL ?? 0).toFixed(2) + ', Comm ' + (d.commissionTotal ?? 0).toFixed(2) + ', Swap ' + (d.swapTotal ?? 0).toFixed(2) + ')',
    '- Max DD: ' + (d.maxDrawdownPct * 100).toFixed(2) + '% \u00b7 Win rate: ' + (d.winRatePct * 100).toFixed(1) + '% \u00b7 Profit factor: ' + stats.profitFactor + ' \u00b7 Avg R: ' + stats.avgR,
    '- Trades: ' + d.totalTrades,
    '',
    '## Per-strategy',
    '| Strategy | Bars | Signals | Trades | Win% | Top no-signal |',
    '|---|---|---|---|---|---|',
    ...breakdown.map(
      (s) =>
        '| ' + s.strategyId + ' | ' + s.totalBarsEvaluated + ' | ' + s.signalsFired + ' | ' + s.tradesOpened + ' | ' + (s.winRatePct * 100).toFixed(0) + '% | ' + topReasonsForExport(s) + ' |',
    ),
  ].join('\n');
}

function topReasonsForExport(s: StrategyPerformance): string {
  return (s.topRejections ?? []).map((r) => r.reason + ' (' + r.count + ')').join(', ') || '\u2014';
}

export function exportReport(
  d: RunDetail,
  stats: ExportStats,
  breakdown: StrategyPerformance[],
  trades: unknown[],
  fmt: string,
): void {
  const content = fmt === 'json'
    ? JSON.stringify({ ...stats, trades }, null, 2)
    : buildMarkdownReport(d, stats, breakdown);
  const mime = fmt === 'json' ? 'application/json' : 'text/markdown';
  const ext = fmt === 'json' ? 'json' : 'md';
  const blob = new Blob([content], { type: mime });
  downloadBlob(blob, 'run-' + d.runId.slice(0, 8) + '.' + ext);
}
