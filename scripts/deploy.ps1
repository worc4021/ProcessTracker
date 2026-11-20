#Requires -Version 5.1
<#
.SYNOPSIS
    Tests, builds the MSI and installs it over whatever is installed (a local development deploy).

.DESCRIPTION
    Run from an elevated PowerShell in the checkout.
      1. dotnet test, without the timing-sensitive tests (trait Category=Perf), so a loaded machine cannot fail a
         deploy (stops on failure; skip with -SkipTests).
      2. scripts\build-msi.ps1 -DevSuffix dev.<yyyyMMddHHmm>: the MSI carries the version in version.txt, and the
         service and proctrack report X.Y.Z-dev.<yyyyMMddHHmm>.
      3. msiexec /i <msi> /qn /l*v artifacts\deploy.log. The MSI upgrades any installed build, including a newer one
         (MajorUpgrade AllowDowngrades), so a dev build always replaces the installed one.
      4. Polls http://127.0.0.1:47811/api/health for up to 60 s until it reports X.Y.Z-dev.<stamp> with a collector tick
         younger than 5 s.

    A fresh machine gets a first install this way too. A phase 1 (script-installed) copy must be removed first with
    scripts\uninstall-legacy.ps1; this script refuses while it is present.

    Exit codes: 0 installed and healthy; 1 installed but unhealthy (reinstall the last release from
    https://github.com/worc4021/ProcessTracker/releases/latest); 3 nothing changed (not elevated, phase 1 install
    present, tests or build failed, or msiexec failed, in which case the MSI has rolled back).

.PARAMETER SkipTests
    Skip step 1.
#>
[CmdletBinding()]
param(
    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'deploy.ps1 must run from an elevated PowerShell (Run as administrator). Nothing was changed.' -ForegroundColor Red
    exit 3
}

$repo = Split-Path -Parent $PSScriptRoot
$releasesUrl = 'https://github.com/worc4021/ProcessTracker/releases/latest'
$healthUrl = 'http://127.0.0.1:47811/api/health'
$healthTimeoutSeconds = 60

# The MSI's launch condition refuses a phase 1 install; say so before spending minutes on tests and a build.
$legacyExe = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'ProcessTracker\current\ProcessTracker.Service.exe'
if (Test-Path -LiteralPath $legacyExe) {
    Write-Host 'A phase 1 install (script-installed, with a "current" junction) is present. Run scripts\uninstall-legacy.ps1 from an elevated PowerShell first (it keeps your data). Nothing was changed.' -ForegroundColor Red
    exit 3
}

$stamp = (Get-Date).ToString('yyyyMMddHHmm')
$devSuffix = "dev.$stamp"
$artifacts = Join-Path $repo 'artifacts'
$log = Join-Path $artifacts 'deploy.log'

try {
    if (-not $SkipTests) {
        Write-Host '== Running tests (without the timing-sensitive Category=Perf ones)' -ForegroundColor Cyan
        & dotnet test --solution (Join-Path $repo 'ProcessTracker.slnx') '--' --filter-not-trait 'Category=Perf' | Out-Host # '--' quoted: a bare -- would be eaten by PowerShell
        if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE." }
    }

    Write-Host "== Building the MSI ($devSuffix)" -ForegroundColor Cyan
    $msi = & (Join-Path $PSScriptRoot 'build-msi.ps1') -DevSuffix $devSuffix | Select-Object -Last 1
    $buildCode = $LASTEXITCODE
    if ($buildCode -ne 0 -or -not $msi -or -not (Test-Path -LiteralPath ([string]$msi))) { throw "build-msi.ps1 failed (exit code $buildCode)." }
    $msi = [string]$msi

    $version = (Get-Content -LiteralPath (Join-Path $repo 'version.txt') -Raw).Trim()
    $expected = "$version-$devSuffix"
}
catch {
    Write-Host "Deploy failed, nothing changed: $_" -ForegroundColor Red
    exit 3
}

Write-Host "== Installing $msi (log: $log)" -ForegroundColor Cyan
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$install = Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/i', "`"$msi`"", '/qn', '/l*v', "`"$log`"") -Wait -PassThru
if ($install.ExitCode -ne 0 -and $install.ExitCode -ne 3010) {
    Write-Host "msiexec failed with exit code $($install.ExitCode); the MSI rolled back and nothing changed. See $log." -ForegroundColor Red
    exit 3
}
if ($install.ExitCode -eq 3010) { Write-Warning 'The install asks for a restart (exit code 3010).' }

Write-Host "== Waiting up to $healthTimeoutSeconds s for $expected to report healthy" -ForegroundColor Cyan
$deadline = (Get-Date).AddSeconds($healthTimeoutSeconds)
$last = 'no answer yet'
while ((Get-Date) -lt $deadline) {
    try {
        $health = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 3
        $last = "version $($health.version), tickAgeSeconds $($health.tickAgeSeconds)"
        if ($health.version -eq $expected -and $null -ne $health.tickAgeSeconds -and [double]$health.tickAgeSeconds -lt 5) {
            Write-Host "Healthy: $expected." -ForegroundColor Green
            exit 0
        }
    }
    catch { $last = $_.Exception.Message }
    Start-Sleep -Seconds 1
}

Write-Host "Installed $expected but it is not healthy after $healthTimeoutSeconds s (last: $last). See the Application event log (source ProcessTracker) and $log." -ForegroundColor Red
Write-Host "To go back to the last release, install its MSI from $releasesUrl" -ForegroundColor Yellow
exit 1
