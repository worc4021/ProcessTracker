#Requires -Version 5.1
<#
.SYNOPSIS
    Installs ProcessTracker: publishes it, registers the Windows service, prepares its data folder, puts proctrack on the
    user PATH and registers the MCP server with Claude Code.

.DESCRIPTION
    Run from an elevated PowerShell in the checkout, as the user whose Claude Code sessions are to be tracked (the user
    profile, PATH and Claude Code config of the elevated account are the ones configured).
      1. Tests and publishes into <Root>\versions\<version> (deploy.ps1 -PublishOnly), then points <Root>\current at it.
      2. Registers the ProcessTracker service (automatic start, LocalSystem) at <Root>\current\ProcessTracker.Service.exe.
      3. Restarts the service on failure (5 s, 5 s, 30 s; counters reset after a day).
      4. Creates %ProgramData%\ProcessTracker (with history\), owned by Administrators and readable only by SYSTEM,
         Administrators and this user, and writes appsettings.json there with UserProfilePath = this user's profile.
         Refuses (nothing changed) if that folder or its history\ is a reparse point (junction or symbolic link).
         Creates the "ProcessTracker" event source in the Application log, where the service logs.
      5. Adds <Root>\current to the user PATH so proctrack resolves.
      6. Starts the service and waits until /api/health reports the new version with a fresh collector tick.
      7. claude mcp add (user scope) for http://127.0.0.1:47811/mcp, if claude is on the PATH, with the header
         X-Claude-Session: ${CLAUDE_CODE_HOST_SESSION_ID:-} (empty, and ignored, in terminal sessions).

    Exit codes: 0 installed, 1 failed (run uninstall.ps1 to remove a partial install), 3 nothing changed.

.PARAMETER Root
    Install root holding versions\ and the current junction.

.PARAMETER TimeoutSeconds
    How long to wait for the service to report healthy after starting it.
#>
[CmdletBinding()]
param(
    [string]$Root = 'C:\Program Files\ProcessTracker',
    [ValidateRange(1, 600)]
    [int]$TimeoutSeconds = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'install.ps1 must run from an elevated PowerShell (Run as administrator). Nothing was changed.' -ForegroundColor Red
    exit 3
}

$serviceName = 'ProcessTracker'
$port = 47811
$current = Join-Path $Root 'current'
$dataDir = Join-Path $env:ProgramData 'ProcessTracker'

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Write-Host 'The ProcessTracker service is already installed. Use deploy.ps1 to upgrade it, or uninstall.ps1 first.' -ForegroundColor Red
    exit 3
}

# Any user can create folders under %ProgramData%: a pre-planted junction or symbolic link would redirect what the
# service (SYSTEM) writes, and the ACL this script sets, to wherever it points.
foreach ($dir in @($dataDir, (Join-Path $dataDir 'history'))) {
    $item = Get-Item -LiteralPath $dir -Force -ErrorAction SilentlyContinue
    if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        Write-Host "$dir is a junction or symbolic link; remove it and run install.ps1 again. Nothing was changed." -ForegroundColor Red
        exit 3
    }
}

# Tells running programs (Explorer, new terminals) that the user environment changed.
function Send-EnvironmentChange {
    if (-not ('ProcessTrackerSetup.NativeMethods' -as [type])) {
        Add-Type -Namespace ProcessTrackerSetup -Name NativeMethods -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
public static extern IntPtr SendMessageTimeout(
    IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeoutMs, out UIntPtr result);
'@
    }
    $result = [UIntPtr]::Zero
    # HWND_BROADCAST, WM_SETTINGCHANGE, SMTO_ABORTIFHUNG
    [ProcessTrackerSetup.NativeMethods]::SendMessageTimeout([IntPtr]0xffff, 0x1A, [UIntPtr]::Zero, 'Environment', 2, 5000, [ref]$result) | Out-Null
}

