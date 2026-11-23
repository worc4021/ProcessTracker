#Requires -Version 5.1
<#
.SYNOPSIS
    Smoke test for the ProcessTracker MSI: install, upgrade, uninstall, purge and the legacy-install launch condition.

.DESCRIPTION
    Run from an elevated PowerShell on a disposable machine (CI: GitHub's Windows runners) where ProcessTracker is not
    installed. It installs and removes the product for real.
      1. Installs -Msi (msiexec /i /qn /l*v) and waits until /api/health reports the installed service's
         InformationalVersion with a collector tick younger than 5 s.
      2. Checks the service (Automatic, Running, recovery 5 s / 5 s / 30 s, reset after a day), the SYSTEM scheduled
         task \ProcessTracker\AutoUpgrade, bin on the machine PATH, the ProcessTracker event source and the data
         folder owner (Administrators). Then re-runs -Msi in maintenance mode: it must exit 0 and skip
         StopUserInstances while the service keeps running.
      3. Writes {"RetentionHours":48} to the data folder's appsettings.json, installs -UpgradeMsi (a build with another
         InformationalVersion, e.g. build-msi.ps1 -DevSuffix smoke2) and waits for health to report it; the setting and
         state.db must survive.
      4. Uninstalls: service, task, PATH entry and event source go, the data folder stays.
      5. Reinstalls, then uninstalls with PURGEDATA=1: the data folder goes.
      6. Smoke_LegacyJunctionPresent_InstallRefused: with a legacy <ProgramFiles>\ProcessTracker\current junction
         present, the install fails with 1603 and the launch-condition message.
    msiexec logs go to -LogDir.

    Exit codes: 0 every assertion passed, 1 otherwise (each failed assertion is printed).

.PARAMETER Msi
    The MSI under test (e.g. artifacts\ProcessTracker-0.1.0-x64.msi).

.PARAMETER UpgradeMsi
    A second build with a different InformationalVersion, installed over -Msi as a major upgrade.

.PARAMETER LogDir
    Folder for the msiexec logs.

.PARAMETER HealthTimeoutSeconds
    How long to wait for /api/health after each install.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Msi,
    [Parameter(Mandatory = $true)]
    [string]$UpgradeMsi,
    [string]$LogDir = (Join-Path ([IO.Path]::GetTempPath()) 'msi-smoke'),
    [ValidateRange(1, 600)]
    [int]$HealthTimeoutSeconds = 90
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$serviceName = 'ProcessTracker'
$taskName = '\ProcessTracker\AutoUpgrade'
$healthUrl = 'http://127.0.0.1:47811/api/health'
$programFiles = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$installDir = Join-Path $programFiles 'ProcessTracker'
$binDir = Join-Path $installDir 'bin'
$dataDir = Join-Path $env:ProgramData 'ProcessTracker'
$settingsPath = Join-Path $dataDir 'appsettings.json'
$eventSourceKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\ProcessTracker'
$msiexec = Join-Path $env:SystemRoot 'System32\msiexec.exe'
$schtasks = Join-Path $env:SystemRoot 'System32\schtasks.exe'
$legacyMessage = 'A ProcessTracker install from scripts\install.ps1 is present. Run scripts\uninstall-legacy.ps1 from an elevated PowerShell first.'

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
    return @{ Code = $code; Output = $text }
}

# msiexec through Start-Process so the exit code is the installer's (1603 etc.), with a verbose log.
function Invoke-Msiexec {
    param([string]$Verb, [string]$Package, [string]$LogName, [string[]]$Properties = @())
    $log = Join-Path $LogDir $LogName
    $argLine = "$Verb `"$Package`" /qn /l*v `"$log`" " + ($Properties -join ' ')
    Write-Host "   msiexec $argLine"
    $process = Start-Process -FilePath $msiexec -ArgumentList $argLine -Wait -PassThru
    return @{ Code = $process.ExitCode; Log = $log }
}

