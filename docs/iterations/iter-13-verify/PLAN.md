# Iter-13 Completion + Multi-Strategy Verification

**Branch**: work on current branch (`phase/8b-bar-tracing` or create `iter/13-verify`)
**Depends on**: iter-13 code merged (commit `81d21a9` + `b3926c8`)
**Goal**: Fill the blank manual verification table in `docs/iterations/iter-13/HANDOVER.md`,
fix an ActiveStrategyIds format bug, and seed the Bars table so the replay path has data.

---

## Read first

- `docs/agents/HOW-TO-WORK.md`
- `docs/iterations/iter-13/HANDOVER.md` — the manual verification table (currently all blank)
- `src/TradingEngine.Host/appsettings.json` — ActiveStrategyIds format bug
- `src/TradingEngine.Web/appsettings.Development.json` — CTrader config
- `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` lines 300-308 — how strategies are loaded in replay

---

## Phase A — Fix ActiveStrategyIds format (5 min)

`src/TradingEngine.Host/appsettings.json` currently has:
```json
"ActiveStrategyIds": ["mean-reversion"]
```

The code reads this with `GetValue<string>("Engine:ActiveStrategyIds")?.Split(',')`.
`GetValue<string>` returns **null** for a JSON array — so the fallback kicks in and all 4
strategies run anyway. The array format is misleading.

Change it to the correct comma-separated string:
```json
"ActiveStrategyIds": "trend-breakout,ema-alignment,mean-reversion,session-breakout"
```

**Note**: The replay path (`BacktestOrchestrator.RunEngineReplayAsync`) ignores this setting
entirely — it uses all configs from `config/strategies/*.json`. Both paths effectively run
all 4 strategies already; this change just makes the Host config correct and readable.

---

## Phase B — Seed the Bars table (15 min)

The Bars table in `data/trading.db` has **0 rows**. The Web replay path reads from it.
Without seeding, any backtest produces 0 evaluations.

Seed from `tests/data/eurusd-h1-bull-2024.csv`:
- 2,000 EURUSD H1 bars, 2024-01-01 to 2024-03-24

**Write a seeder script** at `scripts/seed-bars.ps1`:

```powershell
# Seed EURUSD H1 bars from CSV into trading.db
$csv   = Import-Csv "tests\data\eurusd-h1-bull-2024.csv"
$db    = "data\trading.db"
$tf    = "H1"
$sym   = "EURUSD"

$inserts = $csv | ForEach-Object {
    $id = [System.Guid]::NewGuid().ToString()
    $dt = [DateTime]::Parse($_.DateTime).ToString("yyyy-MM-dd HH:mm:ss")
    "INSERT OR IGNORE INTO Bars (Id, Symbol, Timeframe, OpenTimeUtc, Open, High, Low, Close, Volume) VALUES ('$id','$sym','$tf','$dt','$($_.Open)','$($_.High)','$($_.Low)','$($_.Close)',$($_.Volume));"
}

$sql = $inserts -join "`n"
$sql | sqlite3 $db
Write-Host "Seeded $($inserts.Count) bars."
```

Run it:
```powershell
.\scripts\seed-bars.ps1
sqlite3 data\trading.db "SELECT COUNT(*) FROM Bars;"
# Expected: 2000
```

**Important**: `sqlite3` must be on PATH. If it's not available, use the alternative C# approach below.

### Alternative: C# seed script (if sqlite3 not on PATH)

Create `scripts/SeedBars.csx` and run with `dotnet script`, OR just use a small test:

```powershell
# Check if sqlite3 is available
sqlite3 --version
```

If unavailable, instead create a minimal .NET script `scripts/SeedBars.cs` in a new temp
console project, or use the existing test infrastructure:

```powershell
dotnet run --project src/TradingEngine.Web -- seed-bars tests/data/eurusd-h1-bull-2024.csv
```

But the simplest path: use System.Data.SQLite via PowerShell or a quick dotnet-script. 
If none of those work, note the blocker in HANDOVER.md and skip to Phase D (cTrader path).

---

## Phase C — Run replay backtest and collect iter-13 metrics

### C1 — Switch to replay mode

In `src/TradingEngine.Web/appsettings.Development.json`, change:
```json
"UseForBacktest": "true"
```
to:
```json
"UseForBacktest": "false"
```

### C2 — Start the Web app

```powershell
Start-Process dotnet -ArgumentList "run --project src/TradingEngine.Web --environment Development" -NoNewWindow
Start-Sleep -Seconds 8
```

Check it started:
```powershell
Invoke-WebRequest http://localhost:5000/health -UseBasicParsing 2>$null | Select-Object -Expand StatusCode
# OR just try the API directly
```

### C3 — Trigger a backtest

```powershell
$body = @{
    symbol  = "EURUSD"
    period  = "h1"
    start   = "2024-01-15T00:00:00"
    end     = "2024-02-15T00:00:00"
    balance = 100000
    commissionPerMillion = 30
    spreadPips = 1
} | ConvertTo-Json

