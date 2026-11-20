# Release Automation and winget Distribution Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Merging release-please's release PR publishes a tested, versioned MSI on GitHub and submits it to winget. Machines installed with `winget install worc4021.ProcessTracker` then upgrade themselves daily.

**Architecture:**
- `version.txt` is the only version source. release-please bumps it and tags the release, and a reusable `release.yml` builds and smoke-tests the MSI on a Windows runner, then publishes it.
- A WiX v6 MSI replaces `install.ps1`. Privileged setup steps that WiX cannot express run as `proctrack admin …` subcommands; they are C# and unit-tested.
- A SYSTEM scheduled task runs an admin-only `upgrade.ps1`. The script finds winget under `Program Files\WindowsApps` and runs `winget upgrade` for the package.

**Tech Stack:** .NET 10 (existing solution), WiX Toolset v6 (`WixToolset.Sdk`, `WixToolset.Util.wixext`), GitHub Actions (`googleapis/release-please-action` v4, `actions/checkout`, `actions/setup-dotnet`), `wingetcreate`, PowerShell 5.1 for `upgrade.ps1`.

**Spec:** `docs/superpowers/specs/2026-11-20-release-and-winget-design.md` (it builds on `docs/superpowers/specs/2026-11-20-processtracker-design.md`).

## Global Constraints

- winget PackageIdentifier `worc4021.ProcessTracker`; MSI `Manufacturer="worc4021"`; product name `ProcessTracker`; MSI `UpgradeCode="69B0FAFC-2873-43F9-8377-3B554A10091D"`. Never change the UpgradeCode.
- Per-machine install to `C:\Program Files\ProcessTracker\` (`ProgramFiles64Folder`). The CLI is published single-file self-contained to `C:\Program Files\ProcessTracker\bin\proctrack.exe`, and only `bin\` goes on the **system** PATH.
- Service: name `ProcessTracker`, LocalSystem, automatic start, recovery restart after 5 s, 5 s and 30 s, with the failure count reset after 1 day. Ports 47811 (production) and 47812 (dev) are unchanged.
- Data folder `%ProgramData%\ProcessTracker` and `history\`:
  - ACL with inheritance off: SYSTEM (S-1-5-18) and Administrators (S-1-5-32-544) Full Control; Users (S-1-5-32-545) Read & Execute; owner Administrators.
  - Setup refuses if either folder is a reparse point.
  - Uninstall never deletes the data folder unless `PURGEDATA=1`.
- Event Log source `ProcessTracker` in the Application log.
- Scheduled task `\ProcessTracker\AutoUpgrade`:
  - runs as SYSTEM;
  - triggers daily at 12:00 with a random delay of up to 1 hour (`PT1H`), and at boot after a 10 minute delay (`PT10M`);
  - action `%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "C:\Program Files\ProcessTracker\upgrade.ps1"`.
- The MCP registration command is exactly `claude mcp add --transport http --scope user process-tracker http://127.0.0.1:47811/mcp --header "X-Claude-Session: ${CLAUDE_CODE_HOST_SESSION_ID:-}"`. The header string is passed literally (ArgumentList, no shell). It runs on first install only.
- Versioning:
  - `version.txt` starts at `0.1.0`; release-please uses `release-type: simple` with `bump-minor-pre-major: true`.
  - MSI ProductVersion is the numeric `X.Y.Z`.
  - InformationalVersion is `X.Y.Z` for release builds and `X.Y.Z-dev.yyyyMMddHHmm` for local deploys.
