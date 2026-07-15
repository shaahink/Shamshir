# Shamshir — UI Flows & Visual UX Description

> Written as if seeing each screen. Describes what fields are displayed,
> where data comes from, and when replay vs cTrader is used.
> For LLM analysis — no pixel coordinates, just spatial layout and data lineage.

---

## Navigation

The app uses a dark-themed sidebar navigation with these entries:
- **Dashboard** (`/`)
- **Backtest Runs** (`/runs`)
- **Trades** (`/trades`)
- **Strategies** (`/strategies`)
- **Add-on Packs** (`/addon-packs`)
- **Risk Profiles** (`/risk-profiles`)
- **FTMO Rules** (`/prop-firm-rules`)
- **Governor** (`/governor`)
- **API Docs** (`/scalar/v1`)

---

## PAGE: Backtest Runs List (`/runs`)

### VISUAL: A page showing a table of all completed and in-progress backtest runs.

**Top bar:**
- Title: "Backtest Runs"
- "+ New Backtest" button (right side, opens New Backtest form)
- "Compare (N)" button (appears when 2+ rows are checkbox-selected)

**Comparison panel** (slides open when "Compare" clicked):
A compact table comparing selected runs side by side:

| Run | Net P/L | Max DD% | Trades | Win Rate % |
|-----|--------|---------|--------|------------|
| `abc12` | `+150.23` (green) | `4.2%` (red) | 12 | 58.3% |
| `def34` | `-80.50` (red) | `9.8%` (red) | 8 | 37.5% |

**Main runs table:**

| # | Run ID | Status | Symbol | Net P/L | Max DD | Trades | Win Rate | Created |
|---|--------|--------|--------|---------|--------|--------|----------|---------|
| ☐ | `abc12` | ✓ success | EURUSD, GBPUSD | `+150.23` | 4.2% | 12 | 58.3% | 06-18 14:22 |
| ☐ | `def34` | ✓ success | EURUSD | `-80.50` | 9.8% | 8 | 37.5% | 06-17 09:15 |
| ☐ | `ghi56` | ⚠ running | EURUSD | — | — | — | — | 06-18 14:30 |

Fields displayed:
- **Run ID**: first 8 characters, monospace font
- **Status**: colored badge — green `✓ success` (completed), red `✗ error` (failed), amber `⚠ running`
- **Symbol**: instrument names, parsed from JSON `symbols` array
- **Net P/L**: currency amount, green if positive, red if negative, 2 decimal places
- **Max DD**: percentage with "%" suffix, red text
- **Trades**: integer count
- **Win Rate**: percentage, 1 decimal place
- **Created**: date/time formatted `MM-dd HH:mm`

**Data source:** `GET /api/runs` → array of `RunSummary` objects
- `runId` → "Run ID" column
- `status` → "Status" badge
- `symbols` (JSON) → "Symbol" column
- `netProfit` → "Net P/L" (color-coded)
- `maxDrawdownPct` × 100 → "Max DD %"
- `totalTrades` → "Trades"
- `winRatePct` × 100 → "Win Rate %"
- `createdAtUtc` → "Created"

Row click navigates to the Run Report page (`/runs/:runId`).
Checkbox toggles comparison without navigation.

**States:**
- Loading: spinner centered on page
- Error: red box with error message
- Empty: "No backtest runs yet" with link to New Backtest

---

## PAGE: New Backtest (`/runs/new`)

### VISUAL: A single-page form with sections stacked vertically (max-width container, centered).

**Header:** "New Backtest"

### Section 1: Symbols
A row of toggle chips. Each chip shows the instrument name. Click to select/deselect.
```
[EURUSD ✓] [GBPUSD ✓] [USDJPY] [GBPJPY] [XAUUSD] [AUDUSD] [USDCHF] [USDCAD] [NZDUSD] [EURGBP] [EURJPY] [XAGUSD]
```
Default: EURUSD selected.
**Data source:** Hardcoded list of 12 symbols.

### Section 2: Timeframes
A row of toggle chips. Each chip shows the timeframe code.
```
[1m] [5m] [15m] [30m] [1h ✓] [4h] [1d]
```
Default: h1 selected.
**Data source:** Hardcoded list of 6 timeframes.

