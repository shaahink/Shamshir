# deploy-cbot.ps1 — Build the cBot with auto-stamp and deploy to cTrader Desktop's Sources folder
# so it appears in the "Add Bot" picker immediately.
# Usage: .\scripts\deploy-cbot.ps1 [path-to-algo-dir] [cTrader-sources-path]
param(
    [string]$AlgoDir = "src\TradingEngine.Adapters.CTrader",
    [string]$SourcesPath = $null
)

$args = "build", $AlgoDir, "-p:AutoDeploy=true"

if ($SourcesPath) {
    $args += "-p:CTraderSourcesPath=$SourcesPath"
}

Write-Host "Building + deploying cBot to cTrader..."
Write-Host "  Project: $AlgoDir"
Write-Host "  Sources: $(if ($SourcesPath) { $SourcesPath } else { 'default: %USERPROFILE%\Documents\cAlgo\Sources\Robots' })"

& dotnet $args --no-restore

if ($LASTEXITCODE -eq 0) {
    Write-Host "Done. Restart cTrader Desktop to see 'TradingEngineCBot' in the bot picker." -ForegroundColor Green
    Write-Host "  In cTrader: right-click chart → Add Bot → search 'TradingEngineCBot'" -ForegroundColor Green
    Write-Host "  Verify build: look for '_build' parameter in the cBot settings panel" -ForegroundColor Green
} else {
    Write-Host "Build failed. Check errors above." -ForegroundColor Red
}
