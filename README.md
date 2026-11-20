# ProcessTracker

A single always-on Windows service that records every process's resource use and attributes each process to
the **Claude Code session** that launched it, even after its parent has exited. It finds **orphans** (processes
whose session is gone, such as MATLAB or Dymola engines left behind), keeps a rolling on-disk history for
post-mortems, and lets agents ask how loaded the machine is before they start heavy work.

It has three parts:

- the service, which runs as LocalSystem and samples every 2 s;
- `proctrack`, a command-line client;
- an MCP endpoint that Claude Code sessions use.

The design is in [docs/superpowers/specs/2026-11-20-processtracker-design.md](docs/superpowers/specs/2026-11-20-processtracker-design.md).

## Install

ProcessTracker installs per machine, into `C:\Program Files\ProcessTracker`, from an MSI. Only Windows x64 is
supported, and no .NET runtime is needed (everything is self-contained).

```powershell
winget install worc4021.ProcessTracker
```

The winget package becomes available once it has been published (see [Releasing](#releasing)). Until then, download
`ProcessTracker-<version>-x64.msi` from the
[latest GitHub release](https://github.com/worc4021/ProcessTracker/releases/latest), compare its hash with
`SHA256SUMS.txt` and run it (double-click, or from an elevated PowerShell:
`msiexec /i ProcessTracker-<version>-x64.msi /qn`).

Releases are **not code-signed** until signing is enabled, so Windows SmartScreen shows "Windows protected your PC"
for a downloaded MSI. Choose *More info*, then *Run anyway*, if you trust the download.

The install:

- copies the service to `C:\Program Files\ProcessTracker` and `proctrack.exe` to its `bin\` folder, and adds `bin\` to
  the **system** PATH (open a new terminal to pick it up);
- registers the `ProcessTracker` service (LocalSystem, automatic start, restart on failure after 5 s, 5 s and 30 s)
  and starts it;
- creates `%ProgramData%\ProcessTracker` (see [Ports, data and settings](#ports-data-and-settings)) and the
  `ProcessTracker` event source;
- registers the scheduled task `\ProcessTracker\AutoUpgrade` (see below);
- on the **first** install only, registers the MCP server with Claude Code for the installing user, if `claude` is
  found. If you install Claude Code later, run `proctrack mcp register` from a normal (not elevated) terminal.

### Upgrades

The scheduled task `\ProcessTracker\AutoUpgrade` runs as SYSTEM daily at 12:00 (with a random delay of up to an hour)
and 10 minutes after boot. It runs `upgrade.ps1` from the install folder, which calls
`winget upgrade --id worc4021.ProcessTracker` and writes the outcome to the Application event log (source
`ProcessTracker`; event id 1000 up to date, 1001 upgraded, 1002 failed, 1003 winget not found). You can also upgrade by
hand with `winget upgrade worc4021.ProcessTracker`, or by running a newer MSI.

```powershell
proctrack upgrade-task status     # show the task
proctrack upgrade-task disable    # elevated: stop upgrading automatically
proctrack upgrade-task enable     # elevated: turn it back on
```

An upgrade keeps the data folder and your valid settings.

A graceful stop (including the one during an upgrade) persists state. After a restart, CPU and I/O deltas
continue from the persisted counters, so work done while the service was down is not lost.

### Uninstall

```powershell
winget uninstall worc4021.ProcessTracker
# or, without winget:
msiexec /x ProcessTracker-<version>-x64.msi /qn
```

This removes the service, the files, the PATH entry, the scheduled task and the Claude Code MCP entry, and **keeps**
`%ProgramData%\ProcessTracker` (history, state, settings). To delete that too, pass `PURGEDATA=1` to the MSI:
`msiexec /x ProcessTracker-<version>-x64.msi /qn PURGEDATA=1`.

### Migrating from the phase 1 script install

Earlier checkouts installed with `scripts\install.ps1` (versions under `C:\Program Files\ProcessTracker\versions`,
a `current` junction, and `current` on your user PATH). That script and the junction-based updater are gone, and the
MSI refuses to install while the phase 1 copy is present. From an elevated PowerShell, in the checkout, as the same
user who ran `install.ps1`:

```powershell
scripts\uninstall-legacy.ps1             # keeps %ProgramData%\ProcessTracker, so your history survives
winget install worc4021.ProcessTracker   # or, from a checkout: scripts\deploy.ps1
```

`uninstall-legacy.ps1` stops and deletes the service, removes the `process-tracker` MCP entry if Claude Code has one,
removes `...\ProcessTracker\current` from your user PATH, removes the junction (with `rmdir`, so the target is never
followed) and deletes `versions\`. `-PurgeData` also deletes the data folder and the event source. It refuses to run
against an MSI install.

## Ports, data and settings

- The service listens on `127.0.0.1` only, on port **47811** (dev: **47812**). REST is under `/api` and MCP is at
  `/mcp`. Requests whose `Host` or `Origin` is not loopback on that port are refused (DNS-rebinding guard).
- Data lives in `%ProgramData%\ProcessTracker`:
  - `state.db` (SQLite): process records, sessions, live counters, kill audit;
  - `history\*.pts`: 5-minute segments, kept for `RetentionHours`;
  - `appsettings.json`: settings.

  The folder is owned by `BUILTIN\Administrators`, does not inherit permissions, gives full control to SYSTEM and
  Administrators and read & execute to Users. The installer refuses to proceed if the folder (or `history\`) is a
  junction or symbolic link.
- Settings in `%ProgramData%\ProcessTracker\appsettings.json`:

  | Setting | Default | Notes |
  |---|---|---|
  | `RetentionHours` | 24 | Minimum 2 |
  | `OrphanIdleAlertMinutes` | 30 | |
  | `UserProfilePath` | | Optional override. By default the service resolves each session's user profile itself; set this only to force one profile |

  A `Port` set there is ignored, because `deploy.ps1` and the CLI both use 47811. Only `--Port` on the
  command line changes the port, which is meant for development and tests.

## Logs

As a service, ProcessTracker logs Information and above to the Windows **Application** event log under the source
`ProcessTracker`:

```powershell
Get-WinEvent -ProviderName ProcessTracker -MaxEvents 50
```

In console mode, logs go to the console. There is no log file.

## proctrack

```text
proctrack load                      machine load and the sessions using the most
proctrack sessions                  Claude Code sessions with their totals
proctrack orphans                   orphaned processes, alertable first (a trailing ! marks an alert)
proctrack tree [unit]               a session's process tree (default: the calling session)
proctrack history --unit <id> | --key <pid@ticks> [--minutes 30]
proctrack kill <pid> [--tree] [-y]  kill a tracked process (and its descendants)
proctrack mcp register|unregister   add or remove the Claude Code MCP entry (user scope)
proctrack upgrade-task enable|disable|status
```

`load`, `sessions`, `orphans`, `tree` and `history` accept `--json`; `kill` does not. Use `--dev` to talk to the dev
instance, or set `PROCTRACK_URL` to use any other address.

Exit codes:

| Code | Meaning |
|---|---|
| 0 | OK |
| 1 | Refused or failed |
| 2 | Service unreachable |

The service has no kill capability. `proctrack kill` terminates processes itself, with your rights, after
re-checking the process creation time to guard against PID reuse. It then records the kill in the audit table.

**The kill guard is an accident guard, not a security boundary.** When `proctrack` runs inside a Claude Code
session, it may only kill processes of that same session, and anything else is refused. Inside a *terminal*
Claude session, the short-lived `proctrack` process usually has not been sampled yet, so its session unit cannot
be confirmed and kills are refused there in practice. To kill other
processes, use a terminal outside Claude. An agent can always bypass the guard, for example with
`Stop-Process`.

## MCP

The installer registers the server at user scope for the installing user, and `proctrack mcp register` does the same
later (or for another user) when run from a normal terminal; `proctrack mcp unregister` removes it. The command it runs is:

```powershell
claude mcp add --transport http --scope user process-tracker http://127.0.0.1:47811/mcp --header 'X-Claude-Session: ${CLAUDE_CODE_HOST_SESSION_ID:-}'
```

Claude Code expands the header per session. Terminal sessions do not set `CLAUDE_CODE_HOST_SESSION_ID`, so for
them the header is empty and ignored. The caller is then identified from the TCP connection's owning process.

The tools are all read-only. There are no kill tools.

| Tool | Returns |
|---|---|
| `get_machine_load` | Pressure level, free cores, memory and commit headroom, suggested parallelism, the caller's own usage, the top sessions |
| `get_my_session` | The calling session with its live process tree |
| `list_sessions` | Sessions that are live or recently ended, with their totals |
| `list_orphans` | Orphaned processes, alertable ones first |
| `get_process_history` | One process's usage over the last minutes, by `pid@ticks` key |

## Dashboard

Open <http://127.0.0.1:47811/> in a browser (it redirects to `/dashboard/`). The page is read-only: a live view of
machine pressure, every session with its process tree, the orphans and the load report, refreshed every 2 seconds
while the tab is visible. It is served by the service itself from files embedded in the executable, needs no
internet and runs no inline script. It cannot kill anything. The Orphans tab only copies a
`proctrack kill <pid> --tree` command to the clipboard for you to run yourself. The same loopback-only and
Host/Origin checks as the API apply to it.

## Development

```powershell
dotnet test --solution ProcessTracker.slnx
dotnet run --project src\ProcessTracker.Service -- --console --dev
proctrack --dev sessions
```

`--console --dev` runs the same code as a console app. It listens on port 47812, keeps its data in
`%LOCALAPPDATA%\ProcessTracker-dev`, takes no single-instance mutex and does not read the ProgramData settings,
so it can run next to the installed service. `--Port <n>` and `--DataDir <path>` override the port and data folder.

### Deploying a local build

`scripts\deploy.ps1` installs your checkout as the real service, through the same MSI users get. Run it from an
elevated PowerShell in the checkout:

```powershell
scripts\deploy.ps1              # tests (without Category=Perf), build the MSI, install it, check health
scripts\deploy.ps1 -SkipTests
```

It builds `scripts\build-msi.ps1 -DevSuffix dev.<yyyyMMddHHmm>`, runs `msiexec /i ... /qn /l*v artifacts\deploy.log`, and
polls `/api/health` for up to 60 s until it reports `X.Y.Z-dev.<yyyyMMddHHmm>` with a collector tick younger than
5 s. A dev build reuses the version in `version.txt` and replaces whatever is installed, including a newer release.
It refuses while a phase 1 install is present (run `uninstall-legacy.ps1` first).

| Exit code | Meaning |
|---|---|
| 0 | Installed and healthy |
| 1 | Installed but unhealthy; the script prints the latest release URL to reinstall from |
| 3 | Nothing changed: not elevated, phase 1 install present, tests or build failed, or `msiexec` failed (the MSI rolled back) |

`scripts\build-msi.ps1` alone builds `artifacts\ProcessTracker-<version>-x64.msi`. `scripts\msi-smoke.ps1` is the
install/upgrade/uninstall/purge check that CI runs on GitHub's Windows runners; it changes the machine it runs on,
so run it only on a throwaway one.

## Releasing

Versions come from [conventional commits](https://www.conventionalcommits.org/) on `main`. `release-please` keeps a
release PR open that bumps `version.txt` and `CHANGELOG.md` (while the version is below 1.0, `feat` bumps the minor
and `fix` the patch). Merging that PR tags `v<version>`, and the `release` workflow tests, builds and smoke-tests the
MSI, uploads `ProcessTracker-<version>-x64.msi` and `SHA256SUMS.txt` to the GitHub release and, once enabled,
submits the new version to winget. Nothing is released by hand.

One-time winget setup:

1. Fork [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) to your account.
2. Create a **classic** personal access token with the `public_repo` scope (`wingetcreate` opens a PR against microsoft/winget-pkgs, and fine-grained tokens cannot contribute to repositories you are not a member of) and store it as the repository secret
   `WINGET_TOKEN`.
3. Submit the first version yourself with `wingetcreate new <url of the release MSI>`. Use the PackageIdentifier
   `worc4021.ProcessTracker`; the PR to `winget-pkgs` must be merged before later versions can be updated.
4. Set the repository variable `WINGET_PUBLISHED` to `true`. From then on each release runs
   `wingetcreate update ... --submit`.

Code signing (optional, off by default): set the repository variable `SIGNING_ENABLED` to `true` and add the Azure
Trusted Signing secrets the `release` workflow reads (`AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`,
`SIGNING_ENDPOINT`, `SIGNING_ACCOUNT`, `SIGNING_PROFILE`). Until then releases are unsigned and show the SmartScreen
warning described under [Install](#install).

## License

MIT — see [LICENSE](LICENSE). The software is provided "as is", without warranty of any kind, and the authors
accept no liability for any damage arising from its use.