- The MSI uses `MajorUpgrade AllowDowngrades="yes"` (see Ruling P1 below).
- Actions are pinned to full commit SHAs. Each workflow has least-privilege `permissions:`.
  - Secrets: `WINGET_TOKEN` (a fine-grained token on the user's `winget-pkgs` fork).
  - Repository variables: `WINGET_PUBLISHED` (`true` enables `wingetcreate update --submit`) and `SIGNING_ENABLED` (`true` enables Azure Trusted Signing). Both are off by default; releases ship unsigned.
- Release workflow tests exclude `Category=Perf`.
- Commit trailer: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Never push from tasks; the controller pushes.

**Planning rulings** (deviations from the design, made while planning):
- **P1. Local development deploys.** Design §8 gave them a `9xxx` patch number. Instead, a dev build reuses the version in `version.txt`, and the MSI uses `MajorUpgrade AllowDowngrades="yes"`, so any build replaces whatever is installed.
  - Reason: MSI build numbers top out at 65535, and a time-based patch number would make the next real release look like a downgrade.
- **P2. The scheduled task runs `upgrade.ps1`, not `proctrack self-upgrade`** (design §7).
  - Reason: the MSI upgrade replaces `bin\proctrack.exe`. A running proctrack would lock its own file and force a reboot to finish the upgrade. PowerShell reads the whole script at launch and keeps no lock on it.
  - The script sits in the admin-only install folder and is run by `System32\powershell.exe`, so the design's protection against a user swapping in their own binary still holds.
- **P3. `proctrack upgrade-task enable|disable|status` stays** as the user-facing switch, as in design §7.

## Review Focus

1. **A phase 1 install exists** (a `current` junction under the install root). The MSI must refuse with a message telling the user to run `scripts\uninstall-legacy.ps1`; it must not end up with two services or a broken junction. Test: Task 5 `Smoke_LegacyJunctionPresent_InstallRefused` in `msi-smoke.ps1`.
2. **Upgrading over a running service whose data folder holds user edits.** `RetentionHours` set by the user must survive, and the service must come back up on the new version. Test: Task 5 smoke step "upgrade keeps settings and state".
3. **Uninstall while Claude Code is not installed, or `claude` is missing from PATH.** The MCP unregister step must not fail the uninstall. Test: Task 4 `McpRegistrar_ClaudeMissing_ReturnsSkippedNotError`.
4. **winget missing, or several App Installer versions present.** `upgrade.ps1` must pick the highest version, or log "winget not found" and exit 0. Test: Task 4 `WingetLocator_PicksHighestVersion`, `WingetLocator_None_ReturnsNull`, plus the script's resolution checked by the Pester-free self-test in Task 4 step 6.
5. **A first release before the winget package exists.** `release.yml` must still publish the MSI and skip the winget step with a notice. Test: Task 6 `actionlint` together with the `if: vars.WINGET_PUBLISHED == 'true'` guard, verified by reading the workflow run summary in Task 6 step 6.
6. **Session transcripts when the service runs as SYSTEM.** The profile must come from the session anchor's owner, not from `%USERPROFILE%`, which for SYSTEM is the systemprofile folder. Test: Task 2 `Tracker_UsesAnchorOwnerProfile_ForTranscripts`.

---

## File Structure

```
version.txt                                   0.1.0 (release-please bumps it)
Directory.Build.props                         VersionPrefix read from version.txt; DevSuffix property
.github/workflows/ci.yml                      build + test on push/PR
.github/workflows/release-please.yml          release PR + calls release.yml on release_created
.github/workflows/release.yml                 reusable: test, publish, MSI, smoke test, sign?, upload, winget?
release-please-config.json, .release-please-manifest.json, CHANGELOG.md
src/ProcessTracker.Native/ProfileResolver.cs  owner SID of a process -> ProfileImagePath
src/ProcessTracker.Core/Sessions/IProfileResolver.cs
src/ProcessTracker.Cli/Admin/DataFolderSetup.cs     prepare/remove data folder (ACL, reparse, settings carry-over)
src/ProcessTracker.Cli/Admin/SettingsCarryOver.cs   pure: old appsettings.json text -> new text
src/ProcessTracker.Cli/Admin/UpgradeTask.cs         task XML + schtasks register/remove/enable/disable/query
src/ProcessTracker.Cli/Admin/WingetLocator.cs       pure: pick winget.exe from candidate dirs (used by tests; mirrored in upgrade.ps1)
src/ProcessTracker.Cli/Mcp/McpRegistrar.cs          locate claude, add/remove/get
src/ProcessTracker.Cli/Commands/{AdminCommand,McpCommand,UpgradeTaskCommand}.cs
installer/ProcessTracker.Installer.wixproj    WiX v6 SDK project
installer/Package.wxs                         package, MajorUpgrade, files, service, PATH, event source, CAs, launch condition
installer/upgrade.ps1                         installed to INSTALLFOLDER; resolves winget, runs upgrade, logs
scripts/build-msi.ps1                         publish + build MSI for a version (used by CI and deploy.ps1)
scripts/msi-smoke.ps1                         install/upgrade/uninstall smoke test (CI runner, elevated)
scripts/deploy.ps1                            rewritten: dev MSI build + msiexec + health check
scripts/uninstall-legacy.ps1                  renamed from uninstall.ps1 (phase 1 migration only)
(removed) scripts/install.ps1, src/ProcessTracker.Updater, tests/ProcessTracker.Updater.Tests
README.md                                     install via winget, upgrade, uninstall, dev deploy, migration
```

---

### Task 1: Version source and CI workflow

**Files:**
- Create: `version.txt`, `.github/workflows/ci.yml`
- Modify: `Directory.Build.props`
- Test: `tests/ProcessTracker.Core.Tests/VersionTests.cs`

**Interfaces:**
- Produces:
  - The MSBuild property `VersionPrefix`, read from `$(MSBuildThisFileDirectory)version.txt` (trimmed).
  - An optional property `DevSuffix`. When it is non-empty, `InformationalVersion` = `$(VersionPrefix)-$(DevSuffix)`; otherwise it is `$(VersionPrefix)`. `-p:Version=` overrides are no longer used by scripts.
  - CI job name `build-test`, used later as a required check.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact] public void AssemblyVersion_MatchesVersionTxt()
{
    var root = RepoRoot();                       // walk up from AppContext.BaseDirectory to the dir holding version.txt
    var expected = File.ReadAllText(Path.Combine(root, "version.txt")).Trim();
    var info = typeof(ProcessTracker.Core.Model.ProcessKey).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    Assert.Equal(expected, info);
}
```

- [ ] **Step 2:** Run `dotnet test --project tests/ProcessTracker.Core.Tests/ProcessTracker.Core.Tests.csproj -- --filter-method "*AssemblyVersion_MatchesVersionTxt*"`. Expected: FAIL, because version.txt is missing.
- [ ] **Step 3:**
  1. Create `version.txt` containing `0.1.0` and a newline.
  2. In `Directory.Build.props`, replace the literal `VersionPrefix` with `$([System.IO.File]::ReadAllText('$(MSBuildThisFileDirectory)version.txt').Trim())`, and add the `DevSuffix` → `InformationalVersion` logic.
  3. Write `ci.yml`:
     - triggers: `push` and `pull_request` on all branches;
     - runner: `windows-latest`;
     - steps: checkout, then setup-dotnet with `global-json-file: global.json`, then `dotnet build ProcessTracker.slnx -c Release -warnaserror`, then `dotnet test --solution ProcessTracker.slnx -c Release --no-build -- --filter-not-trait "Category=Perf"`;
     - `permissions: contents: read`;
     - actions pinned to SHAs.
- [ ] **Step 4:** Run the test. Expected: PASS. Then run `dotnet build ProcessTracker.slnx -warnaserror` and `dotnet build -p:DevSuffix=dev.202611201200 src/ProcessTracker.Service -getProperty:InformationalVersion`. Expected: `0.1.0-dev.202611201200`. Lint the workflow with `actionlint` (download its release binary into the scratch area) and expect no findings.
- [ ] **Step 5:** Commit `build: version.txt as the single version source; CI workflow`.

---

### Task 2: Transcript profile from the session anchor's owner

**Files:**
- Create: `src/ProcessTracker.Core/Sessions/IProfileResolver.cs`, `src/ProcessTracker.Native/ProfileResolver.cs`
- Modify: `src/ProcessTracker.Core/Tracking/{Tracker,TrackerOptions}.cs`, `src/ProcessTracker.Service/{Program,ServiceOptions}.cs` (register the resolver; an empty `UserProfilePath` now means "per session from the anchor's owner")
- Test: `tests/ProcessTracker.Core.Tests/Tracking/TrackerProfileTests.cs`, `tests/ProcessTracker.Native.Tests/ProfileResolverTests.cs`

**Interfaces:**
- Produces: `interface IProfileResolver { string? ProfileOf(ProcessKey key); }`. It returns null when the process is gone or the owner has no profile.
- `ProfileResolver` (Native) works like this:
  1. `OpenProcess(QUERY_LIMITED)` and check the create time against the key.
  2. `OpenProcessToken(TOKEN_QUERY)`, then `GetTokenInformation(TokenUser)` to get the SID string.
  3. Read `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\<SID>\ProfileImagePath` and expand environment variables.
  4. Cache the result per SID.
- `Tracker` gets a new optional constructor parameter `IProfileResolver? profiles = null`, so existing call sites still compile.
  - Transcript reads for a unit use `TrackerOptions.UserProfilePath` when it is set.
  - Otherwise they use `profiles.ProfileOf(session anchor key)`.
  - It keeps one `TranscriptReader` per profile path.

- [ ] **Step 1: Write the failing tests**
  - `Tracker_UsesAnchorOwnerProfile_ForTranscripts`: a fake resolver maps the anchor key to a temp profile dir holding a transcript fixture, and `UserProfilePath` is empty. Assert that the session's Title is set from that transcript.
  - `Tracker_ConfiguredProfile_WinsOverResolver`.
  - `Tracker_ResolverReturnsNull_NoTitleNoThrow`.
  - Native: `ProfileOf_CurrentProcess_IsCurrentUserProfile` (asserts it equals `Environment.GetFolderPath(UserProfile)`, case-insensitively) and `ProfileOf_WrongCreateTime_Null`.
- [ ] **Step 2:** Run them. Expected: FAIL (compile).
- [ ] **Step 3:** Implement. Put the P/Invokes in `NativeMethods.cs`. Close the token handle in a `finally`.
- [ ] **Step 4:** Run the Core and Native tests, then the full suite. Expected: all pass, no warnings.
- [ ] **Step 5:** Commit `feat: read session transcripts from the session owner's profile`.