# Owner BUILTIN\Administrators and a DACL without inheritance: SYSTEM and Administrators full control, the installing
# user read & execute. The service (SYSTEM) reads appsettings.json from here and state.db holds process command lines.
# The owner matters when the folder already existed: a user who created it would otherwise keep owner rights to it.
function New-DataDirectorySecurity {
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetOwner((New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')))
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $rules = @(
        @((New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')), 'FullControl'),      # SYSTEM
        @((New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')), 'FullControl'),  # BUILTIN\Administrators
        @([Security.Principal.WindowsIdentity]::GetCurrent().User, 'ReadAndExecute')                  # installing user
    )
    foreach ($rule in $rules) {
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            $rule[0],
            [System.Security.AccessControl.FileSystemRights]$rule[1],
            $inherit,
            [System.Security.AccessControl.PropagationFlags]::None,
            [System.Security.AccessControl.AccessControlType]::Allow)))
    }
    return $acl
}

try {
    # 1. Test and publish, then point current at the new version.
    $version = & (Join-Path $PSScriptRoot 'deploy.ps1') -Root $Root -PublishOnly
    if (-not ($version -is [string]) -or -not $version) { throw "deploy.ps1 -PublishOnly returned no version ($version)." }
    $target = Join-Path $Root "versions\$version"

    Write-Host "== Pointing $current at $target" -ForegroundColor Cyan
    $existing = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
    if ($existing) {
        if (-not ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "$current exists and is not a junction." }
        cmd /d /c rmdir "$current" # removes the junction only, never the target's contents
        if ($LASTEXITCODE -ne 0) { throw "Could not remove the old junction $current." }
    }
    cmd /d /c mklink /J "$current" "$target" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Could not create the junction $current." }

    # 2-3. Register the service and its recovery actions.
    Write-Host "== Registering the $serviceName service" -ForegroundColor Cyan
    $binaryPath = '"' + (Join-Path $current 'ProcessTracker.Service.exe') + '"'
    New-Service -Name $serviceName -BinaryPathName $binaryPath -StartupType Automatic `
        -DisplayName 'ProcessTracker' -Description 'Tracks which Claude Code session owns each process.' | Out-Null
    sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "sc.exe failure $serviceName failed with exit code $LASTEXITCODE." }

    # 4. Data folder with an explicit ACL, then the settings (only after the ACL is in place).
    Write-Host "== Preparing $dataDir" -ForegroundColor Cyan
    $acl = New-DataDirectorySecurity
    $dataDirs = @($dataDir, (Join-Path $dataDir 'history'))
    foreach ($dir in $dataDirs) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    foreach ($dir in $dataDirs) { Set-Acl -LiteralPath $dir -AclObject $acl }
    $settingsPath = Join-Path $dataDir 'appsettings.json'
    $settings = [ordered]@{}
    if (Test-Path -LiteralPath $settingsPath) { # keep settings from an earlier install
        foreach ($p in (Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json).PSObject.Properties) { $settings[$p.Name] = $p.Value }
    }
    $settings['UserProfilePath'] = $env:USERPROFILE
    [IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding($false)))

    # The service logs to the Application event log under this source (Get-WinEvent -ProviderName ProcessTracker).
    if (-not [Diagnostics.EventLog]::SourceExists($serviceName)) {
        Write-Host "== Creating the $serviceName event source in the Application log" -ForegroundColor Cyan
        [Diagnostics.EventLog]::CreateEventSource($serviceName, 'Application')
    }

    # 5. proctrack on the user PATH (kept as REG_EXPAND_SZ so entries like %USERPROFILE%\... still expand).
    Write-Host "== Adding $current to the user PATH" -ForegroundColor Cyan
    $envKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
    try {
        $userPath = [string]$envKey.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        $entries = @($userPath -split ';' | Where-Object { $_ })
        if (-not ($entries | Where-Object { $_.TrimEnd('\') -eq $current.TrimEnd('\') })) {
            $envKey.SetValue('Path', (($entries + $current) -join ';'), [Microsoft.Win32.RegistryValueKind]::ExpandString)
            Send-EnvironmentChange
        }
    }
    finally { $envKey.Close() }
    $env:Path = "$env:Path;$current"

    # 6. Start and wait for health.
    Write-Host "== Starting the service and waiting for $version to report healthy" -ForegroundColor Cyan
    Start-Service -Name $serviceName
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $healthy = $false
    $health = $null
    while (-not $healthy -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        try {
            $health = Invoke-RestMethod -Uri "http://127.0.0.1:$port/api/health" -TimeoutSec 2
            # Compare the version without build metadata (+...), as the updater does.
            $healthy = (([string]$health.version -split '\+')[0] -eq $version) `
                -and $null -ne $health.tickAgeSeconds -and [double]$health.tickAgeSeconds -lt 5
        }
        catch { $health = $null } # not listening yet
    }
    if (-not $healthy) {
        throw "The service did not report $version healthy within $TimeoutSeconds s (last answer: $($health | ConvertTo-Json -Compress)). See Event Viewer > Windows Logs > Application."
    }

    # 7. Register the MCP server with Claude Code. The header is passed literally: Claude Code expands it per session.
    # The ":-" default keeps sessions without the variable (terminal sessions) valid: the empty header is ignored.
    Write-Host '== Registering the MCP server with Claude Code' -ForegroundColor Cyan
    $mcpUrl = "http://127.0.0.1:$port/mcp"
    $claude = Get-Command claude -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $claude) {
        Write-Warning "claude was not found on the PATH; register the MCP server yourself: claude mcp add --transport http --scope user process-tracker $mcpUrl --header 'X-Claude-Session: `${CLAUDE_CODE_HOST_SESSION_ID:-}'"
    }
    else {
        & $claude mcp add --transport http --scope user process-tracker $mcpUrl --header 'X-Claude-Session: ${CLAUDE_CODE_HOST_SESSION_ID:-}'
        if ($LASTEXITCODE -ne 0) { Write-Warning "claude mcp add failed with exit code $LASTEXITCODE; the service is installed regardless." }
    }
}
catch {
    Write-Host "Install failed: $_" -ForegroundColor Red
    Write-Host 'Run uninstall.ps1 to remove what was installed, fix the problem, then run install.ps1 again.' -ForegroundColor Red
    exit 1
}

Write-Host "Installed ProcessTracker $version. Open a new terminal and run: proctrack sessions" -ForegroundColor Green
exit 0