### Section 3: Date Range
Three quick-select buttons in a row:
```
[Last Month ✓]  [Last Quarter]  [Last Year]
```
Below them, two date input fields side by side:
```
Start: [2026-05-23]    End: [2026-06-23]
```
Default: Last Month (30 days back from today).
**Data source:** Client-side date calculation.

### Section 4: Configuration — 4-field grid (2×2)

| Field | Label | Input Type | Default |
|-------|-------|-----------|---------|
| Initial Balance | "Initial Balance" | Number | 100,000 |
| Commission | "Commission per M" | Number | 30 |
| Spread | "Spread in pips" | Number, step 0.1 | 1 |
| Risk Profile | "Risk Profile" | Dropdown | First profile |

**Risk Profile dropdown data source:** `GET /api/risk-profiles` → list of profiles with `id` and `displayName`

### Section 5: Data Venue
Dropdown selector with these options:
```
[Default (replay) ✓]  ← uses BacktestReplayAdapter, credential-free, deterministic
[Replay (stored bars)]  ← same as default
[cTrader (live stream)]  ← uses NetMQBrokerAdapter, requires credentials, non-deterministic
```
**IMPORTANT: When "Default" or "Replay" is selected:** The backtest reads bars from the SQLite `Bars` table. No credentials needed. Fill at bar close. Cost-aware. Fully deterministic.
**IMPORTANT: When "cTrader" is selected:** The backtest launches `ctrader-cli.exe` as a subprocess. Requires `CTrader:CtId`, `CTrader:PwdFile`, `CTrader:Account` set in `appsettings.Development.json` or environment variables. Uses NetMQ lock-step protocol. Non-deterministic (cTrader backtester). Fills and bars come from cTrader servers.

**Data source:** Hardcoded options. Venue maps to `StartRunRequest.venue`:
- `""` or `"replay"` → BacktestReplayAdapter (replay)
- `"ctrader"` → cTrader path

### Section 6: Add-on Pack & Regime
Two controls side by side:
```
Add-on Pack: [None ✓]            ☐ Disable Regime Detection
```
**Add-on Pack dropdown data source:** `GET /api/addon-packs` → packs with `id` and `name`
Options: None, breakeven-only, scalp-tight, runner-aggressive
**When a pack is selected:** Its add-ons replace the strategy's add-ons for this run.

### Section 7: Strategies
A vertical list of strategy cards. Each card shows:
```
┌─────────────────────────────────────┐
│ ☑ Trend Breakout v1                 │
│   id: trend-breakout                │
│   Trades: 12 | Win: 8 | PnL: +150   │
└─────────────────────────────────────┘
```
All enabled strategies pre-selected by default.
If none are enabled, the first strategy is selected.

**Data source:** `GET /api/strategies` → strategy summaries with stats (`totalTrades`, `winningTrades`, `totalPnL`)

**Per-strategy overrides section** (appears below each selected strategy):
```
Add-on Pack: [None ▾]
JSON Override:
┌─────────────────────────┐
│ {                        │
│   "parameters": {        │
│     "lookbackBars": 15   │
│   }                      │
│ }                        │
└─────────────────────────┘
```
Users can enter JSON to override strategy parameters for this run.
The JSON is validated client-side — parse error shows error message near the textarea.

### Section 8: Resolved Config Preview
A summary grid showing the final merged configuration before submission:
```
┌──────────────┬─────────────────────────────────┐
│ Symbols      │ EURUSD, GBPUSD                   │
│ Timeframes   │ H1                              │
│ Date Range   │ 2026-05-23 → 2026-06-23          │
│ Balance      │ $100,000.00                      │
│ Commission   │ 30 per million                   │
│ Spread       │ 1 pip                            │
│ Risk Profile │ Standard (0.5% risk, 4% daily DD) │
│ Venue        │ Replay (BacktestReplayAdapter)    │
│ Pack         │ None                              │
│ Regime       │ Enabled                           │
│ Strategies   │ trend-breakout, ema-alignment      │
└──────────────┴─────────────────────────────────┘
```

