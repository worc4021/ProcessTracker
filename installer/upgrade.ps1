<#
.SYNOPSIS
    Upgrades ProcessTracker through winget, or from the GitHub release when winget cannot. Run daily and at boot by the
    SYSTEM scheduled task \ProcessTracker\AutoUpgrade.

.DESCRIPTION
    Windows PowerShell 5.1, no dependencies. Lives in the admin-only install folder and is started by
    System32\WindowsPowerShell\v1.0\powershell.exe (never by proctrack.exe: the MSI upgrade replaces bin\proctrack.exe,
    and a running proctrack would lock it).
      1. Resolves winget.exe the same way as WingetLocator in proctrack: SYSTEM has no per-user alias, so the newest
         Microsoft.DesktopAppInstaller_<version>_x64__8wekyb3d8bbwe folder under C:\Program Files\WindowsApps wins.
      2. Runs winget upgrade --id worc4021.ProcessTracker --exact --silent --scope machine ...
      3. GitHub fallback, when winget is not found or reports no installed package (an MSI installed without winget):
         runs bin\proctrack.exe admin fetch-release --asset msi --out <fresh folder under the temp folder>, which
         downloads the latest release's MSI only when it is newer, verified against SHA256SUMS.txt, and prints its
         path. proctrack has exited by then, so msiexec /i <path> /qn /norestart can replace it. The folder is deleted
         afterwards.
      4. Writes the outcome to the Application event log (source ProcessTracker), and to the output:
           1000 up to date | 1001 upgraded (or reinstalled) through winget | 1002 failed with code N
           | 1004 upgraded from a GitHub release
         Without the event source only the output is written.
    Exits 0 except on an internal script error (1).

.PARAMETER WhatIf
    Only resolve winget and print the command lines that would run. Changes nothing.
