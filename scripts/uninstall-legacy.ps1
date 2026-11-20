#Requires -Version 5.1
<#
.SYNOPSIS
    Removes a phase 1 (script-installed) ProcessTracker, so the MSI can be installed. Not for MSI installs: remove those
    with winget or msiexec.

.DESCRIPTION
    The phase 1 install (scripts\install.ps1, now retired) put versions under <Root>\versions, pointed the junction
    <Root>\current at one of them and added <Root>\current to the USER PATH. The MSI refuses to install while that
    junction exists. Run this once, from an elevated PowerShell as the user who ran install.ps1 (the user PATH and Claude
    Code config of the elevated account are the ones cleaned up). Safe to run on a partial install.
      1. Stops and deletes the ProcessTracker service.
      2. Removes the process-tracker MCP entry, only if Claude Code has one (claude mcp get / remove, user scope).
      3. Removes <Root>\current from the user PATH.
      4. Removes the junction with rmdir (never Remove-Item, which would follow it) and deletes <Root>\versions.
    %ProgramData%\ProcessTracker (history, state, settings) is KEPT by default: the MSI adopts that folder, so history
    survives the migration. Then install the MSI (winget install worc4021.ProcessTracker, or scripts\deploy.ps1).

    Exit codes: 0 removed, 1 failed, 3 nothing changed (not elevated, data folder is a link, or this is an MSI install).

.PARAMETER Root
    Install root holding versions\ and the current junction.

.PARAMETER PurgeData
    Also deletes %ProgramData%\ProcessTracker and the ProcessTracker event source. Refuses (before changing anything)
    if %ProgramData%\ProcessTracker is a junction or symbolic link, which would make it delete the link's target.
#>
[CmdletBinding()]
param(
    [string]$Root = 'C:\Program Files\ProcessTracker',
    [switch]$PurgeData
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'uninstall-legacy.ps1 must run from an elevated PowerShell (Run as administrator). Nothing was changed.' -ForegroundColor Red
    exit 3
}

$serviceName = 'ProcessTracker'
$current = Join-Path $Root 'current'
$dataDir = Join-Path $env:ProgramData 'ProcessTracker'

# An MSI install has bin\proctrack.exe and no junction; deleting its service here would break it.
if ((Test-Path -LiteralPath (Join-Path $Root 'bin\proctrack.exe')) -and -not (Test-Path -LiteralPath $current)) {
    Write-Host "$Root holds an MSI install, not a phase 1 install. Remove it with 'winget uninstall worc4021.ProcessTracker' or msiexec /x. Nothing was changed." -ForegroundColor Red
    exit 3
}

if ($PurgeData) {
    $item = Get-Item -LiteralPath $dataDir -Force -ErrorAction SilentlyContinue
    if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        Write-Host "$dataDir is a junction or symbolic link; -PurgeData refuses to delete through it. Nothing was changed." -ForegroundColor Red
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

try {
    # Service: stop (a graceful stop persists state), then delete.
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($service) {
        Write-Host "== Stopping and deleting the $serviceName service" -ForegroundColor Cyan
        if ($service.Status -ne 'Stopped') {
            Stop-Service -Name $serviceName -Force
            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        }
        sc.exe delete $serviceName | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "sc.exe delete $serviceName failed with exit code $LASTEXITCODE." }
    }
    else {
        Write-Host "The $serviceName service is not installed." -ForegroundColor Cyan
    }

    # Claude Code MCP entry, only if one is registered (claude mcp get exits non-zero when it is not).
    $claude = Get-Command claude -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $claude) {
        Write-Warning 'claude was not found on the PATH; if an MCP entry exists, remove it yourself: claude mcp remove process-tracker --scope user'
    }
    else {
        & $claude mcp get process-tracker *> $null
        if ($LASTEXITCODE -eq 0) {
            Write-Host '== Removing the MCP server from Claude Code' -ForegroundColor Cyan
            & $claude mcp remove process-tracker --scope user
            if ($LASTEXITCODE -ne 0) { Write-Warning "claude mcp remove failed with exit code $LASTEXITCODE; remove it yourself: claude mcp remove process-tracker --scope user" }
        }
        else { Write-Host 'No process-tracker MCP entry in Claude Code.' -ForegroundColor Cyan }
    }

    # User PATH entry (kept as REG_EXPAND_SZ).
    $envKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
    try {
        $userPath = [string]$envKey.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        $entries = @($userPath -split ';' | Where-Object { $_ })
        $kept = @($entries | Where-Object { $_.TrimEnd('\') -ne $current.TrimEnd('\') })
        if ($kept.Count -ne $entries.Count) {
            Write-Host "== Removing $current from the user PATH" -ForegroundColor Cyan
            $envKey.SetValue('Path', ($kept -join ';'), [Microsoft.Win32.RegistryValueKind]::ExpandString)
            Send-EnvironmentChange
        }
    }
    finally { $envKey.Close() }

    # Installed files: the junction first, with rmdir (removes the link, never the target), then the version folders.
    if (Test-Path -LiteralPath $Root) {
        Write-Host "== Removing $Root" -ForegroundColor Cyan
        $link = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($link) {
            if (-not ($link.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "$current is not a junction; remove it yourself." }
            cmd /d /c rmdir "$current"
            if ($LASTEXITCODE -ne 0) { throw "Could not remove the junction $current." }
        }
        $versions = Join-Path $Root 'versions'
        if (Test-Path -LiteralPath $versions) { Remove-Item -LiteralPath $versions -Recurse -Force }
        if (-not (Get-ChildItem -LiteralPath $Root -Force)) { Remove-Item -LiteralPath $Root -Force }
        else { Write-Warning "$Root still contains other files; left in place." }
    }

    if ($PurgeData) {
        if (Test-Path -LiteralPath $dataDir) {
            Write-Host "== Deleting $dataDir" -ForegroundColor Cyan
            Remove-Item -LiteralPath $dataDir -Recurse -Force
        }
        if ([Diagnostics.EventLog]::SourceExists($serviceName)) {
            Write-Host "== Deleting the $serviceName event source" -ForegroundColor Cyan
            [Diagnostics.EventLog]::DeleteEventSource($serviceName)
        }
    }
    else {
        Write-Host "Kept $dataDir (history, state, settings) and the $serviceName event source; run with -PurgeData to delete them." -ForegroundColor Cyan
    }
}
catch {
    Write-Host "Removal failed: $_" -ForegroundColor Red
    exit 1
}

Write-Host 'Phase 1 ProcessTracker removed. Install the MSI next (winget install worc4021.ProcessTracker, or scripts\deploy.ps1).' -ForegroundColor Green
exit 0
