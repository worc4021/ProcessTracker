# ProcessTracker: automated releases and winget distribution — Design

Date: 2026-11-20 · Status: draft for approval · Builds on: `2026-11-20-processtracker-design.md` (phase 1, implemented)

## 1. Intent

Releases should be produced without manual work. ProcessTracker should be installable with `winget install`, and an
installed copy should keep itself current. The user chose **approach A**: winget is the single install and upgrade
channel. A daily scheduled task runs `winget upgrade` for the package, and nothing else updates the installation.

## 2. Goals, non-goals, anti-goals

**Goals**
- A merge to `main` with conventional commits (`feat:`, `fix:`, …) results in a versioned GitHub Release with a built
  installer, with no version number or release notes edited by hand.
- `winget install <id>` installs the service, CLI and MCP registration in one step (one UAC prompt).
- Each release automatically opens the matching pull request to `microsoft/winget-pkgs`.
- An installed machine picks up a new version within a day of it reaching winget, unattended.
- `winget uninstall` removes everything it added (service, event source, scheduled task, PATH entry, MCP entry).
  Data in `%ProgramData%\ProcessTracker` is kept unless the user asks to purge it.

**Non-goals**
- Self-updating from GitHub Releases inside the service (that was approach B).
- A private winget REST source, the Microsoft Store, or Chocolatey.
- Health-check rollback for winget upgrades. An MSI rolls back a failed *install*; a version that installs fine but
  runs badly is fixed forward by the next release.

**Anti-goals: it has failed if…**
- …a release needs any manual step beyond merging the release pull request (the first winget submission is the only
  exception, and it happens once).
- …an upgrade loses tracking state. `state.db` and the history files survive upgrades: the MSI does not own them.
- …the installer leaves a SYSTEM service reading configuration a non-admin can change.
- …the auto-upgrade task can be used to run arbitrary code with elevated rights.

## 3. Constraints and facts

- The repository is public at `github.com/worc4021/ProcessTracker`. Code is on `main`, the license is MIT, and commits
  already use conventional prefixes.
- The service needs elevation to install: it is a LocalSystem Windows service.
- winget packages must be installers (MSI, MSIX, EXE, portable or zip). winget cannot run `install.ps1`.
- New versions reach `winget upgrade` only after `winget-pkgs` moderation, typically within hours to about 2 days.
- `winget` is installed per user (App Installer). A scheduled task that calls it must run as a user, not as SYSTEM.
- Phase 1 behaviour to preserve: ports 47811 and 47812, the data folder and its ACL, Event Log source `ProcessTracker`,
  the MCP registration with the `X-Claude-Session: ${CLAUDE_CODE_HOST_SESSION_ID:-}` header, and the CLI on PATH.

## 4. Approaches considered

| Decision | Chosen | Rejected and why |
|---|---|---|
| Update channel | **A: winget only**, plus a daily scheduled `winget upgrade` | B, the service self-updating from GitHub Releases: the installed version winget sees would go stale and the two mechanisms would fight, and a SYSTEM service downloading code is a larger attack surface. |
| Versioning and release notes | **release-please**: keeps a "release vX.Y.Z" PR up to date from conventional commits; merging it tags the commit and creates the GitHub Release | GitVersion or tag-on-every-merge: no changelog review step, and every merge becomes a release. |
| Installer technology | **MSI built with WiX Toolset v6** (`WixToolset.Sdk`, built by `dotnet build`) | Inno Setup or NSIS EXE: service registration and clean upgrades are custom code. MSIX: Windows services in MSIX are awkward and restricted. |
| Upgrades in the MSI | **MajorUpgrade** with a stable UpgradeCode; each release installs over the previous one | Patches (MSP): unnecessary complexity. |
| Submitting to winget | **`wingetcreate update --submit`** in the release workflow, using a token with `public_repo` scope on a fork of `winget-pkgs` | A third-party action: an extra dependency for something wingetcreate does officially. |
| Code signing | **Optional signing step**, active only when the signing secrets exist (Azure Trusted Signing via `azure/trusted-signing-action`) | Mandatory signing from day one: blocks releasing until the account is set up (see §11). |

## 5. Release pipeline

```
push to main ──► release-please.yml ──► opens or updates "chore(main): release X.Y.Z" PR (version, CHANGELOG.md)
merge that PR ──► release-please tags vX.Y.Z and creates the GitHub Release
tag vX.Y.Z    ──► release.yml (windows-latest):
                    dotnet test (excluding Category=Perf)
                    dotnet publish Service, CLI → staging folder (self-contained, win-x64, Version=X.Y.Z)
                    build MSI (WiX) → ProcessTracker-X.Y.Z-x64.msi
                    [sign MSI and binaries if signing secrets are set]
                    upload MSI + SHA256SUMS.txt to the release
                    wingetcreate update <PackageId> --version X.Y.Z --urls <msi url> --submit
ci.yml (every push / PR): build + test, no release
```

