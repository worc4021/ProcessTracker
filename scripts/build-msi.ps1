#Requires -Version 5.1
<#
.SYNOPSIS
    Publishes ProcessTracker and builds its MSI and user-mode zip: <OutDir>\ProcessTracker-<X.Y.Z>-x64.msi and
    <OutDir>\ProcessTracker-<X.Y.Z>-x64.zip.

.DESCRIPTION
      1. Publishes the service self-contained into <OutDir>\stage\ and proctrack single-file self-contained into
         <OutDir>\stage\bin\ (win-x64).
      2. Copies installer\upgrade.ps1 into the stage.
      3. Builds installer\ProcessTracker.Installer.wixproj with -p:StageDir=<stage> -p:ProductVersion=<version.txt>.
      4. Copies the MSI to <OutDir> and writes its full path to the output (the only output; progress goes to the host).
      5. Zips the stage without upgrade.ps1 (the machine install's auto-upgrade script) into <OutDir>: the user-mode
         install (proctrack user install) extracts it, ProcessTracker.Service.exe at the root and bin\proctrack.exe.
    -SkipPublish skips steps 1 and 2 and packages the stage that is already there, e.g. after signing its binaries.
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

.PARAMETER SkipPublish
    Package the existing <OutDir>\stage again (MSI and zip) without publishing into it.
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [ValidatePattern('^[0-9A-Za-z.-]*$')]
    [string]$DevSuffix = '',
    [string]$OutDir = 'artifacts',
    [switch]$SkipPublish
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

# Zips the stage, except the top-level upgrade.ps1, with '/' entry separators (Compress-Archive in Windows PowerShell
# 5.1 writes '\', which other unzip tools take as part of the file name).
function New-StageZip([string]$Stage, [string]$Zip) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $Zip) { Remove-Item -LiteralPath $Zip -Force }
    $root = [IO.Path]::GetFullPath($Stage).TrimEnd('\') + '\'
    $archive = [IO.Compression.ZipFile]::Open($Zip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in (Get-ChildItem -LiteralPath $Stage -Recurse -File | Sort-Object FullName)) {
            $entry = $file.FullName.Substring($root.Length).Replace('\', '/')
            if ($entry -ieq 'upgrade.ps1') { continue }
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry,
                [IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally { $archive.Dispose() }
}

try {
    $version = (Get-Content -LiteralPath (Join-Path $repo 'version.txt') -Raw).Trim()
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "version.txt must hold a numeric X.Y.Z version (got '$version')." }

    if ($SkipPublish) {
        if (-not (Test-Path -LiteralPath (Join-Path $stage 'ProcessTracker.Service.exe'))) { throw "-SkipPublish needs a published stage in $stage." }
    }
    else {
        # A stale stage would put files from an earlier build into the MSI.
        if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
        New-Item -ItemType Directory -Path $stage -Force | Out-Null

        $common = @('-c', $Configuration, '-r', 'win-x64', '--self-contained', "-p:DevSuffix=$DevSuffix", '-warnaserror')
        Write-Host "== Publishing the service into $stage" -ForegroundColor Cyan
        Invoke-Dotnet publish (Join-Path $repo 'src\ProcessTracker.Service\ProcessTracker.Service.csproj') @common -o $stage
        Write-Host "== Publishing proctrack into $stage\bin" -ForegroundColor Cyan
        Invoke-Dotnet publish (Join-Path $repo 'src\ProcessTracker.Cli\ProcessTracker.Cli.csproj') @common '-p:PublishSingleFile=true' -o (Join-Path $stage 'bin')
        Copy-Item -LiteralPath (Join-Path $repo 'installer\upgrade.ps1') -Destination $stage
    }

    # --no-incremental: the harvested stage is not an MSBuild input, so an incremental build could keep an older MSI.
    Write-Host "== Building the MSI ($version)" -ForegroundColor Cyan
    $wixOut = Join-Path $out 'wix'
    Invoke-Dotnet build $wixproj -c $Configuration --no-incremental '-warnaserror' "-p:StageDir=$stage" "-p:ProductVersion=$version" -o $wixOut

    $name = "ProcessTracker-$version-x64.msi"
    $msi = Join-Path $out $name
    Copy-Item -LiteralPath (Join-Path $wixOut $name) -Destination $msi -Force
    Write-Host "Built $msi" -ForegroundColor Green

    $zip = Join-Path $out "ProcessTracker-$version-x64.zip"
    Write-Host "== Zipping the stage into $zip" -ForegroundColor Cyan
    New-StageZip $stage $zip
    Write-Host "Built $zip" -ForegroundColor Green
    Write-Output $msi
    exit 0
}
catch {
    Write-Host "build-msi.ps1 failed: $_" -ForegroundColor Red
    exit 1
}