---

### Task 3: `proctrack admin prepare-data | remove-data`

**Files:**
- Create: `src/ProcessTracker.Cli/Admin/{DataFolderSetup,SettingsCarryOver}.cs`, `src/ProcessTracker.Cli/Commands/AdminCommand.cs`
- Modify: `src/ProcessTracker.Cli/Program.cs` (register the hidden `admin` command)
- Test: `tests/ProcessTracker.Cli.Tests/Admin/{SettingsCarryOverTests,DataFolderSetupTests}.cs`

**Interfaces:**
- `static class SettingsCarryOver { string Rewrite(string? existingJson); }`
  - Keeps only `RetentionHours`, `OrphanIdleAlertMinutes` and `UserProfilePath` when they are present and valid.
  - Output is indented JSON. An unparseable or missing input gives `{}`.
- `sealed class DataFolderSetup(string dataDir)` has two methods:
  - `SetupResult Prepare()`:
    1. Refuse if `dataDir` or `dataDir\history` is a reparse point.
    2. Create both folders.
    3. Apply the protected ACL and owner (Global Constraints) to the folder. Children inherit it.
    4. Read any existing `appsettings.json`, delete it, and write `SettingsCarryOver.Rewrite(old)` as a new file.
  - `SetupResult Remove(bool purge)`: refuses on a reparse point; deletes the folder only when `purge` is true.