### Section 9: Start Button
```
[  ▶ Start Backtest  ]
```
On click:
- Validates: ≥1 symbol, ≥1 timeframe, ≥1 strategy, valid date range (start < end), balance > 0
- Submits `POST /api/runs` with the full `StartRunRequest` payload
- Navigates to Live Monitor (`/runs/:runId/monitor`)

### Duplicate Mode
When navigated with `?sourceRunId=XXX`:
- Pre-fills all fields from `GET /api/runs/:id`
- Pre-selects the source run's symbols and timeframes
- Pre-fills dates, balance, commission, spread
- Pre-selects risk profile from source run
- Optionally pre-selects pack via `?usePackId=...`
- Optionally pre-checks regime disable via `?disableRegime=true`

---

## PAGE: Live Monitor (`/runs/:runId/monitor`)

### VISUAL: A real-time dashboard showing the backtest in progress.

**Header bar:**
```
Live Monitor — Run abc12def3
[Cancel Run] (red button)              [View Report]
```
- "Cancel Run" sends `DELETE /api/runs/:runId`
- "View Report" navigates to `/runs/:runId` (disabled until run completes)

**Breach banner** (red, full-width, appears only on breach):
```
⚠ Protection mode: Daily drawdown limit reached
```
Clears on recovery (when drawdown drops below 2%).

**Progress bar** (full-width, green fill):
```
████████████████░░░░░░░░░░░░░░░░░░░░░░  45.3%
Bars: 2,145 / 4,736               Speed: 42.5 bars/s
ETA: 1m 3s                         Elapsed: 50s
Sim time: 2024-03-15 14:00
```
Data from SignalR `RunProgress` event:
- `barsProcessed` / `barsTotal` → percentage + progress bar width
- `barsPerSec` → speed
- `etaSeconds` → ETA (formatted as mm:ss)
- Wall-clock timer → elapsed
- `simTimeUtc` → simulation time

**Stat tiles** (2 rows × 4 grid, 8 tiles):

| Tile | Value | Source Field |
|------|-------|-------------|
| Status | `running` (green) / `completed` / `failed` (red) | `RunProgress.status` |
| Equity | `$100,452.30` | `RunProgress.equity` |
| Balance | `$100,000.00` | `RunProgress.balance` |
| Open Positions | `2` | `RunProgress.openPositions` |
| Daily DD % | `1.2%` (amber if >2%, red if >4%) | `RunProgress.dailyDdPct × 100` |
| Max DD % | `0.8%` | `RunProgress.maxDdPct × 100` |
| Governor | `Normal` | `RunProgress.governorState` |
| Distance to Limit | `$19,548.00` (green) | `RunProgress.distanceToDailyLimit × 100` |

**Counter tiles** (1 row × 6 grid):

| Counter | Value | Source |
|---------|-------|--------|
| Signals | 98 | `RunProgress.counters.signals` |
| Orders | 17 | `RunProgress.counters.orders` |
| Fills | 34 | `RunProgress.counters.fills` |
| Closes | 17 | `RunProgress.counters.closes` |
| Rejections | 5 | `RunProgress.counters.rejections` |
| Breaches | 0 | `RunProgress.counters.breaches` |

**Live equity chart:**
- Height: 288px, full-width
- Two lines: green equity curve (solid, 2px) + blue balance line (dashed, 1px)
- Auto-scrolling: oldest points drop off as new ones arrive
- Max 500 points retained
- Time axis shows simulation time
- **No drawdown line** (drawdown shown in post-run report only)
- **Data source:** Accumulated from `RunProgress.equity` + `RunProgress.balance` on each progress event

**Live journal stream** (scrollable panel, height ~500px):
- Each entry shows: `██:██:██` (sim time) | kind badge | symbol | reason
- Example entries:
  ```
  14:02:15 SIGNAL  EURUSD Long — trend-breakout: Breakout above 20-bar high
  14:02:15 ORDER   EURUSD — Accepted, 0.12 lots, risk $50.00
  14:02:15 FILL    EURUSD — 0.12 lots @ 1.0850
  14:05:30 CLOSE   EURUSD — TP hit, +$32.50 (+$30.00 gross − $2.50 comm − $0.00 swap)
  14:08:00 SIGNAL  EURUSD Long — REJECTED: MAX_POSITIONS (3 open)
  ```
