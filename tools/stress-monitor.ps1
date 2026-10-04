# Long-run stability sampler (Phase 10, time-boxed variant of the 8h spec).
# Launches the app, samples CPU / WorkingSet / Handles / Threads at a fixed
# interval, writes CSV, prints first-vs-last comparison.
# ASCII-only comments (PS 5.1 reads UTF-8-no-BOM as ANSI).
param(
    [int]$Minutes = 10,
    [int]$IntervalSeconds = 30
)
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repoRoot "src\CuinProcessEase.App\bin\Release\net10.0-windows\CuinProcessEase.App.exe"
$outCsv = Join-Path $repoRoot "release\stress-samples.csv"

if (-not (Test-Path $exe)) { throw "app exe not found (build Release first): $exe" }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outCsv) | Out-Null

Write-Host ("== launching app: " + $exe)
$proc = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 5

$samples = New-Object System.Collections.ArrayList
$deadline = (Get-Date).AddMinutes($Minutes)
while ((Get-Date) -lt $deadline -and -not $proc.HasExited) {
    $proc.Refresh()
    $row = [pscustomobject]@{
        Time = (Get-Date -Format "HH:mm:ss")
        CPU_s = [math]::Round($proc.TotalProcessorTime.TotalSeconds, 1)
        WorkingSetMB = [math]::Round($proc.WorkingSet64 / 1MB, 1)
        PrivateMB = [math]::Round($proc.PrivateMemorySize64 / 1MB, 1)
        Handles = $proc.HandleCount
        Threads = $proc.Threads.Count
    }
    [void]$samples.Add($row)
    Write-Host ("sample: " + $row.Time + "  ws=" + $row.WorkingSetMB + "MB priv=" + $row.PrivateMB + "MB cpu=" + $row.CPU_s + "s h=" + $row.Handles + " t=" + $row.Threads)
    Start-Sleep -Seconds $IntervalSeconds
}

$samples | Export-Csv -Path $outCsv -NoTypeInformation -Encoding UTF8

if ($samples.Count -ge 2) {
    $first = $samples[0]
    $last = $samples[-1]
    Write-Host ""
    Write-Host "== summary (first -> last) =="
    Write-Host ("WorkingSet MB : " + $first.WorkingSetMB + " -> " + $last.WorkingSetMB)
    Write-Host ("Private MB    : " + $first.PrivateMB + " -> " + $last.PrivateMB)
    Write-Host ("Handles       : " + $first.Handles + " -> " + $last.Handles)
    Write-Host ("Threads       : " + $first.Threads + " -> " + $last.Threads)
    Write-Host ("CPU seconds   : " + $first.CPU_s + " -> " + $last.CPU_s + " (delta " + [math]::Round($last.CPU_s - $first.CPU_s, 1) + "s over " + ($Minutes) + "min)")
}
Write-Host ("csv: " + $outCsv)

if (-not $proc.HasExited) {
    $null = $proc.CloseMainWindow()
    Start-Sleep -Seconds 3
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
}