- `record SetupResult(bool Ok, string Message)`.
- CLI:
  - `proctrack admin prepare-data [--data-dir <path>]` (default `%ProgramData%\ProcessTracker`): exit 0 on success, 1 on refusal or failure; requires elevation.
  - `proctrack admin remove-data [--purge] [--data-dir <path>]`.
  - Hidden from `--help`.

- [ ] **Step 1: Write the failing tests**
  - `Rewrite_KeepsKnownValidKeys_DropsOthers` (input `{"RetentionHours":48,"DataDir":"D:\\x","Kestrel":{}}`, output contains RetentionHours 48 and no DataDir or Kestrel).
  - `Rewrite_InvalidJson_ReturnsEmptyObject`.
  - `Rewrite_DropsOutOfRangeValues` (RetentionHours -1 or 10000).
  - `Prepare_ReparsePoint_Refused` (create a junction in the temp dir with `cmd /c mklink /J`).
  - `Prepare_PlantedSettingsFile_IsReplacedWithFreshFile`: plant a file with an extra ACE for Everyone, run Prepare, then assert the new file has no explicit Everyone ACE. These run without elevation in a temp dir; skip only the owner and ACL assertions that need admin, via `Assert.Skip` when not elevated.
  - `Remove_WithoutPurge_KeepsFolder`.
- [ ] **Step 2:** Run them. Expected: FAIL (compile).
- [ ] **Step 3:** Implement. Use `DirectorySecurity` with `SetAccessRuleProtection(true, false)` and SIDs, following the logic in `scripts/install.ps1` step 4, which this replaces.
- [ ] **Step 4:** Run the Cli tests and the full suite. Expected: pass.
- [ ] **Step 5:** Commit `feat(cli): admin prepare-data/remove-data for the installer`.

---

### Task 4: MCP registration, auto-upgrade task, `upgrade.ps1`

