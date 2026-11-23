# Regression checks for installer/upgrade.ps1. Run under Windows PowerShell 5.1 and pwsh, non-elevated:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests\upgrade-script.tests.ps1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\installer\upgrade.ps1')   # loads the functions only

$failures = 0
function Check([bool]$Condition, [string]$Name) {
    if ($Condition) { Write-Output "ok   $Name" } else { Write-Output "FAIL $Name"; $script:failures++ }
}

# stderr output must not become a terminating error (5.1) and the exit code must survive.
$run = Invoke-Native 'cmd.exe' @('/c', 'echo err 1>&2 & exit 3')
Check ($run.Code -eq 3) 'exit code 3 preserved when the program writes to stderr'
Check ($run.Output -match 'err') 'stderr captured'
$o = Get-Outcome $run.Code $run.Output
Check ($o.EventId -eq 1002 -and $o.Message -like '*failed with code 3*') 'code 3 -> event 1002 "failed with code 3"'

Check ((Get-Outcome 0 '').EventId -eq 1001) 'code 0 -> 1001'
Check ((Get-Outcome -1978335189 '').EventId -eq 1000) '0x8A15002B -> 1000'
$n = Get-Outcome -1978335212 ''
Check ($n.EventId -eq 1002 -and $n.Message -like '*no installed package*') '0x8A150014 -> 1002 with explanation'
Check ((Get-Outcome 5 ('x' * 5000)).Message.Length -lt 2400) 'winget output truncated in the message'