- **Stick-to-bottom behavior:** Auto-scrolls to latest only when user is near bottom.
  Otherwise shows "↓ jump to latest" button.
- Max 2,000 entries retained (trims to last 2,000)
- **Data sources (dual):**
  1. SignalR `RunProgress.recentJournal[]` — push (primary)
  2. REST polling `GET /api/runs/:runId/journal?afterSeq=N&limit=200` every 2s — pull (fallback)
- Deduplicates by `seq` field

**SignalR connection details:**
- Hub URL: `/hubs/run`
- Transport: WebSocket preferred, LongPolling fallback
- Events received: `RunProgress` (every bar), `RunCompleted` (once at end)
- Client sends: `JoinRun(runId)` on connect, `LeaveRun(runId)` on leave

---

## PAGE: Run Report (`/runs/:runId`)

### VISUAL: A detailed post-mortem report page with multiple sections stacked vertically.

**Header:**
```
Run abc12def3 — EURUSD, GBPUSD · H1 · 2024-01-01 → 2024-06-30 · $100,000 initial
```
Lineage info below (small text, only if present):
```
parent: parent-run-id  |  dataset: d7a8f9b1e2c3  |  config: a1b2c3d4e5f6
```

**Action buttons bar:**
```
[Duplicate] [↓ Journal NDJSON] [↓ CSV] [↓ Report JSON] [↓ Report MD] [Monitor] [Analyzer]
```
- **Duplicate:** Opens modal → select add-on pack + regime toggle → navigates to `/runs/new?sourceRunId=XXX&usePackId=...&disableRegime=...`
  (Duplicate from replay: re-runs with same bars, deterministic. Duplicate from cTrader: fresh non-deterministic re-run.)
- **Journal NDJSON:** Direct download from `GET /api/runs/{id}/journal/export` — newline-delimited JSON of every StepRecord
- **CSV:** Downloads trades CSV from `GET /api/export/trades.csv?runId=XXX`
- **Report JSON/MD:** Client-side assembled from `RunDetail` + trades

### Section: Summary Tiles (10 tiles, responsive grid)

| Tile | Value | Calculation / Source |
|------|-------|---------------------|
| Net P/L | `$1,250.00` | `runDetail.netProfit` (green if +, red if −) |
| Return % | `1.25%` | `(netProfit / initialBalance) × 100` |
| Max DD | `4.2%` | `runDetail.maxDrawdownPct × 100` |
| Profit Factor | `1.85` | `Σ|wins| / Σ|losses|` (from trades list) |
| Win Rate | `58.3%` | `runDetail.winRatePct × 100` |
| Trades | `24` | `runDetail.totalTrades` |
| Gross P/L | `$1,580.00` | `runDetail.grossPnL` or summed from trades |
| Commission | `−$250.00` | `runDetail.commissionTotal` or summed from trades |
| Swap | `−$80.00` | `runDetail.swapTotal` or summed from trades |
| Avg R | `0.45` | Average of all `trade.rMultiple` |

**Reconciliation badges** (3 small badges below tiles):
```
✓ Net = Σ trades      ✓ Closes = Trades      ✓ Gross - Comm - Swap = Net
```
Client-side verification. Shows green checkmark or red "MISMATCH" text.
Replay mode: these should always match (deterministic, no data loss).
cTrader mode: may show mismatches due to cBot timing / incomplete drain.

### Section: Equity & Drawdown Chart
- Full-width, ~384px height
- Three-lined chart using lightweight-charts:
  - **Green solid line:** Equity curve (value from `EquityPoint.equity`)
  - **Blue dashed line:** Balance line (optional, from `EquityPoint.balance`)
  - **Red histogram (inverted):** Drawdown %, client-side computed as `((value - peak) / peak) × 100`
