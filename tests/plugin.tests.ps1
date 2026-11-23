# Checks for the Claude Code plugin (plugin/) and its marketplace (.claude-plugin/marketplace.json).
# Run under Windows PowerShell 5.1 and pwsh, non-elevated:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests\plugin.tests.ps1
# Nothing here touches the network (except the hook against 127.0.0.1:1 and a dev build on 47812 if one runs), Task Scheduler or %LOCALAPPDATA%: the scripts are
# dot-sourced, which loads only their functions, and every side effect is replaced by a recording fake.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$pluginDir = Join-Path $repo 'plugin'
$installScript = Join-Path $pluginDir 'scripts\install-user.ps1'
$sessionScript = Join-Path $pluginDir 'scripts\session-start.ps1'

$failures = 0
function Check([bool]$Condition, [string]$Name) {
    if ($Condition) { Write-Output "ok   $Name" } else { Write-Output "FAIL $Name"; $script:failures++ }
}
function Read-Json([string]$Path) { Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json }
function Get-Names($Object) { @($Object.PSObject.Properties | ForEach-Object { $_.Name }) }
function New-TestDir {
    $d = Join-Path ([IO.Path]::GetTempPath()) ('pt-plugin-tests-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $d | Out-Null
    return $d
}

# Runs one case in a child scope, so the dot-sourced functions and the fakes it defines don't leak into the next case.
# A case that throws counts as a failure of that case and the run goes on.
function Invoke-Case([string]$Name, [scriptblock]$Body) {
    try { & $Body }
    catch { Write-Output "FAIL $Name (threw: $($_.Exception.Message))"; $script:failures++ }
}

# ---------------------------------------------------------------- manifests

Invoke-Case 'manifests' {
    $market = Read-Json (Join-Path $repo '.claude-plugin\marketplace.json')
    $plugin = Read-Json (Join-Path $pluginDir '.claude-plugin\plugin.json')
    $entry = @($market.plugins)
    Check ($market.name -eq 'processtracker') 'marketplace name is processtracker'
    Check ($market.owner.name -eq 'worc4021') 'marketplace owner is worc4021'
    Check ($entry.Count -eq 1 -and $entry[0].name -eq 'process-tracker') 'marketplace lists one plugin, process-tracker'
    Check ($plugin.name -eq 'process-tracker' -and $plugin.name -eq $entry[0].name) 'marketplace.json and plugin.json parse and names match'
    Check ($entry[0].source.source -eq 'git-subdir' -and $entry[0].source.url -eq 'https://github.com/worc4021/ProcessTracker.git' -and $entry[0].source.path -eq 'plugin') 'plugin source is the plugin subdirectory of the repo'
    Check ($plugin.version -match '^[0-9]+\.[0-9]+\.[0-9]+$') 'plugin.json version is X.Y.Z'
    # release-please bumps plugin.json in the release PR; the release workflow moves the ref after tagging.
    $ref = [string]$entry[0].source.ref
    Check ($ref -match '^v\d+\.\d+\.\d+$') 'marketplace ref is vX.Y.Z'
    Check ($ref -match '^v\d+\.\d+\.\d+$' -and $plugin.version -match '^[0-9]+\.[0-9]+\.[0-9]+$' -and [version]$ref.Substring(1) -le [version]$plugin.version) 'marketplace ref version <= plugin.json version'
    Check ($plugin.homepage -eq 'https://github.com/worc4021/ProcessTracker' -and $plugin.license -eq 'MIT' -and $plugin.description) 'plugin.json has homepage, license MIT and a description'

    $mcp = Read-Json (Join-Path $pluginDir '.mcp.json')
    $server = $mcp.mcpServers.'process-tracker'
    Check ((Get-Names $mcp.mcpServers).Count -eq 1 -and $server.type -eq 'http' -and $server.url -eq 'http://127.0.0.1:47811/mcp') '.mcp.json declares the process-tracker http server'
    Check ($server.headers.'X-Claude-Session' -eq '${CLAUDE_CODE_HOST_SESSION_ID:-}') '.mcp.json sends X-Claude-Session'
}

Invoke-Case 'hooks.json' {
    $hooksText = Get-Content -LiteralPath (Join-Path $pluginDir 'hooks\hooks.json') -Raw -Encoding UTF8
    $hooks = ($hooksText | ConvertFrom-Json).hooks
    Check ($hooksText -notmatch 'onFailure' -and $hooksText -notmatch 'permissionDecision') 'hooks.json has no onFailure block and no permissionDecision'

    $pre = @($hooks.PreToolUse)
    $preHook = @($pre[0].hooks)
    Check ($pre.Count -eq 1 -and $pre[0].matcher -eq 'Bash|PowerShell|Task|Agent') 'PreToolUse matcher is Bash|PowerShell|Task|Agent'
    Check ($preHook.Count -eq 1 -and $preHook[0].type -eq 'command' -and $preHook[0].command -eq 'cmd.exe') 'hooks.json PreToolUse runs cmd.exe (exec form)'
    $expected = @('/d', '/s', '/c', 'curl.exe -s -f --connect-timeout 0.25 --max-time 2 --data-binary @- http://127.0.0.1:47811/api/hooks/pre-tool-use & exit /b 0')
    Check ((@($preHook[0].args) -join "`n") -eq ($expected -join "`n")) 'PreToolUse runs curl.exe through cmd and always exits 0'
    Check ($preHook[0].timeout -eq 3) 'PreToolUse timeout 3'

    $start = @($hooks.SessionStart)
    $startHook = @($start[0].hooks)
    Check ($start.Count -eq 1 -and $startHook.Count -eq 1 -and $startHook[0].command -eq 'powershell.exe') 'SessionStart runs powershell.exe'
    $expectedStart = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', '${CLAUDE_PLUGIN_ROOT}/scripts/session-start.ps1')
    Check ((@($startHook[0].args) -join "`n") -eq ($expectedStart -join "`n")) 'SessionStart args (exec form)'
    Check ($startHook[0].timeout -eq 10) 'SessionStart timeout 10'
}

# Runs the PreToolUse hook the way Claude Code spawns an exec-form hook: the command with each args element as one
# argument (Windows quoting rules, as Node and .NET's ArgumentList apply them), the payload on stdin. 47811 in the args is
# replaced by $Port. Returns @{ Code; Out; Err; Ms }.
function ConvertTo-CommandLineArgument([string]$Arg) {
    # The MSVC/CommandLineToArgvW quoting that .NET's ArgumentList and Node use; for Windows PowerShell 5.1, whose
    # ProcessStartInfo has no ArgumentList.
    if ($Arg -ne '' -and $Arg -notmatch '[\s"]') { return $Arg }
    $sb = New-Object Text.StringBuilder
    [void]$sb.Append('"')
    $slashes = 0
    foreach ($c in $Arg.ToCharArray()) {
        if ($c -eq '\') { $slashes++; continue }
        if ($c -eq '"') { [void]$sb.Append('\' * ($slashes * 2 + 1)) } elseif ($slashes) { [void]$sb.Append('\' * $slashes) }
        $slashes = 0
        [void]$sb.Append($c)
    }
    [void]$sb.Append('\' * ($slashes * 2)).Append('"')
    return $sb.ToString()
}
function Invoke-PreToolUseHook([string]$Port, [string]$Payload) {
    $hook = @(@((Read-Json (Join-Path $pluginDir 'hooks\hooks.json')).hooks.PreToolUse)[0].hooks)[0]
    $hookArgs = @($hook.args | ForEach-Object { $_ -replace '127\.0\.0\.1:47811', "127.0.0.1:$Port" })
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = $hook.command
    if ($null -ne $psi.PSObject.Properties['ArgumentList']) { foreach ($a in $hookArgs) { $psi.ArgumentList.Add($a) } }
    else { $psi.Arguments = (@($hookArgs | ForEach-Object { ConvertTo-CommandLineArgument $_ }) -join ' ') }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $p = [Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEndAsync(); $err = $p.StandardError.ReadToEndAsync()
    $p.StandardInput.Write($Payload); $p.StandardInput.Close()
    if (-not $p.WaitForExit(10000)) { $p.Kill(); throw 'the hook did not finish within 10 s' }
    $watch.Stop()
    return @{ Code = $p.ExitCode; Out = $out.Result; Err = $err.Result; Ms = $watch.ElapsedMilliseconds }
}
$heavyPayload = '{"session_id":"plugin-tests","hook_event_name":"PreToolUse","tool_name":"Bash","tool_input":{"command":"dotnet build -m"},"cwd":"C:\repo"}'

Invoke-Case 'hook closed port' {
    $r = Invoke-PreToolUseHook '1' $heavyPayload
    Write-Output "     (closed port: $($r.Ms) ms)"
    Check ($r.Code -eq 0) 'PreToolUse hook against a closed port exits 0'
    Check ($r.Out -eq '' -and $r.Err -eq '') 'PreToolUse hook against a closed port prints nothing (stdout and stderr)'
    Check ($r.Ms -lt 600) 'PreToolUse hook against a closed port returns within 600 ms (connect timeout 0.25 s)'
}

# Needs a dev build answering on 47812 (ProcessTracker.Service.exe --dev); skipped otherwise.
Invoke-Case 'hook responder' {
    $dev = $null
    try { $dev = Invoke-RestMethod -Uri 'http://127.0.0.1:47812/api/health' -UseBasicParsing -TimeoutSec 1 } catch { }
    if ($null -eq $dev -or $dev.mode -ne 'dev') { Write-Output 'skip PreToolUse hook against a responder (no dev build on 47812)'; return }
    $r = Invoke-PreToolUseHook '47812' $heavyPayload
    $json = $null
    try { $json = $r.Out | ConvertFrom-Json } catch { }
    Check ($r.Code -eq 0 -and $r.Out.Trim().StartsWith('{') -and $null -ne $json -and $r.Err -eq '') 'PreToolUse hook passes the JSON answer through'
}

Invoke-Case 'commands' {
    $install = Get-Content -LiteralPath (Join-Path $pluginDir 'commands\install.md') -Raw
    $uninstall = Get-Content -LiteralPath (Join-Path $pluginDir 'commands\uninstall.md') -Raw
    $status = Get-Content -LiteralPath (Join-Path $pluginDir 'commands\status.md') -Raw
    Check ($install.Contains('powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_PLUGIN_ROOT}/scripts/install-user.ps1"')) 'install.md runs install-user.ps1'
    Check ($uninstall.Contains('user uninstall') -and $uninstall.Contains('--purge-data')) 'uninstall.md runs proctrack user uninstall, --purge-data only on request'
    Check ($status.Contains('user status') -and $status.Contains('proctrack load')) 'status.md runs proctrack user status and proctrack load'
}

# ---------------------------------------------------------------- install-user.ps1

# Fakes shared by the install cases. $script:calls records what would have happened.
$installFakes = {
    $script:calls = New-Object System.Collections.ArrayList
    function Test-Elevated { $false }
    function Get-Health { $null }
    function Test-UserTask([string]$Name) { $false }
    function Expand-Archive { param([string]$LiteralPath, [string]$DestinationPath, [switch]$Force) [void]$script:calls.Add("extract $DestinationPath") }
    $script:installCode = 0
    function Invoke-Native([string]$Exe, [string[]]$Arguments) {
        [void]$script:calls.Add("run $Exe $($Arguments -join ' ')")
        $code = 0
        if ($Arguments[0] -eq 'user') { $code = $script:installCode }
        @{ Code = $code; Output = 'fake output' }
    }
    function Find-Proctrack { 'C:\fake\proctrack.exe' }
    function Test-MachineService { $false }
    $script:taskAppDir = $null
    function Get-TaskAppDir([string]$Name) { $script:taskAppDir }
    $script:release = @{
        tag_name = 'v0.2.0'
        assets   = @(
            @{ name = 'ProcessTracker-0.2.0-x64.zip'; browser_download_url = 'https://github.com/worc4021/ProcessTracker/releases/download/v0.2.0/ProcessTracker-0.2.0-x64.zip' },
            @{ name = 'SHA256SUMS.txt'; browser_download_url = 'https://github.com/worc4021/ProcessTracker/releases/download/v0.2.0/SHA256SUMS.txt' })
    } | ConvertTo-Json -Depth 5
    $script:zipBytes = [Text.Encoding]::ASCII.GetBytes('not really a zip')
    $sha = [Security.Cryptography.SHA256]::Create()
    $script:goodHash = (($sha.ComputeHash($script:zipBytes) | ForEach-Object { $_.ToString('x2') }) -join '')
    $script:sumsHash = $script:goodHash
    $script:redirect = $null
    function Invoke-WebRequest {
        [CmdletBinding()]
        param([string]$Uri, [string]$OutFile, [switch]$UseBasicParsing, [int]$MaximumRedirection, [hashtable]$Headers,
            [switch]$PassThru, [switch]$SkipHttpErrorCheck, [int]$TimeoutSec, [string]$Method, $Body, [string]$ContentType)
        [void]$script:calls.Add("get $Uri")
        if ($Uri -like 'https://api.github.com/*') { return [pscustomobject]@{ StatusCode = 200; Content = $script:release; Headers = @{} } }
        if ($script:redirect -and $Uri -like 'https://github.com/*/releases/download/*.zip') { return [pscustomobject]@{ StatusCode = 302; Content = ''; Headers = @{ Location = $script:redirect } } }
        if ($Uri -like '*/SHA256SUMS.txt') { [IO.File]::WriteAllText($OutFile, "$($script:sumsHash)  ProcessTracker-0.2.0-x64.zip`n") }
        elseif ($Uri -like '*-x64.zip*') { [IO.File]::WriteAllBytes($OutFile, $script:zipBytes) }
        else { throw "unexpected download $Uri" }
        return [pscustomobject]@{ StatusCode = 200; Content = ''; Headers = @{} }
    }
}

# Points %TEMP% and %LOCALAPPDATA% at a fresh folder for one case and restores them afterwards.
function Use-TestEnvironment([scriptblock]$Body) {
    $saved = @{ TEMP = $env:TEMP; TMP = $env:TMP; LOCALAPPDATA = $env:LOCALAPPDATA }
    $root = New-TestDir
    try {
        $env:TEMP = Join-Path $root 'temp'; $env:TMP = $env:TEMP; $env:LOCALAPPDATA = Join-Path $root 'local'
        New-Item -ItemType Directory -Path $env:TEMP, $env:LOCALAPPDATA | Out-Null
        & $Body $root
    }
    finally {
        $env:TEMP = $saved.TEMP; $env:TMP = $saved.TMP; $env:LOCALAPPDATA = $saved.LOCALAPPDATA
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Invoke-Case 'install refuses when elevated' {
    . $installScript
    . $installFakes
    function Test-Elevated { $true }
    $threw = $false
    try { Invoke-InstallUser | Out-Null } catch { $threw = $true; $message = $_.Exception.Message }
    Check $threw 'install-user.ps1 refuses when elevated (throws)'
    Check ($message -match 'elevated') 'refusal message says elevated'
    Check (@($script:calls | Where-Object { $_ -like 'get *' }).Count -eq 0) 'install-user.ps1 refuses when elevated: no download call'
}

Invoke-Case 'install rejects hash mismatch' {
    Use-TestEnvironment {
        param($root)
        . $installScript
        . $installFakes
        $script:sumsHash = '0' * 64
        $threw = $false
        try { Invoke-InstallUser | Out-Null } catch { $threw = $true; $message = $_.Exception.Message }
        Check ($threw -and $message -match 'SHA-256') 'install-user.ps1 rejects hash mismatch (throws)'
        Check (@($script:calls | Where-Object { $_ -like 'get *.zip' }).Count -eq 1) 'the zip was downloaded'
        Check (@($script:calls | Where-Object { $_ -like 'extract *' -or $_ -like 'run *' }).Count -eq 0) 'install-user.ps1 rejects hash mismatch: no extract, nothing run'
        Check (@(Get-ChildItem -LiteralPath $env:TEMP -Recurse -File).Count -eq 0) 'downloads deleted after a mismatch'
    }
}

Invoke-Case 'install happy path' {
    Use-TestEnvironment {
        param($root)
        . $installScript
        . $installFakes
        # The fake Expand-Archive creates the layout a real zip would.
        function Expand-Archive {
            param([string]$LiteralPath, [string]$DestinationPath, [switch]$Force)
            [void]$script:calls.Add("extract $DestinationPath")
            New-Item -ItemType Directory -Path (Join-Path $DestinationPath 'bin') -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $DestinationPath 'bin\proctrack.exe') -Value 'x'
        }
        $script:ExitCode = $null
        $out = Invoke-InstallUser | Out-String
        $appDir = Join-Path $env:LOCALAPPDATA 'Programs\ProcessTracker\app-0.2.0'
        $exe = Join-Path $appDir 'bin\proctrack.exe'
        Check (Test-Path -LiteralPath $exe) 'extracted to %LOCALAPPDATA%\Programs\ProcessTracker\app-0.2.0'
        Check ($script:calls -contains "run $exe user install --app-dir $appDir") 'runs proctrack user install --app-dir <app dir>'
        Check ($script:calls -contains "run $exe mcp unregister") 'runs proctrack mcp unregister'
        Check ($script:ExitCode -eq 0 -and $out -match 'fake output') 'prints proctrack output and keeps its exit code'
        Check (@($script:calls | Where-Object { $_ -like 'get https://api.github.com/repos/worc4021/ProcessTracker/releases/latest' }).Count -eq 1) 'reads the latest release from the GitHub API'
        Check (@(Get-ChildItem -LiteralPath $env:TEMP -Recurse -File).Count -eq 0) 'downloads deleted after install'
    }
}

Invoke-Case 'install refuses foreign redirect' {
    Use-TestEnvironment {
        param($root)
        . $installScript
        . $installFakes
        $script:redirect = 'https://evil.example/x.zip'
        $threw = $false
        try { Invoke-InstallUser | Out-Null } catch { $threw = $true; $message = $_.Exception.Message }
        Check ($threw -and $message -match 'evil\.example') 'redirect to a foreign host is refused'
        Check (@($script:calls | Where-Object { $_ -like '*evil.example*' }).Count -eq 0) 'the foreign host is never contacted'
        Check (@($script:calls | Where-Object { $_ -like 'extract *' }).Count -eq 0) 'nothing extracted after a refused redirect'
    }
}

Invoke-Case 'install follows allowlisted redirect' {
    Use-TestEnvironment {
        param($root)
        . $installScript
        . $installFakes
        $script:redirect = 'https://release-assets.githubusercontent.com/abc/ProcessTracker-0.2.0-x64.zip.bin?sig=1'
        try { Invoke-InstallUser | Out-Null } catch { }   # the fake extract leaves no proctrack.exe; only the hops matter
        Check (@($script:calls | Where-Object { $_ -like 'get https://release-assets.githubusercontent.com/*' }).Count -eq 1) 'redirect to release-assets.githubusercontent.com is followed'
    }
}

# Creates %LOCALAPPDATA%\Programs\ProcessTracker\app-<v>, complete (with bin\proctrack.exe) or not, plus a marker file.
function New-AppDir([string]$Version, [bool]$Complete) {
    $dir = Join-Path $env:LOCALAPPDATA "Programs\ProcessTracker\app-$Version"
    New-Item -ItemType Directory -Path (Join-Path $dir 'bin') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $dir 'marker.txt') -Value 'keep'
    if ($Complete) { Set-Content -LiteralPath (Join-Path $dir 'bin\proctrack.exe') -Value 'x' }
    return $dir
}

Invoke-Case 'install does not redownload when app-<latest> exists and health is down' {
    Use-TestEnvironment {
        param($root)
        . $installScript
        . $installFakes
        $dir = New-AppDir '0.2.0' $true
        $script:taskAppDir = $dir
        $script:ExitCode = $null
        Invoke-InstallUser | Out-Null
        $exe = Join-Path $dir 'bin\proctrack.exe'
        Check (@($script:calls | Where-Object { $_ -like 'get *.zip' -or $_ -like 'get *SHA256SUMS*' -or $_ -like 'extract *' }).Count -eq 0) 'install does not redownload when app-<latest> exists and health is down'
        Check ($script:calls -contains "run $exe user install --app-dir $dir") 'registers the existing app-<latest> again (repairs a stopped install)'
        Check ((Test-Path -LiteralPath (Join-Path $dir 'marker.txt')) -and $script:ExitCode -eq 0) 'the existing folder is kept'
    }
}

Invoke-Case 'install does not redownload an older release than the installed one' {
    Use-TestEnvironment {
        param($root)
        . $installScript
        . $installFakes
        $dir = New-AppDir '0.3.0' $true
        Invoke-InstallUser | Out-Null
        Check (@($script:calls | Where-Object { $_ -like 'get *.zip' -or $_ -like 'extract *' }).Count -eq 0) 'health down, installed 0.3.0 > latest 0.2.0: no download'
        Check ($script:calls -contains "run $(Join-Path $dir 'bin\proctrack.exe') user install --app-dir $dir") 'registers the installed app-0.3.0 again'
    }
}

Invoke-Case 'install never deletes the folder the task points to' {
    Use-TestEnvironment {
        param($root)
        . $installScript
        . $installFakes
        $dir = New-AppDir '0.2.0' $false   # incomplete, and the task runs it
        $script:taskAppDir = $dir
        $threw = $false
        try { Invoke-InstallUser | Out-Null } catch { $threw = $true }
        Check ($threw -and (Test-Path -LiteralPath (Join-Path $dir 'marker.txt'))) 'install never deletes the folder the task points to'
        Check (@($script:calls | Where-Object { $_ -like 'get *.zip' -or $_ -like 'extract *' }).Count -eq 0) 'nothing downloaded into the task''s folder'
    }
}

Invoke-Case 'install replaces an incomplete leftover and cleans up a failed install' {
    Use-TestEnvironment {
        param($root)
        . $installScript
        . $installFakes
        function Expand-Archive {
            param([string]$LiteralPath, [string]$DestinationPath, [switch]$Force)
            [void]$script:calls.Add("extract $DestinationPath")
            New-Item -ItemType Directory -Path (Join-Path $DestinationPath 'bin') -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $DestinationPath 'bin\proctrack.exe') -Value 'x'
        }
        $dir = New-AppDir '0.2.0' $false   # incomplete leftover, not the task's folder
        $script:installCode = 1
        $script:ExitCode = $null
        Invoke-InstallUser | Out-Null
        Check (@($script:calls | Where-Object { $_ -like 'extract *' }).Count -eq 1) 'an incomplete leftover app-<v> is downloaded again'
        Check ($script:ExitCode -eq 1) 'exit code of a failed proctrack user install is kept'
        Check (-not (Test-Path -LiteralPath $dir)) 'app-<v> extracted by this run is removed when user install fails'
    }
}

Invoke-Case 'install keeps a failed task folder it extracted' {
    Use-TestEnvironment {
        param($root)
        . $installScript
        . $installFakes
        function Expand-Archive {
            param([string]$LiteralPath, [string]$DestinationPath, [switch]$Force)
            New-Item -ItemType Directory -Path (Join-Path $DestinationPath 'bin') -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $DestinationPath 'bin\proctrack.exe') -Value 'x'
        }
        $script:taskAppDir = Join-Path $env:LOCALAPPDATA 'Programs\ProcessTracker\app-0.2.0'   # task points at a missing folder
        $script:installCode = 1
        Invoke-InstallUser | Out-Null
        Check (Test-Path -LiteralPath $script:taskAppDir) 'a failed install never removes the folder the task points to'
    }
}

Invoke-Case 'install with the machine service installed but stopped' {
    Use-TestEnvironment {
        param($root)
        . $installScript
        . $installFakes
        function Test-MachineService { $true }
        $out = Invoke-InstallUser | Out-String
        Check ($out -match 'machine-wide service is installed') 'service installed, not answering: says the machine-wide service is in use'
        Check (($script:calls -join '|') -eq 'run C:\fake\proctrack.exe mcp unregister') 'service installed, not answering: no download, only mcp unregister'
    }
}

Invoke-Case 'install defers to the machine service' {
    . $installScript
    . $installFakes
    function Get-Health { [pscustomobject]@{ version = '0.2.0'; mode = 'service' } }
    function Test-UserTask([string]$Name) { $true }
    $out = Invoke-InstallUser | Out-String
    Check ($out -match 'machine-wide service') 'service mode: says the machine-wide service is in use'
    Check ($out -match 'proctrack user uninstall') 'service mode with a user task: points at proctrack user uninstall'
    Check (@($script:calls | Where-Object { $_ -like 'get *' }).Count -eq 0) 'service mode: no download'
    Check (($script:calls -join '|') -eq 'run C:\fake\proctrack.exe mcp unregister') 'service mode: runs only proctrack mcp unregister'
}

Invoke-Case 'install defers to the machine service without proctrack' {
    . $installScript
    . $installFakes
    function Get-Health { [pscustomobject]@{ version = '0.2.0'; mode = 'service' } }
    function Find-Proctrack { $null }
    $out = Invoke-InstallUser | Out-String
    Check ($out -match 'proctrack mcp unregister') 'service mode, proctrack not found: hint to run proctrack mcp unregister'
    Check ($script:calls.Count -eq 0) 'service mode, proctrack not found: nothing run'
}

Invoke-Case 'install skips a current user-mode copy' {
    . $installScript
    . $installFakes
    function Get-Health { [pscustomobject]@{ version = '0.2.0'; mode = 'user'; owner = [Security.Principal.WindowsIdentity]::GetCurrent().Name } }
    $out = Invoke-InstallUser | Out-String
    Check ($out -match 'already') 'own user-mode copy at the latest version: nothing to do'
    Check (@($script:calls | Where-Object { $_ -like 'get *.zip' -or $_ -like 'run *' }).Count -eq 0) 'no zip download when not strictly newer'
}

Invoke-Case 'SHA256SUMS parsing' {
    . $installScript
    $sums = Read-Sha256Sums ("$('ab' * 32)  a.zip`n`n$('CD' * 32)  SHA256SUMS-other.txt`n")
    Check ($sums['a.zip'] -eq ('ab' * 32) -and $sums['SHA256SUMS-other.txt'] -eq ('cd' * 32)) 'SHA256SUMS: two-space format, lower-cased'
    $threw = $false
    try { Read-Sha256Sums 'nonsense line' | Out-Null } catch { $threw = $true }
    Check $threw 'SHA256SUMS: malformed line throws'
}

# ---------------------------------------------------------------- session-start.ps1

$sessionFakes = {
    $script:posts = New-Object System.Collections.ArrayList
    $script:runs = New-Object System.Collections.ArrayList
    $script:taskExists = $false
    function Test-UserTask([string]$Name) { $script:taskExists }
    function Invoke-Native([string]$Exe, [string[]]$Arguments) { [void]$script:runs.Add("$Exe $($Arguments -join ' ')"); @{ Code = 0; Output = '' } }
}
function New-ConnectionRefused {
    New-Object System.Net.WebException('Unable to connect to the remote server', [System.Net.WebExceptionStatus]::ConnectFailure)
}

Invoke-Case 'session-start unreachable' {
    . $sessionScript
    . $sessionFakes
    function Invoke-WebRequest {
        [CmdletBinding()]
        param([string]$Uri, [string]$Method, [switch]$UseBasicParsing, [string]$ContentType, $Body, [int]$TimeoutSec,
            [int]$MaximumRedirection, [switch]$SkipHttpErrorCheck)
        [void]$script:posts.Add($Uri)
        throw (New-ConnectionRefused)
    }
    $out = Invoke-SessionStart '{"session_id":"s1","hook_event_name":"SessionStart"}'
    Check ($out -match '/process-tracker:install') 'session-start.ps1 prints install offer when unreachable'
    Check ($script:posts -contains 'http://127.0.0.1:47811/api/hooks/session-start') 'session-start posts to /api/hooks/session-start'
    Check ($script:runs.Count -eq 0) 'no task to start when none exists'
}

Invoke-Case 'session-start starts an existing user task' {
    . $sessionScript
    . $sessionFakes
    $script:taskExists = $true
    function Invoke-WebRequest {
        [CmdletBinding()]
        param([string]$Uri, [string]$Method, [switch]$UseBasicParsing, [string]$ContentType, $Body, [int]$TimeoutSec,
            [int]$MaximumRedirection, [switch]$SkipHttpErrorCheck)
        throw (New-ConnectionRefused)
    }
    $out = Invoke-SessionStart '{}'
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    Check ($script:runs -contains "schtasks.exe /Run /TN \ProcessTracker\User-$sid") 'runs schtasks /Run on \ProcessTracker\User-<SID>'
    Check ($out -match 'started' -and $out -notmatch '/process-tracker:install') 'prints one line saying it was started'
    Check (@($out -split "`n" | Where-Object { $_.Trim() }).Count -eq 1) 'one line'
}

Invoke-Case 'session-start 403' {
    . $sessionScript
    . $sessionFakes
    function Invoke-WebRequest {
        [CmdletBinding()]
        param([string]$Uri, [string]$Method, [switch]$UseBasicParsing, [string]$ContentType, $Body, [int]$TimeoutSec,
            [int]$MaximumRedirection, [switch]$SkipHttpErrorCheck)
        [pscustomobject]@{ StatusCode = 403; Content = 'this ProcessTracker belongs to another user; ask an admin to install the machine-wide service' }
    }
    $out = Invoke-SessionStart '{}'
    Check ($out -eq 'ProcessTracker on this machine belongs to another user; ask an admin to install the machine-wide service.') 'session-start prints the other-user line on 403'
}

Invoke-Case 'session-start passes the response through' {
    . $sessionScript
    . $sessionFakes
    $script:body = $null
    function Invoke-WebRequest {
        [CmdletBinding()]
        param([string]$Uri, [string]$Method, [switch]$UseBasicParsing, [string]$ContentType, $Body, [int]$TimeoutSec,
            [int]$MaximumRedirection, [switch]$SkipHttpErrorCheck)
        $script:body = [Text.Encoding]::UTF8.GetString([byte[]]$Body)
        $script:timeout = $TimeoutSec
        [pscustomobject]@{ StatusCode = 200; Content = '{"hookSpecificOutput":{"hookEventName":"SessionStart","additionalContext":"hi"}}' }
    }
    $out = Invoke-SessionStart '{"session_id":"s1","cwd":"C:\\Users\\caf\u00e9"}'
    Check ($out -eq '{"hookSpecificOutput":{"hookEventName":"SessionStart","additionalContext":"hi"}}') 'session-start writes the response body to stdout'
    $sent = $script:body | ConvertFrom-Json
    $version = (Read-Json (Join-Path $pluginDir '.claude-plugin\plugin.json')).version
    Check ($sent.plugin_version -eq $version -and $sent.session_id -eq 's1') 'session-start adds plugin_version from plugin.json'
    Check ($script:timeout -eq 3) 'session-start POST timeout 3 s'
}

Invoke-Case 'session-start other status is silent' {
    . $sessionScript
    . $sessionFakes
    function Invoke-WebRequest {
        [CmdletBinding()]
        param([string]$Uri, [string]$Method, [switch]$UseBasicParsing, [string]$ContentType, $Body, [int]$TimeoutSec,
            [int]$MaximumRedirection, [switch]$SkipHttpErrorCheck)
        [pscustomobject]@{ StatusCode = 500; Content = 'boom' }
    }
    $out = Invoke-SessionStart '{}'
    Check ([string]::IsNullOrEmpty($out)) 'session-start prints nothing on other errors'
}

# The real script end to end, pointed at a closed port and a task that doesn't exist through its environment seams, so
# it neither reaches the real tracker on 47811 nor starts the developer's own task.
Invoke-Case 'session-start process' {
    $exe = (Get-Process -Id $PID).Path
    $saved = $ErrorActionPreference
    $savedEnv = @{ Url = $env:PROCTRACK_HOOK_URL_BASE; Task = $env:PROCTRACK_USER_TASK }
    $ErrorActionPreference = 'Continue'
    try {
        $env:PROCTRACK_HOOK_URL_BASE = 'http://127.0.0.1:1'
        $env:PROCTRACK_USER_TASK = '\ProcessTracker\User-plugin-tests-no-such-task'
        $out = '{"session_id":"plugin-tests"}' | & $exe -NoProfile -ExecutionPolicy Bypass -File $sessionScript 2>&1 | Out-String
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $saved
        $env:PROCTRACK_HOOK_URL_BASE = $savedEnv.Url; $env:PROCTRACK_USER_TASK = $savedEnv.Task
    }
    Check ($code -eq 0) 'session-start.ps1 exits 0'
    Check ($out.Trim() -eq 'ProcessTracker is not running. Offer the user /process-tracker:install once; it installs a user-only copy without admin rights.') 'session-start.ps1 prints only the install offer (no error text)'
}

Invoke-Case 'session-start seams default to production' {
    $savedEnv = @{ Url = $env:PROCTRACK_HOOK_URL_BASE; Task = $env:PROCTRACK_USER_TASK }
    try {
        Remove-Item Env:PROCTRACK_HOOK_URL_BASE, Env:PROCTRACK_USER_TASK -ErrorAction SilentlyContinue
        . $sessionScript
        $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        Check ($script:SessionStartUri -eq 'http://127.0.0.1:47811/api/hooks/session-start') 'session-start URL defaults to 127.0.0.1:47811'
        Check ((Get-UserTaskName) -eq "\ProcessTracker\User-$sid") 'session-start task name defaults to \ProcessTracker\User-<SID>'
    }
    finally { $env:PROCTRACK_HOOK_URL_BASE = $savedEnv.Url; $env:PROCTRACK_USER_TASK = $savedEnv.Task }
}

if ($failures -gt 0) { Write-Output "$failures check(s) failed"; exit 1 }
exit 0
