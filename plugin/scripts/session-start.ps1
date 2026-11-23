<#
.SYNOPSIS
    The plugin's SessionStart hook. Reads the hook input from stdin and prints what Claude should know.

.DESCRIPTION
    Windows PowerShell 5.1 or pwsh, no dependencies. Always exits 0 and prints at most one of:
      - when the process listening on 127.0.0.1:47811 runs as another Windows user (neither this user nor SYSTEM), that
        the port is held by another user's process; nothing is posted then. When the owner can't be looked up at all
        (no Get-NetTCPConnection, WMI unavailable), the script goes on as if the check had passed;
      - the body of POST http://127.0.0.1:47811/api/hooks/session-start (the hook input plus plugin_version);
      - on 403, that ProcessTracker belongs to another user;
      - when nothing answers and the machine-wide ProcessTracker service is installed (sc query), that it is installed
        but not running (a leftover user task is not started: user mode exits while the service is installed);
      - when nothing answers and this user's \ProcessTracker\User-<SID> task exists, that the task was started (it only
        starts an existing install; it never installs, updates or elevates anything);
      - when nothing answers and there is no such task, the offer of /process-tracker:install.
    Any other outcome prints nothing.
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Test seams (tests/plugin.tests.ps1): PROCTRACK_HOOK_URL_BASE replaces http://127.0.0.1:47811 and PROCTRACK_USER_TASK the
# task name. Unset in production. Each is honoured only when harmless (a loopback host; a task under \ProcessTracker\),
# otherwise the default is used.
$script:DefaultUrlBase = 'http://127.0.0.1:47811'

function Resolve-UrlBase([string]$Value) {
    # $Value without a trailing slash when it is an http(s) URL whose host is 127.0.0.1, localhost or [::1]; else the default.
    if (-not $Value) { return $script:DefaultUrlBase }
    $trimmed = $Value.TrimEnd('/')
    $uri = $null
    if (-not [Uri]::TryCreate($trimmed, [UriKind]::Absolute, [ref]$uri)) { return $script:DefaultUrlBase }
    if (@('http', 'https') -notcontains $uri.Scheme -or $uri.UserInfo) { return $script:DefaultUrlBase }
    if (@('127.0.0.1', 'localhost') -notcontains $uri.Host.ToLowerInvariant() -and -not (Test-Ipv6Loopback $uri)) { return $script:DefaultUrlBase }
    return $trimmed
}

function Test-Ipv6Loopback([Uri]$Uri) {
    # [::1]; Windows PowerShell 5.1 spells its Host out in full, so compare the address, not the text.
    if ($Uri.HostNameType -ne [UriHostNameType]::IPv6) { return $false }
    $ip = $null
    return [Net.IPAddress]::TryParse($Uri.DnsSafeHost.Trim('[', ']'), [ref]$ip) -and $ip.Equals([Net.IPAddress]::IPv6Loopback)
}

$script:UrlBase = Resolve-UrlBase $env:PROCTRACK_HOOK_URL_BASE
$script:SessionStartUri = "$($script:UrlBase)/api/hooks/session-start"
$script:PluginJson = Join-Path (Split-Path -Parent $PSScriptRoot) '.claude-plugin\plugin.json'
$script:SystemSid = 'S-1-5-18'