- Time scale shows date labels
- **Data source:** `GET /api/runs/{id}/equity` → `{ timestampUtc, equity, balance }[]`
- **For replay:** Equity points are flushed from `BufferedEquitySink` on run completion
- **For cTrader:** Equity points come from the venue's AccountStream during the run

### Section: Daily PnL Timeline
- Horizontal bar chart, each bar = one day
- Green bars = profitable day, red bars = losing day
- Tooltip on hover shows date + PnL amount
- **Data source:** `GET /api/runs/{id}/daily-pnl` → `{ date, pnl }[]`

### Section: Per-Strategy Funnel
A table showing strategy-level funnel statistics:

| Strategy | Bars | Signals | Trades | Win% | Top No-Signal Reasons |
|----------|------|---------|--------|------|-----------------------|
| trend-breakout | 4,736 | 15 | 8 | 62.5% | Not enough bars (1,200), Range-bound (800), SL too wide (150) |
| ema-alignment | 4,736 | 12 | 6 | 50.0% | Not enough bars (1,200), Wrong regime (600) |

Below the table: a **rejection reason histogram** per strategy.
Horizontal bars showing frequency of each rejection reason.
Green bars = most common reasons.

**Data source:** `GET /api/runs/{id}/analytics/strategies` → `StrategyPerformance[]`
- `totalBarsEvaluated` → Bars
- `signalsFired` → Signals
- `tradesOpened` → Trades
- `winRatePct` → Win%
- `topRejections[]` → "Top No-Signal Reasons" + histogram

### Section: MAE vs MFE Scatter Chart
- Scatter plot, each point = one trade
- X-axis: MAE (max adverse excursion in pips, orange)
- Y-axis: MFE (max favorable excursion in pips, green)
- Points in quadrants: top-right = good (small MAE, large MFE), bottom-left = bad
- **Data source:** Derived from trades list — `trade.maxAdverseExcursion` and `trade.maxFavorableExcursion`

### Section: Trades Table
Full table with color-coded cells. Clickable rows → navigates to /trades/:id.

| Sym | Dir | Lots | Entry | Exit | Type | SL | TP | Gross | Comm | Swap | Net | Pips | R | MAE | MFE | Exit | Strategy | Hold |
|-----|-----|------|-------|------|------|----|----|-------|------|------|-----|------|----|-----|-----|------|----------|------|
| EURUSD | Long | 0.12 | 1.0850 | 1.0885 | market | 1.0820 | 1.0920 | +$42.00 | −$2.50 | −$0.50 | +$39.00 | +35.0 | 0.8 | −10.5 | +38.2 | TP | trend-breakout | 3h 12m |

Column details:
- **Gross, Comm, Swap, Net:** Currency format, 2dp. Green if positive, red if negative.
- **Pips:** 1 decimal place
- **R:** R-multiple, 1 decimal place
- **MAE, MFE:** Pips, 1 decimal place
- **Hold:** Formatted as `Xh Xm` (from `durationSeconds`)

**Data source:** `GET /api/runs/{id}/trades` → `TradeSummary[]`
- For replay: 18 columns all populated
- For cTrader: Some cost columns may show 0 if cBot doesn't itemize costs (known gap 31-A2)

### Section: Journal
Filter bar with kind badges:
```
[All] [SIGNAL] [ORDER] [FILL] [CLOSE] [REJECTED] [CANCELLED] [TRAIL] [BREAKEVEN] [PARTIAL] [ADDON]
```
Below: scrollable journal entries list (max height ~800px).

Each journal entry shows:
```
14:02:15  [FILL] EURUSD  trend-breakout  0.12 lots filled @ 1.0850
```
Where the [FILL] badge is green and the order is **joined** with its corresponding ORDER entry:
```
14:02:15  ORDER → FILL  EURUSD  trend-breakout  0.12 lots @ 1.0850  SL=1.0820 TP=1.0920
```
The UI merges `OrderProposed` entries with their subsequent `OrderFilled`/`OrderCancelled`/`OrderRejected` outcomes by matching `orderId` from the `eventJson` field.

**Data source:** `GET /api/runs/{id}/journal?limit=200`