# The download folder's DACL: protected, full control for SYSTEM and Administrators only. Applied to a folder of our
# own; as its owner we can still read the ACL back (and delete it through the parent's rights) afterwards.
function Get-RuleSummary($Security) {
    return @($Security.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier]) |
        ForEach-Object { "$($_.IdentityReference.Value)|$($_.AccessControlType)|$($_.FileSystemRights)|$($_.IsInherited)" } | Sort-Object)
}
$expectedRules = @('S-1-5-18|Allow|FullControl|False', 'S-1-5-32-544|Allow|FullControl|False')
$built = New-UpgradeFolderSecurity
Check ($built.AreAccessRulesProtected -and ((Get-RuleSummary $built) -join ';') -eq ($expectedRules -join ';')) 'New-UpgradeFolderSecurity: protected, SYSTEM and Administrators full control only'
$aclDir = Join-Path ([System.IO.Path]::GetTempPath()) ('pt-acl-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $aclDir | Out-Null
try {
    Protect-UpgradeFolder $aclDir
    $applied = Get-Acl -LiteralPath $aclDir
    Check ($applied.AreAccessRulesProtected -and ((Get-RuleSummary $applied) -join ';') -eq ($expectedRules -join ';')) 'Protect-UpgradeFolder leaves exactly those two rules, protected, on the folder'
}
finally {
    # Locked out now except for the owner's implicit WRITE_DAC: restore the inherited ACL, then delete.
    Invoke-Native (Join-Path $env:SystemRoot 'System32\icacls.exe') @($aclDir, '/reset', '/q') | Out-Null
    Remove-Item -LiteralPath $aclDir -Recurse -Force -ErrorAction SilentlyContinue
}
Check (-not (Test-Path -LiteralPath $aclDir)) 'the protected test folder could be removed again'

# ---- GitHub fallback. The functions upgrade.ps1 calls are replaced from here on: nothing runs, nothing is installed.
# Protect-UpgradeFolder (checked above) is recorded only: applied for real it would lock this non-elevated test out of
# the folder its fake fetch-release writes into.
$script:protected = @()
function Protect-UpgradeFolder([string]$Path) { $script:protected += $Path }
$script:events = @()
$script:calls = @()
$script:fetchCode = 0
$script:msiexecCode = 0
$script:wingetCode = 0
$script:wingetPath = $null
function Write-Outcome {
    param([int]$EventId, [string]$Message, [string]$Type = 'Information')
    $script:events += , @{ EventId = $EventId; Message = $Message; Type = $Type }
}
function Find-Winget { if ($script:wingetPath) { return @{ Path = $script:wingetPath; Denied = $false } } return @{ Path = $null; Denied = $false } }
function Invoke-Native {
    param([string]$Exe, [string[]]$Arguments)
    $script:calls += , @{ Exe = $Exe; Arguments = $Arguments }
    $name = [IO.Path]::GetFileName($Exe)
    if ($name -eq 'winget.exe') { return @{ Code = $script:wingetCode; Output = 'winget output' } }
    if ($name -eq 'msiexec.exe') { return @{ Code = $script:msiexecCode; Output = '' } }
    # proctrack.exe admin fetch-release --asset msi --out <dir>: "downloads" the MSI into <dir> as the real one does.
    $script:fetchOut = $Arguments[[Array]::IndexOf($Arguments, '--out') + 1]
    if ($script:fetchCode -ne 0) { return @{ Code = $script:fetchCode; Output = 'fetch-release said no' } }
    $msi = Join-Path $script:fetchOut 'ProcessTracker-0.3.0-x64.msi'
    [IO.File]::WriteAllText($msi, 'msi')
    return @{ Code = 0; Output = "$msi`r`n" }
}
function Reset-Fallback([int]$Fetch = 0, [int]$Msiexec = 0, [string]$Winget = $null, [int]$WingetCode = 0) {
    $script:events = @(); $script:calls = @(); $script:fetchOut = $null; $script:protected = @()
    $script:fetchCode = $Fetch; $script:msiexecCode = $Msiexec; $script:wingetPath = $Winget; $script:wingetCode = $WingetCode
}
function Get-Call([string]$ExeName) { return , @($script:calls | Where-Object { [IO.Path]::GetFileName($_.Exe) -eq $ExeName }) }

Reset-Fallback
$printed = Invoke-Upgrade -WhatIf | Out-String
Check ($printed -match 'admin fetch-release --asset msi --out ') 'upgrade.ps1 falls back to fetch-release when winget is missing (-WhatIf prints it)'
Check ($printed -match [regex]::Escape((Join-Path (Split-Path $PSScriptRoot) 'installer\bin\proctrack.exe'))) '-WhatIf names bin\proctrack.exe next to upgrade.ps1'
Check ($script:calls.Count -eq 0 -and $script:events.Count -eq 0) '-WhatIf runs nothing and writes no event'

Reset-Fallback
Invoke-Upgrade | Out-Null
$msiexec = Get-Call 'msiexec.exe'
Check ($script:events.Count -eq 1 -and $script:events[0].EventId -eq 1004 -and $script:events[0].Message -eq 'ProcessTracker upgraded from GitHub release 0.3.0.') 'upgrade.ps1 writes 1004 after a successful fallback'
Check ((Get-Call 'proctrack.exe').Count -eq 1 -and $null -eq (Get-Call 'winget.exe')[0]) 'winget missing: fetch-release ran, winget did not'
Check ($msiexec.Count -eq 1 -and $msiexec[0].Exe -eq (Join-Path $env:SystemRoot 'System32\msiexec.exe')) 'msiexec runs by its full System32 path'
Check ($msiexec.Count -eq 1 -and (($msiexec[0].Arguments -join '|') -eq ('/i|' + (Join-Path $script:fetchOut 'ProcessTracker-0.3.0-x64.msi') + '|/qn|/norestart'))) 'msiexec /i <verified msi> /qn /norestart'
Check ($script:fetchOut -like ([IO.Path]::GetTempPath() + 'ProcessTracker-upgrade-*')) 'fetch-release downloads into a fresh folder under the temp folder'
Check ($script:protected.Count -eq 1 -and $script:protected[0] -eq $script:fetchOut) 'the download folder is protected before fetch-release writes into it'
Check (-not (Test-Path -LiteralPath $script:fetchOut)) 'the download folder is deleted afterwards'

Reset-Fallback -Msiexec 1603
Invoke-Upgrade | Out-Null
Check ($script:events.Count -eq 1 -and $script:events[0].EventId -eq 1002 -and $script:events[0].Message -like '*1603*') 'msiexec failure -> 1002 with the msiexec code'
Check (-not (Test-Path -LiteralPath $script:fetchOut)) 'the download folder is deleted after a failed install'

Reset-Fallback -Fetch 3
Invoke-Upgrade | Out-Null
Check ($script:events.Count -eq 1 -and $script:events[0].EventId -eq 1000 -and (Get-Call 'msiexec.exe').Count -eq 0) 'fetch-release exit 3 -> 1000 up to date, no msiexec'

Reset-Fallback -Fetch 1
Invoke-Upgrade | Out-Null
Check ($script:events.Count -eq 1 -and $script:events[0].EventId -eq 1002 -and (Get-Call 'msiexec.exe').Count -eq 0) 'fetch-release exit 1 -> 1002, no msiexec'

$fakeWinget = 'C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.0.0.0_x64__8wekyb3d8bbwe\winget.exe'
Reset-Fallback -Winget $fakeWinget -WingetCode -1978335212
Invoke-Upgrade | Out-Null
Check ((Get-Call 'winget.exe').Count -eq 1 -and (Get-Call 'proctrack.exe').Count -eq 1 -and $script:events.Count -eq 1 -and $script:events[0].EventId -eq 1004) 'winget "no installed package" -> fetch-release fallback'

Reset-Fallback -Winget $fakeWinget -WingetCode 0
Invoke-Upgrade | Out-Null
Check ((Get-Call 'proctrack.exe').Count -eq 0 -and $script:events.Count -eq 1 -and $script:events[0].EventId -eq 1001) 'winget upgrade succeeds -> 1001, no fallback'

Reset-Fallback -Winget $fakeWinget -WingetCode -1978335189
Invoke-Upgrade | Out-Null
Check ((Get-Call 'proctrack.exe').Count -eq 0 -and $script:events.Count -eq 1 -and $script:events[0].EventId -eq 1000) 'winget up to date -> 1000, no fallback'

Reset-Fallback -Winget $fakeWinget
$printed = Invoke-Upgrade -WhatIf | Out-String
Check ($printed -match 'winget\.exe" upgrade --id' -and $printed -match 'admin fetch-release' -and $script:calls.Count -eq 0) '-WhatIf with winget prints the winget command and the fallback, runs nothing'

if ($failures -gt 0) { exit 1 }
exit 0