**Files:**
- Create:
  - `src/ProcessTracker.Cli/Mcp/McpRegistrar.cs`
  - `src/ProcessTracker.Cli/Admin/{UpgradeTask,WingetLocator}.cs`
  - `src/ProcessTracker.Cli/Commands/{McpCommand,UpgradeTaskCommand}.cs`
  - `installer/upgrade.ps1`
- Modify: `AdminCommand.cs` (adds `register-task` and `remove-task`)
- Test: `tests/ProcessTracker.Cli.Tests/{Mcp/McpRegistrarTests,Admin/UpgradeTaskTests,Admin/WingetLocatorTests}.cs`

**Interfaces:**
- `McpRegistrar(Func<string?> locateClaude, IProcessRunner runner)`:
  - `McpResult Register()` and `McpResult Unregister()`, where `enum McpResult { Done, AlreadyInState, Skipped }`.
  - `Skipped` means `claude` was not found. It is not an error.
  - It locates claude via PATH, then `%USERPROFILE%\.local\bin\claude.exe`.
  - `Register` first runs `claude mcp get process-tracker`, and returns `AlreadyInState` if that exits 0.
  - Arguments go through ProcessStartInfo.ArgumentList, exactly as in Global Constraints.
- `interface IProcessRunner { (int ExitCode, string Output) Run(string exe, IReadOnlyList<string> args, TimeSpan timeout); }`, with a real implementation `ProcessRunner`.
- `static class WingetLocator { string? Pick(IEnumerable<string> candidateDirs); }`
  - It picks from `Microsoft.DesktopAppInstaller_<version>_x64__8wekyb3d8bbwe` directory names, parsing `<version>` with `Version.TryParse`.
  - It returns the highest version's `winget.exe` path, or null.
- `static class UpgradeTask`:
  - `string BuildXml(string installDir)`: the Task Scheduler 1.4 XML, with principal `S-1-5-18`, `HighestAvailable` run level, the triggers and action from Global Constraints, `MultipleInstancesPolicy IgnoreNew`, and `ExecutionTimeLimit PT2H`.
  - `int Register(string installDir)`: writes the XML to a temp file as UTF-16, then runs `schtasks /Create /TN "\ProcessTracker\AutoUpgrade" /XML <file> /F`.
  - `int Remove()`, `int SetEnabled(bool)` (`schtasks /Change /ENABLE|/DISABLE`), and `string Query()`.
- CLI:
  - `proctrack mcp register|unregister`, run as the user. Exit 0 for Done, AlreadyInState or Skipped (Skipped prints a hint); exit 1 on a claude error.
  - `proctrack upgrade-task enable|disable|status`, which needs elevation for enable and disable.
  - `proctrack admin register-task --install-dir <dir>` and `proctrack admin remove-task`, both hidden.
- `installer/upgrade.ps1` (PowerShell 5.1, no dependencies):
  1. Resolves winget with the same rule as `WingetLocator`.
  2. Runs `& $winget upgrade --id worc4021.ProcessTracker --exact --silent --scope machine --accept-package-agreements --accept-source-agreements --disable-interactivity`.
  3. Maps the exit code:
     - 0 → event 1001 "upgraded (or reinstalled)";
     - `-1978335189` (0x8A15002B, update not applicable) → event 1000 "up to date";
     - winget not found → event 1003, exit 0;
     - anything else → event 1002 "failed with code N".
  4. Writes the event with `Write-EventLog -LogName Application -Source ProcessTracker`. If the source is missing, it falls back to `Write-Output` only.
  5. Exits 0, except on an internal script error.

- [ ] **Step 1: Write the failing tests**
  - `McpRegistrar_ClaudeMissing_ReturnsSkippedNotError`.
  - `McpRegistrar_Register_PassesHeaderLiterally` (the fake runner captures args; assert the element `X-Claude-Session: ${CLAUDE_CODE_HOST_SESSION_ID:-}` is present verbatim, and that `--scope` is followed by `user`).
  - `McpRegistrar_AlreadyRegistered_DoesNotAddAgain`.
  - `McpRegistrar_Unregister_RunsRemove`.
  - `WingetLocator_PicksHighestVersion` (dirs `…_1.9.25200.0_x64__8wekyb3d8bbwe`, `…_1.10.320.0_x64__8wekyb3d8bbwe` → picks 1.10.320.0).
  - `WingetLocator_IgnoresOtherArchesAndJunk`.
  - `WingetLocator_None_ReturnsNull`.
  - `UpgradeTask_Xml_RunsAsSystemWithPinnedPowershellAndScript` (parse the XML: UserId `S-1-5-18`, Command ends with `System32\WindowsPowerShell\v1.0\powershell.exe`, Arguments contain `-File "C:\Program Files\ProcessTracker\upgrade.ps1"`, a CalendarTrigger at 12:00 with RandomDelay `PT1H`, a BootTrigger with Delay `PT10M`).