### Section: Per-Bar "Why" Table
Scrollable table (max height ~500px) showing every bar's evaluation result:
```
| Sim Time | Strategy | Signal | Reason |
|----------|----------|--------|--------|
| 01-01 14:00 | trend-breakout | — | Not enough bars (have 1, need 55) |
| 01-01 15:00 | trend-breakout | — | Not enough bars (have 2, need 55) |
| ... |
| 01-02 10:00 | trend-breakout | Long | Signal: breakout | Accepted | 0.12 lots |
| 01-02 11:00 | trend-breakout | — | REJECTED: MAX_POSITIONS |
```

**Data source:** `GET /api/runs/{id}/bar-decisions` → `JournalEntry[]` (BarClosed only, with strategyVerdicts)

---

## PAGE: Run Analyzer (`/runs/:runId/analyzer`)

### VISUAL: A 2×2 grid of histogram charts + 1 scatter chart.

| Chart | Color | Data | Source |
|-------|-------|------|--------|
| R-Multiple Distribution | Green | 20-bin histogram of all trade R-multiples | `analytics.rMultiples[]` |
| Holding Time | Blue | 20-bin histogram of trade duration in seconds | `analytics.holdingTimes[]` |
| PnL by Hour (UTC) | Yellow | 20-bin histogram of PnL by hour of day | `analytics.pnlByHour[]` |
| PnL by Day of Week | Purple | Histogram of PnL by weekday | `analytics.pnlByDay[]` |
| MAE vs MFE | Pink | Scatter plot (same as report section) | `analytics.maeMfe[]` |

**Data source:** `GET /api/runs/{id}/analytics` → `RunAnalytics`

---

## PAGE: Strategy Detail (`/strategies/:id`)

### VISUAL: A read-only detail page with per-field edit capability.

**Header:** Strategy display name + ID

**Sections:**

1. **Identity:** displayName (editable text), id (read-only), enabled (toggle switch)

2. **Risk Profile:** dropdown to select from available risk profiles
   - **Data source:** `GET /api/risk-profiles`

3. **Regime Filter:** 5 checkboxes:
   ```
   ☑ Allow Trending    ☑ Allow Ranging    ☑ Allow High Volatility
   ☑ Allow Low Vol     ☑ Allow Unknown
   ```
   Changing these updates the `regimeFilterJson` field.

4. **Order Entry:** dropdown for entry method
   ```
   Method: [Market ▾]
   Max Slippage Pips: [2.0]
   ```
   Options: Market, LimitOffset, MarketWithSlippage.
   When LimitOffset selected: shows `Limit Offset Pips` and `Limit Order Expiry Bars` fields.

5. **Position Management:** 6 sub-sections
   - **Stop Loss (mandatory baseline):** Method dropdown (AtrMultiple/FixedPips/SwingBased), ATR Multiple, Max Pips
   - **Take Profit (mandatory baseline):** Method dropdown (RrMultiple/FixedPips/AtrMultiple), RR Multiple
   - **Breakeven:** Enabled toggle, Trigger R-Multiple, Offset Pips
   - **Trailing:** Enabled toggle, Mode (Auto/Custom), Method, ATR Multiple, Step Pips, Activate After Breakeven
   - **Ride:** Enabled toggle, ADX Floor, Relaxed ATR Multiple
   - **Dynamic SL/TP:** Enabled toggle, ATR Multiple for SL, RR Multiple for TP
   - **Partial TP:** Enabled toggle, Trigger R-Multiple, Close Fraction

6. **Parameters:** Per-strategy dynamic parameters as editable fields
   - e.g. for Trend Breakout: `lookbackBars=20`, `maPeriod=50`, `atrPeriod=14`
   - These are typed per strategy — textareas for JSON override at form level

7. **Reentry:**
   - Block While Same Direction Open (toggle)
   - Cooldown Bars After SL (number)
   - Cooldown Bars After TP (number)
   - Cooldown Bars After Entry (number)

**Data source:** `GET /api/strategies/{id}` → `StrategyDetail`
Changes saved via `PUT /api/strategies/{id}/config` (patches modified JSON fields)

---