#>
param(
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
$packageId = 'worc4021.ProcessTracker'
$source = 'ProcessTracker'
$noUpdateCode = -1978335189   # 0x8A15002B: no applicable update
$noPackageCode = -1978335212  # 0x8A150014: no installed package found matching the input criteria
$fetchUpToDateCode = 3        # proctrack admin fetch-release: the latest release is not newer
$msiRebootRequiredCode = 3010 # ERROR_SUCCESS_REBOOT_REQUIRED: installed; a restart completes it

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

function Get-LastLine([string]$Text) {
    $lines = @($Text -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if ($lines.Count -eq 0) { return '' }
    return $lines[$lines.Count - 1]
}

function New-UpgradeFolderSecurity {
    # A protected DACL (nothing inherited) granting full control to SYSTEM and Administrators only.
    $security = New-Object System.Security.AccessControl.DirectorySecurity
    $security.SetAccessRuleProtection($true, $false)
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            (New-Object System.Security.Principal.SecurityIdentifier($sid)),
            [System.Security.AccessControl.FileSystemRights]::FullControl,
            ([System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [System.Security.AccessControl.InheritanceFlags]::ObjectInherit),
            [System.Security.AccessControl.PropagationFlags]::None,
            [System.Security.AccessControl.AccessControlType]::Allow)
        $security.AddAccessRule($rule)
    }
    return $security
}

function Protect-UpgradeFolder([string]$Path) {
    Set-Acl -LiteralPath $Path -AclObject (New-UpgradeFolderSecurity)
}

function Invoke-GitHubFallback {
    # Downloads the latest release's MSI through proctrack (hash-verified, only when newer) and installs it.
    param([switch]$WhatIf)
    $proctrack = Join-Path $PSScriptRoot 'bin\proctrack.exe'
    $msiexec = Join-Path $env:SystemRoot 'System32\msiexec.exe'
    # As SYSTEM this is C:\Windows\Temp, where Users may create files and folders (inherited by subfolders); so the
    # folder gets an explicit protected DACL for SYSTEM and Administrators right after it is created.
    $outDir = Join-Path ([System.IO.Path]::GetTempPath()) ('ProcessTracker-upgrade-' + [Guid]::NewGuid().ToString('N'))
    $fetchArgs = @('admin', 'fetch-release', '--asset', 'msi', '--out', $outDir)

    if ($WhatIf) {
        Write-Output "Would run: `"$proctrack`" $($fetchArgs -join ' ')"
        Write-Output "and on exit 0: `"$msiexec`" /i <the printed MSI path> /qn /norestart"
        return
    }

    New-Item -ItemType Directory -Path $outDir | Out-Null # fails if it exists: the folder must be ours alone
    try {
        Protect-UpgradeFolder $outDir
        $fetch = Invoke-Native $proctrack $fetchArgs
        Write-Output $fetch.Output
        if ($fetch.Code -eq $fetchUpToDateCode) {
            Write-Outcome 1000 'ProcessTracker is up to date.'
            return
        }
        # On exit 0 the last line is the verified MSI, which must be inside the folder created above.
        $msi = Get-LastLine $fetch.Output
        $valid = $fetch.Code -eq 0 `
            -and [System.IO.Path]::GetFileName($msi) -match '^ProcessTracker-(?<v>\d+\.\d+\.\d+)-x64\.msi$' `
            -and [System.IO.Path]::GetDirectoryName($msi) -ieq $outDir `
            -and (Test-Path -LiteralPath $msi -PathType Leaf)
        if (-not $valid) {
            $tail = "$($fetch.Output)".Trim()
            if ($tail.Length -gt 2000) { $tail = $tail.Substring($tail.Length - 2000) }
            Write-Outcome 1002 "ProcessTracker auto-upgrade from GitHub failed with code $($fetch.Code) (fetch-release). Output: $tail" 'Warning'
            return
        }
        $version = $Matches['v']
        $install = Invoke-Native $msiexec @('/i', $msi, '/qn', '/norestart')
        if ($install.Code -eq 0 -or $install.Code -eq $msiRebootRequiredCode) {
            Write-Outcome 1004 "ProcessTracker upgraded from GitHub release $version."
        }
        else {
            Write-Outcome 1002 "ProcessTracker auto-upgrade from GitHub release $version failed with code $($install.Code) (msiexec)." 'Warning'
        }
    }
    finally { Remove-Item -LiteralPath $outDir -Recurse -Force -ErrorAction SilentlyContinue }
}

function Invoke-Upgrade {
    param([switch]$WhatIf)
    $found = Find-Winget
    if ($null -eq $found.Path) {
        if ($found.Denied) { Write-Output 'winget not found (access denied enumerating WindowsApps); trying the GitHub release.' }
        else { Write-Output 'winget not found; trying the GitHub release.' }
        Invoke-GitHubFallback -WhatIf:$WhatIf
        return
    }

    $winget = $found.Path
    $wingetArgs = @('upgrade', '--id', $packageId, '--exact', '--silent', '--scope', 'machine',
        '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity')

    if ($WhatIf) {
        Write-Output "winget: $winget"
        Write-Output "Would run: `"$winget`" $($wingetArgs -join ' ')"
        Write-Output "If winget finds no installed package ($noPackageCode):"
        Invoke-GitHubFallback -WhatIf
        return
    }

    $run = Invoke-Native $winget $wingetArgs
    Write-Output $run.Output
    if ($run.Code -eq $noPackageCode) {
        Write-Output "winget found no installed package '$packageId'; trying the GitHub release."
        Invoke-GitHubFallback
        return
    }
    $outcome = Get-Outcome $run.Code $run.Output
    Write-Outcome $outcome.EventId $outcome.Message $outcome.Type
}

# Dot-sourcing (tests/upgrade-script.tests.ps1) only loads the functions.
if ($MyInvocation.InvocationName -eq '.') { return }

try {
    Invoke-Upgrade -WhatIf:$WhatIf
    exit 0
}
catch {
    Write-Output "upgrade.ps1 failed: $($_.Exception.Message)"
    try { if (-not $WhatIf -and [System.Diagnostics.EventLog]::SourceExists($source)) { Write-EventLog -LogName Application -Source $source -EventId 1002 -EntryType Error -Message "upgrade.ps1 internal error: $($_.Exception.Message)" } } catch { }
    exit 1
}
