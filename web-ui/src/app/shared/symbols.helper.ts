export function formatSymbols(run: { symbols: string | string[]; symbol: string }): string {
  try {
    const s = typeof run.symbols === 'string' ? JSON.parse(run.symbols) : run.symbols;
    if (Array.isArray(s) && s.length > 1) return s.join(', ');
  } catch { /* fall through */ }
  return run.symbol;
}
