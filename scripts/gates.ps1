# gates.ps1 — the fast gate battery, suites in PARALLEL (iter-structural-edge S1 speed work).
# Build first (serial — everything depends on it), then Unit + Integration + Sim-fast as
# concurrent jobs. Prints one summary line per suite and exits non-zero if anything failed.
#
#   scripts/gates.ps1              # build + all three suites
#   scripts/gates.ps1 -NoBuild     # suites only (already built)
param([switch]$NoBuild)
$ErrorActionPreference = 'Continue'
$repo = Split-Path -Parent $PSScriptRoot
$sw = [System.Diagnostics.Stopwatch]::StartNew()

if (-not $NoBuild) {
    dotnet build (Join-Path $repo 'TradingEngine.slnx') -c Debug | Select-String 'error|Build succeeded|FAILED' | Select-Object -First 5
    if ($LASTEXITCODE -ne 0) { Write-Error 'BUILD FAILED'; exit 1 }
    Write-Host "build OK ($([int]$sw.Elapsed.TotalSeconds)s)"
}

$suites = @(
    @{ Name = 'Unit';        Args = @('test', (Join-Path $repo 'tests\TradingEngine.Tests.Unit'), '--no-build') },
    @{ Name = 'Integration'; Args = @('test', (Join-Path $repo 'tests\TradingEngine.Tests.Integration'), '--no-build') },
    @{ Name = 'Sim-fast';    Args = @('test', (Join-Path $repo 'tests\TradingEngine.Tests.Simulation'), '--no-build',
                                       '--filter', 'RequiresCTrader!=true&Category!=E2E&Category!=Slow&Category!=NetMQ') }
)

$jobs = foreach ($s in $suites) {
    Start-Job -Name $s.Name -ScriptBlock {
        param($dotnetArgs)
        $out = & dotnet @dotnetArgs 2>$null
        $summary = ($out | Select-String 'Passed!|Failed!' | Select-Object -Last 1)
        if ($null -eq $summary) { $summary = 'NO SUMMARY (suite crashed?)' }
        [pscustomobject]@{ Summary = "$summary"; Failed = ($LASTEXITCODE -ne 0) }
    } -ArgumentList (,$s.Args)
}

$failed = $false
foreach ($j in $jobs) {
    $r = Receive-Job -Job $j -Wait
    Write-Host ("{0,-12} {1}" -f $j.Name, $r.Summary)
    if ($r.Failed) { $failed = $true }
    Remove-Job -Job $j -Force
}
Write-Host "gates total: $([int]$sw.Elapsed.TotalSeconds)s"
if ($failed) { exit 1 } else { exit 0 }