- [ ] **Step 2:** Run them. Expected: FAIL (compile).
- [ ] **Step 3:** Implement the C# parts and `upgrade.ps1`.
- [ ] **Step 4:** Run the Cli tests and the full suite. Expected: pass.
- [ ] **Step 5:** Parse-check `installer/upgrade.ps1` in Windows PowerShell 5.1 and pwsh 7 (`[System.Management.Automation.Language.Parser]::ParseFile`). Expected: 0 errors.
- [ ] **Step 6:** Run `upgrade.ps1 -WhatIf`. Add a `[switch]$WhatIf` that only resolves and prints the winget path and the command line it would run. Running it non-elevated on this machine is fine. Expected: it prints a path under `C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_` (or "winget not found" if access is denied) and exits 0.
- [ ] **Step 7:** Commit `feat(cli): MCP registration, auto-upgrade task and upgrade script`.

---

### Task 5: WiX MSI and smoke test

**Files:**
- Create: `installer/ProcessTracker.Installer.wixproj`, `installer/Package.wxs`, `scripts/build-msi.ps1`, `scripts/msi-smoke.ps1`
- Modify: `ProcessTracker.slnx` (adds the installer project, built only in Release), `Directory.Packages.props` (WixToolset.Sdk and WixToolset.Util.wixext at the latest 6.x)

**Interfaces:**
- `scripts/build-msi.ps1 -Configuration Release [-DevSuffix <s>] [-OutDir artifacts]`:
  1. Publishes the Service self-contained into `artifacts\stage\` and the CLI single-file self-contained into `artifacts\stage\bin\`.
  2. Copies `installer\upgrade.ps1` into `stage\`.
  3. Builds the wixproj with `-p:StageDir=… -p:ProductVersion=<version.txt>`.
  4. Writes `artifacts\ProcessTracker-<X.Y.Z>-x64.msi` and prints its path.
- `Package.wxs`:
  - `Package Scope="perMachine" Manufacturer="worc4021" Name="ProcessTracker" UpgradeCode=<GC> Version="$(ProductVersion)"`.
  - `MajorUpgrade AllowDowngrades="yes"`.
  - Files harvested from `$(StageDir)` with WiX v6 `<Files Include="…\**" />`.
  - `ServiceInstall`/`ServiceConfig` (FailureActions)/`ServiceControl Start="install" Stop="both" Remove="uninstall" Wait="yes"` for `ProcessTracker.Service.exe`.
  - `Environment Name="PATH" Part="last" System="yes" Value="[INSTALLFOLDER]bin"`.
  - `util:EventSource Name="ProcessTracker" Log="Application"` with `EventMessageFile` set to the .NET `EventLogMessages.dll` path; follow how the current install.ps1 creates the source.
  - Deferred, no-impersonate custom actions that run `[INSTALLFOLDER]bin\proctrack.exe`. Each has a WiX `Condition`, `Return="check"` unless noted, and is ordered against `StartServices`/`StopServices`/`RemoveFiles`:
    - `admin prepare-data` on install and upgrade, before StartServices;
    - `admin register-task --install-dir "[INSTALLFOLDER]"` on install and upgrade, after InstallFiles;
    - `admin remove-task` on full uninstall only, before RemoveFiles, `Return="ignore"`;
    - `admin remove-data` plus `--purge` when `PURGEDATA=1`, on full uninstall only, `Return="ignore"`.
  - Immediate, impersonated custom actions:
    - `mcp register` after InstallFinalize, condition `NOT Installed AND NOT WIX_UPGRADE_DETECTED`, `Return="ignore"`;
    - `mcp unregister` before RemoveFiles on full uninstall (`REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE`), `Return="ignore"`.
  - Launch condition: refuse when `[ProgramFiles64Folder]ProcessTracker\current\ProcessTracker.Service.exe` exists (found with a `FileSearch`), with the message "A ProcessTracker install from scripts\install.ps1 is present. Run scripts\uninstall-legacy.ps1 from an elevated PowerShell first."
- `scripts/msi-smoke.ps1 -Msi <path> -UpgradeMsi <path>`, which must run elevated:
  1. Install with `msiexec /i <msi> /qn /l*v install.log`.
  2. Poll `http://127.0.0.1:47811/api/health` until it reports the expected version with `tickAgeSeconds` < 5 (90 s timeout).
  3. Assert that the service is Automatic and Running, that the task exists and runs as SYSTEM, that `bin` is on the machine PATH, that the event source exists, and that the data folder owner is Administrators.
  4. Write `{"RetentionHours":48}` to the data folder's appsettings.json.
  5. Install `<UpgradeMsi>` and poll health again; it must report the upgrade's InformationalVersion.
  6. Assert that RetentionHours 48 survived and that `state.db` still exists.
  7. Uninstall with `msiexec /x … /qn`, then assert the service, task, PATH entry and event source are gone and the data folder remains.
  8. Uninstall with `PURGEDATA=1` after a reinstall, then assert the data folder is gone.
  9. `Smoke_LegacyJunctionPresent_InstallRefused`: create a dummy `current` junction pointing at a temp folder that holds an empty `ProcessTracker.Service.exe`, run the install, and assert exit 1603 with the launch-condition message in the log. Remove the junction with `cmd /c rmdir` afterwards.
  - Exit 0 if every assertion passed, otherwise 1, printing each failed assertion.

