#Requires -Version 5.1
<#
.SYNOPSIS
    Uninstalls ProcessTracker: stops and deletes the service, removes the Claude Code MCP entry, the PATH entry and the
    installed versions. Keeps %ProgramData%\ProcessTracker (history, state, settings, logs) unless -PurgeData is given.

.DESCRIPTION
    Run from an elevated PowerShell as the user who ran install.ps1 (the user PATH and Claude Code config of the
    elevated account are the ones cleaned up). Safe to run on a partial install.

    Exit codes: 0 uninstalled, 1 failed, 3 nothing changed (not elevated).

.PARAMETER Root
    Install root holding versions\ and the current junction.

.PARAMETER PurgeData
    Also deletes %ProgramData%\ProcessTracker.
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
    Write-Host 'uninstall.ps1 must run from an elevated PowerShell (Run as administrator). Nothing was changed.' -ForegroundColor Red
    exit 3
}

$serviceName = 'ProcessTracker'
$current = Join-Path $Root 'current'
$dataDir = Join-Path $env:ProgramData 'ProcessTracker'

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

    # Claude Code MCP entry.
    $claude = Get-Command claude -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $claude) {
        Write-Warning 'claude was not found on the PATH; remove the MCP entry yourself: claude mcp remove process-tracker --scope user'
    }
    else {
        Write-Host '== Removing the MCP server from Claude Code' -ForegroundColor Cyan
        & $claude mcp remove process-tracker --scope user
        if ($LASTEXITCODE -ne 0) { Write-Warning "claude mcp remove failed with exit code $LASTEXITCODE (it may not have been registered)." }
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
    }
    else {
        Write-Host "Kept $dataDir (history, state, settings, logs); run with -PurgeData to delete it." -ForegroundColor Cyan
    }
}
catch {
    Write-Host "Uninstall failed: $_" -ForegroundColor Red
    exit 1
}

Write-Host 'ProcessTracker uninstalled.' -ForegroundColor Green
exit 0
