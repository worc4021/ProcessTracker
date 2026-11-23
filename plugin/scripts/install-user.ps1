<#
.SYNOPSIS
    Installs a user-only copy of ProcessTracker (no admin rights). Run by /process-tracker:install, never on its own.

.DESCRIPTION
    Windows PowerShell 5.1 or pwsh, no dependencies.
      1. Refuses when elevated: the user-only copy belongs to the signed-in user, not to an administrator.
      2. If http://127.0.0.1:47811/api/health answers with mode "service" (or without a mode: a service that predates
         it), or the ProcessTracker Windows service is installed (sc query, checked before any download), the
         machine-wide service is in use and nothing is installed (an
         existing per-user task is pointed out, with the command that removes it), but proctrack mcp unregister still
         runs when proctrack is found (otherwise a hint says to run it). An own running user-mode copy that is already
         at the latest version is left alone; another user's copy is refused.
      3. Reads https://api.github.com/repos/worc4021/ProcessTracker/releases/latest, downloads
         ProcessTracker-<v>-x64.zip and SHA256SUMS.txt to %TEMP%, following redirects only to github.com,
         objects.githubusercontent.com and release-assets.githubusercontent.com, checks the SHA-256 and extracts to
         %LOCALAPPDATA%\Programs\ProcessTracker\app-<v>. Nothing is downloaded when app-<v> is already complete, or when
         nothing answers and the highest installed app-<X.Y.Z> is not older; that folder is registered again instead.
         The folder the per-user task runs is never deleted.
      4. Runs app-<v>\bin\proctrack.exe user install --app-dir <that folder> and prints its output; an older copy of
         yours that is still running is restarted on the new version by it. When that fails, a folder this run
         extracted is removed again.
      5. Runs proctrack mcp unregister, so a leftover user-scope MCP entry doesn't duplicate the plugin's.
    Exits with the code of proctrack user install, or 1 when it refuses or fails before that.
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # the 5.1 progress bar slows Invoke-WebRequest down by an order of magnitude

$script:Repo = 'worc4021/ProcessTracker'
$script:LatestUri = "https://api.github.com/repos/$($script:Repo)/releases/latest"
$script:RedirectHosts = @('github.com', 'objects.githubusercontent.com', 'release-assets.githubusercontent.com')
$script:HealthUri = 'http://127.0.0.1:47811/api/health'
$script:PluginJson = Join-Path (Split-Path -Parent $PSScriptRoot) '.claude-plugin\plugin.json'
$script:ExitCode = 0