- [ ] **Step 1:** Write `msi-smoke.ps1` first; it is the test for this task. Parse-check it. Expected: 0 errors.
- [ ] **Step 2:** Write the wixproj, `Package.wxs` and `build-msi.ps1`. Run `scripts/build-msi.ps1 -Configuration Release` and expect an MSI in `artifacts\`. Then run `scripts/build-msi.ps1 -Configuration Release -DevSuffix smoke2 -OutDir artifacts\upgrade`, the upgrade candidate, which has the same ProductVersion; AllowDowngrades makes it a major upgrade.
- [ ] **Step 3:** Validate the MSI: `wix msi validate artifacts\ProcessTracker-0.1.0-x64.msi` (WiX v6 `wix` dotnet tool; install it locally into the scratch area). Expected: no ICE errors. Do **not** run msiexec locally (R3). The smoke test runs in CI, in Task 6.
- [ ] **Step 4:** Commit `feat(installer): WiX MSI with service, data folder, PATH, event source, auto-upgrade task and MCP registration`.

---

### Task 6: Release workflows

**Files:**
- Create: `.github/workflows/release-please.yml`, `.github/workflows/release.yml`, `release-please-config.json`, `.release-please-manifest.json`, `CHANGELOG.md` (header only)
- Modify: `.github/workflows/ci.yml`, which adds a `msi-smoke` job: it builds both MSIs via build-msi.ps1, then runs `scripts/msi-smoke.ps1` (GitHub's Windows runners run elevated).

**Interfaces:**
- `release-please-config.json`: `{"packages":{".":{"release-type":"simple","bump-minor-pre-major":true,"package-name":"ProcessTracker","extra-files":["version.txt"]}}}`, written so release-please's `simple` updater bumps `version.txt`. `.release-please-manifest.json` is `{".":"0.1.0"}`.
- `release-please.yml`:
  - `on: push: branches: [main]`.
  - `permissions: contents: write, pull-requests: write`.
  - Job `release-please` runs `googleapis/release-please-action` and exposes the outputs `release_created` and `tag_name`.
  - Job `build`: `needs: release-please`, `if: needs.release-please.outputs.release_created == 'true'`, `uses: ./.github/workflows/release.yml`, `with: tag: ${{ needs.release-please.outputs.tag_name }}`, `secrets: inherit`.
- `release.yml` (`on: workflow_call`, input `tag`), job on `windows-latest`, `permissions: contents: write`. Steps:
  1. Check out the tag.
  2. Run the tests, excluding Perf.
  3. Run `build-msi.ps1`.
  4. Run `msi-smoke.ps1`, using the same MSI rebuilt with `-DevSuffix smoke` as the upgrade candidate.
  5. **If `vars.SIGNING_ENABLED == 'true'`:** sign `ProcessTracker.Service.exe`, `bin\proctrack.exe` and the MSI with `azure/trusted-signing-action`. The MSI is rebuilt after the binaries are signed, so the order is sign binaries → build MSI → sign MSI. Secrets: `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`, `SIGNING_ACCOUNT`, `SIGNING_PROFILE`, `SIGNING_ENDPOINT`.
  6. Write `SHA256SUMS.txt`.
  7. `gh release upload <tag> <msi> SHA256SUMS.txt --clobber`.
  8. **If `vars.WINGET_PUBLISHED == 'true'`:** download `wingetcreate.exe` (pinned release URL, verified by SHA256), then run `wingetcreate update worc4021.ProcessTracker --version X.Y.Z --urls <release asset URL> --submit --token ${{ secrets.WINGET_TOKEN }}`.
  9. **Otherwise:** write `::notice::winget step skipped (WINGET_PUBLISHED is not true)` to the job summary.

- [ ] **Step 1:** Write the files. Pin every action to a full SHA and add a `# vX.Y.Z` comment.
- [ ] **Step 2:** Run `actionlint` on all workflows. Expected: no findings.
- [ ] **Step 3:** Validate the release-please config against its JSON schema (`https://raw.githubusercontent.com/googleapis/release-please/main/schemas/config.json`) with a small local check. Expected: valid.
- [ ] **Step 4:** Commit `ci: release-please, reusable release workflow, MSI smoke test`.
- [ ] **Step 5 (controller):** Push the branch and watch the `ci.yml` run, including `msi-smoke`, with `gh run watch`. Expected: green. If it is red, fix forward on the branch.
- [ ] **Step 6 (controller, with the user):** Merge to `main` and confirm that release-please opens a "chore(main): release 0.1.0" PR, or a later version if commits warrant it. Merging that PR is the user's call. After the merge, confirm the Release has the MSI and SHA256SUMS and that the job summary shows the winget-skipped notice.

