# ==============================================================================
# Codex Switchboard - Performance Benchmark Harness
# Monotonic Milestones Recording for Startup (T0-T15) and Shutdown (S0-S16 / A-G)
# ==============================================================================
[CmdletBinding()]
param(
    [string]$BinaryPath,
    [string]$OutputDir,
    [int]$ColdSamples = 5,
    [int]$WarmSamples = 10
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($BinaryPath)) {
    $BinaryPath = Join-Path $RepoRoot "dist\benchmark-bin\CodexSwitchboard.exe"
}
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $RepoRoot "dist\perf-results"
}

if (-not (Test-Path $BinaryPath)) {
    throw "Target binary not found at '$BinaryPath'. Please publish first."
}

if (-not (Test-Path $OutputDir)) {
    New-Item -Path $OutputDir -ItemType Directory -Force | Out-Null
}

function Run-SingleSample {
    param(
        [string]$Scenario = "IDLE",
        [int]$AutoCloseDelayMs = 200,
        [string]$RunLabel = "Sample"
    )

    $runDir = Join-Path $OutputDir "run-$RunLabel"
    if (Test-Path $runDir) { Remove-Item -Path $runDir -Recurse -Force }
    New-Item -Path $runDir -ItemType Directory -Force | Out-Null

    $prevEnv = $env:CODEX_SWITCHBOARD_PERF_DIR
    $env:CODEX_SWITCHBOARD_PERF_DIR = $runDir

    try {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $args = "--perf-scenario=$Scenario --perf-auto-close-delay-ms=$AutoCloseDelayMs"
        $proc = Start-Process -FilePath $BinaryPath -ArgumentList $args -PassThru
        
        $exited = $proc.WaitForExit(20000)
        $sw.Stop()

        if (-not $exited) {
            $proc.Kill()
            Write-Warning "Process timed out on scenario $Scenario ($RunLabel)"
            return $null
        }

        $startupFile = Join-Path $runDir "startup-latest.json"
        $shutdownFile = Join-Path $runDir "shutdown-latest.json"

        $startupData = if (Test-Path $startupFile) { Get-Content $startupFile -Raw | ConvertFrom-Json } else { $null }
        $shutdownData = if (Test-Path $shutdownFile) { Get-Content $shutdownFile -Raw | ConvertFrom-Json } else { $null }

        return [PSCustomObject]@{
            Label = $RunLabel
            Scenario = $Scenario
            WallClockMs = $sw.Elapsed.TotalMilliseconds
            Startup = $startupData
            Shutdown = $shutdownData
        }
    }
    finally {
        $env:CODEX_SWITCHBOARD_PERF_DIR = $prevEnv
    }
}

function Get-Stats {
    param([double[]]$Values)
    if ($Values.Count -eq 0) { return [PSCustomObject]@{ Min=0; Median=0; P90=0; Max=0 } }
    $sorted = $Values | Sort-Object
    $count = $sorted.Count
    $min = $sorted[0]
    $max = $sorted[-1]
    
    $median = if ($count % 2 -eq 1) {
        $sorted[[math]::Floor($count / 2)]
    } else {
        ($sorted[($count/2) - 1] + $sorted[$count/2]) / 2.0
    }

    $p90Index = [math]::Ceiling($count * 0.9) - 1
    if ($p90Index -ge $count) { $p90Index = $count - 1 }
    $p90 = $sorted[$p90Index]

    return [PSCustomObject]@{
        Min = [math]::Round($min, 1)
        Median = [math]::Round($median, 1)
        P90 = [math]::Round($p90, 1)
        Max = [math]::Round($max, 1)
    }
}

Write-Host "====================================================================" -ForegroundColor Cyan
Write-Host " Codex Switchboard Performance Measurement Baseline" -ForegroundColor Cyan
Write-Host " Binary: $BinaryPath" -ForegroundColor Cyan
Write-Host "====================================================================" -ForegroundColor Cyan

# 1. Warm-up run (discarded)
Write-Host "`n[0/3] Executing single untimed warm-up pass..." -ForegroundColor DarkGray
$null = Run-SingleSample -Scenario "IDLE" -AutoCloseDelayMs 200 -RunLabel "warmup"

# 2. Warm Startup Samples
Write-Host "`n[1/3] Collecting $WarmSamples WARM startup samples..." -ForegroundColor Yellow
$warmResults = @()
for ($i = 1; $i -le $WarmSamples; $i++) {
    Write-Host "  Warm sample $i/$WarmSamples..." -NoNewline
    $res = Run-SingleSample -Scenario "IDLE" -AutoCloseDelayMs 200 -RunLabel "warm-$i"
    if ($res) {
        $interactive = ($res.Startup.milestones | Where-Object { $_.MilestoneName -eq "T12:UIInteractive" }).ElapsedMillisecondsFromStart
        Write-Host " T12: $($interactive)ms (WallClock: $($res.WallClockMs.ToString('F0'))ms)" -ForegroundColor Green
        $warmResults += $res
    } else {
        Write-Host " FAILED" -ForegroundColor Red
    }
}

