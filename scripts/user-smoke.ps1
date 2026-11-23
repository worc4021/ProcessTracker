#Requires -Version 5.1
<#
.SYNOPSIS
    Smoke test for the user-mode zip (ProcessTracker-<X.Y.Z>-x64.zip): start, health, takeover and stop.

.DESCRIPTION
    Run on a machine where the ProcessTracker machine service is not installed and nothing listens on 47811 (CI: a
    GitHub Windows runner, in a job without msi-smoke, which installs the service). It starts and stops user-mode
    instances for real, as the account running the script.
      1. Extracts -Zip twice, into <work>\first and <work>\second; checks ProcessTracker.Service.exe is at the zip root,
         bin\proctrack.exe is present and upgrade.ps1 is not.
      2. Starts <first>\ProcessTracker.Service.exe --user --DataDir <work>\data and expects /api/health to report mode
         "user" and this account as the owner within 30 s.
      3. Takeover: starts <second>\ProcessTracker.Service.exe --user --takeover <first> --DataDir <work>\data and
         expects, within 60 s, the first instance to exit (code 0), the listener on 47811 to be the second instance and
         its log to say it took over. Both folders are outside %LOCALAPPDATA%\Programs\ProcessTracker, so the takeover
         leaves the logon task alone (it logs that and skips UserTask.Register) and neither instance self-updates.
      4. Runs <second>\bin\proctrack.exe admin stop-user-instances, which sets Global\ProcessTracker.UserMode.Exit and
         waits up to 10 s for the instance mutex, and expects exit 0 and the second instance gone (code 0) within 10 s.
    Everything is removed afterwards: instances still running are stopped, the work folder is deleted, and a
    \ProcessTracker\User-<SID> task the run created is removed.

    Known gap: the plan asks for a run as a freshly created standard user (proctrack user install through a scheduled
    task, since the runner is an elevated admin). This script runs the binaries directly with --user --DataDir as the
    invoking account instead, so proctrack user install, the logon task and a non-admin owner are not exercised here.

    Exit codes: 0 every assertion passed ("user smoke: OK"), 1 otherwise (each failed assertion is printed).

.PARAMETER Zip
    The zip under test (e.g. artifacts\ProcessTracker-0.2.0-x64.zip).

.PARAMETER WorkDir
    Scratch folder for the extracted copies and the data folder; created, and deleted afterwards.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Zip,
    [string]$WorkDir = (Join-Path ([IO.Path]::GetTempPath()) ('pt-user-smoke-' + [Guid]::NewGuid().ToString('N')))
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$port = 47811
$healthUrl = "http://127.0.0.1:$port/api/health"
$serviceExeName = 'ProcessTracker.Service.exe'
$schtasks = Join-Path $env:SystemRoot 'System32\schtasks.exe'
$me = [Security.Principal.WindowsIdentity]::GetCurrent()
$taskName = "\ProcessTracker\User-$($me.User.Value)"

$failures = New-Object System.Collections.Generic.List[string]

# Records one assertion; -Critical stops the run (later steps would only cascade).
function Assert-That {
    param([bool]$Condition, [string]$Message, [switch]$Critical)
    if ($Condition) {
        Write-Host "  PASS  $Message" -ForegroundColor Green
        return
    }
    Write-Host "  FAIL  $Message" -ForegroundColor Red
    $failures.Add($Message)
    if ($Critical) { throw [System.OperationCanceledException]::new("Critical assertion failed: $Message") }
}

function Write-Step([string]$Text) { Write-Host "== $Text" -ForegroundColor Cyan }

# Runs a native program; returns its exit code and combined output. 'Continue' keeps Windows PowerShell 5.1 from
# turning stderr output into a terminating error.
function Invoke-Native {
    param([string]$Exe, [string[]]$Arguments)
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $text = & $Exe @Arguments 2>&1 | ForEach-Object { "$_" } | Out-String
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $saved }
    return @{ Code = $code; Output = $text.Trim() }
}

function Test-TaskPresent { return (Invoke-Native $schtasks @('/Query', '/TN', $taskName)).Code -eq 0 }

# One field of /api/health as a string; $null when nothing answers or the field is missing (an older build).
function Get-HealthField([string]$Name) {
    try { $health = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 2 -UseBasicParsing }
    catch { return $null } # not listening (yet)
    $field = $health.PSObject.Properties[$Name]
    if ($null -eq $field) { return $null }
    return [string]$field.Value
}

# The process listening on 47811, or $null.
function Get-ListenerPid {
    $listen = @(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue)
    if ($listen.Count -eq 0) { return $null }
    return [int]$listen[0].OwningProcess
}

# Starts the service exe; the WinExe has no console, so nothing to redirect. Reading Handle keeps ExitCode readable.
function Start-Instance([string]$AppDir, [string]$ArgumentLine) {
    $exe = Join-Path $AppDir $serviceExeName
    Write-Host "   $exe $ArgumentLine"
    $process = Start-Process -FilePath $exe -ArgumentList $ArgumentLine -WorkingDirectory $AppDir -PassThru
    $null = $process.Handle
    return $process
}

# Polls until $Condition holds or $Seconds pass; returns whether it held.
function Wait-Until([scriptblock]$Condition, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return [bool](& $Condition)
}

function Read-Log([string]$DataDir) {
    $log = Join-Path $DataDir 'logs\processtracker.log'
    if (-not (Test-Path -LiteralPath $log)) { return '' }
    # The running instance keeps the file open for writing (FileShare.Read): open it with ReadWrite sharing.
    $stream = [IO.File]::Open($log, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try { return (New-Object IO.StreamReader($stream)).ReadToEnd() }
    finally { $stream.Dispose() }
}

function Expand-Zip([string]$Path, [string]$Destination) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($Path, $Destination)
}

