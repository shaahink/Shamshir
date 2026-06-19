export interface RunSummary {
  runId: string;
  status: string;
  symbol: string;
  period: string;
  symbols: string;
  periods: string;
  startedAtUtc: string;
  completedAtUtc: string | null;
  netProfit: number;
  maxDrawdownPct: number;
  totalTrades: number;
  winningTrades: number;
  winRatePct: number;
  errorMessage: string | null;
}

export interface RunDetail {
  runId: string;
  status: string;
  symbol: string;
  period: string;
  symbols: string;
  periods: string;
  startedAtUtc: string;
  completedAtUtc: string | null;
  backtestFrom: string;
  backtestTo: string;
  initialBalance: number;
  netProfit: number;
  grossPnL: number;
  commissionTotal: number;
  swapTotal: number;
  maxDrawdownPct: number;
  totalTrades: number;
  winningTrades: number;
  winRatePct: number;
  errorMessage: string | null;
  exitCode: number;
  effectiveConfigJson: string | null;
  reportJsonPath: string | null;
}

export interface TradeSummary {
  id: string;
  positionId: string;
  orderId: string;
  symbol: string;
  direction: string;
  lots: number;
  entryPrice: number;
  exitPrice: number;
  openedAtUtc: string;
  closedAtUtc: string;
  grossPnLAmount: number;
  commissionAmount: number;
  swapAmount: number;
  netPnLAmount: number;
  pnLPips: number;
  rMultiple: number;
  maxAdverseExcursion: number;
  maxFavorableExcursion: number;
  exitReason: string;
  strategyId: string;
  durationSeconds: number;
}

export interface JournalEntry {
  seq: number;
  simTimeUtc: string;
  kind: string | null;
  symbol: string | null;
  strategyId: string | null;
  reason: string | null;
  detail: string | null;
}

export interface EquityPoint {
  timestampUtc: string;
  equity: number;
  balance: number;
}

export interface DailyPnl {
  date: string;
  pnl: number;
}

export interface RunAnalytics {
  rMultiples: number[];
  holdingTimes: number[];
  pnlByHour: { key: string; value: number }[];
  pnlByDay: { key: string; value: number }[];
  maeMfe: { x: number; y: number }[];
}

export interface StartRunRequest {
  symbol: string;
  period: string;
  start: string;
  end: string;
  balance: number;
  commissionPerMillion: number;
  spreadPips: number;
  symbols: string[];
  periods: string[];
  strategyIds: string[];
  riskProfileId?: string;
  venue?: string;
}

export interface RiskProfile {
  id: string;
  displayName: string;
  riskPerTradePercent: number;
  maxDailyDrawdownPercent: number;
  maxTotalDrawdownPercent: number;
  maxConcurrentPositions: number;
  lotSizingMethod: string;
  propFirmRuleSetId: string;
}

export interface StartRunResponse {
  runId: string;
  status: string;
}

export interface GovernorState {
  state: string;
  sizeMultiplier: number;
  consecutiveLosses: number;
  dayNetPnLFraction: number;
  distanceToDailyLimitFraction: number;
  reason: string | null;
}

export interface ProtectionDay {
  id: string;
  date: string;
  startEquity: number;
  minEquity: number;
  endEquity: number;
  maxDailyDdUsedFraction: number;
  finalGovernorState: string | null;
  breachOccurred: boolean;
  tradesOpened: number;
  tradesClosed: number;
  signalsBlocked: number;
}

export interface ProtectionEntry {
  atUtc: string;
  category: string;
  reason: string;
  equityAtTime: number;
  dailyDdUsedFraction: number;
}

export interface PipelineEvent {
  seq: number;
  simTimeUtc: string;
  kind: string | null;
  symbol: string | null;
  strategyId: string | null;
  reason: string | null;
  detail: string | null;
}

export interface StrategySummary {
  id: string;
  displayName: string;
  isEnabled: boolean;
  stats: StrategyStats;
}

export interface StrategyStats {
  totalTrades: number;
  winningTrades: number;
  totalPnL: number;
  profitFactor: number;
  winStreak: number;
  lossStreak: number;
  lastRegime: number;
  winRate: number;
}

export interface StrategyDetail {
  id: string;
  displayName: string;
  isEnabled: boolean;
  enabled: boolean;
  timeframe: string;
  symbols: string[];
  riskProfileId: string;
  parametersJson: string | null;
  positionManagementJson: string | null;
  orderEntryJson: string | null;
  regimeFilterJson: string | null;
  reentryJson: string | null;
}

export interface BarData {
  time: number;
  open: number;
  high: number;
  low: number;
  close: number;
}