# 3. Cold-ish Startup Samples (inter-sample cool-down delay)
Write-Host "`n[2/3] Collecting $ColdSamples COLD-ish startup samples (with process inactivity pause)..." -ForegroundColor Yellow
$coldResults = @()
for ($i = 1; $i -le $ColdSamples; $i++) {
    Write-Host "  Cooldown pause (2.5s)..." -NoNewline -ForegroundColor DarkGray
    Start-Sleep -Milliseconds 2500
    Write-Host " Cold sample $i/$ColdSamples..." -NoNewline
    $res = Run-SingleSample -Scenario "IDLE" -AutoCloseDelayMs 200 -RunLabel "cold-$i"
    if ($res) {
        $interactive = ($res.Startup.milestones | Where-Object { $_.MilestoneName -eq "T12:UIInteractive" }).ElapsedMillisecondsFromStart
        Write-Host " T12: $($interactive)ms (WallClock: $($res.WallClockMs.ToString('F0'))ms)" -ForegroundColor Green
        $coldResults += $res
    } else {
        Write-Host " FAILED" -ForegroundColor Red
    }
}

# 4. Shutdown Scenarios A-G
Write-Host "`n[3/3] Collecting SHUTDOWN scenarios (A-G)..." -ForegroundColor Yellow
$shutdownScenarios = @("A", "B", "C", "D", "E", "F", "G")
$shutdownResults = @{}
foreach ($sc in $shutdownScenarios) {
    Write-Host "  Shutdown Scenario $sc..." -NoNewline
    $res = Run-SingleSample -Scenario $sc -AutoCloseDelayMs 100 -RunLabel "shutdown-$sc"
    if ($res) {
        $shutTotal = $res.Shutdown.totalElapsedMs
        $winHide = $res.Shutdown.windowHiddenMs
        Write-Host " Total: $($shutTotal)ms, WinHidden: $($winHide)ms" -ForegroundColor Green
        $shutdownResults[$sc] = $res
    } else {
        Write-Host " FAILED" -ForegroundColor Red
    }
}

# Aggregate Metrics
function Extract-MilestoneValues {
    param($results, [string]$milestone)
    $list = @()
    foreach ($r in $results) {
        $m = $r.Startup.milestones | Where-Object { $_.MilestoneName -eq $milestone }
        if ($m) { $list += [double]$m.ElapsedMillisecondsFromStart }
    }
    return $list
}

$summary = [PSCustomObject]@{
    Warm = [PSCustomObject]@{
        T7_MainWindow = Get-Stats (Extract-MilestoneValues $warmResults "T7:MainWindowConstructed")
        T9_Activate = Get-Stats (Extract-MilestoneValues $warmResults "T9:WindowActivateCalled")
        T11_ShellLoaded = Get-Stats (Extract-MilestoneValues $warmResults "T11:ShellLoaded")
        T12_Interactive = Get-Stats (Extract-MilestoneValues $warmResults "T12:UIInteractive")
        T13_CachedCards = Get-Stats (Extract-MilestoneValues $warmResults "T13:CachedCardsVisible")
        T15_BackgroundInit = Get-Stats (Extract-MilestoneValues $warmResults "T15:BackgroundInitFinished")
    }
    Cold = [PSCustomObject]@{
        T7_MainWindow = Get-Stats (Extract-MilestoneValues $coldResults "T7:MainWindowConstructed")
        T9_Activate = Get-Stats (Extract-MilestoneValues $coldResults "T9:WindowActivateCalled")
        T11_ShellLoaded = Get-Stats (Extract-MilestoneValues $coldResults "T11:ShellLoaded")
        T12_Interactive = Get-Stats (Extract-MilestoneValues $coldResults "T12:UIInteractive")
        T13_CachedCards = Get-Stats (Extract-MilestoneValues $coldResults "T13:CachedCardsVisible")
        T15_BackgroundInit = Get-Stats (Extract-MilestoneValues $coldResults "T15:BackgroundInitFinished")
    }
    ShutdownScenarios = $shutdownResults
}

$summaryJsonPath = Join-Path $OutputDir "benchmark-summary.json"
$summary | ConvertTo-Json -Depth 5 | Set-Content $summaryJsonPath -Encoding utf8

Write-Host "`n====================================================================" -ForegroundColor Cyan
Write-Host " BENCHMARK COMPLETE - RESULTS SUMMARY" -ForegroundColor Cyan
Write-Host " Summary File: $summaryJsonPath" -ForegroundColor Cyan
Write-Host "====================================================================" -ForegroundColor Cyan

Write-Host "`nWARM STARTUP (T12: FIRST INTERACTIVE FRAME):" -ForegroundColor White
Write-Host "  Min:    $($summary.Warm.T12_Interactive.Min) ms"
Write-Host "  Median: $($summary.Warm.T12_Interactive.Median) ms"
Write-Host "  P90:    $($summary.Warm.T12_Interactive.P90) ms"
Write-Host "  Max:    $($summary.Warm.T12_Interactive.Max) ms"

Write-Host "`nCOLD STARTUP (T12: FIRST INTERACTIVE FRAME):" -ForegroundColor White
Write-Host "  Min:    $($summary.Cold.T12_Interactive.Min) ms"
Write-Host "  Median: $($summary.Cold.T12_Interactive.Median) ms"
Write-Host "  P90:    $($summary.Cold.T12_Interactive.P90) ms"
Write-Host "  Max:    $($summary.Cold.T12_Interactive.Max) ms"

Write-Host "`nSHUTDOWN SCENARIOS (TOTAL MS):" -ForegroundColor White
foreach ($sc in $shutdownScenarios) {
    $res = $shutdownResults[$sc]
    Write-Host "  Scenario $sc : Total = $($res.Shutdown.totalElapsedMs) ms, WindowHide = $($res.Shutdown.windowHiddenMs) ms"
}

return $summary
