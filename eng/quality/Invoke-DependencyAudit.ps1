# ==============================================================================
# Codex Switchboard - Dependency Vulnerability Audit Gate
# ARCH-R6 Quality Infrastructure
# ==============================================================================
[CmdletBinding()]
param(
    [string]$SolutionPath,
    [switch]$IncludeOutdated = $false
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($SolutionPath)) {
    $RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $SolutionPath = Join-Path $RepoRoot "CodexSwitcher.slnx"
}

$resolvedSolution = [System.IO.Path]::GetFullPath($SolutionPath)
Write-Host "Auditing dependencies for: $resolvedSolution" -ForegroundColor Cyan

# 1. Run machine-readable vulnerability check
Write-Host "Querying NuGet advisory database via dotnet CLI (JSON format)..." -ForegroundColor Yellow
$jsonRaw = & dotnet package list --project $resolvedSolution --include-transitive --vulnerable --format json --output-version 1 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to execute 'dotnet package list --vulnerable'. Exit code: $LASTEXITCODE. Output: $jsonRaw"
    exit $LASTEXITCODE
}

$report = $jsonRaw | ConvertFrom-Json

$criticalCount = 0
$highCount = 0
$moderateCount = 0
$lowCount = 0
$vulnerabilities = @()

if ($report.projects) {
    foreach ($proj in $report.projects) {
        $projName = [System.IO.Path]::GetFileName($proj.path)
        if ($proj.frameworks) {
            foreach ($fw in $proj.frameworks) {
                $packages = @()
                if ($fw.topLevelPackages) { $packages += $fw.topLevelPackages }
                if ($fw.transitivePackages) { $packages += $fw.transitivePackages }

                foreach ($pkg in $packages) {
                    if ($pkg.vulnerabilities) {
                        foreach ($vuln in $pkg.vulnerabilities) {
                            $sev = [string]$vuln.severity
                            if ($sev -match "^Critical") { $criticalCount++ }
                            elseif ($sev -match "^High") { $highCount++ }
                            elseif ($sev -match "^Moderate") { $moderateCount++ }
                            elseif ($sev -match "^Low") { $lowCount++ }
                            else { $lowCount++ }

                            $vulnerabilities += [PSCustomObject]@{
                                Project     = $projName
                                PackageId   = $pkg.id
                                Version     = $pkg.resolvedVersion
                                Severity    = $sev
                                AdvisoryUrl = $vuln.advisoryurl
                            }
                        }
                    }
                }
            }
        }
    }
}

Write-Host "--- Vulnerability Summary ---" -ForegroundColor Cyan
Write-Host "CRITICAL : $criticalCount" -ForegroundColor $(if ($criticalCount -gt 0) { "Red" } else { "Green" })
Write-Host "HIGH     : $highCount" -ForegroundColor $(if ($highCount -gt 0) { "Red" } else { "Green" })
Write-Host "MODERATE : $moderateCount" -ForegroundColor $(if ($moderateCount -gt 0) { "Yellow" } else { "Green" })
Write-Host "LOW      : $lowCount" -ForegroundColor $(if ($lowCount -gt 0) { "Yellow" } else { "Green" })

if ($vulnerabilities.Count -gt 0) {
    Write-Host "`nVulnerable packages detected:" -ForegroundColor Yellow
    $vulnerabilities | Format-Table -AutoSize | Out-String | Write-Host
}

# 2. Check outdated if requested
if ($IncludeOutdated) {
    Write-Host "`nQuerying outdated packages (informational)..." -ForegroundColor Yellow
    $outdatedRaw = & dotnet package list --project $resolvedSolution --outdated --format json --output-version 1 2>&1
    if ($LASTEXITCODE -eq 0) {
        $outdatedReport = $outdatedRaw | ConvertFrom-Json
        $outdatedList = @()
        if ($outdatedReport.projects) {
            foreach ($proj in $outdatedReport.projects) {
                $projName = [System.IO.Path]::GetFileName($proj.path)
                if ($proj.frameworks) {
                    foreach ($fw in $proj.frameworks) {
                        if ($fw.topLevelPackages) {
                            foreach ($pkg in $fw.topLevelPackages) {
                                $outdatedList += [PSCustomObject]@{
                                    Project   = $projName
                                    PackageId = $pkg.id
                                    Resolved  = $pkg.resolvedVersion
                                    Latest    = $pkg.latestVersion
                                }
                            }
                        }
                    }
                }
            }
        }
        if ($outdatedList.Count -gt 0) {
            Write-Host "Outdated Top-Level Packages:" -ForegroundColor Yellow
            $outdatedList | Format-Table -AutoSize | Out-String | Write-Host
        } else {
            Write-Host "All top-level packages are up to date." -ForegroundColor Green
        }
    }
}

# 3. Apply R6 security gate policy
if ($criticalCount -gt 0 -or $highCount -gt 0) {
    Write-Error "DEPENDENCY AUDIT FAILED: $criticalCount CRITICAL and $highCount HIGH vulnerability advisories found."
    exit 1
}

if ($moderateCount -gt 0) {
    Write-Warning "MODERATE advisories found ($moderateCount). Action recommended in next maintenance cycle."
}

Write-Host "Dependency vulnerability gate PASSED (0 Critical, 0 High)." -ForegroundColor Green
exit 0
