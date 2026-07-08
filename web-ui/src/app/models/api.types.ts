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
  venue?: string | null;
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
  // iter-strategy-system P2 (D5): persisted run selection.
  runPlanJson?: string;
  venue?: string | null;
  riskProfileId?: string | null;
  governorEnabled?: boolean;
  regimeEnabled?: boolean;
  commissionPerMillion?: number;
  spreadPips?: number;
  wallElapsedMs: number;
  barsPerSec: number;
  totalBars: number;
  exitResolution?: string | null;
  // P4.1 (F11): whether this run used the exploration preset.
  explorationMode?: boolean;
  // P4.1 (F11): whether excursion paths were recorded.
  recordExcursions?: boolean;
}

export interface TradeListResponse {
  totalCount: number;
  trades: TradeSummary[];
}

export interface TradeSummary {
  id: string;
  positionId: string;
  orderId: string;
  runId?: string | null;
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
  entryType?: string | null;
  stopLoss?: number;
  takeProfit?: number | null;
}

// iter-38 A4 / W-A1 + W-D3: the trade-detail endpoint (/api/trades/{id}) returns TradeDetailResponse — a richer,
// distinct shape from the list's TradeSummary. This dedicated interface ends the "TradeSummary used for the
// detail view" type-lie: stopLoss is REQUIRED (the chart SL marker depends on it), takeProfit is nullable, and
// the field set matches the backend DTO exactly. Keep this in sync with Dtos/Trades/TradeDetailResponse.cs.
export interface TradeDetail {
  id: string;
  positionId: string;
  orderId: string;
  symbol: string;
  direction: string;
  lots: number;
  entryPrice: number;
  exitPrice: number;
  stopLoss: number;
  takeProfit: number | null;
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
  timeframe: string;
  entryReason?: string;
  entryRegime?: string;
  entrySnapshotJson?: string;
  exitDetailJson?: string;
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

// iter-strategy-system P1 (D3): a builder row = strategy × symbol × timeframe × add-on pack.
export interface RunRow {
  strategyId: string;
  symbol: string;
  timeframe: string;
  packId?: string;
  enabled?: boolean;
}

export interface StartRunRequest {
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
  perStrategyPackIds?: Record<string, string>;
  // iter-strategy-system P1: when present, rows supersede the symbols×periods×strategies cross-product.
  rows?: RunRow[];
  // Run-level governor toggle (D4). Default true.
  governorEnabled?: boolean;
  // Run-level protection toggles (P5). Default true = ruleset defaults apply.
  dailyDdEnabled?: boolean;
  maxDdEnabled?: boolean;
  forceCloseOnBreachEnabled?: boolean;
  // iter-redesign P3.2: strip all add-ons (breakeven/trailing/partial/ride/dynamic) -> baseline SL/TP only.
  stripAddOns?: boolean;
  // Tape replay playback speed: 0 = paused, 0.1–10 = multiplier. Default 10.
  speed?: number;
  honestFills?: boolean;
  // P3.2: record per-trade MAE/MFE excursion paths (tape-only, opt-in).
  recordExcursions?: boolean;
  // P3.2: one-click exploration preset — SL=ATR×4, TP=none, add-ons OFF, governor OFF.
  explorationMode?: boolean;
  /** P6: run tape + cTrader side-by-side with same config for reconciliation. Tape-only (venue must be 'tape'). */
  compareBoth?: boolean;
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
  maxExposurePerCurrencyPercent?: number;
  drawdownScaleThreshold?: number;
  drawdownScaleFloor?: number;
  fixedLots?: number;
  fixedDollarRisk?: number;
  kellyFraction?: number;
  antiMartingaleMultiplier?: number;
  antiMartingaleMaxSteps?: number;
  sizeModifiers?: Record<string, unknown>;
}

export type RiskProfileEdit = RiskProfile;

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
  entryRule?: string | null;
  exitFormula?: string | null;
  // P2.5: falsifiable-hypothesis metadata.
  thesis?: string | null;
  expectedTradesPerWeek?: number | null;
  expectedHoldBars?: number | null;
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
  riskProfileId: string;
  parametersJson: string | null;
  positionManagementJson: string | null;
  orderEntryJson: string | null;
  regimeFilterJson: string | null;
  reentryJson?: string | null;
  // P2.5: falsifiable-hypothesis metadata.
  thesis?: string | null;
  expectedTradesPerWeek?: number | null;
  expectedHoldBars?: number | null;
}

export interface BarData {
  time: number;
  open: number;
  high: number;
  low: number;
  close: number;
}

// iter-redesign P6.2: trade-detail chart payload (GET /api/trades/{id}/chart).
export interface ChartMarker {
  time: number; // unix seconds
  price: number;
  kind: string; // Entry | Exit | StopLoss | TakeProfit
}

export interface TradeChartResponse {
  tradeId: string;
  symbol: string;
  timeframe: string;
  direction: string;
  bars: BarData[];
  markers: ChartMarker[];
}

// iter-redesign P5: per-bar decision narrative (GET /api/runs/{id}/bars).
export interface BarStrategyVerdict {
  strategyId: string;
  signalFired: boolean;
  direction?: string | null;
  reason: string;
}

export interface BarRiskSnapshot {
  equity: number;
  balance: number;
  dailyDrawdown: number;
  maxDrawdown: number;
  openPositions: number;
  inProtectionMode: boolean;
  governorState: string;
}

