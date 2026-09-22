# ==============================================================================
# Codex Switchboard Release Build & Packaging Script
# ARCH-R6 Deterministic Packaging & Quality Gates
# ==============================================================================
[CmdletBinding()]
param(
    [string]$Version = "0.2.1-preview.9",
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
$ManifestPath = Join-Path $DistDir "manifest-sha256.txt"
$SanitizerScript = Join-Path $RepoRoot "eng\quality\Invoke-ReleaseSanitizer.ps1"

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
    Write-Host "[1/8] Cleaning dist directory..." -ForegroundColor Yellow
    Remove-Item -Path $DistDir -Recurse -Force
}
New-Item -Path $DistDir -ItemType Directory -Force | Out-Null
New-Item -Path $StagingDir -ItemType Directory -Force | Out-Null

# 2. Restore and build solution
Write-Host "[2/8] Restoring and building CodexSwitcher.slnx in Release mode..." -ForegroundColor Yellow
dotnet restore "$RepoRoot\CodexSwitcher.slnx" --locked-mode
if ($LASTEXITCODE -ne 0) { throw "Locked restore failed with exit code $LASTEXITCODE" }

dotnet build "$RepoRoot\CodexSwitcher.slnx" -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }

# 3. Run unit tests
if (-not $SkipTests) {
    Write-Host "[3/8] Executing offline test suite..." -ForegroundColor Yellow
    dotnet test "$RepoRoot\CodexSwitcher.slnx" -c Release --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE" }
} else {
    Write-Host "[3/8] Skipping tests (-SkipTests specified)..." -ForegroundColor DarkGray
}

# 4. Publish application
Write-Host "[4/8] Publishing unpackaged application (win-x64)..." -ForegroundColor Yellow
$publishArgs = @(
    "publish",
    "$RepoRoot\src\CodexSwitcher.App\CodexSwitcher.App.csproj",
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=false",
    "--no-restore",
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
    "--no-restore",
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
Write-Host "[5/8] Bundling documentation and licensing notices..." -ForegroundColor Yellow
$docFiles = @("LICENSE", "README.md", "README_RU.md", "THIRD_PARTY_NOTICES.md", "SECURITY.md", "PRIVACY.md", "CHANGELOG.md")
foreach ($doc in $docFiles) {
    $src = Join-Path $RepoRoot $doc
    if (Test-Path $src) {
        Copy-Item -Path $src -Destination $StagingDir -Force
    } else {
        Write-Warning "Documentation file not found: $doc"
    }
}

# 6. Scan staging directory for forbidden artifacts and secret leakage
Write-Host "[6/8] Running release sanitization scan on staging directory..." -ForegroundColor Yellow
& $SanitizerScript -TargetPath $StagingDir
if ($LASTEXITCODE -ne 0) { throw "Staging directory sanitization scan failed." }

# 7. Package deterministic ZIP archive
Write-Host "[7/8] Creating deterministic release archive: $ZipName..." -ForegroundColor Yellow
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

# Determine entry timestamp: repository commit timestamp or fixed epoch
$commitEpoch = $null
try {
    $gitTimestamp = & git -C $RepoRoot log -1 --format=%cI 2>$null
    if ($gitTimestamp) {
        $commitEpoch = [DateTimeOffset]::Parse($gitTimestamp.Trim())
    }
} catch {}
if (-not $commitEpoch) {
    $commitEpoch = [DateTimeOffset]::new(2026, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
}

if (Test-Path $ZipPath) {
    Remove-Item -Path $ZipPath -Force
}

# Deterministically collect and sort all staged files by relative path (ordinal)
$stagedFiles = Get-ChildItem -Path $StagingDir -Recurse -File | Sort-Object {
    $_.FullName.Substring($StagingDir.Length).TrimStart("\", "/").Replace("\", "/")
}

$zipFileStream = [System.IO.File]::Create($ZipPath)
try {
    $archive = [System.IO.Compression.ZipArchive]::new($zipFileStream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $stagedFiles) {
            $relPath = $file.FullName.Substring($StagingDir.Length).TrimStart("\", "/").Replace("\", "/")
            $entry = $archive.CreateEntry($relPath, [System.IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $commitEpoch

            $entryStream = $entry.Open()
            try {
                $fileStream = [System.IO.File]::OpenRead($file.FullName)
                try {
                    $fileStream.CopyTo($entryStream)
                }
                finally {
                    $fileStream.Dispose()
                }
            }
            finally {
                $entryStream.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}
finally {
    $zipFileStream.Dispose()
}

# 8. Sanitize packaged ZIP artifact (Order: publish -> package -> sanitize -> hash)
Write-Host "[8/8] Running release sanitization scan on packaged ZIP artifact..." -ForegroundColor Yellow
& $SanitizerScript -TargetPath $ZipPath
if ($LASTEXITCODE -ne 0) { throw "Packaged artifact sanitization scan failed." }

# Compute SHA-256 and write checksum file
$hash = (Get-FileHash -Path $ZipPath -Algorithm SHA256).Hash
$sumLine = "$hash  $ZipName"
[System.IO.File]::WriteAllText($SumsPath, "$sumLine`n", [System.Text.Encoding]::UTF8)

# Generate payload manifest (relative path + SHA256 for each staged file)
$manifestLines = @()
foreach ($file in $stagedFiles) {
    $relPath = $file.FullName.Substring($StagingDir.Length).TrimStart("\", "/").Replace("\", "/")
    $fileHash = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash
    $manifestLines += "$fileHash  $relPath"
}
[System.IO.File]::WriteAllLines($ManifestPath, $manifestLines, [System.Text.Encoding]::UTF8)

# Clean temporary staging directory
Remove-Item -Path $StagingDir -Recurse -Force

$zipItem = Get-Item $ZipPath
$zipSizeMb = [math]::Round($zipItem.Length / 1MB, 2)

Write-Host "==================================================" -ForegroundColor Green
Write-Host " Release Package Created Successfully (Deterministic)" -ForegroundColor Green
Write-Host "==================================================" -ForegroundColor Green
Write-Host "Archive:  $ZipPath"
Write-Host "Size:     $zipSizeMb MB ($($zipItem.Length) bytes)"
Write-Host "SHA-256:  $hash"
Write-Host "Checksum: $SumsPath"
Write-Host "Manifest: $ManifestPath"
