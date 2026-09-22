# ==============================================================================
# Codex Switchboard - Repository Quality Gates Verification
# ARCH-R6 Single Developer & CI Verification Command
# ==============================================================================
[CmdletBinding()]
param(
    [string]$Version = "0.2.1-preview.9",
    [switch]$SkipPackaging = $false
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$SolutionPath = Join-Path $RepoRoot "CodexSwitcher.slnx"
$GlobalJsonPath = Join-Path $RepoRoot "global.json"
$DependencyAuditScript = Join-Path $RepoRoot "eng\quality\Invoke-DependencyAudit.ps1"
$BuildReleaseScript = Join-Path $RepoRoot "scripts\build-release.ps1"

Write-Host "====================================================================" -ForegroundColor Cyan
Write-Host " Codex Switchboard - Repository-Wide Quality Gates Verification" -ForegroundColor Cyan
Write-Host " Root: $RepoRoot" -ForegroundColor Cyan
Write-Host "====================================================================" -ForegroundColor Cyan

$gateCount = if ($SkipPackaging) { 6 } else { 7 }
$currentGate = 1

function Run-Gate {
    param(
        [string]$Name,
        [scriptblock]$Action
    )
    Write-Host "`n[$script:currentGate/$script:gateCount] GATE: $Name" -ForegroundColor Yellow
    $startTime = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        & $Action
        $startTime.Stop()
        Write-Host "  -> PASSED in $($startTime.Elapsed.TotalSeconds.ToString('F1'))s" -ForegroundColor Green
        $script:currentGate++
    }
    catch {
        $startTime.Stop()
        Write-Host "`n  -> FAILED: $Name" -ForegroundColor Red
        Write-Error $_
        exit 1
    }
}

# --- GATE 1: SDK Toolchain Pinning ---
Run-Gate "SDK Toolchain Verification (global.json)" {
    if (-not (Test-Path $GlobalJsonPath)) {
        throw "global.json not found at $GlobalJsonPath"
    }
    $globalJson = Get-Content $GlobalJsonPath -Raw | ConvertFrom-Json
    $expectedSdk = $globalJson.sdk.version
    $actualSdk = (& dotnet --version).Trim()
    Write-Host "  Expected SDK: $expectedSdk (rollForward: $($globalJson.sdk.rollForward))"
    Write-Host "  Detected SDK: $actualSdk"
    if ($actualSdk -ne $expectedSdk) {
        Write-Warning "Detected SDK ($actualSdk) differs from exact pinned SDK ($expectedSdk)."
    }
}

# --- GATE 2: Locked Dependency Restore ---
Run-Gate "Locked Dependency Restore (--locked-mode)" {
    & dotnet restore $SolutionPath --locked-mode
    if ($LASTEXITCODE -ne 0) {
        throw "Locked restore failed with exit code $LASTEXITCODE."
    }
}

# --- GATE 3: Code Formatting Verification ---
Run-Gate "Format Verification (--verify-no-changes)" {
    & dotnet format $SolutionPath --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Format verification failed. Source does not conform to .editorconfig baseline."
    }
}

# --- GATE 4: Dependency Vulnerability Audit ---
Run-Gate "Machine-Readable Dependency Vulnerability Audit" {
    & powershell -ExecutionPolicy Bypass -File $DependencyAuditScript -SolutionPath $SolutionPath
    if ($LASTEXITCODE -ne 0) {
        throw "Dependency vulnerability audit failed with exit code $LASTEXITCODE."
    }
}

# --- GATE 5: Release Compilation & Static Analysis ---
Run-Gate "Release Compilation & Static Analysis (WarningsAsErrors)" {
    & dotnet build $SolutionPath -c Release --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Release build failed with exit code $LASTEXITCODE."
    }
}

# --- GATE 6: Full Offline Test Suite & Architecture Invariants ---
Run-Gate "Regression & Architecture Tests (653+ Offline Suite)" {
    & dotnet test $SolutionPath -c Release --no-build --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Test suite execution failed with exit code $LASTEXITCODE."
    }
}

# --- GATE 7: Release Packaging & Dual Sanitization (Optional) ---
if (-not $SkipPackaging) {
    Run-Gate "Deterministic Release Packaging & Artifact Sanitization" {
        & powershell -ExecutionPolicy Bypass -File $BuildReleaseScript -Version $Version -SkipTests
        if ($LASTEXITCODE -ne 0) {
            throw "Deterministic release packaging or artifact sanitization failed."
        }
    }
}

# --- Final Check: Git Diff & Hygiene ---
Write-Host "`nVerifying git diff and working tree hygiene..." -ForegroundColor Yellow
$diffOutput = & git -C $RepoRoot diff --check
if ($LASTEXITCODE -ne 0) {
    Write-Error "git diff --check detected whitespace or conflict marker issues (exit code $LASTEXITCODE)."
    exit 1
}

Write-Host "====================================================================" -ForegroundColor Green
Write-Host " ALL REPOSITORY QUALITY GATES PASSED (Exit 0)" -ForegroundColor Green
Write-Host "====================================================================" -ForegroundColor Green
exit 0