# msiexec writes its log as UTF-16 or ANSI depending on the version; read either.
function Read-MsiLog([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return '' }
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -ge 2 -and (($bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) -or $bytes[1] -eq 0)) {
        return [Text.Encoding]::Unicode.GetString($bytes).TrimStart([char]0xFEFF)
    }
    return [Text.Encoding]::Default.GetString($bytes)
}

function Invoke-ComMethod($Object, [string]$Name, [object[]]$Arguments = @()) {
    return $Object.GetType().InvokeMember($Name, [Reflection.BindingFlags]::InvokeMethod, $null, $Object, $Arguments)
}

function Get-ComProperty($Object, [string]$Name, [object[]]$Arguments = @()) {
    return $Object.GetType().InvokeMember($Name, [Reflection.BindingFlags]::GetProperty, $null, $Object, $Arguments)
}

# A value from the MSI's Property table, read through the Windows Installer automation interface.
function Get-MsiProperty([string]$Package, [string]$Name) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = Invoke-ComMethod $installer 'OpenDatabase' @($Package, 0) # read-only
    try {
        $view = Invoke-ComMethod $database 'OpenView' @("SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = '$Name'")
        Invoke-ComMethod $view 'Execute' | Out-Null
        $record = Invoke-ComMethod $view 'Fetch'
        $value = if ($null -ne $record) { Get-ComProperty $record 'StringData' @(1) } else { $null }
        Invoke-ComMethod $view 'Close' | Out-Null
        return $value
    }
    finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($database) }
}

# Product codes of the installed products that share the MSI's UpgradeCode.
function Get-RelatedProducts([string]$UpgradeCode) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    # Called inline: returning the StringList from a function would let PowerShell enumerate (and, if empty, drop) it.
    $related = $installer.GetType().InvokeMember('RelatedProducts', [Reflection.BindingFlags]::GetProperty, $null, $installer, @($UpgradeCode))
    $codes = @()
    $count = Get-ComProperty $related 'Count'
    for ($i = 0; $i -lt $count; $i++) { $codes += [string](Get-ComProperty $related 'Item' @($i)) }
    return , $codes
}

