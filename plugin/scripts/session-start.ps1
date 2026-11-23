<#
.SYNOPSIS
    The plugin's SessionStart hook. Reads the hook input from stdin and prints what Claude should know.

.DESCRIPTION
    Windows PowerShell 5.1 or pwsh, no dependencies. Always exits 0 and prints at most one of:
      - the body of POST http://127.0.0.1:47811/api/hooks/session-start (the hook input plus plugin_version);
      - on 403, that ProcessTracker belongs to another user;
      - when nothing answers and this user's \ProcessTracker\User-<SID> task exists, that the task was started (it only
        starts an existing install; it never installs, updates or elevates anything);
      - when nothing answers and there is no such task, the offer of /process-tracker:install.
    Any other outcome prints nothing.
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$script:SessionStartUri = 'http://127.0.0.1:47811/api/hooks/session-start'
$script:PluginJson = Join-Path (Split-Path -Parent $PSScriptRoot) '.claude-plugin\plugin.json'

function Get-UserTaskName { "\ProcessTracker\User-$([Security.Principal.WindowsIdentity]::GetCurrent().User.Value)" }

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
    $result = Send-SessionStart (Add-PluginVersion $InputJson (Get-PluginVersion))
    if ($result.Status -eq 200) { return $result.Body }
    if ($result.Status -eq 403) {
        return 'ProcessTracker on this machine belongs to another user; ask an admin to install the machine-wide service.'
    }
    if ($result.Status -ne 0) { return '' }

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
