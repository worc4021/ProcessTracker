<#
.SYNOPSIS
    Upgrades ProcessTracker through winget. Run daily and at boot by the SYSTEM scheduled task \ProcessTracker\AutoUpgrade.

.DESCRIPTION
    Windows PowerShell 5.1, no dependencies. Lives in the admin-only install folder and is started by
    System32\WindowsPowerShell\v1.0\powershell.exe (never by proctrack.exe: the MSI upgrade replaces bin\proctrack.exe,
    and a running proctrack would lock it).
      1. Resolves winget.exe the same way as WingetLocator in proctrack: SYSTEM has no per-user alias, so the newest
         Microsoft.DesktopAppInstaller_<version>_x64__8wekyb3d8bbwe folder under C:\Program Files\WindowsApps wins.
      2. Runs winget upgrade --id worc4021.ProcessTracker --exact --silent --scope machine ...
      3. Writes the outcome to the Application event log (source ProcessTracker), and to the output:
           1000 up to date | 1001 upgraded (or reinstalled) | 1002 failed with code N | 1003 winget not found
         Without the event source only the output is written.
    Exits 0 except on an internal script error (1).

.PARAMETER WhatIf
    Only resolve winget and print the command line that would run. Changes nothing.
#>
param(
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
$packageId = 'worc4021.ProcessTracker'
$source = 'ProcessTracker'
$noUpdateCode = -1978335189   # 0x8A15002B: no applicable update
$noPackageCode = -1978335212  # 0x8A150014: no installed package found matching the input criteria

function Write-Outcome {
    param([int]$EventId, [string]$Message, [string]$Type = 'Information')
    Write-Output $Message
    if ($WhatIf) { return }
    try {
        if ([System.Diagnostics.EventLog]::SourceExists($source)) {
            Write-EventLog -LogName Application -Source $source -EventId $EventId -EntryType $Type -Message $Message
        }
    }
    catch { Write-Output "Could not write to the event log: $($_.Exception.Message)" }
}

function Find-Winget {
    # Returns @{ Path = <path or $null>; Denied = <bool> }
    $root = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'WindowsApps'
    try {
        $best = $null
        $bestVersion = $null
        foreach ($dir in [System.IO.Directory]::GetDirectories($root)) {
            $name = [System.IO.Path]::GetFileName($dir)
            if ($name -notmatch '^Microsoft\.DesktopAppInstaller_(?<v>[^_]+)_x64__8wekyb3d8bbwe$') { continue }
            $v = $null
            if (-not [version]::TryParse($Matches['v'], [ref]$v)) { continue }
            if ($null -eq $bestVersion -or $v -gt $bestVersion) { $best = $dir; $bestVersion = $v }
        }
        if ($null -eq $best) { return @{ Path = $null; Denied = $false } }
        $exe = Join-Path $best 'winget.exe'
        if (-not (Test-Path -LiteralPath $exe)) { return @{ Path = $null; Denied = $false } }
        return @{ Path = $exe; Denied = $false }
    }
    catch [System.UnauthorizedAccessException] { return @{ Path = $null; Denied = $true } }
}

function Invoke-Native {
    # Runs a native program and returns its exit code and combined output. Windows PowerShell 5.1 turns the first
    # stderr line of "2>&1" into a terminating error while $ErrorActionPreference is 'Stop', which would lose the real
    # exit code; so the call runs with 'Continue' locally.
    param([string]$Exe, [string[]]$Arguments)
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $text = & $Exe @Arguments 2>&1 | ForEach-Object { "$_" } | Out-String
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $saved }
    return @{ Code = $code; Output = $text }
}

function Get-Outcome {
    param([int]$Code, [string]$Output)
    $tail = if ($Output.Length -gt 2000) { $Output.Substring($Output.Length - 2000) } else { $Output }
    if ($Code -eq 0) { return @{ EventId = 1001; Type = 'Information'; Message = 'ProcessTracker upgraded (or reinstalled) through winget.' } }
    if ($Code -eq $noUpdateCode) { return @{ EventId = 1000; Type = 'Information'; Message = 'ProcessTracker is up to date.' } }
    if ($Code -eq $noPackageCode) {
        return @{ EventId = 1002; Type = 'Warning'; Message = "ProcessTracker auto-upgrade failed with code ${Code}: winget found no installed package '$packageId' matching the input criteria (was it installed with the MSI and not through winget, or for another scope?)." }
    }
    return @{ EventId = 1002; Type = 'Warning'; Message = "ProcessTracker auto-upgrade failed with code ${Code}. winget output (last 2000 characters): $($tail.Trim())" }
}

# Dot-sourcing (tests/upgrade-script.tests.ps1) only loads the functions.
if ($MyInvocation.InvocationName -eq '.') { return }

try {
    $found = Find-Winget
    if ($null -eq $found.Path) {
        if ($found.Denied) { Write-Outcome 1003 'winget not found (access denied enumerating WindowsApps)' 'Warning' }
        else { Write-Outcome 1003 'winget not found' 'Warning' }
        exit 0
    }

    $winget = $found.Path
    $wingetArgs = @('upgrade', '--id', $packageId, '--exact', '--silent', '--scope', 'machine',
        '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity')

    if ($WhatIf) {
        Write-Output "winget: $winget"
        Write-Output "Would run: `"$winget`" $($wingetArgs -join ' ')"
        exit 0
    }

    $run = Invoke-Native $winget $wingetArgs
    Write-Output $run.Output
    $outcome = Get-Outcome $run.Code $run.Output
    Write-Outcome $outcome.EventId $outcome.Message $outcome.Type
    exit 0
}
catch {
    Write-Output "upgrade.ps1 failed: $($_.Exception.Message)"
    try { if (-not $WhatIf -and [System.Diagnostics.EventLog]::SourceExists($source)) { Write-EventLog -LogName Application -Source $source -EventId 1002 -EntryType Error -Message "upgrade.ps1 internal error: $($_.Exception.Message)" } } catch { }
    exit 1
}