function Get-UserTaskName {
    $override = $env:PROCTRACK_USER_TASK
    if ($override -and $override.StartsWith('\ProcessTracker\', [StringComparison]::OrdinalIgnoreCase)) { return $override }
    return "\ProcessTracker\User-$([Security.Principal.WindowsIdentity]::GetCurrent().User.Value)"
}

function Invoke-Native {
    # Runs a native program and returns its exit code and combined output, with 'Continue' locally so a stderr line
    # doesn't become a terminating error in Windows PowerShell 5.1.
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

function Test-MachineService {
    # True when the ProcessTracker Windows service is installed, running or not (sc query needs no admin rights).
    (Invoke-Native 'sc.exe' @('query', 'ProcessTracker')).Code -eq 0
}

function Get-ListenerOwnerSid([string]$Address, [int]$Port) {
    # Who runs the process listening on $Address:$Port:
    #   - its owner's SID when it can be read (always so for this user's own processes);
    #   - S-1-5-18 when it is the ProcessTracker machine service's process (a standard user may not read SYSTEM's SID);
    #   - '' when the owner can't be read and it isn't that service: another account's process;
    #   - $null when nothing listens or the lookup itself fails (no Get-NetTCPConnection, WMI unavailable).
    try { $listen = @(Get-NetTCPConnection -LocalAddress $Address -LocalPort $Port -State Listen -ErrorAction Stop) }
    catch { return $null }   # also what "nothing listens" looks like
    if ($listen.Count -eq 0) { return $null }
    $owningPid = [int]$listen[0].OwningProcess
    try {
        # The service first: it is the common case on machines with the MSI, and the cheaper query (about 0.4 s).
        $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='ProcessTracker'" -ErrorAction Stop
        if ($null -ne $service -and [int]$service.ProcessId -eq $owningPid) { return $script:SystemSid }
        $process = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId=$owningPid" -ErrorAction Stop
        if ($null -eq $process) { return $null }
        $owner = Invoke-CimMethod -InputObject $process -MethodName GetOwnerSid -ErrorAction Stop
        if ($owner.ReturnValue -eq 0 -and $owner.Sid) { return [string]$owner.Sid }
        return ''
    }
    catch { return $null }
}

function Test-ForeignListener {
    # True when the tracker's port is held by a process of another Windows user (not this user, not SYSTEM).
    $uri = [Uri]$script:UrlBase
    $address = '127.0.0.1'
    if (Test-Ipv6Loopback $uri) { $address = '::1' }
    $sid = Get-ListenerOwnerSid $address $uri.Port
    if ($null -eq $sid) { return $false }
    $me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    return -not ($sid -eq $me -or $sid -eq $script:SystemSid)
}

function Get-PluginVersion {
    try { return [string](Get-Content -LiteralPath $script:PluginJson -Raw | ConvertFrom-Json).version } catch { return '' }
}

function Add-PluginVersion([string]$Json, [string]$Version) {
    # Inserts "plugin_version" as the first member of the hook input object, leaving the rest byte for byte as it was.
    $member = '"plugin_version":' + (ConvertTo-Json $Version -Compress)
    if ($Json -match '^\s*\{\s*\}\s*$' -or $Json -notmatch '^\s*\{') { return '{' + $member + '}' }
    $open = $Json.IndexOf('{')
    return '{' + $member + ',' + $Json.Substring($open + 1)
}

function Send-SessionStart([string]$Body) {
    # @{ Status = <HTTP status, 0 when nothing answered>; Body = <response text> }
    $p = @{ Uri = $script:SessionStartUri; Method = 'Post'; UseBasicParsing = $true; ContentType = 'application/json'
        Body = [Text.Encoding]::UTF8.GetBytes($Body); TimeoutSec = 3; MaximumRedirection = 0 }
    if ($PSVersionTable.PSVersion.Major -ge 7) { $p['SkipHttpErrorCheck'] = $true }
    try {
        $r = Invoke-WebRequest @p
        return @{ Status = [int]$r.StatusCode; Body = [string]$r.Content }
    }
    catch {
        # Windows PowerShell 5.1 throws a WebException for an HTTP error status; it carries the response.
        $e = $_.Exception
        while ($null -ne $e) {
            if ($null -ne $e.Response) { return @{ Status = [int]$e.Response.StatusCode; Body = '' } }
            $e = $e.InnerException
        }
        return @{ Status = 0; Body = '' }
    }
}

function Invoke-SessionStart([string]$InputJson) {
    # Returns the text to print (possibly empty).
    if (Test-ForeignListener) {
        # An impostor would receive the hook input and could inject context: post nothing.
        return "ProcessTracker: port $(([Uri]$script:UrlBase).Port) is held by another Windows user's process; not using it. Ask an admin to install the machine-wide service."
    }
    $result = Send-SessionStart (Add-PluginVersion $InputJson (Get-PluginVersion))
    if ($result.Status -eq 200) { return $result.Body }
    if ($result.Status -eq 403) {
        return 'ProcessTracker on this machine belongs to another user; ask an admin to install the machine-wide service.'
    }
    if ($result.Status -ne 0) { return '' }

    # User mode exits (code 4) while the machine service is installed, so a leftover user task would not help.
    if (Test-MachineService) {
        return 'ProcessTracker: the machine-wide service is installed but not running; ask an admin to start it.'
    }
    $task = Get-UserTaskName
    if (Test-UserTask $task) {
        if ((Invoke-Native 'schtasks.exe' @('/Run', '/TN', $task)).Code -eq 0) {
            return "ProcessTracker was not running; started your user-only copy (scheduled task $task), its tools answer in a few seconds."
        }
        return "ProcessTracker is not running and starting your user-only copy (scheduled task $task) failed; /process-tracker:status shows details."
    }
    return 'ProcessTracker is not running. Offer the user /process-tracker:install once; it installs a user-only copy without admin rights.'
}

# Dot-sourcing (tests/plugin.tests.ps1) only loads the functions.
if ($MyInvocation.InvocationName -eq '.') { return }

try {
    try { [Console]::InputEncoding = New-Object Text.UTF8Encoding($false) } catch { }
    $text = Invoke-SessionStart ([Console]::In.ReadToEnd())
    if ($text) {
        $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes($text)
        $stdout = [Console]::OpenStandardOutput()
        $stdout.Write($bytes, 0, $bytes.Length)
        $stdout.Flush()
    }
}
catch { }
exit 0