$instances = New-Object System.Collections.Generic.List[System.Diagnostics.Process]
$taskExisted = $true
$first = Join-Path $WorkDir 'first'
$second = Join-Path $WorkDir 'second'
$dataDir = Join-Path $WorkDir 'data'

try {
    $Zip = (Resolve-Path -LiteralPath $Zip).ProviderPath
    $taskExisted = Test-TaskPresent

    Write-Step 'Preconditions'
    Assert-That ($null -eq (Get-Service -Name 'ProcessTracker' -ErrorAction SilentlyContinue)) 'the ProcessTracker machine service is not installed (user mode exits 4 while it is)' -Critical
    Assert-That ($null -eq (Get-ListenerPid)) "nothing listens on $port yet" -Critical

    Write-Step "Extract $Zip"
    New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null
    Expand-Zip $Zip $first
    Expand-Zip $Zip $second
    Assert-That (Test-Path -LiteralPath (Join-Path $first $serviceExeName)) "$serviceExeName is at the zip root" -Critical
    Assert-That (Test-Path -LiteralPath (Join-Path $first 'bin\proctrack.exe')) 'bin\proctrack.exe is in the zip' -Critical
    Assert-That (-not (Test-Path -LiteralPath (Join-Path $first 'upgrade.ps1'))) 'upgrade.ps1 (the machine install''s) is not in the zip'

    Write-Step 'Start the first instance'
    $one = Start-Instance $first "--user --DataDir `"$dataDir`""
    $instances.Add($one)
    $healthy = Wait-Until { $one.HasExited -or (Get-HealthField 'mode') -eq 'user' } 30
    Assert-That ($healthy -and -not $one.HasExited) "/api/health reports mode user within 30 s (exited: $($one.HasExited)$(if ($one.HasExited) { ", code $($one.ExitCode)" }))" -Critical
    $owner = Get-HealthField 'owner'
    Assert-That ($owner -eq $me.Name) "/api/health owner is $($me.Name) (is $owner)"
    Assert-That ((Get-ListenerPid) -eq $one.Id) "the first instance (pid $($one.Id)) listens on $port"

    Write-Step 'Take over with the second copy'
    $two = Start-Instance $second "--user --takeover `"$first`" --DataDir `"$dataDir`""
    $instances.Add($two)
    $tookOver = Wait-Until { $two.HasExited -or ($one.HasExited -and (Get-ListenerPid) -eq $two.Id) } 60
    Assert-That ($tookOver -and -not $two.HasExited) "within 60 s the first instance exits and the second (pid $($two.Id)) listens on $port (second exited: $($two.HasExited)$(if ($two.HasExited) { ", code $($two.ExitCode)" }))" -Critical
    Assert-That ($one.ExitCode -eq 0) "the first instance exited with code 0 (code $($one.ExitCode))"
    Assert-That ((Get-HealthField 'mode') -eq 'user') 'the second instance answers /api/health with mode user'
    $logged = Wait-Until { (Read-Log $dataDir) -match 'Took over from' } 60
    Assert-That $logged 'the second instance logged "Took over from" (its collector ticked within 60 s)'
    $skipped = Wait-Until { (Read-Log $dataDir) -match 'is not an app folder of the user-mode install' } 5
    Assert-That $skipped 'the takeover left the logon task alone (its folder is outside %LOCALAPPDATA%\Programs\ProcessTracker)'

    Write-Step 'Stop through the exit event'
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $stop = Invoke-Native (Join-Path $second 'bin\proctrack.exe') @('admin', 'stop-user-instances')
    Assert-That ($stop.Code -eq 0) "proctrack admin stop-user-instances exits 0 (exit $($stop.Code): $($stop.Output))"
    $left = [Math]::Max(0, 10000 - [int]$clock.ElapsedMilliseconds)
    Assert-That ($two.WaitForExit($left)) "the second instance exits within 10 s of the exit event ($([int]$clock.Elapsed.TotalSeconds) s)"
    if ($two.HasExited) { Assert-That ($two.ExitCode -eq 0) "the second instance exited with code 0 (code $($two.ExitCode))" }
    Assert-That ($null -eq (Get-ListenerPid)) "nothing listens on $port afterwards"
}
catch [System.OperationCanceledException] {
    Write-Host "Stopped: $($_.Exception.Message)" -ForegroundColor Red
}
catch {
    $failures.Add("Unexpected error: $_")
    Write-Host "Unexpected error: $_" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace
}
finally {
    $running = @($instances | Where-Object { -not $_.HasExited })
    if ($running.Count -gt 0) {
        $proctrack = Join-Path $second 'bin\proctrack.exe'
        if (Test-Path -LiteralPath $proctrack) { Invoke-Native $proctrack @('admin', 'stop-user-instances') | Out-Null }
        foreach ($p in $running) {
            if (-not $p.WaitForExit(10000)) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; $p.WaitForExit(5000) | Out-Null }
        }
    }
    if (-not $taskExisted -and (Test-TaskPresent)) { Invoke-Native $schtasks @('/Delete', '/TN', $taskName, '/F') | Out-Null }
    if (Test-Path -LiteralPath $WorkDir) {
        $log = Read-Log $dataDir
        if ($failures.Count -gt 0 -and $log) { Write-Host "-- $dataDir\logs\processtracker.log:"; Write-Host $log }
        Remove-Item -LiteralPath $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $WorkDir) { Write-Host "Could not delete $WorkDir completely." -ForegroundColor Yellow }
    }
}

if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) assertion(s) failed:" -ForegroundColor Red
    foreach ($f in $failures) { Write-Host "  - $f" -ForegroundColor Red }
    exit 1
}
Write-Host 'user smoke: OK' -ForegroundColor Green
exit 0