$resp = Invoke-WebRequest -Uri "http://localhost:5000/api/backtest/start" `
    -Method POST -ContentType "application/json" -Body $body -UseBasicParsing
$runId = ($resp.Content | ConvertFrom-Json).runId
Write-Host "RunId: $runId"
```

### C4 — Poll until complete (max 5 min)

```powershell
$done = $false
for ($i = 0; $i -lt 60 -and -not $done; $i++) {
    Start-Sleep -Seconds 5
    $status = (Invoke-WebRequest "http://localhost:5000/api/backtest/$runId/status" -UseBasicParsing).Content | ConvertFrom-Json
    Write-Host "Status: $($status.status)"
    if ($status.status -in @("completed","failed")) { $done = $true }
}
$status | ConvertTo-Json
```

### C5 — Query the DB for metrics

```powershell
# Total bars evaluated for this run
sqlite3 data\trading.db "SELECT COUNT(*) FROM BarEvaluations WHERE RunId='$runId';"

# Signals fired
sqlite3 data\trading.db "SELECT COUNT(*) FROM BarEvaluations WHERE RunId='$runId' AND SignalFired=1;"

# Top rejection reasons (non-signal)
sqlite3 data\trading.db @"
SELECT Reason, COUNT(*) as cnt
FROM BarEvaluations
WHERE RunId='$runId' AND SignalFired=0
GROUP BY Reason ORDER BY cnt DESC LIMIT 5;
"@

# Trades opened
sqlite3 data\trading.db "SELECT COUNT(*) FROM TradeResults WHERE RunId='$runId';"

# Per-strategy breakdown
sqlite3 data\trading.db @"
SELECT StrategyId,
       COUNT(*) as TotalBars,
       SUM(SignalFired) as Signals
FROM BarEvaluations
WHERE RunId='$runId'
GROUP BY StrategyId;
"@
```

### C6 — Restore cTrader mode

Revert `UseForBacktest` back to `"true"` in `appsettings.Development.json`.

---

## Phase D — Attempt cTrader path (best-effort)

**Precondition**: cTrader desktop app must be running and authenticated with the `seankiaa` account.
An agent cannot start cTrader — this is a user action. If the platform is not running, skip
this phase and document it as "requires user to run cTrader manually".

If cTrader IS running:
1. Ensure `UseForBacktest: "true"` in appsettings.Development.json
2. Start the Web app (if not already running)
3. Trigger a backtest via the same API call as Phase C
4. Watch the status — if it fails with a connection error, document the error message verbatim
5. If it succeeds, collect the same metrics from the DB

---

## Phase E — Fill in HANDOVER.md

Open `docs/iterations/iter-13/HANDOVER.md`. Fill the manual verification table:

```markdown
| Check | Status |
|-------|--------|
| cTrader backtest from UI (UseForBacktest: true, EURUSD H1 1-month) | [PASS / BLOCKED: <reason>] |
| SIGNAL events in blue on Progress page | [PASS (N signals visible) / BLOCKED: no browser] |
| Strategy breakdown table on Detail page | [PASS: N rows / BLOCKED: no browser] |
| Rejection reasons displayed | [PASS / BLOCKED: no browser] |
```

And fill the metrics:

```markdown
| Metric | Value |
|--------|-------|
| TotalBarsEvaluated | [from Phase C query] |
| SignalsFired | [from Phase C query] |
| TradesOpened | [from Phase C query] |
| Top rejection reason | [from Phase C query] |
```

If replay produced 0 bars (seeding failed), document that the Bars table seeder is a
prerequisite and left for the user to run before retrying.

---

## Verification

```powershell
dotnet build --no-incremental
dotnet test tests/TradingEngine.Tests.Unit --no-build
dotnet test tests/TradingEngine.Tests.Simulation --no-build --filter "ReplayBacktest"
```

All must pass before writing HANDOVER entries.

---

## Commit

Commit all changes (appsettings fix, seeder script, filled HANDOVER):

```
fix: correct ActiveStrategyIds to comma-separated string, seed bars, complete iter-13 verification
```

---

## What NOT to do

- Do not change any strategy logic
- Do not change BacktestOrchestrator or EngineWorker
- Do not change channel modes or adapter code
- Do not start iter-14 — this task is iter-13 completion only