# The installed service's InformationalVersion (what /api/health reports), without build metadata.
function Get-InstalledVersion {
    $exe = Join-Path $installDir 'ProcessTracker.Service.exe'
    if (-not (Test-Path -LiteralPath $exe)) { return $null }
    return ([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion -split '\+')[0]
}

# Polls /api/health until it reports $Version with a collector tick younger than 5 s; returns the last answer.
function Wait-Healthy([string]$Version) {
    $deadline = (Get-Date).AddSeconds($HealthTimeoutSeconds)
    $health = $null
    while ((Get-Date) -lt $deadline) {
        try {
            $health = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 2
            if ((([string]$health.version -split '\+')[0] -eq $Version) -and $null -ne $health.tickAgeSeconds -and [double]$health.tickAgeSeconds -lt 5) {
                return @{ Ok = $true; Health = $health }
            }
        }
        catch { $health = $null } # not listening yet
        Start-Sleep -Milliseconds 500
    }
    return @{ Ok = $false; Health = $health }
}

function Test-ServicePresent { return $null -ne (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) }

function Test-TaskPresent { return (Invoke-Native $schtasks @('/Query', '/TN', $taskName)).Code -eq 0 }

function Test-BinOnMachinePath {
    $path = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    foreach ($entry in ($path -split ';')) {
        if ($entry -and $entry.Trim().TrimEnd('\') -ieq $binDir) { return $true }
    }
    return $false
}

function Assert-Installed([string]$Package, [string]$Label) {
    $productCode = Get-MsiProperty $Package 'ProductCode'
    $related = Get-RelatedProducts (Get-MsiProperty $Package 'UpgradeCode')
    Assert-That ($related.Count -eq 1 -and $related[0] -eq $productCode) "$Label is the one installed product for the UpgradeCode (installed: $($related -join ', '); expected $productCode)"
}

function Assert-ServiceConfiguration {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    Assert-That ($null -ne $service) "service $serviceName exists"
    if ($null -eq $service) { return }
    Assert-That ($service.StartType -eq 'Automatic') "service start type is Automatic (is $($service.StartType))"
    Assert-That ($service.Status -eq 'Running') "service is Running (is $($service.Status))"
    $config = Invoke-Native (Join-Path $env:SystemRoot 'System32\sc.exe') @('qc', $serviceName)
    Assert-That ($config.Output -match 'SERVICE_START_NAME\s*:\s*LocalSystem') 'service runs as LocalSystem'
    $failure = Invoke-Native (Join-Path $env:SystemRoot 'System32\sc.exe') @('qfailure', $serviceName)
    $delays = @([regex]::Matches($failure.Output, 'RESTART -- Delay = (\d+)') | ForEach-Object { $_.Groups[1].Value })
    Assert-That ($failure.Output -match 'RESET_PERIOD \(in seconds\)\s*:\s*86400') 'service failure count resets after 86400 s'
    Assert-That (($delays -join ',') -eq '5000,5000,30000') "service restarts after 5 s, 5 s and 30 s (got $($delays -join ', '))"
}

function Assert-TaskRunsAsSystem {
    $query = Invoke-Native $schtasks @('/Query', '/TN', $taskName, '/XML')
    Assert-That ($query.Code -eq 0) "scheduled task $taskName exists"
    if ($query.Code -ne 0) { return }
    $xml = [xml]$query.Output.Trim()
    $ns = New-Object Xml.XmlNamespaceManager($xml.NameTable)
    $ns.AddNamespace('t', 'http://schemas.microsoft.com/windows/2004/02/mit/task')
    $userId = $xml.SelectSingleNode('//t:Principals/t:Principal/t:UserId', $ns)
    Assert-That ($null -ne $userId -and $userId.InnerText -eq 'S-1-5-18') "scheduled task runs as SYSTEM (UserId $(if ($userId) { $userId.InnerText }))"
    $arguments = $xml.SelectSingleNode('//t:Actions/t:Exec/t:Arguments', $ns)
    $expected = "-File `"$(Join-Path $installDir 'upgrade.ps1')`""
    Assert-That ($null -ne $arguments -and $arguments.InnerText.EndsWith($expected)) "scheduled task runs $expected (got $(if ($arguments) { $arguments.InnerText }))"
}

function Assert-EventSource {
    $present = Test-Path -LiteralPath $eventSourceKey
    Assert-That $present 'event source ProcessTracker exists in the Application log'
    if (-not $present) { return }
    $file = [Environment]::ExpandEnvironmentVariables([string](Get-ItemProperty -LiteralPath $eventSourceKey).EventMessageFile)
    Assert-That (Test-Path -LiteralPath $file) "event source EventMessageFile exists ($file)"
}

function Assert-DataFolderOwner {
    $present = Test-Path -LiteralPath $dataDir
    Assert-That $present "data folder $dataDir exists"
    if (-not $present) { return }
    $owner = (Get-Acl -LiteralPath $dataDir).GetOwner([Security.Principal.SecurityIdentifier]).Value
    Assert-That ($owner -eq 'S-1-5-32-544') "data folder owner is Administrators (is $owner)"
}

function Assert-Uninstalled([string]$Package) {
    $related = Get-RelatedProducts (Get-MsiProperty $Package 'UpgradeCode')
    Assert-That ($related.Count -eq 0) "no product with the UpgradeCode is installed (installed: $($related -join ', '))"
    Assert-That (-not (Test-ServicePresent)) "service $serviceName is gone"
    Assert-That (-not (Test-TaskPresent)) "scheduled task $taskName is gone"
    Assert-That (-not (Test-BinOnMachinePath)) "$binDir is gone from the machine PATH"
    Assert-That (-not (Test-Path -LiteralPath $eventSourceKey)) 'event source ProcessTracker is gone'
    Assert-That (-not (Test-Path -LiteralPath $installDir)) "$installDir is gone"
}

function Install-AndWait([string]$Package, [string]$LogName, [string]$Label) {
    $run = Invoke-Msiexec '/i' $Package $LogName
    Assert-That ($run.Code -eq 0) "$Label exits 0 (exit $($run.Code), log $($run.Log))" -Critical
    $version = Get-InstalledVersion
    Assert-That ($null -ne $version) "$Label installed ProcessTracker.Service.exe" -Critical
    $wait = Wait-Healthy $version
    Assert-That $wait.Ok "/api/health reports $version with a fresh tick within $HealthTimeoutSeconds s (last answer: $($wait.Health | ConvertTo-Json -Compress))" -Critical
    return $version
}

function Test-LegacyJunctionPresentInstallRefused {
    Write-Step 'Smoke_LegacyJunctionPresent_InstallRefused'
    $createdInstallDir = -not (Test-Path -LiteralPath $installDir)
    $legacyTarget = Join-Path ([IO.Path]::GetTempPath()) ("pt-legacy-" + [Guid]::NewGuid().ToString('N'))
    $junction = Join-Path $installDir 'current'
    New-Item -ItemType Directory -Path $legacyTarget -Force | Out-Null
    try {
        [IO.File]::WriteAllBytes((Join-Path $legacyTarget 'ProcessTracker.Service.exe'), [byte[]]@())
        New-Item -ItemType Directory -Path $installDir -Force | Out-Null
        $link = Invoke-Native (Join-Path $env:SystemRoot 'System32\cmd.exe') @('/d', '/c', 'mklink', '/J', $junction, $legacyTarget)
        Assert-That ($link.Code -eq 0) "created the legacy junction $junction -> $legacyTarget" -Critical

        $run = Invoke-Msiexec '/i' $Msi 'legacy.log'
        Assert-That ($run.Code -eq 1603) "install with a legacy junction present exits 1603 (exit $($run.Code))"
        Assert-That ((Read-MsiLog $run.Log).Contains($legacyMessage)) 'the install log holds the legacy-install launch-condition message'
        Assert-That (-not (Test-ServicePresent)) 'the refused install registered no service'
    }
    finally {
        if (Test-Path -LiteralPath $junction) {
            Invoke-Native (Join-Path $env:SystemRoot 'System32\cmd.exe') @('/d', '/c', 'rmdir', $junction) | Out-Null # the junction only
        }
        Remove-Item -LiteralPath $legacyTarget -Recurse -Force -ErrorAction SilentlyContinue
        if ($createdInstallDir -and (Test-Path -LiteralPath $installDir) -and -not (Get-ChildItem -LiteralPath $installDir -Force)) {
            Remove-Item -LiteralPath $installDir -Force
        }
    }
}

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'msi-smoke.ps1 must run elevated (Run as administrator).' -ForegroundColor Red
    exit 1
}

try {
    $Msi = (Resolve-Path -LiteralPath $Msi).ProviderPath
    $UpgradeMsi = (Resolve-Path -LiteralPath $UpgradeMsi).ProviderPath
    New-Item -ItemType Directory -Path $LogDir -Force | Out-Null

    Write-Step 'Preconditions'
    Assert-That (-not (Test-ServicePresent)) "service $serviceName is not installed yet (run on a clean machine)" -Critical
    Assert-That (-not (Test-Path -LiteralPath $dataDir)) "$dataDir does not exist yet" -Critical

    Write-Step "Install $Msi"
    $version = Install-AndWait $Msi 'install.log' 'install'
    Assert-Installed $Msi 'the MSI'
    Assert-ServiceConfiguration
    Assert-TaskRunsAsSystem
    Assert-That (Test-BinOnMachinePath) "$binDir is on the machine PATH"
    Assert-That (Test-Path -LiteralPath (Join-Path $binDir 'proctrack.exe')) 'bin\proctrack.exe is installed'
    Assert-EventSource
    Assert-DataFolderOwner
    Assert-That (Test-Path -LiteralPath (Join-Path $dataDir 'state.db')) 'state.db exists after the first install'
    $installLog = Read-MsiLog (Join-Path $LogDir 'install.log')
    Assert-That ($installLog -notmatch 'McpRegister') 'the install no longer registers MCP'
    Assert-That ($installLog -match 'Action start \d+:\d+:\d+: StopUserInstances\.') 'the install stopped user-mode instances'

    Write-Step "Re-run $Msi (maintenance mode, the service keeps running)"
    $rerun = Invoke-Msiexec '/i' $Msi 'rerun.log'
    Assert-That ($rerun.Code -eq 0) "re-running the installed MSI exits 0 (exit $($rerun.Code), log $($rerun.Log))"
    Assert-That ((Read-MsiLog $rerun.Log) -match 'Skipping action: StopUserInstances \(condition is false\)') 'the re-run skipped StopUserInstances (the Service component has no action)'
    Assert-That ((Get-Service -Name $serviceName -ErrorAction SilentlyContinue).Status -eq 'Running') 'the service is still Running after the re-run'

    Write-Step 'Change a setting'
    [IO.File]::WriteAllText($settingsPath, '{"RetentionHours":48}', (New-Object Text.UTF8Encoding($false)))

    Write-Step "Upgrade to $UpgradeMsi"
    $upgradeVersion = Install-AndWait $UpgradeMsi 'upgrade.log' 'upgrade'
    Assert-That ($upgradeVersion -ne $version) "the upgrade replaced $version with $upgradeVersion"
    Assert-Installed $UpgradeMsi 'the upgrade MSI'
    Assert-ServiceConfiguration
    Assert-TaskRunsAsSystem
    $upgradeLog = Read-MsiLog (Join-Path $LogDir 'upgrade.log')
    Assert-That ($upgradeLog -notmatch 'McpRegister') 'the upgrade no longer registers MCP'
    Assert-That ($upgradeLog -match 'Action start \d+:\d+:\d+: StopUserInstances\.') 'the upgrade stopped user-mode instances'
    $retention = $null
    try { $retention = (Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json).RetentionHours } catch { $retention = $null }
    Assert-That ($retention -eq 48) "RetentionHours 48 survived the upgrade (is $retention)"
    Assert-That (Test-Path -LiteralPath (Join-Path $dataDir 'state.db')) 'state.db survived the upgrade'

    Write-Step 'Uninstall'
    $run = Invoke-Msiexec '/x' $UpgradeMsi 'uninstall.log'
    Assert-That ($run.Code -eq 0) "uninstall exits 0 (exit $($run.Code), log $($run.Log))"
    Assert-Uninstalled $UpgradeMsi
    Assert-That (Test-Path -LiteralPath $settingsPath) "the data folder (and $settingsPath) remains after uninstall"

    Write-Step 'Reinstall, then uninstall with PURGEDATA=1'
    Install-AndWait $Msi 'reinstall.log' 'reinstall' | Out-Null
    $run = Invoke-Msiexec '/x' $Msi 'purge.log' @('PURGEDATA=1')
    Assert-That ($run.Code -eq 0) "uninstall with PURGEDATA=1 exits 0 (exit $($run.Code), log $($run.Log))"
    Assert-Uninstalled $Msi
    Assert-That (-not (Test-Path -LiteralPath $dataDir)) "$dataDir is gone after uninstall with PURGEDATA=1"

    Test-LegacyJunctionPresentInstallRefused
}
catch [System.OperationCanceledException] {
    Write-Host "Stopped: $($_.Exception.Message)" -ForegroundColor Red
}
catch {
    $failures.Add("Unexpected error: $_")
    Write-Host "Unexpected error: $_" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace
}

Write-Host "msiexec logs: $LogDir"
if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) assertion(s) failed:" -ForegroundColor Red
    foreach ($f in $failures) { Write-Host "  - $f" -ForegroundColor Red }
    exit 1
}
Write-Host 'All MSI smoke assertions passed.' -ForegroundColor Green
exit 0
