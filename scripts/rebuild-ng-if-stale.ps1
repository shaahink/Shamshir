param(
    [string]$NgProjectDir,
    [string]$NgBuildStamp
)

$srcDir = Join-Path $NgProjectDir 'src'
$cfg = Join-Path $NgProjectDir 'angular.json'
$pkg = Join-Path $NgProjectDir 'package.json'

$wwwroot = Join-Path $NgProjectDir '..' 'src' 'TradingEngine.Web' 'wwwroot'
$indexHtml = Join-Path $wwwroot 'index.html'

$srcFiles = @(Get-ChildItem -Path $srcDir -Recurse -Include '*.ts', '*.html' -File -ErrorAction SilentlyContinue)
$configFiles = @(Get-Item $cfg, $pkg -ErrorAction SilentlyContinue)
$all = $srcFiles + $configFiles

if ($all.Count -eq 0) {
    Write-Host "Angular: no source files found, skipping auto-build"
    exit 0
}

$newestSrc = ($all | Sort-Object LastWriteTime -Descending | Select-Object -First 1).LastWriteTime

if (Test-Path $indexHtml) {
    $outputTime = (Get-Item $indexHtml).LastWriteTime
    if ($outputTime -ge $newestSrc) {
        Write-Host "Angular: wwwroot up to date (index.html" $outputTime ">= src" $newestSrc ")"
        exit 0
    }
}

Write-Host "Angular: src is newer than wwwroot (src" $newestSrc "> index.html present)," " rebuilding..."
npm --prefix $NgProjectDir run build
if ($LASTEXITCODE -eq 0) {
    '' | Out-File -LiteralPath $NgBuildStamp -Encoding utf8
    Write-Host "Angular: build complete"
} else {
    Write-Warning "Angular build FAILED (exit code $LASTEXITCODE)"
}
