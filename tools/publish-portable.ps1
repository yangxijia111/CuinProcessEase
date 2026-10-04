# Portable x64 release packaging (Phase 10):
# dotnet publish self-contained -> collect exes -> zip.
# ASCII-only comments (PS 5.1 reads UTF-8-no-BOM as ANSI).
param(
    [string]$Version = "1.0.0"
)
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot "release\publish"
$outZip = Join-Path $repoRoot "release\CuinProcessEase-$Version-portable-win-x64.zip"

if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outZip) | Out-Null

Write-Host "== dotnet publish (self-contained win-x64) =="
dotnet publish (Join-Path $repoRoot "src\CuinProcessEase.App\CuinProcessEase.App.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:EnableCompressionInSingleFile=false `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# Deliverables per dev doc: main exe + elevated helper exe in one folder.
$required = @(
    (Join-Path $publishDir "CuinProcessEase.App.exe"),
    (Join-Path $publishDir "CuinProcessEase.ElevatedHelper.exe")
)
foreach ($f in $required) {
    if (-not (Test-Path $f)) { throw "missing deliverable: $f" }
}

Write-Host "== zipping portable package =="
if (Test-Path $outZip) { Remove-Item -Force $outZip }
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $outZip
Write-Host ("package: " + $outZip + " (" + [math]::Round((Get-Item $outZip).Length / 1MB, 1) + " MB)")