export interface BarNarrative {
  simTimeUtc: string;
  firstSeq: number;
  eventCount: number;
  regime?: string | null;
  verdicts: BarStrategyVerdict[];
  proposalCount: number;
  gateRejections: string[];
  risk?: BarRiskSnapshot | null;
  fillCount: number;
  closeCount: number;
  rejectionCount: number;
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
  dailyDdBase?: string;
  maxWeeklyLossPercent?: number;
  maxMonthlyLossPercent?: number;
  dailyResetTimeUtc?: string;
  dailyResetTimezone?: string;
  newsImpactFilter?: string;
  newsWindowMinutesBefore?: number;
  newsWindowMinutesAfter?: number;
  weekendCloseUtc?: string;
  weekendNoOpenUtc?: string;
  protectionResetPolicy?: string;
  requireProfitTarget?: boolean;
  equityDefinition?: string;
  gracePeriod?: Record<string, unknown>;
  toggles?: Record<string, unknown>;
}

export type PropFirmRuleEdit = PropFirmRule;

export interface GovernorOptions {
  enabled: boolean;
  coolingOffBars?: number;
  coolingOffConsecutiveLosses?: number;
  profitLockDayFraction?: number;
  profitLockEnabled?: boolean;
  profitLockFraction?: number;
  softStopDailyDdFraction?: number;
  hardStopDailyDdFraction?: number;
  sizeMultiplier?: number;
  streakReduceAt?: number;
  streakMultiplier?: number;
  streakPauseAt?: number;
  lossBandFractions?: number[];
  lossBandMultipliers?: number[];
}

export interface GovernorOptionsEdit {
  enabled: boolean;
  coolingOffBars?: number;
  coolingOffConsecutiveLosses?: number;
  profitLockDayFraction?: number;
  profitLockEnabled?: boolean;
  profitLockFraction?: number;
  softStopDailyDdFraction?: number;
  hardStopDailyDdFraction?: number;
  sizeMultiplier?: number;
  streakReduceAt?: number;
  streakMultiplier?: number;
  streakPauseAt?: number;
  lossBandFractions: string;
  lossBandMultipliers: string;
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

export interface SystemInfo {
  version: string;
  branch: string;
  buildDate: string;
  dataPaths: { tradingDb: string };
  activeRuns: number;
  runningRuns: number;
  marketDataAvailable: boolean;
}

export interface ResetRequest {
  scope: 'runs' | 'config' | 'marketdata' | 'all';
  confirm: string;
}

export interface ResetResponse {
  scope: string;
  status: string;
}

export interface InventoryItem {
  symbol: string;
  timeframe: string;
  source: string;
  firstBar: string;
  lastBar: string;
  barCount: number;
}

export interface NarrativeEvent {
  seq: number;
  simTime: string;
  severity: string;
  category: string;
  headline: string;
  detail: string;
}

export interface NarrativeResponse {
  events: NarrativeEvent[];
  latestSeq: number;
  hasMore: boolean;
}

// P3.5 — exit-lab types
export interface TradeExcursionResponse {
  tradeId: string;
  pathJson: string;
}

export interface ExitRule {
  slAtrMultiple: number;
  tpRrMultiple: number | null;
  beTriggerR: number | null;
  beOffsetPips: number | null;
  trailAtrMultiple: number | null;
  partialTriggerR: number | null;
  partialCloseFraction: number | null;
  referenceAtrPips: number;
}

export interface ExitLabCellResponse {
  rule: ExitRule;
  tradeCount: number;
  winRate: number;
  avgR: number;
  medianR: number;
  avgHoldBars: number;
  maxDdContributionR: number;
  tradeRValues: number[];
  passProbability: number;
  isPlateauCenter: boolean;
}

export interface ExitLabEvaluateResponse {
  totalTrades: number;
  totalCells: number;
  malformedPathCount: number;
  cells: ExitLabCellResponse[];
  defaultSlMultiples: number[];
  defaultTpMultiples: (number | null)[];
}

export interface ExitLabEvaluateRequest {
  runIds: string[];
  positionIds: string[];
  referenceAtrPips: number;
  slMultiples?: number[];
  tpMultiples?: (number | null)[];
  beTriggers?: (number | null)[];
  trailMultiples?: (number | null)[];
}

export interface SaveCalibrationRequest {
  strategyId: string;
  symbol: string;
  entryTimeframe: string;
  regime?: string | null;
  rule: ExitRule;
  datasetId: string;
  isStartUtc: string;
  isEndUtc: string;
  oosStartUtc?: string | null;
  oosEndUtc?: string | null;
}

export interface PipelineSummary {
  id: string;
  name: string;
  status: string;
  currentStepIndex: number;
  startedAtUtc: string;
  completedAtUtc: string | null;
  stepCount: number;
}

export interface PipelineDetail {
  id: string;
  name: string;
  status: string;
  currentStepIndex: number;
  playbookJson: string;
  artifactDir: string | null;
  startedAtUtc: string;
  completedAtUtc: string | null;
  steps: PipelineStep[];
}

export interface PipelineStep {
  stepIndex: number;
  kind: string;
  status: string;
  paramHash: string;
  verdictJson: string | null;
  artifactPath: string | null;
  startedAtUtc: string | null;
  completedAtUtc: string | null;
}

export interface PassProbabilityEstimate {
  probabilityOfPass: number;
  probabilityOfDailyBreach: number;
  probabilityOfMaxBreach: number;
  expectedDaysToTarget: number;
  projectedFinalEquity: number;
  recommendation: string;
}
