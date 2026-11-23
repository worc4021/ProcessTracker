<#
.SYNOPSIS
    Installs a user-only copy of ProcessTracker (no admin rights). Run by /process-tracker:install, never on its own.

.DESCRIPTION
    Windows PowerShell 5.1 or pwsh, no dependencies.
      1. Refuses when elevated: the user-only copy belongs to the signed-in user, not to an administrator.
      2. If http://127.0.0.1:47811/api/health answers with mode "service", the machine-wide service is in use and nothing
         is installed (an existing per-user task is pointed out, with the command that removes it), but proctrack mcp
         unregister still runs when proctrack is found (otherwise a hint says to run it). An own user-mode copy
         that is already at the latest version is left alone; another user's copy is refused.
      3. Reads https://api.github.com/repos/worc4021/ProcessTracker/releases/latest, downloads
         ProcessTracker-<v>-x64.zip and SHA256SUMS.txt to %TEMP%, following redirects only to github.com,
         objects.githubusercontent.com and release-assets.githubusercontent.com, checks the SHA-256 and extracts to
         %LOCALAPPDATA%\Programs\ProcessTracker\app-<v>.
      4. Runs app-<v>\bin\proctrack.exe user install --app-dir <that folder> and prints its output.
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

function Find-Proctrack {
    # proctrack.exe on the PATH (the MSI puts it there), else the highest user-mode app-<v>\bin\proctrack.exe; $null if none.
    $onPath = Get-Command 'proctrack.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $onPath) { return $onPath.Source }
    try { $programs = Get-ProgramsDir } catch { return $null }
    if (-not (Test-Path -LiteralPath $programs)) { return $null }
    $best = $null
    $bestVersion = $null
    foreach ($dir in Get-ChildItem -LiteralPath $programs -Directory -Filter 'app-*') {
        $v = ConvertTo-PlainVersion ($dir.Name.Substring(4))
        $exe = Join-Path $dir.FullName 'bin\proctrack.exe'
        if ($null -eq $v -or $dir.Name.Substring(4) -ne $v.ToString() -or -not (Test-Path -LiteralPath $exe)) { continue }
        if ($null -eq $bestVersion -or $v -gt $bestVersion) { $best = $exe; $bestVersion = $v }
    }
    return $best
}

function Install-Release($Release, [hashtable]$Headers) {
    # Downloads and verifies the release zip, extracts it to app-<v> and returns that folder.
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

        $appDir = Join-Path (Get-ProgramsDir) "app-$($Release.Version)"
        $staging = "$appDir.partial"
        if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
        Expand-Archive -LiteralPath $zipPath -DestinationPath $staging -Force
        if (-not (Test-Path -LiteralPath (Join-Path $staging 'bin\proctrack.exe'))) {
            Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
            throw "$($Release.ZipName) has no bin\proctrack.exe."
        }
        if (Test-Path -LiteralPath $appDir) { Remove-Item -LiteralPath $appDir -Recurse -Force }
        Move-Item -LiteralPath $staging -Destination $appDir
        return $appDir
    }
    finally { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
}

function Invoke-InstallUser {
    # Writes its messages to the output, sets $script:ExitCode and throws when it refuses or fails.
    if (Test-Elevated) {
        throw 'Refusing to run elevated: the user-only copy belongs to your own account. Run /process-tracker:install from a normal (non-elevated) Claude Code session.'
    }

    $taskName = Get-UserTaskName
    $health = Get-Health
    if ($null -ne $health -and $health.mode -eq 'service') {
        Write-Output "ProcessTracker's machine-wide service is running on this machine and the plugin uses it; no user-only copy is needed."
        if (Test-UserTask $taskName) {
            Write-Output "Your user-only copy (scheduled task $taskName) is no longer used; 'proctrack user uninstall' removes it."
        }
        # The plugin provides the MCP entry; a user-scope one from the MSI would list the same server twice.
        $proctrack = Find-Proctrack
        if ($proctrack) { Write-Output (Invoke-Native $proctrack @('mcp', 'unregister')).Output.TrimEnd() }
        else { Write-Output "proctrack was not found; run 'proctrack mcp unregister' to remove a user-scope MCP entry that duplicates the plugin's." }
        $script:ExitCode = 0
        return
    }

    $me = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    if ($null -ne $health -and $health.mode -eq 'user' -and $health.owner -and $health.owner -ne $me) {
        throw "The ProcessTracker on this machine belongs to another user ($($health.owner)); ask an admin to install the machine-wide service."
    }

    $headers = @{ 'User-Agent' = "ProcessTracker/$(Get-PluginVersion)" }
    $release = Get-LatestRelease $headers
    if ($null -ne $health -and $health.mode -eq 'user') {
        $running = ConvertTo-PlainVersion ([string]$health.version)
        if ($null -ne $running -and -not ($release.Version -gt $running)) {
            Write-Output "Your user-only ProcessTracker $running is already installed and running; the latest release is $($release.Version). Nothing to do."
            $script:ExitCode = 0
            return
        }
    }

    Write-Output "Downloading ProcessTracker $($release.Version)..."
    $appDir = Install-Release $release $headers
    $proctrack = Join-Path $appDir 'bin\proctrack.exe'

    $install = Invoke-Native $proctrack @('user', 'install', '--app-dir', $appDir)
    Write-Output $install.Output.TrimEnd()
    $mcp = Invoke-Native $proctrack @('mcp', 'unregister')
    Write-Output $mcp.Output.TrimEnd()
    $script:ExitCode = $install.Code
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