function Test-Elevated {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-UserTaskName { "\ProcessTracker\User-$([Security.Principal.WindowsIdentity]::GetCurrent().User.Value)" }

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

function Test-UserTask([string]$Name) { (Invoke-Native 'schtasks.exe' @('/Query', '/TN', $Name)).Code -eq 0 }

function Get-Health {
    # The /api/health object, or $null when nothing answers.
    try {
        $r = Invoke-WebRequest -Uri $script:HealthUri -UseBasicParsing -TimeoutSec 3 -MaximumRedirection 0
        return ($r.Content | ConvertFrom-Json)
    }
    catch { return $null }
}

function Get-PluginVersion {
    try { return [string](Get-Content -LiteralPath $script:PluginJson -Raw | ConvertFrom-Json).version } catch { return '0.0.0' }
}

function ConvertTo-PlainVersion([string]$Text) {
    # "0.2.0+abc" or "0.2.0-rc1" -> [version]0.2.0; $null when it isn't X.Y.Z.
    if ($Text -match '^([0-9]+\.[0-9]+\.[0-9]+)') { return [version]$Matches[1] }
    return $null
}

function Invoke-Hop {
    # One request with automatic redirects off. Returns the response whatever its status (3xx included); throws when no
    # response arrives. 5.1 returns a 3xx response when errors are silenced; pwsh needs -SkipHttpErrorCheck for that.
    param([string]$Uri, [string]$OutFile, [hashtable]$Headers, [int]$TimeoutSec)
    $p = @{ Uri = $Uri; UseBasicParsing = $true; MaximumRedirection = 0; Headers = $Headers; TimeoutSec = $TimeoutSec
        ErrorAction = 'SilentlyContinue' }
    if ($PSVersionTable.PSVersion.Major -ge 7) { $p['SkipHttpErrorCheck'] = $true }
    if ($OutFile) { $p['OutFile'] = $OutFile; $p['PassThru'] = $true }
    try { $r = Invoke-WebRequest @p }
    catch { throw "Download of $Uri failed: $($_.Exception.Message)" }
    if ($null -eq $r) { throw "Download of $Uri failed." }
    return $r
}

function Invoke-Download {
    # GET with redirects followed by hand: at most 5, https only, and only to the allowlisted hosts.
    param([string]$Uri, [string]$OutFile, [hashtable]$Headers, [int]$TimeoutSec = 600)
    $current = New-Object Uri($Uri)
    for ($hop = 0; $hop -le 5; $hop++) {
        $r = Invoke-Hop $current.AbsoluteUri $OutFile $Headers $TimeoutSec
        $code = [int]$r.StatusCode
        if ($code -ge 300 -and $code -lt 400) {
            $location = [string](@($r.Headers['Location']) | Select-Object -First 1)
            if (-not $location) { throw "Download of $current failed: HTTP $code without a Location header." }
            $next = New-Object Uri($current, $location)
            if ($next.Scheme -ne 'https' -or $script:RedirectHosts -notcontains $next.Host.ToLowerInvariant()) {
                throw "Refused a redirect from $($current.Host) to $($next.Scheme)://$($next.Host): only https to $($script:RedirectHosts -join ', ') is allowed."
            }
            $current = $next
            continue
        }
        if ($code -ne 200) { throw "Download of $current failed with HTTP $code." }
        return $r
    }
    throw "Download of $Uri failed: more than 5 redirects."
}

function Read-Sha256Sums([string]$Text) {
    # "<hex>  <name>" lines -> @{ name = lower-case hex }. Blank lines are skipped; anything else malformed throws.
    $sums = @{}
    foreach ($line in ($Text -split "`r?`n")) {
        if ($line.Trim() -eq '') { continue }
        if ($line -notmatch '^([0-9A-Fa-f]{64})  (\S.*)$') { throw "SHA256SUMS.txt has a malformed line: '$line'" }
        $sums[$Matches[2].TrimEnd()] = $Matches[1].ToLowerInvariant()
    }
    return $sums
}

function Get-LatestRelease([hashtable]$Headers) {
    # @{ Version; Tag; ZipName; ZipUrl; SumsUrl } for the latest release; throws when it lacks the assets.
    $apiHeaders = $Headers.Clone()
    $apiHeaders['Accept'] = 'application/vnd.github+json'
    $r = Invoke-Download $script:LatestUri $null $apiHeaders 30
    $json = [string]$r.Content | ConvertFrom-Json
    $tag = [string]$json.tag_name
    if ($tag -notmatch '^v([0-9]+\.[0-9]+\.[0-9]+)$') { throw "The latest release tag '$tag' is not vX.Y.Z." }
    $version = $Matches[1]
    $zipName = "ProcessTracker-$version-x64.zip"
    $prefix = "https://github.com/$($script:Repo)/releases/download/$tag/"
    $urls = @{}
    foreach ($name in @($zipName, 'SHA256SUMS.txt')) {
        $asset = @($json.assets | Where-Object { $_.name -eq $name }) | Select-Object -First 1
        if ($null -eq $asset) { throw "Release $tag has no asset $name." }
        if ([string]$asset.browser_download_url -ne ($prefix + $name)) { throw "Release $tag asset $name points outside the repository's releases." }
        $urls[$name] = $prefix + $name
    }
    return @{ Version = [version]$version; Tag = $tag; ZipName = $zipName; ZipUrl = $urls[$zipName]; SumsUrl = $urls['SHA256SUMS.txt'] }
}

function Get-Sha256([string]$Path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try { return (($sha.ComputeHash($stream) | ForEach-Object { $_.ToString('x2') }) -join '') }
    finally { $stream.Dispose(); $sha.Dispose() }
}

function Get-ProgramsDir {
    $local = $env:LOCALAPPDATA
    if (-not $local -or -not [IO.Path]::IsPathRooted($local)) { throw '%LOCALAPPDATA% is not set to an absolute path.' }
    return Join-Path $local 'Programs\ProcessTracker'
}

function Get-InstalledApp {
    # The highest complete user-mode install: @{ Version; Dir } for %LOCALAPPDATA%\Programs\ProcessTracker\app-<X.Y.Z> with
    # bin\proctrack.exe, or $null.
    try { $programs = Get-ProgramsDir } catch { return $null }
    if (-not (Test-Path -LiteralPath $programs)) { return $null }
    $best = $null
    foreach ($dir in Get-ChildItem -LiteralPath $programs -Directory -Filter 'app-*') {
        $name = $dir.Name.Substring(4)
        $v = ConvertTo-PlainVersion $name
        if ($null -eq $v -or $name -ne $v.ToString() -or -not (Test-AppComplete $dir.FullName)) { continue }
        if ($null -eq $best -or $v -gt $best.Version) { $best = @{ Version = $v; Dir = $dir.FullName } }
    }
    return $best
}

function Test-AppComplete([string]$Dir) { Test-Path -LiteralPath (Join-Path $Dir 'bin\proctrack.exe') }

function Find-Proctrack {
    # proctrack.exe on the PATH (the MSI puts it there), else the one of the highest user-mode install; $null if none.
    $onPath = Get-Command 'proctrack.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $onPath) { return $onPath.Source }
    $installed = Get-InstalledApp
    if ($null -eq $installed) { return $null }
    return Join-Path $installed.Dir 'bin\proctrack.exe'
}

function Test-MachineService {
    # True when the ProcessTracker Windows service is installed, running or not (sc query needs no admin rights).
    (Invoke-Native 'sc.exe' @('query', 'ProcessTracker')).Code -eq 0
}

function Get-TaskAppDir([string]$Name) {
    # The app folder the per-user task runs (the parent of its service exe), or $null when there is no such task.
    $q = Invoke-Native 'schtasks.exe' @('/Query', '/TN', $Name, '/XML')
    if ($q.Code -ne 0 -or $q.Output -notmatch '<Command>\s*([^<]+?)\s*</Command>') { return $null }
    $command = [Net.WebUtility]::HtmlDecode($Matches[1]).Trim('"')
    try { return Split-Path -Parent $command } catch { return $null }
}

function Test-SamePath([string]$A, [string]$B) {
    if (-not $A -or -not $B) { return $false }
    $a1 = [IO.Path]::GetFullPath($A).TrimEnd('\')
    $b1 = [IO.Path]::GetFullPath($B).TrimEnd('\')
    return [string]::Equals($a1, $b1, [StringComparison]::OrdinalIgnoreCase)
}

function Install-Release($Release, [hashtable]$Headers, [string]$TaskAppDir) {
    # Downloads and verifies the release zip and extracts it to app-<v> (not already complete). Returns that folder. An
    # incomplete leftover app-<v> is replaced, unless the per-user task runs it.
    $appDir = Join-Path (Get-ProgramsDir) "app-$($Release.Version)"
    if ((Test-Path -LiteralPath $appDir) -and (Test-SamePath $appDir $TaskAppDir)) {
        throw "$appDir is incomplete but your scheduled task runs it; run 'proctrack user uninstall' (or delete the task) and install again."
    }
    $temp = Join-Path $env:TEMP ('ProcessTracker-install-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temp | Out-Null
    try {
        $sumsPath = Join-Path $temp 'SHA256SUMS.txt'
        $zipPath = Join-Path $temp $Release.ZipName
        Invoke-Download $Release.SumsUrl $sumsPath $Headers 60 | Out-Null
        Invoke-Download $Release.ZipUrl $zipPath $Headers 600 | Out-Null
        $sums = Read-Sha256Sums ([IO.File]::ReadAllText($sumsPath))
        $expected = $sums[$Release.ZipName]
        if (-not $expected) { throw "SHA256SUMS.txt of $($Release.Tag) has no line for $($Release.ZipName)." }
        $actual = Get-Sha256 $zipPath
        if ($actual -ne $expected) { throw "SHA-256 mismatch for $($Release.ZipName): expected $expected, got $actual. Nothing was installed." }

        $staging = "$appDir.partial"
        if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
        Expand-Archive -LiteralPath $zipPath -DestinationPath $staging -Force
        if (-not (Test-AppComplete $staging)) {
            Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
            throw "$($Release.ZipName) has no bin\proctrack.exe."
        }
        if (Test-Path -LiteralPath $appDir) { Remove-Item -LiteralPath $appDir -Recurse -Force }
        Move-Item -LiteralPath $staging -Destination $appDir
        return $appDir
    }
    finally { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
}

function Write-MachineServiceInUse([string]$TaskName, [bool]$Answering) {
    if ($Answering) { Write-Output "ProcessTracker's machine-wide service is running on this machine and the plugin uses it; no user-only copy is needed." }
    else { Write-Output "ProcessTracker's machine-wide service is installed on this machine but not answering; the plugin uses it, so no user-only copy is needed. Ask an admin to start the ProcessTracker service." }
    if (Test-UserTask $TaskName) {
        Write-Output "Your user-only copy (scheduled task $TaskName) is no longer used; 'proctrack user uninstall' removes it."
    }
    # The plugin provides the MCP entry; a user-scope one from the MSI would list the same server twice.
    $proctrack = Find-Proctrack
    if ($proctrack) { Write-Output (Invoke-Native $proctrack @('mcp', 'unregister')).Output.TrimEnd() }
    else { Write-Output "proctrack was not found; run 'proctrack mcp unregister' to remove a user-scope MCP entry that duplicates the plugin's." }
}

function Invoke-InstallUser {
    # Writes its messages to the output, sets $script:ExitCode and throws when it refuses or fails.
    if (Test-Elevated) {
        throw 'Refusing to run elevated: the user-only copy belongs to your own account. Run /process-tracker:install from a normal (non-elevated) Claude Code session.'
    }

    $taskName = Get-UserTaskName
    $health = Get-Health
    # A service that predates the mode field answers without it: that is the machine-wide service (as in session-start).
    $mode = $null
    if ($null -ne $health) {
        $mode = [string]$health.mode
        if (-not $mode) { $mode = 'service' }
    }
    if ($mode -eq 'service') {
        Write-MachineServiceInUse $taskName $true
        $script:ExitCode = 0
        return
    }
    # Installed but not the one answering (stopped, or a user-mode copy still holds the port): nothing to download.
    if (Test-MachineService) {
        Write-MachineServiceInUse $taskName $false
        $script:ExitCode = 0
        return
    }

    $me = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    if ($mode -eq 'user' -and $health.owner -and $health.owner -ne $me) {
        throw "The ProcessTracker on this machine belongs to another user ($($health.owner)); ask an admin to install the machine-wide service."
    }

    $headers = @{ 'User-Agent' = "ProcessTracker/$(Get-PluginVersion)" }
    $release = Get-LatestRelease $headers
    if ($mode -eq 'user') {
        $running = ConvertTo-PlainVersion ([string]$health.version)
        if ($null -ne $running -and -not ($release.Version -gt $running)) {
            Write-Output "Your user-only ProcessTracker $running is already installed and running; the latest release is $($release.Version). Nothing to do."
            $script:ExitCode = 0
            return
        }
    }

    # Download only what isn't on disk yet: a complete app-<latest>, or (nothing answering) an installed copy that is not
    # older than the latest release, is registered again instead, which also repairs a stopped install.
    $taskAppDir = Get-TaskAppDir $taskName
    $latestDir = Join-Path (Get-ProgramsDir) "app-$($release.Version)"
    $installed = Get-InstalledApp
    $created = $false
    if (Test-AppComplete $latestDir) {
        Write-Output "ProcessTracker $($release.Version) is already on disk; registering it."
        $appDir = $latestDir
    }
    elseif ($null -eq $health -and $null -ne $installed -and -not ($release.Version -gt $installed.Version)) {
        Write-Output "ProcessTracker $($installed.Version) is already on disk and the latest release is $($release.Version); registering it."
        $appDir = $installed.Dir
    }
    else {
        Write-Output "Downloading ProcessTracker $($release.Version)..."
        $appDir = Install-Release $release $headers $taskAppDir
        $created = $true
    }
    $proctrack = Join-Path $appDir 'bin\proctrack.exe'

    $install = Invoke-Native $proctrack @('user', 'install', '--app-dir', $appDir)
    Write-Output $install.Output.TrimEnd()
    $mcp = Invoke-Native $proctrack @('mcp', 'unregister')
    Write-Output $mcp.Output.TrimEnd()
    $script:ExitCode = $install.Code
    if ($install.Code -ne 0 -and $created -and -not (Test-SamePath $appDir $taskAppDir)) {
        Remove-Item -LiteralPath $appDir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Output "Removed $appDir again, since it was not installed."
    }
}

# Dot-sourcing (tests/plugin.tests.ps1) only loads the functions.
if ($MyInvocation.InvocationName -eq '.') { return }

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Invoke-InstallUser
    exit $script:ExitCode
}
catch {
    Write-Output "ProcessTracker was not installed: $($_.Exception.Message)"
    exit 1
}