- **Workflow wiring.** `release.yml` is a reusable workflow called from `release-please.yml` when a release is created,
  rather than triggered by the tag. Tags pushed by `GITHUB_TOKEN` do not trigger other workflows, and this avoids
  needing a PAT for that.
- **Starting version.** `version.txt` starts at `0.1.0`, with `bump-minor-pre-major: true`, so while the version is
  0.x a `feat:` bumps the minor version and a `fix:` bumps the patch.
- **winget step before the package exists.** The `wingetcreate update` step runs only when the repository variable
  `WINGET_PUBLISHED` is `true`. Until then the workflow attaches the MSI and skips that step with a notice. The
  maintainer does a one-time setup: fork `winget-pkgs`, create `WINGET_TOKEN`, submit the first version with
  `wingetcreate new`, and set the variable once that is merged.
- **Version source of truth.** release-please updates `version.txt`, and `Directory.Build.props` reads `VersionPrefix`
  from it, so the binaries, MSI ProductVersion, health endpoint and tag all agree. The MSI version must be numeric
  `major.minor.patch`; pre-release versions are not published to winget.
- **Changelog.** `CHANGELOG.md` is maintained by release-please.

## 6. The MSI

Per-machine install with `InstallScope="perMachine"` and one UAC prompt. The MSI owns these files and these system
changes:

- **Files:** `C:\Program Files\ProcessTracker\` holds the service, the CLI and the self-contained runtime, installed
  flat (the phase 1 `versions\` folder plus `current` junction layout is not used for MSI installs). A `bin\` subfolder
  holds only `proctrack.exe` and its dependencies, and that subfolder is what goes on PATH.
- **Service:** `ServiceInstall`: name `ProcessTracker`, LocalSystem, automatic start, recovery actions
  restart/5 s/5 s/30 s, `ServiceControl` to stop it before upgrades and uninstall and start it after install.
- **Data folder:** `%ProgramData%\ProcessTracker` and `history\` are created by a deferred custom action running as
  SYSTEM, with the same rules as `install.ps1` today:
  - The ACL has inheritance off: SYSTEM and Administrators get full control, the Users group gets read and execute,
    and the owner is Administrators.
  - Install refuses if the folder or `history\` is a reparse point.
  - If `appsettings.json` already exists, its recognised values (`RetentionHours`, `OrphanIdleAlertMinutes`,
    `UserProfilePath`) are read and the file is deleted and written fresh under the protected ACL. User settings
    therefore survive upgrades, and a pre-planted file cannot keep its own owner or ACL, which closes parked finding
    PK3. Unrecognised keys are dropped.
  - The data folder is never removed by uninstall. A `PURGEDATA=1` property on uninstall removes it.
- **Event source:** `ProcessTracker` in the Application log, created on install and removed on uninstall.
- **PATH:** the system PATH gets `…\ProcessTracker\bin` through the WiX `Environment` element, added on install and
  removed on uninstall.
- **Auto-upgrade task:** see §7.
- **MCP registration:** a custom action that runs as the installing user, not elevated, after the service has started:
  - It runs `proctrack mcp register`, a new CLI command that calls `claude mcp add --transport http --scope user
    process-tracker http://127.0.0.1:47811/mcp --header "X-Claude-Session: ${CLAUDE_CODE_HOST_SESSION_ID:-}"`.
  - It runs on **first install only**, not on upgrades. Upgrades run as SYSTEM through the auto-upgrade task, where
    there is no user to register for.
  - The target is a single-user workstation where the person who uses Claude Code is also the admin who installs
    (main spec §3). Installs by a different admin are out of scope.
  - If `claude` is not found it records that and continues, so the install still succeeds. The README tells the user
    to run `proctrack mcp register` themselves in that case.
  - On uninstall, `proctrack mcp unregister` runs `claude mcp remove`.
  - The same commands are available to run by hand.
- **User profile for transcripts:** the service stops needing `UserProfilePath` in its config. For each session it
  takes the owner SID of the session's anchor `claude.exe` and looks up the profile path under
  `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\<SID>\ProfileImagePath`. That makes the MSI
  independent of who installs it. A `UserProfilePath` set in config still overrides the lookup.

## 7. Auto-upgrade task