## PAGE: Add-on Pack List (`/addon-packs`)

### VISUAL: Card grid showing all defined packs.

Each card:
```
┌──────────────────────────────────┐
│ Breakeven Only                    │
│ id: breakeven-only                │
│ Regime: enabled                   │
│ Created: 2026-06-21               │
│ [Edit] [Delete]                   │
└──────────────────────────────────┘
```

"New Pack" button creates a pack with default values (trailing AtrMultiple=2.5, breakeven disabled).

**Data source:** `GET /api/addon-packs` → `AddOnPack[]`

### Add-on Pack Detail (`/addon-packs/:id`)

**Sections:** Name, Description, Regime Detection Enabled toggle

**Add-on Values section:**
Same structure as strategy Position Management but with the note:
"These values are used as defaults. Enable Auto mode on a strategy to auto-tune per symbol/timeframe."

Each add-on has Mode (Auto/Custom) and numeric fields.
**Auto mode means:** the numeric values shown are ignored — `AddOnAutoTuner` computes them at entry.
**Custom mode means:** the stored numeric values are used verbatim.

**Preview button:** opens `GET /api/addons/preview?tf=H1&atrPips=20&spreadPips=1` → shows auto-tuned numbers for that context.

---

## PAGE: Risk Profiles (`/risk-profiles`)

### List view: Table of profiles

| Display Name | Risk/Trade | Daily DD | Total DD | Max Positions | Actions |
|---|---|---|---|---|---|
| Conservative | 0.25% | 3% | 6% | 2 | [Edit] [Duplicate] [Delete] |
| Standard | 0.50% | 4% | 8% | 3 | [Edit] [Duplicate] [Delete] |
| Aggressive | 2.00% | 5% | 10% | 5 | [Edit] [Duplicate] [Delete] |

### Detail view: Full form with all RiskProfile fields

**Data source:** `GET /api/risk-profiles` for list, `GET /api/risk-profiles/{id}` for detail
Saved via `PUT /api/risk-profiles/{id}`

---

## PAGE: FTMO Rules (`/prop-firm-rules`)

### List view: Table of rulesets

| Display Name | Daily Loss | Total Loss | Profit Target | Actions |
|---|---|---|---|---|
| FTMO Standard | 5% | 10% | 10% | [Edit] [Duplicate] [Delete] |
| FTMO Aggressive | 8% | 15% | 20% | [Edit] [Duplicate] [Delete] |

### Detail view: Full form with all PropFirmRuleSet fields
Including: DrawdownType, DailyResetTime, DailyResetTimezone, NewsWindow, WeekendRestriction, ProtectionResetPolicy, ForceCloseOnBreach, 9 ProtectionToggles

**Data source:** `GET /api/prop-firm-rules` / `GET /api/prop-firm-rules/{id}`

---

## PAGE: Governor (`/governor`)

Single form page with GovernorOptions fields:

**Section: General**
- `Enabled`: toggle
- `Cooling Off Bars`: number (default 24)
- `Profit Lock`: toggle + fraction (default 0.6)

**Section: Loss Bands**
- Band 1: fraction (0.4) → multiplier (0.5)
- Band 2: fraction (0.6) → multiplier (0.0)

**Section: Streak Control**
- Reduce at: 3 consecutive losses
- Multiplier: 0.5
- Pause at: 5 consecutive losses

**Data source:** `GET /api/governor-options` → `PUT /api/governor-options`

**IMPORTANT:** The governor `Enabled` toggle here must be ANDed with the prop-firm ruleset's `GovernorEnabled` toggle in `ProtectionToggles`. Both must be ON for the governor to block trades at the `PreTradeGate`.

---

## Data Flow Summary — Replay vs cTrader

### When Venue = Replay (BacktestReplayAdapter)