---

### Task 7: Retire the phase 1 installer; dev deploy via MSI; README

**Files:**
- Delete: `scripts/install.ps1`, `src/ProcessTracker.Updater/`, `tests/ProcessTracker.Updater.Tests/` (also removed from `ProcessTracker.slnx`)
- Rename: `scripts/uninstall.ps1` → `scripts/uninstall-legacy.ps1`. Its help text says it is only for removing a phase 1 (script-installed) copy before installing the MSI.
- Rewrite: `scripts/deploy.ps1`
- Modify: `README.md`

**Interfaces:**
- `deploy.ps1 [-SkipTests]`, which requires elevation:
  1. Run the tests, excluding Perf.
  2. Run `build-msi.ps1 -DevSuffix dev.<yyyyMMddHHmm>`.
  3. Run `msiexec /i <msi> /qn /l*v artifacts\deploy.log`.
  4. Poll health for `X.Y.Z-dev.<stamp>` (60 s).
  5. Exit codes: 0 healthy; 1 installed but unhealthy, which prints the last released MSI URL (`https://github.com/worc4021/ProcessTracker/releases/latest`) to reinstall; 3 if msiexec failed, in which case the MSI has rolled back and nothing changed.
- README:
  - Install with `winget install worc4021.ProcessTracker`, or the MSI from Releases. Explain the SmartScreen warning while releases are unsigned.
  - Auto-upgrade and `proctrack upgrade-task`.
  - Uninstall with `winget uninstall worc4021.ProcessTracker`, or `msiexec /x … PURGEDATA=1`.
  - `proctrack mcp register` if Claude Code was installed later.
  - Dev deploy and migrating from phase 1 (`uninstall-legacy.ps1`).
  - Fix the `--json` claim, which does not apply to `kill` (parked PK2).
  - Add a "Releasing" section: conventional commits, merge the release PR, and the one-time winget setup (fork `winget-pkgs`, create `WINGET_TOKEN`, run `wingetcreate new` for the first version, set `WINGET_PUBLISHED=true`).

- [ ] **Step 1:** Delete, rename and rewrite as above. Parse-check every script, expecting 0 errors.
- [ ] **Step 2:** Run `dotnet build ProcessTracker.slnx -warnaserror` and `dotnet test --solution ProcessTracker.slnx`. Expected: pass, with the Updater tests gone.
- [ ] **Step 3:** Commit `chore: retire script installer and junction updater; deploy.ps1 builds and installs the MSI; README`.
- [ ] **Step 4 (controller, with the user, on this machine):**
  1. Run `scripts\uninstall-legacy.ps1` elevated to remove the phase 1 install. Keep the data folder: no `-PurgeData`.
  2. Run `scripts\deploy.ps1` elevated.
  3. Expect health OK, `proctrack sessions` to work from a new terminal, `schtasks /Query /TN \ProcessTracker\AutoUpgrade` to show the task, and the earlier history and state to still be present.
