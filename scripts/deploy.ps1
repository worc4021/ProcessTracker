#Requires -Version 5.1
<#
.SYNOPSIS
    Tests, publishes and deploys ProcessTracker to the installed service, rolling back if the new version is unhealthy.

.DESCRIPTION
    Run from an elevated PowerShell in the checkout, after install.ps1 has registered the service.
      1. dotnet test (stops on failure).
      2. Version = VersionPrefix + "-local.yyyyMMddHHmm".
      3. Publishes the service, the CLI (proctrack) and the updater, self-contained win-x64, into <Root>\versions\<version>.
      4. Copies the updater to %TEMP% and runs it from there (it must not run from a folder it swaps). The updater stops
         the service, points <Root>\current at the new folder, starts it and waits for /api/health to report the new
         version; on failure it points back to the previous version.
      5. Prints the outcome.

    Exit codes: 0 Upgraded, 1 RolledBack, 2 RollbackFailed (the updater's), 3 nothing changed (not elevated, service not
    installed, tests or publish failed).

.PARAMETER Root
    Install root holding versions\ and the current junction.

.PARAMETER SimulateUnhealthy
    Builds the service with SIMULATE_UNHEALTHY, so /api/health reports version "unhealthy" and the deploy rolls back.
    For testing the rollback only.

.PARAMETER TimeoutSeconds
    How long the updater waits for the new version to report healthy.

.PARAMETER PublishOnly
    Steps 1-3 only; writes the new version to the pipeline. Used by install.ps1.
#>
[CmdletBinding()]
param(
    [string]$Root = 'C:\Program Files\ProcessTracker',
    [switch]$SimulateUnhealthy,
    [ValidateRange(1, 600)]
    [int]$TimeoutSeconds = 15,
    [switch]$PublishOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'deploy.ps1 must run from an elevated PowerShell (Run as administrator). Nothing was changed.' -ForegroundColor Red
    exit 3
}

$repo = Split-Path -Parent $PSScriptRoot

# Runs dotnet with its output on the console (never in the pipeline, which carries -PublishOnly's result).
function Invoke-Dotnet {
    & dotnet @args | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args -join ' ') failed with exit code $LASTEXITCODE." }
}

$versionDir = $null
try {
    if (-not $PublishOnly -and -not (Get-Service -Name ProcessTracker -ErrorAction SilentlyContinue)) {
        throw 'The ProcessTracker service is not installed; run install.ps1 first.'
    }

    Write-Host '== Running tests' -ForegroundColor Cyan
    Invoke-Dotnet test --solution (Join-Path $repo 'ProcessTracker.slnx')

    $prefix = (& dotnet msbuild (Join-Path $repo 'src\ProcessTracker.Service\ProcessTracker.Service.csproj') -getProperty:VersionPrefix)
    if ($LASTEXITCODE -ne 0 -or -not $prefix) { throw 'Could not read VersionPrefix.' }
    $version = "$("$prefix".Trim())-local.$((Get-Date).ToString('yyyyMMddHHmm'))"

    $candidate = Join-Path $Root "versions\$version"
    if (Test-Path -LiteralPath $candidate) { throw "$candidate already exists; wait a minute and deploy again." }
    New-Item -ItemType Directory -Path $candidate -Force | Out-Null
    $versionDir = $candidate

    Write-Host "== Publishing $version to $versionDir" -ForegroundColor Cyan
    $publish = @('-c', 'Release', '-r', 'win-x64', '--self-contained', "-p:Version=$version", '-o', $versionDir)
    $serviceArgs = $publish
    if ($SimulateUnhealthy) { $serviceArgs = $publish + '-p:SimulateUnhealthy=true' }
    Invoke-Dotnet publish (Join-Path $repo 'src\ProcessTracker.Service') @serviceArgs
    Invoke-Dotnet publish (Join-Path $repo 'src\ProcessTracker.Cli') @publish
    Invoke-Dotnet publish (Join-Path $repo 'src\ProcessTracker.Updater') @publish
}
catch {
    if ($versionDir -and (Test-Path -LiteralPath $versionDir)) {
        Remove-Item -LiteralPath $versionDir -Recurse -Force -ErrorAction SilentlyContinue # a plain folder, never a junction
    }
    if ($PublishOnly) { throw }
    Write-Host "Deploy failed, nothing changed: $_" -ForegroundColor Red
    exit 3
}

if ($PublishOnly) { return $version }

Write-Host "== Upgrading the service to $version" -ForegroundColor Cyan
$updaterDir = Join-Path ([IO.Path]::GetTempPath()) "ProcessTracker.Updater.$version"
New-Item -ItemType Directory -Path $updaterDir -Force | Out-Null
$updater = Join-Path $updaterDir 'ProcessTracker.Updater.exe'
Copy-Item -LiteralPath (Join-Path $versionDir 'ProcessTracker.Updater.exe') -Destination $updater -Force

& $updater --root $Root --version $version --timeout $TimeoutSeconds
$code = $LASTEXITCODE
Remove-Item -LiteralPath $updaterDir -Recurse -Force -ErrorAction SilentlyContinue

switch ($code) {
    0 { Write-Host "Outcome: Upgraded to $version." -ForegroundColor Green }
    1 { Write-Host "Outcome: RolledBack. $version was unhealthy; the previous version is serving again." -ForegroundColor Yellow }
    2 { Write-Host 'Outcome: RollbackFailed. Neither version reported healthy; check the service and Event Viewer.' -ForegroundColor Red }
    default { Write-Host "Outcome: the updater changed nothing (exit code $code)." -ForegroundColor Red }
}
exit $code
