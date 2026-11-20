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

if ($failures -gt 0) { exit 1 }
exit 0
