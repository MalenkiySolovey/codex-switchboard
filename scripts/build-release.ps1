# ==============================================================================
# Codex Switchboard Release Build & Packaging Script
# ==============================================================================
[CmdletBinding()]
param(
    [string]$Version = "0.1.3",
    [switch]$SkipTests = $false,
    [switch]$PublicRelease = $false
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$DistDir = Join-Path $RepoRoot "dist"
$StagingDir = Join-Path $DistDir "staging"
$ZipName = "CodexSwitchboard-$Version-win-x64.zip"
$ZipPath = Join-Path $DistDir $ZipName
$SumsPath = Join-Path $DistDir "SHA256SUMS.txt"

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host " Building Codex Switchboard v$Version (win-x64)" -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

# Check publication release gate
if ($PublicRelease) {
    if ($env:CODEX_MONITOR_REDISTRIBUTION_CONFIRMED -ne "1") {
        Write-Host "ERROR: Public release is blocked!" -ForegroundColor Red
        Write-Host "PRIVATE_UPSTREAM_PUBLICATION_RIGHTS requires explicit confirmation from @NeMoSova19." -ForegroundColor Red
        Write-Host "Set env var CODEX_MONITOR_REDISTRIBUTION_CONFIRMED=1 once written authorization is obtained." -ForegroundColor Red
        throw "Aborting public release build due to unresolved publication rights gate."
    }
    Write-Host "Mode: PUBLIC RELEASE (Authorized)" -ForegroundColor Green
} else {
    Write-Host "Mode: LOCAL RELEASE CANDIDATE ONLY" -ForegroundColor Yellow
    Write-Host "Publication Gate: PRIVATE_UPSTREAM_PUBLICATION_RIGHTS = USER_CONFIRMATION_REQUIRED" -ForegroundColor Yellow
}

# 1. Clean previous dist
if (Test-Path $DistDir) {
    Write-Host "[1/7] Cleaning dist directory..." -ForegroundColor Yellow
    Remove-Item -Path $DistDir -Recurse -Force
}
New-Item -Path $DistDir -ItemType Directory -Force | Out-Null
New-Item -Path $StagingDir -ItemType Directory -Force | Out-Null

# 2. Build solution
Write-Host "[2/7] Building CodexSwitcher.slnx in Release mode..." -ForegroundColor Yellow
dotnet build "$RepoRoot\CodexSwitcher.slnx" -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }

# 3. Run unit tests
if (-not $SkipTests) {
    Write-Host "[3/7] Executing offline test suite..." -ForegroundColor Yellow
    dotnet test "$RepoRoot\CodexSwitcher.slnx" -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE" }
} else {
    Write-Host "[3/7] Skipping tests (-SkipTests specified)..." -ForegroundColor DarkGray
}

# 4. Publish application
Write-Host "[4/7] Publishing unpackaged application (win-x64)..." -ForegroundColor Yellow
$publishArgs = @(
    "publish",
    "$RepoRoot\src\CodexSwitcher.App\CodexSwitcher.App.csproj",
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=false",
    "-o", $StagingDir
)
dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE" }

Write-Host "Publishing self-contained KeyBroker payload..." -ForegroundColor Yellow
$brokerPublishArgs = @(
    "publish",
    "$RepoRoot\src\CodexSwitchboard.KeyBroker\CodexSwitchboard.KeyBroker.csproj",
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-o", $StagingDir
)
dotnet @brokerPublishArgs
if ($LASTEXITCODE -ne 0) { throw "Broker publish failed with exit code $LASTEXITCODE" }

# Verify primary executable name
$expectedExe = Join-Path $StagingDir "CodexSwitchboard.exe"
if (-not (Test-Path $expectedExe)) {
    throw "Expected binary '$expectedExe' not found in publish staging directory."
}

$expectedBrokerExe = Join-Path $StagingDir "CodexSwitchboard.KeyBroker.exe"
if (-not (Test-Path $expectedBrokerExe)) {
    throw "Expected binary '$expectedBrokerExe' not found in publish staging directory."
}

# 5. Bundle legal & documentation files
Write-Host "[5/7] Bundling documentation and licensing notices..." -ForegroundColor Yellow
$docFiles = @("LICENSE", "README.md", "README_RU.md", "THIRD_PARTY_NOTICES.md", "SECURITY.md", "PRIVACY.md", "CHANGELOG.md")
foreach ($doc in $docFiles) {
    $src = Join-Path $RepoRoot $doc
    if (Test-Path $src) {
        Copy-Item -Path $src -Destination $StagingDir -Force
    } else {
        Write-Warning "Documentation file not found: $doc"
    }
}

# 6. Scan for forbidden artifacts and secret leakage
Write-Host "[6/7] Running release sanitization scan..." -ForegroundColor Yellow
$forbiddenPatterns = @(
    "codex.exe",
    "auth.json",
    "*.env",
    "id_rsa*",
    "id_ed25519*",
    "audit.log",
    "usage-cache.json",
    "profiles.json",
    "*totp*.bin",
    "totp"
)

$violations = @()
$stagedFiles = Get-ChildItem -Path $StagingDir -Recurse -File

foreach ($file in $stagedFiles) {
    foreach ($pat in $forbiddenPatterns) {
        if ($file.Name -like $pat) {
            $violations += "Forbidden file pattern match [$pat]: $($file.FullName)"
        }
    }

    # Check for private username leak in text / markdown files
    if ($file.Extension -in @(".txt", ".md", ".json", ".xml", ".config", ".deps", ".runtimeconfig")) {
        $text = [System.IO.File]::ReadAllText($file.FullName)
        if ($text -match "Malenkiy" + "_Solovey") {
            $violations += "Developer username leak detected in: $($file.FullName)"
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Host "SANITY SCAN FAILED: Forbidden items found in release staging!" -ForegroundColor Red
    $violations | ForEach-Object { Write-Host " - $_" -ForegroundColor Red }
    throw "Release packaging aborted due to sanitization scan violations."
}
Write-Host "Sanitization scan passed (0 forbidden artifacts, 0 secret leaks)." -ForegroundColor Green

# 7. Package ZIP and compute SHA-256
Write-Host "[7/7] Creating release archive: $ZipName..." -ForegroundColor Yellow
Compress-Archive -Path "$StagingDir\*" -DestinationPath $ZipPath -Force

$hash = (Get-FileHash -Path $ZipPath -Algorithm SHA256).Hash
$sumLine = "$hash  $ZipName"
[System.IO.File]::WriteAllText($SumsPath, "$sumLine`n", [System.Text.Encoding]::UTF8)

# Clean temporary staging directory
Remove-Item -Path $StagingDir -Recurse -Force

$zipItem = Get-Item $ZipPath
$zipSizeMb = [math]::Round($zipItem.Length / 1MB, 2)

Write-Host "==================================================" -ForegroundColor Green
Write-Host " Release Package Created Successfully" -ForegroundColor Green
Write-Host "==================================================" -ForegroundColor Green
Write-Host "Archive:  $ZipPath"
Write-Host "Size:     $zipSizeMb MB ($($zipItem.Length) bytes)"
Write-Host "SHA-256:  $hash"
Write-Host "Checksum: $SumsPath"
