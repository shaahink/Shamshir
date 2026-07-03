param(
    [string]$NgProjectDir,
    [string]$NgBuildStamp
)

if (-not $NgProjectDir) {
    Write-Host "Angular: NgProjectDir not set, skipping auto-build"
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
    Write-Host "Angular: no source files found, skipping auto-build"
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

Write-Host "Angular: src is newer than wwwroot (src $newestSrc > index.html), rebuilding..."
npm --prefix $NgProjectDir run build
if ($LASTEXITCODE -ne 0) {
    Write-Warning "Angular build FAILED (exit code $LASTEXITCODE)"
    exit $LASTEXITCODE
}

'' | Out-File -LiteralPath $NgBuildStamp -Encoding utf8
Write-Host "Angular: build complete"
