export interface RunSummary {
  runId: string;
  createdAtUtc?: string;
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
  grossPnL: number;
  commissionTotal: number;
  swapTotal: number;
  errorMessage: string | null;
  parentRunId?: string | null;
  datasetId?: string | null;
  configSetId?: string | null;
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
  parentRunId?: string | null;
  datasetId?: string | null;
  configSetId?: string | null;
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
  timeframe?: string;
  // @deprecated Use stopLoss/takeProfit instead (iter-38 W-A1). These are kept for backward compat only.
  slPrice?: number;
  tpPrice?: number;
  // iter-38 W-A1: the trade-detail API returns stopLoss/takeProfit directly (TradeDetailResponse DTO).
  stopLoss?: number;
  takeProfit?: number | null;
}

// iter-36 K5: the journal is now the lossless StepRecord stream (GET /api/runs/{id}/journal). eventKind
// is the StepRecord event type; decisionReason is the gate accept/reject reason. Legacy fields kept
// optional so older views compile during the iter-37 journal-surface migration.
export interface RiskSnapshot {
  balance: number;
  equity: number;
  floatingPnL: number;
  dailyDrawdown: number;
  maxDrawdown: number;
  weeklyDrawdown: number;
  monthlyDrawdown: number;
  inProtectionMode: boolean;
  protectionCause: string | null;
  governorState: string;
  openPositions: number;
}

export interface StrategyVerdict {
  strategyId: string;
  hadEnoughBars: boolean;
  signalFired: boolean;
  direction: string | null;
  reason: string;
  indicators: Record<string, number> | null;
}

export interface JournalEntry {
  seq: number;
  simTimeUtc: string;
  eventKind?: string | null;
  eventJson?: string | null;
  effectKinds?: string[] | null;
  effectsJson?: string | null;
  risk?: RiskSnapshot | null;
  strategyVerdicts?: StrategyVerdict[] | null;
  decisionReason?: string | null;
  regime?: string | null;
  kind?: string | null;
  symbol?: string | null;
  strategyId?: string | null;
  reason?: string | null;
  detail?: string | null;
}

// iter-37 F2 — per-strategy decision funnel (GET /api/runs/{id}/analytics/strategies), built from the
// StepRecord journal's per-bar verdicts.
export interface StrategyPerformance {
  strategyId: string;
  totalBarsEvaluated: number;
  signalsFired: number;
  tradesOpened: number;
  wins: number;
  losses: number;
  winRatePct: number;
  topRejections: { reason: string; count: number }[];
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
  strategyOverrides?: Record<string, Record<string, unknown>>;
  usePackId?: string;
  disableRegime?: boolean;
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
  allowHedging?: boolean;
  maxSlPips?: number;
  maxExposurePercent?: number;
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

export interface StrategySummary {
  id: string;
  displayName: string;
  isEnabled: boolean;
  createdAtUtc?: string;
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

// iter-38 S7 NG-R4: interfaces previously missing, now added so typed services can drop 'any'.
export interface PropFirmRule {
  id: string;
  displayName: string;
  drawdownType: string;
  maxDailyLossPercent: number;
  maxTotalLossPercent: number;
  profitTargetPercent: number;
  minTradingDays: number;
  forceCloseOnBreach: boolean;
  allowTradesDuringNews: boolean;
  allowWeekendHolding: boolean;
}

export interface GovernorOptions {
  enabled: boolean;
  coolingOffBars?: number;
  coolingOffConsecutiveLosses?: number;
  profitLockDayFraction?: number;
  profitLockEnabled?: boolean;
  softStopDailyDdFraction?: number;
  hardStopDailyDdFraction?: number;
  sizeMultiplier?: number;
  lossBandFractions?: string;
  lossBandMultipliers?: string;
}

// iter-38 S10 U1: add-on pack types matching the backend AddOnPack record + auto-tune preview.
export interface AddOnPack {
  id: string;
  name: string;
  description: string | null;
  addOns: PositionManagementOptions;
  regimeDetectionEnabled: boolean;
  createdAtUtc?: string;
}

export interface PositionManagementOptions {
  stopLoss?: { method: string; atrMultiple?: number };
  takeProfit?: { method: string; rrMultiple?: number };
  breakeven?: { enabled: boolean; triggerRMultiple?: number; offsetPips?: number };
  trailing?: { method: string; atrMultiple?: number; stepPips?: number };
  partialTp?: { enabled: boolean; fraction?: number; offsetPips?: number };
  ride?: { enabled: boolean; adxFloor?: number; relaxedAtrMultiple?: number };
  dynamicSlTp?: { enabled: boolean; atrMultiple?: number; rrMultiple?: number };
}

export interface AutoTunePreview {
  atrMultiple?: number;
  rrMultiple?: number;
  triggerR?: number;
  offsetPips?: number;
  adxFloor?: number;
  relaxedAtrMultiple?: number;
}