```
User clicks Start → POST /api/runs (venue="" or "replay")
  → BacktestOrchestrator.RunEngineReplayAsync()
  → Creates inner IHost → registers BacktestReplayAdapter
  → EngineRunner.RunAsync()
    → BacktestReplayAdapter.ConnectAsync() — loads bars from SQLite Bars table
    → KernelBacktestLoop.RunFromBrokerAsync()
      → For each bar from venue:
        → BarEvaluator: strategies evaluate, indicators computed
        → Kernel: PreTradeGate gates, KernelSizing sizes
        → EffectExecutor: SubmitOrder → BacktestReplayAdapter.SubmitOrderAsync
          → Fills at bar close (market) or when price reaches limit (limit)
          → TradeCostCalculator stamps Gross/Commission/Swap/Net
        → EquityObserved: AccountUpdate from venue
        → ChannelJournalWriter: StepRecord journaled
    → On completion: equity flushed to EquitySnapshots

Journal: StepRecord stream (lossless Wait channel)
Equity chart: from flushed BufferedEquitySink → EquitySnapshots
Trades: from TradeResultEntity in Trades table
Costs: full itemization (Gross, Commission, Swap, Net)
Fills: bar-close (market) or limit-price-reached
Determinism: byte-identical across runs (same DatasetId + ConfigSetId)
Credentials: none required
```

### When Venue = cTrader (NetMQBrokerAdapter)

```
User clicks Start → POST /api/runs (venue="ctrader")
  → BacktestOrchestrator.Start()
  → Reads CTrader:UseForBacktest=true from config
  → Launches ctrader-cli.exe as subprocess
    → cBot connects via NetMQ DEALER/ROUTER (ports 15555/15556)
    → Lock-step protocol:
      1. cBot → hello (symbols, periods, barsLoaded)
      2. engine → hello_ack
      3. cBot → bar (OHLCV, account, simTime)
      4. engine → bar_done (commands: submit_order, close_position, modify_sl)
      5. cBot executes commands at sim-time, returns exec frames
      6. Repeat for each bar
      7. cBot → stats, engine → shutdown

Journal: same StepRecord stream
Equity chart: from cBot AccountUpdate frames (via KernelFeedback)
Trades: from ExecutionEvent frames mapped to TradeResult (via EffectExecutor)
Costs: from cBot commission/swap fields in exec frames
  → NOTE: cBot may not itemize costs (known gap 31-A2)
Fills: from cTrader backtester engine (tick-based, non-deterministic)
Determinism: NO — cTrader backtester may produce different fills
Credentials: CTrader:CtId, CTrader:PwdFile, CTrader:Account required
```

---

## Key UI Patterns

### Color coding for PnL
- **Profit (positive):** Green text/dark green background
- **Loss (negative):** Red text/dark red background
- **Neutral/zero:** White/gray

### Column formatting in tables
- **Currency** (Gross, Comm, Swap, Net): 2 decimal places, $ prefix
- **Pips**: 1 decimal place
- **R-multiple**: 1 decimal place
- **Percentage** (Win Rate, DD): multiplied by 100, "%" suffix
- **Duration**: `Xh Xm Xs` format
- **DateTime**: `MM-dd HH:mm` or `MM-dd HH:mm:ss`

### Status badges
- `✓ success` — green (completed, exit code 0)
- `✗ error` — red (failed)
- `⚠ running` — amber (in progress)
- `◐ starting` — gray (initializing)

### Error states
- **Network error:** Red toast/banner with "Connection lost" or "Service unavailable"
- **Validation error:** Inline red text below the offending field
- **Server error (500):** Red banner with error message
- **Empty state:** "No data" placeholder with suggestion text

### Loading states
- **Page load:** Centered spinner
- **Table load:** Skeleton rows or spinner
- **Chart load:** Empty chart area with spinner overlay
- **Live monitor:** Continuous spinner until first progress event

### Action buttons
- **Primary** (Submit, Start): Green or blue filled button
- **Danger** (Cancel, Delete): Red filled button
- **Secondary** (Edit, Duplicate, Export): Outline or gray button
- **Icon buttons** (Download, Refresh): Small icon-only buttons

### Navigation patterns
- **Breadcrumb:** Not used — sidebar context provides orientation
- **Back links:** "← All Runs", "← All Strategies" at top of detail pages
- **Related links:** Monitor/Analyzer links from Run Report header
- **External links:** "API Docs" opens Scalar in new tab
