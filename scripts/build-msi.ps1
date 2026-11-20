#Requires -Version 5.1
<#
.SYNOPSIS
    Publishes ProcessTracker and builds its MSI: <OutDir>\ProcessTracker-<X.Y.Z>-x64.msi.

.DESCRIPTION
      1. Publishes the service self-contained into <OutDir>\stage\ and proctrack single-file self-contained into
         <OutDir>\stage\bin\ (win-x64).
      2. Copies installer\upgrade.ps1 into the stage.
      3. Builds installer\ProcessTracker.Installer.wixproj with -p:StageDir=<stage> -p:ProductVersion=<version.txt>.
      4. Copies the MSI to <OutDir> and writes its full path to the output (the only output; progress goes to the host).
    The MSI's ProductVersion is always the numeric X.Y.Z from version.txt; -DevSuffix only changes the
    InformationalVersion that /api/health and proctrack report (X.Y.Z-<DevSuffix>). The MSI upgrades any installed
    build, including one of the same version (MajorUpgrade AllowDowngrades).

    Exit codes: 0 built, 1 failed.

.PARAMETER Configuration
    Build configuration (Release for anything that is installed).

.PARAMETER DevSuffix
    Appended to the InformationalVersion, e.g. dev.202611201200. Empty for release builds.

.PARAMETER OutDir
    Output folder, relative to the repository root unless rooted.
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [ValidatePattern('^[0-9A-Za-z.-]*$')]
    [string]$DevSuffix = '',
    [string]$OutDir = 'artifacts'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$out = if ([IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $repo $OutDir }
$stage = Join-Path $out 'stage'
$wixproj = Join-Path $repo 'installer\ProcessTracker.Installer.wixproj'

function Invoke-Dotnet {
    & dotnet @args | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args[0]) failed with exit code $LASTEXITCODE." }
}

try {
    $version = (Get-Content -LiteralPath (Join-Path $repo 'version.txt') -Raw).Trim()
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "version.txt must hold a numeric X.Y.Z version (got '$version')." }

    # A stale stage would put files from an earlier build into the MSI.
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    New-Item -ItemType Directory -Path $stage -Force | Out-Null

    $common = @('-c', $Configuration, '-r', 'win-x64', '--self-contained', "-p:DevSuffix=$DevSuffix", '-warnaserror')
    Write-Host "== Publishing the service into $stage" -ForegroundColor Cyan
    Invoke-Dotnet publish (Join-Path $repo 'src\ProcessTracker.Service\ProcessTracker.Service.csproj') @common -o $stage
    Write-Host "== Publishing proctrack into $stage\bin" -ForegroundColor Cyan
    Invoke-Dotnet publish (Join-Path $repo 'src\ProcessTracker.Cli\ProcessTracker.Cli.csproj') @common '-p:PublishSingleFile=true' -o (Join-Path $stage 'bin')
    Copy-Item -LiteralPath (Join-Path $repo 'installer\upgrade.ps1') -Destination $stage

    # --no-incremental: the harvested stage is not an MSBuild input, so an incremental build could keep an older MSI.
    Write-Host "== Building the MSI ($version)" -ForegroundColor Cyan
    $wixOut = Join-Path $out 'wix'
    Invoke-Dotnet build $wixproj -c $Configuration --no-incremental '-warnaserror' "-p:StageDir=$stage" "-p:ProductVersion=$version" -o $wixOut

    $name = "ProcessTracker-$version-x64.msi"
    $msi = Join-Path $out $name
    Copy-Item -LiteralPath (Join-Path $wixOut $name) -Destination $msi -Force
    Write-Host "Built $msi" -ForegroundColor Green
    Write-Output $msi
    exit 0
}
catch {
    Write-Host "build-msi.ps1 failed: $_" -ForegroundColor Red
    exit 1
}
