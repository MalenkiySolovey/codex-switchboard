# ==============================================================================
# Codex Switchboard - Release Sanitizer Gate
# ARCH-R6 Quality Infrastructure
# ==============================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$TargetPath
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$resolvedPath = [System.IO.Path]::GetFullPath($TargetPath)
if (-not (Test-Path $resolvedPath)) {
    Write-Error "Sanitizer target does not exist: $resolvedPath"
    exit 1
}

Write-Host "Running release sanitization scan on: $resolvedPath" -ForegroundColor Cyan

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
    "totp",
    "packages.lock.json",
    "Directory.Build.props",
    "Directory.Packages.props",
    "global.json",
    "*.cs",
    "*.csproj",
    "*.sln*",
    "ARCH_*.md"
)

$textExtensions = @(".txt", ".md", ".json", ".xml", ".config", ".deps", ".deps.json", ".runtimeconfig.json")
$developerUsername = "Malenkiy" + "_Solovey"

$violations = @()

$isZip = [System.IO.Path]::GetExtension($resolvedPath) -ieq ".zip"

if ($isZip) {
    Write-Host "Scanning ZIP archive entries and streams..." -ForegroundColor Yellow
    $zip = [System.IO.Compression.ZipFile]::OpenRead($resolvedPath)
    try {
        foreach ($entry in $zip.Entries) {
            $entryName = [System.IO.Path]::GetFileName($entry.FullName)
            
            # Check forbidden file patterns
            foreach ($pat in $forbiddenPatterns) {
                if ($entryName -like $pat) {
                    $violations += "Forbidden entry in ZIP [$pat]: $($entry.FullName)"
                }
            }

            # Check text contents for private username leak
            $ext = [System.IO.Path]::GetExtension($entryName)
            if ($ext -in $textExtensions -or $entryName.EndsWith(".deps.json") -or $entryName.EndsWith(".runtimeconfig.json")) {
                $stream = $entry.Open()
                try {
                    $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8)
                    try {
                        $content = $reader.ReadToEnd()
                        if ($content -match $developerUsername) {
                            $violations += "Developer username leak detected in ZIP entry: $($entry.FullName)"
                        }
                    }
                    finally {
                        $reader.Dispose()
                    }
                }
                finally {
                    $stream.Dispose()
                }
            }
        }
    }
    finally {
        $zip.Dispose()
    }
} else {
    Write-Host "Scanning directory files and contents..." -ForegroundColor Yellow
    $files = Get-ChildItem -Path $resolvedPath -Recurse -File
    foreach ($file in $files) {
        foreach ($pat in $forbiddenPatterns) {
            if ($file.Name -like $pat) {
                $violations += "Forbidden file pattern match [$pat]: $($file.FullName)"
            }
        }

        # Check for private username leak in text / metadata files
        if ($file.Extension -in $textExtensions -or $file.Name.EndsWith(".deps.json") -or $file.Name.EndsWith(".runtimeconfig.json")) {
            $text = [System.IO.File]::ReadAllText($file.FullName)
            if ($text -match $developerUsername) {
                $violations += "Developer username leak detected in: $($file.FullName)"
            }
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Host "`nSANITY SCAN FAILED: Forbidden items or leaks found in release target!" -ForegroundColor Red
    $violations | ForEach-Object { Write-Host " - $_" -ForegroundColor Red }
    Write-Error "Release packaging / sanity gate aborted due to sanitization scan violations ($($violations.Count) violations)."
    exit 1
}

Write-Host "Sanitization scan passed for [$resolvedPath] (0 forbidden artifacts, 0 secret leaks)." -ForegroundColor Green
exit 0
