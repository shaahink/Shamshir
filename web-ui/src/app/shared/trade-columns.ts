export interface ColumnDef {
  key: string;
  label: string;
  format?: string;
  colorFn?: (v: number) => string;
}

export const TRADE_COLUMNS: ColumnDef[] = [
  { key: 'symbol', label: 'Sym' },
  { key: 'direction', label: 'Dir' },
  { key: 'lots', label: 'Lots', format: 'number' },
  { key: 'entryPrice', label: 'Entry', format: 'number' },
  { key: 'exitPrice', label: 'Exit', format: 'number' },
  {
    key: 'grossPnLAmount',
    label: 'Gross',
    format: 'currency',
    colorFn: (v: number) => (v >= 0 ? '#34d399' : '#f87171'),
  },
  { key: 'commissionAmount', label: 'Comm', format: 'currency' },
  { key: 'swapAmount', label: 'Swap', format: 'currency' },
  {
    key: 'netPnLAmount',
    label: 'Net P/L',
    format: 'currency',
    colorFn: (v: number) => (v >= 0 ? '#34d399' : '#f87171'),
  },
  { key: 'pnLPips', label: 'Pips', format: 'pips' },
  { key: 'rMultiple', label: 'R', format: 'number' },
  { key: 'exitReason', label: 'Exit' },
  { key: 'strategyId', label: 'Strategy' },
];