- **Task:** the MSI registers a scheduled task `\ProcessTracker\AutoUpgrade` that **runs as SYSTEM**. Every binary
  in its chain sits in an admin-only location, so no user-writable file can be swapped in to get elevation.
  - A user-context task with highest privileges was rejected: it would invoke `winget.exe` through the
    user-writable `%LOCALAPPDATA%\Microsoft\WindowsApps` alias, which a medium-integrity process could replace.
- **Triggers:** daily at 12:00 local time with a random delay of up to 1 hour, and 10 minutes after system start.
- **Action:** `C:\Program Files\ProcessTracker\bin\proctrack.exe self-upgrade`. That command:
  - resolves winget to the highest-version
    `C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe\winget.exe`, which is admin-only
    (this is the supported way to run winget in the SYSTEM context);
  - runs `upgrade --id <PackageId> --exact --silent --scope machine --accept-package-agreements
    --accept-source-agreements --disable-interactivity`;
  - writes the outcome (up to date / upgraded to X / failed with code N) to the `ProcessTracker` event log source.
    Failures are only logged; the next day's run retries.
- **Why winget cannot be steered to other code:** it installs only what the winget-pkgs manifest points to, a pinned
  URL plus SHA256. The command line is fixed and takes no input from files or the network.
- **Control:** `proctrack upgrade-task disable|enable|status` (elevated) switches the task off or on. Uninstall
  removes it.

## 8. Local development deploys

- `deploy.ps1` changes to build the MSI locally and install it with `msiexec /i`. The version is
  `X.Y.Z` taken from `version.txt` with the patch number set to `9xxx` (a time-based counter), so a dev build always
  upgrades a released build and the next release upgrades a dev build after a version bump. It then polls
  `/api/health`.
- There is no automatic rollback. On failure the script prints the previous release's MSI URL to reinstall.
- `install.ps1` and `uninstall.ps1` are replaced by the MSI. The `ProcessTracker.Updater` project and the
  `versions\` plus junction logic are removed. Phase 1 machines installed with `install.ps1` need `uninstall.ps1`
  run once before the MSI is installed; the MSI detects such an install (a `current` junction exists) and refuses
  with that message.

## 9. Security

- Release builds run only on tags created by release-please on `main`. The workflow uses least-privilege `permissions:`
  (`contents: write` for the release only) and pins third-party actions to commit SHAs.
- The winget token is a fine-grained token limited to the user's `winget-pkgs` fork, stored as the
  `WINGET_TOKEN` secret.
- The MSI and binaries are signed whenever signing secrets are configured. `SHA256SUMS.txt` is always published.
- The service's attack surface is unchanged: loopback only, rebinding guard, no kill capability.

## 10. Phasing

1. CI and release automation: `ci.yml`, `release-please.yml`, `release.yml` building the zip and MSI, and
   `version.txt` as the version source.
2. MSI: WiX project, service, data folder custom action, event source, PATH, `proctrack mcp register/unregister`,
   profile lookup from the anchor's SID, retirement of `install.ps1`/`uninstall.ps1`/Updater, and the new `deploy.ps1`.
3. Auto-upgrade scheduled task and `proctrack upgrade-task`.
4. winget: first manual submission via `wingetcreate new`, then the automatic `wingetcreate update --submit` step.
5. Optional: code signing once the account exists.

## 11. Decided by the user

- **winget PackageIdentifier:** `worc4021.ProcessTracker`, with publisher and MSI Manufacturer `worc4021`.
- **Code signing:** releases ship unsigned for now. The signing step is built but stays switched off until signing
  secrets are added. Manual installs will see a SmartScreen warning.

## 12. Left to the builder

- The exact WiX authoring and how custom actions are implemented (a small C# custom action DLL or `proctrack`
  subcommands).
- release-please configuration details (`release-type: simple`, files to bump).
- The test strategy for the MSI: an install, upgrade and uninstall smoke test on the CI runner.

## 13. Calls made by Claude (not explicitly decided by the user)

- release-please, WiX v6 MSI and wingetcreate as the tool choices.
- Retiring `install.ps1`, `uninstall.ps1`, the Updater and the junction layout in favour of the MSI.
- Daily auto-upgrade as SYSTEM through `proctrack self-upgrade`, with winget resolved under the admin-only
  `Program Files\WindowsApps`. Outcomes go to the event log, and a failure retries the next day.
- User settings in `appsettings.json` are carried across upgrades by rewriting the file under the protected ACL.
- MCP registration runs on first install only. The target is a single-user, admin-owned workstation.
- release.yml is called from release-please's workflow, and the version starts at 0.1.0 with pre-1.0 minor bumps.
  The winget step waits until a repository variable is set.
- The service looks up the user profile from the anchor process owner's SID.
- Data is kept on uninstall unless `PURGEDATA=1`.
- Code signing is optional, controlled by whether its secrets are set.
