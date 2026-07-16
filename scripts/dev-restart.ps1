# dev-restart.ps1 — the fast backend loop as ONE step (iter-dx-speed D3, delivered during
# iter-structural-edge S1): kill the running Web host -> rebuild -> relaunch detached -> wait
# for health. Avoids the MSB3026/27 file-lock dance and the forgotten-ASPNETCORE_ENVIRONMENT
# trap (creds silently don't load without Development).
#
#   scripts/dev-restart.ps1                 # kill + build + relaunch on :5134
#   scripts/dev-restart.ps1 -NoBuild        # kill + relaunch only (config/DB-only changes)
#   scripts/dev-restart.ps1 -Port 5177
#   scripts/dev-restart.ps1 -KillOnly       # just free the port / release DLL locks
param(
    [int]$Port = 5134,
    [switch]$NoBuild,
    [switch]$KillOnly
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$webDir = Join-Path $repo 'src\TradingEngine.Web'

# 1. kill any running host (dotnet.exe carrying TradingEngine.Web.dll)
$killed = 0
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
    Where-Object { $_.CommandLine -like '*TradingEngine.Web.dll*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -Confirm:$false; $killed++ }
Write-Host "killed $killed running host(s)"
if ($KillOnly) { exit 0 }

# 2. rebuild (Angular staleness is checked by the csproj target; if it trips, run
#    `npm --prefix web-ui run build` first — .NET 10 static assets can't rebuild it inline)
if (-not $NoBuild) {
    dotnet build $webDir -c Debug
    if ($LASTEXITCODE -ne 0) { Write-Error "build failed"; exit 1 }
}

# 3. relaunch detached + wait for health
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = "http://localhost:$Port"
$p = Start-Process -FilePath dotnet -ArgumentList 'bin\Debug\net10.0\TradingEngine.Web.dll' `
    -WorkingDirectory $webDir -WindowStyle Hidden -PassThru
Write-Host "host PID: $($p.Id)"

$deadline = (Get-Date).AddSeconds(60)
while ((Get-Date) -lt $deadline) {
    try {
        $r = Invoke-WebRequest -Uri "http://localhost:$Port/api/system/health" -UseBasicParsing -TimeoutSec 2
        if ($r.StatusCode -eq 200) { Write-Host "READY http://localhost:$Port ($($r.Content))"; exit 0 }
    } catch {}
    Start-Sleep -Milliseconds 500
}
Write-Error "host did not become healthy within 60s (PID $($p.Id))"
exit 1
