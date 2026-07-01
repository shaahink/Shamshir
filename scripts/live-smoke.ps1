<#
.SYNOPSIS
  Live Monitor Chain diagnostic — tests all server-side links without a browser.
.DESCRIPTION
  Run ANY TIME the app is running. Tests 3 things:
  1. Journal has entries (engine is running and persisting)
  2. SignalR negotiate endpoint responds (hub is registered)
  3. SPA bundle exists (wwwroot/index.html present)

  If all pass, the break is in the browser/transport layer — run the E2E tests.
  If any fail, fix server-side before touching frontend code.

.EXAMPLE
  powershell -File scripts\live-smoke.ps1
  powershell -File scripts\live-smoke.ps1 -Url http://localhost:5134
#>

param([string]$Url = "http://localhost:5134")

$ErrorActionPreference = "Continue"
$ok = $true

Write-Host "=== Live Monitor Chain Smoke ===" -ForegroundColor Cyan
Write-Host "Target: $Url"
Write-Host ""

# ── Test 1: Journal has entries ──
Write-Host "[1/3] Journal entries ... " -NoNewline
try {
    $runs = Invoke-RestMethod "$Url/api/runs" -TimeoutSec 5
    $last = $runs | Select-Object -First 1
    $journal = Invoke-RestMethod "$Url/api/runs/$($last.runId)/journal?limit=1" -TimeoutSec 5
    if ($journal -and $journal.Count -ge 0) {
        Write-Host "PASS ($($journal.Count) entries for run $($last.runId.Substring(0,8)))" -ForegroundColor Green
    } else {
        Write-Host "FAIL — journal returned null" -ForegroundColor Red
        $ok = $false
    }
} catch {
    Write-Host "FAIL — $_" -ForegroundColor Red
    $ok = $false
}

# ── Test 2: SignalR negotiate ──
Write-Host "[2/3] SignalR negotiate ... " -NoNewline
try {
    $neg = Invoke-RestMethod -Method Post "$Url/hubs/run/negotiate?negotiateVersion=1" -TimeoutSec 5
    $transports = ($neg.availableTransports | ForEach-Object { $_.transport }) -join ','
    Write-Host "PASS ($transports)" -ForegroundColor Green
} catch {
    Write-Host "FAIL — $_" -ForegroundColor Red
    $ok = $false
}

# ── Test 3: SPA bundle ──
Write-Host "[3/3] SPA bundle ........ " -NoNewline
$wwwroot = Join-Path (Split-Path $PSScriptRoot -Parent) "src\TradingEngine.Web\wwwroot"
$index = Join-Path $wwwroot "index.html"
if (Test-Path $index) {
    Write-Host "PASS (wwwroot/index.html exists)" -ForegroundColor Green
} else {
    Write-Host "FAIL — wwwroot missing, run: cd web-ui && npm run build" -ForegroundColor Red
    $ok = $false
}

# ── Summary ──
Write-Host ""
if ($ok) {
    Write-Host "=== ALL SERVER-SIDE CHECKS PASS ===" -ForegroundColor Green
    Write-Host "Next: run E2E tests to verify browser transport"
    Write-Host "  cd web-ui"
    Write-Host "  npx playwright test tests/e2e/live-monitor-links.spec.ts --reporter=line"
} else {
    Write-Host "=== FIX FAILURES ABOVE, THEN RETRY ===" -ForegroundColor Red
}
