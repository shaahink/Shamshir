# check-errors.ps1 — Full-stack error diagnosis
# Reads recent errors from both backend (Serilog) and frontend (JSON-lines) log files.
# AI agents can call this to assess system health after changes.
param(
    [int]$Minutes = 15,
    [int]$MaxLines = 50,
    [switch]$SinceLastCheck   # show only errors since the last time this script ran
)

$ErrorActionPreference = "SilentlyContinue"
$repoRoot = git rev-parse --show-toplevel 2>$null
if (-not $repoRoot) { $repoRoot = Get-Location }
$webLogDir = Join-Path $repoRoot "src\TradingEngine.Web\logs"
$hostLogDir = Join-Path $repoRoot "src\TradingEngine.Host\logs"
$frontendJsonl = Join-Path $repoRoot "logs\frontend-errors.jsonl"

$markerFile = Join-Path $webLogDir ".check-errors-last-run"
$since = if ($SinceLastCheck -and (Test-Path $markerFile)) {
    [DateTime]::Parse((Get-Content $markerFile))
} else {
    (Get-Date).AddMinutes(-$Minutes)
}
$now = Get-Date

Write-Host "=== check-errors.ps1 === since $($since.ToString('yyyy-MM-dd HH:mm:ss')) ===" -ForegroundColor Cyan
Write-Host ""

# --- Backend Serilog errors ---
$backendCount = 0
Get-ChildItem $webLogDir -Filter "web-*.log" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | ForEach-Object {
    $lines = Get-Content $_.FullName | Where-Object { $_ -match '\[(ERR|FTL|WRN)\]' }
    foreach ($line in $lines) {
        if ($line -match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})') {
            $ts = [DateTime]::Parse($matches[1])
            if ($ts -ge $since) {
                $backendCount++
                $sev = if ($line -match '\[ERR\]') { "Red" } elseif ($line -match '\[FTL\]') { "Magenta" } else { "Yellow" }
                Write-Host $line -ForegroundColor $sev
            }
        }
    }
} | Select-Object -First $MaxLines
if ($backendCount -eq 0) { Write-Host "[Backend] No errors in window" -ForegroundColor Green }

Write-Host ""

# --- Frontend JSON-lines errors ---
$frontendCount = 0
if (Test-Path $frontendJsonl) {
    Get-Content $frontendJsonl | ForEach-Object {
        try {
            $r = $_ | ConvertFrom-Json
            $ts = [DateTime]::Parse($r.timestamp)
            if ($ts -ge $since) {
                $frontendCount++
                $color = if ($r.kind -eq 'error' -or $r.kind -eq 'unhandled') { "Red" } else { "Yellow" }
                Write-Host "[$($r.kind)] $($r.message)" -ForegroundColor $color
                if ($r.url) { Write-Host "  at $($r.url):$($r.line):$($r.col)" -ForegroundColor Gray }
                if ($r.stack) { Write-Host "  $($r.stack.Substring(0, [Math]::Min(200, $r.stack.Length)))" -ForegroundColor DarkGray }
            }
        } catch { }
    } | Select-Object -First $MaxLines
}
if ($frontendCount -eq 0) { Write-Host "[Frontend] No errors in window" -ForegroundColor Green }

Write-Host ""
Write-Host "=== Total: $backendCount backend, $frontendCount frontend errors ===" -ForegroundColor $(
    if ($backendCount + $frontendCount -eq 0) { "Green" } else { "Red" }
)

# Save marker for next --SinceLastCheck run.
$now.ToString('yyyy-MM-dd HH:mm:ss') | Set-Content $markerFile
