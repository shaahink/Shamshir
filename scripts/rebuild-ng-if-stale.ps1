param(
    [string]$NgProjectDir,
    [string]$NgBuildStamp
)

$srcDir = Join-Path $NgProjectDir 'src'
$cfg = Join-Path $NgProjectDir 'angular.json'
$pkg = Join-Path $NgProjectDir 'package.json'

$srcFiles = @(Get-ChildItem -Path $srcDir -Recurse -Include '*.ts', '*.html' -File -ErrorAction SilentlyContinue)
$configFiles = @(Get-Item $cfg, $pkg -ErrorAction SilentlyContinue)
$all = $srcFiles + $configFiles

if ($all.Count -eq 0) {
    Write-Host "Angular: no source files found, skipping auto-build"
    exit 0
}

$newest = ($all | Sort-Object LastWriteTime -Descending | Select-Object -First 1).LastWriteTime

$stamp = Get-Item $NgBuildStamp -ErrorAction SilentlyContinue
if ($stamp -and $newest -le $stamp.LastWriteTime) {
    Write-Host "Angular: up to date"
    exit 0
}

Write-Host "Angular: rebuilding..."
npm --prefix $NgProjectDir run build
if ($LASTEXITCODE -eq 0) {
    '' | Out-File -LiteralPath $NgBuildStamp -Encoding utf8
    Write-Host "Angular: build complete"
} else {
    Write-Warning "Angular build FAILED (exit code $LASTEXITCODE)"
}
