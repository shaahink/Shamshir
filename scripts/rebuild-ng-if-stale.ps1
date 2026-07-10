param(
    [string]$NgProjectDir,
    [string]$NgBuildStamp
)

if (-not $NgProjectDir) {
    Write-Host "Angular: NgProjectDir not set, skipping check"
    exit 0
}

$NgProjectDir = [System.IO.Path]::GetFullPath($NgProjectDir)

$srcDir = [IO.Path]::Combine($NgProjectDir, 'src')
$cfg = [IO.Path]::Combine($NgProjectDir, 'angular.json')
$pkg = [IO.Path]::Combine($NgProjectDir, 'package.json')

$wwwroot = [IO.Path]::GetFullPath([IO.Path]::Combine($NgProjectDir, '..', 'src', 'TradingEngine.Web', 'wwwroot'))
$indexHtml = [IO.Path]::Combine($wwwroot, 'index.html')

$srcFiles = @(Get-ChildItem -Path $srcDir -Recurse -Include '*.ts', '*.html' -File -ErrorAction SilentlyContinue)
$configFiles = @(Get-Item $cfg, $pkg -ErrorAction SilentlyContinue)
$all = $srcFiles + $configFiles

if ($all.Count -eq 0) {
    Write-Host "Angular: no source files found, skipping check"
    exit 0
}

$newestSrc = ($all | Sort-Object LastWriteTime -Descending | Select-Object -First 1).LastWriteTime

if (Test-Path $indexHtml) {
    $outputTime = (Get-Item $indexHtml).LastWriteTime
    if ($outputTime -ge $newestSrc) {
        Write-Host "Angular: wwwroot up to date (index.html $outputTime >= src $newestSrc)"
        exit 0
    }
}

Write-Host "Angular: STALE! src ($newestSrc) is newer than wwwroot index.html."
Write-Host ""
Write-Host "The Angular source has changed since the last build."
Write-Host "Re-run with:  npm --prefix $NgProjectDir run build"
Write-Host "Then re-run dotnet build."
Write-Host ""
Write-Host "The Angular output cannot be rebuilt inside dotnet build because"
Write-Host ".NET 10's static web assets pipeline evaluates wwwroot before targets run."
exit 1
