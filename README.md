# ProcessTracker

An always-on Windows process that records every process's resource use and attributes each process to
the **Claude Code session** that launched it, even after its parent has exited. It finds **orphans** (processes
whose session is gone, such as MATLAB or Dymola engines left behind), keeps a rolling on-disk history for
post-mortems, and lets agents ask how loaded the machine is before they start heavy work.

It has three parts:

- the tracker, which samples every 2 s. It runs either as a LocalSystem service (MSI) or as a user-only copy under
  your own account (no admin rights needed);
- `proctrack`, a command-line client;
- an MCP endpoint and two hooks that Claude Code sessions use, packaged as a Claude Code plugin.

The design is in [docs/superpowers/specs/2026-11-20-processtracker-design.md](docs/superpowers/specs/2026-11-20-processtracker-design.md).

## Install

Only Windows x64 is supported, and no .NET runtime is needed (everything is self-contained). There are two ways to
install, and the plugin route is the easy one.

### Claude Code plugin (no admin rights)

In Claude Code:

```text
/plugin marketplace add worc4021/ProcessTracker
/plugin install process-tracker@processtracker
/process-tracker:install
```

The plugin adds the MCP server, a SessionStart hook and a PreToolUse hook (see [Hooks](#hooks)). The last command
installs a **user-only copy** of ProcessTracker for you: it downloads the latest release zip, checks its hash, creates a
per-user logon task, starts the tracker and reports the version. It needs no admin rights and refuses to run elevated.
If the machine-wide service is already running, it only says so. Run it again to repair or update the install.

Claude Code does not update third-party marketplaces by default, so turn it on once: run `/plugin`, open
*Marketplaces*, choose `processtracker` and select *Enable auto-update*. Without it, the plugin stays at the version you
installed. The tracker itself keeps updating either way.

Other plugin commands:

| Command | What it does |
|---|---|
| `/process-tracker:status` | Runs `proctrack user status` and `proctrack load` and shows both |
| `/process-tracker:uninstall [purge-data]` | Removes the user-only copy (see [Uninstall](#uninstall)) |

The user-only copy is not on your PATH. The plugin finds it by itself. To run it by hand, use the full path:
`%LOCALAPPDATA%\Programs\ProcessTracker\app-<version>\bin\proctrack.exe`.

### Machine-wide service (MSI)

This installs per machine, into `C:\Program Files\ProcessTracker`, from an MSI. It needs admin rights and gives the full
picture (see [User-only vs machine-wide](#user-only-vs-machine-wide)).

```powershell
winget install worc4021.ProcessTracker
```

The winget package becomes available once it has been published (see
[Publishing to winget](#publishing-to-winget)). Until then, download
`ProcessTracker-<version>-x64.msi` from the
[latest GitHub release](https://github.com/worc4021/ProcessTracker/releases/latest), compare its hash with
`SHA256SUMS.txt` and run it (double-click, or from an elevated PowerShell:
`msiexec /i ProcessTracker-<version>-x64.msi /qn`). Each release has the MSI, the zip the user-only copy uses, and
`SHA256SUMS.txt`.

Releases are **not code-signed** until signing is enabled, so Windows SmartScreen shows "Windows protected your PC"
for a downloaded MSI. Choose *More info*, then *Run anyway*, if you trust the download. See
[Trust and updates](#trust-and-updates) for what that means for automatic updates.

The install:

- copies the service to `C:\Program Files\ProcessTracker` and `proctrack.exe` to its `bin\` folder, and adds `bin\` to
  the **system** PATH (open a new terminal to pick it up);
- stops any running user-only instance (of any user), so the service can take the port;
- registers the `ProcessTracker` service (LocalSystem, automatic start, restart on failure after 5 s, 5 s and 30 s)
  and starts it;
- creates `%ProgramData%\ProcessTracker` (see [Ports, data and settings](#ports-data-and-settings)) and the
  `ProcessTracker` event source;
- registers the scheduled task `\ProcessTracker\AutoUpgrade` (see below).

The MSI does **not** register an MCP entry any more: the plugin owns it. If you do not use the plugin, run
`proctrack mcp register` from a normal (not elevated) terminal. If you used the plugin and also have a user-scope
`process-tracker` entry from an earlier install, remove the old one with `proctrack mcp unregister`, so the tools do not
appear twice.

### Upgrades

The scheduled task `\ProcessTracker\AutoUpgrade` runs as SYSTEM daily at 12:00 (with a random delay of up to an hour)
and 10 minutes after boot. It runs `upgrade.ps1` from the install folder, which first tries
`winget upgrade --id worc4021.ProcessTracker`. If winget is missing or does not know the package, it falls back to the
latest GitHub release: it downloads the MSI and `SHA256SUMS.txt` (through `proctrack admin fetch-release`), checks the
hash and installs the MSI only if the release is newer than what is installed. The outcome goes to the Application event
log (source `ProcessTracker`):

| Event id | Meaning |
|---|---|
| 1000 | Up to date |
| 1001 | Upgraded (or reinstalled) through winget |
| 1002 | Failed |
| 1004 | Upgraded from a GitHub release |

You can also upgrade by hand with `winget upgrade worc4021.ProcessTracker`, or by running a newer MSI.

```powershell
proctrack upgrade-task status     # show the task
proctrack upgrade-task disable    # elevated: stop upgrading automatically
proctrack upgrade-task enable     # elevated: turn it back on
```

An upgrade keeps the data folder and your valid settings.

A graceful stop (including the one during an upgrade) persists state. After a restart, CPU and I/O deltas
continue from the persisted counters, so work done while the service was down is not lost.

### Uninstall

Each install is removed on its own.

**User-only copy:** from Claude Code, `/process-tracker:uninstall` (add `purge-data` to delete the data too), or from a
terminal:

```powershell
proctrack user uninstall                # stops it, removes the logon task and the program files, keeps the data
proctrack user uninstall --purge-data   # also deletes %LOCALAPPDATA%\ProcessTracker
```

`proctrack` is not on your PATH in this mode, so use the full path shown under
[Install](#claude-code-plugin-no-admin-rights). It never touches an MSI install.

**Machine-wide service:**

```powershell
winget uninstall worc4021.ProcessTracker
# or, without winget:
msiexec /x ProcessTracker-<version>-x64.msi /qn
```

This removes the service, the files, the PATH entry, the scheduled task and the Claude Code MCP entry if one was
registered with `proctrack mcp register`, and **keeps** `%ProgramData%\ProcessTracker` (history, state, settings). To
delete that too, pass `PURGEDATA=1` to the MSI: `msiexec /x ProcessTracker-<version>-x64.msi /qn PURGEDATA=1`. The MSI
does not remove user-only copies. One that is left behind starts again at its owner's next logon.

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

## User-only vs machine-wide

Both modes run the same executable. They share one single-instance mutex, so only one instance serves port 47811.

| | Machine-wide (MSI) | User-only (plugin) | Development (`--console --dev`) |
|---|---|---|---|
| Runs as | LocalSystem | the logged-in user | the developer |
| Started by | the service manager, automatic | a per-user logon task | by hand |
| Port | 47811 | 47811 | 47812 |
| Data | `%ProgramData%\ProcessTracker` | `%LOCALAPPDATA%\ProcessTracker` | `%LOCALAPPDATA%\ProcessTracker-dev` |
| Settings file | `%ProgramData%\ProcessTracker\appsettings.json` | `%LOCALAPPDATA%\ProcessTracker\appsettings.json` | none |
| Logs | Application event log | a rolling log file in the data folder | console |
| Window | none | none | console |
| What it sees | all processes | your own non-elevated processes; others count toward load as unattributed | as user-only |
| Last user input | not available (service session) | available | available |

User-only mode installs under `%LOCALAPPDATA%\Programs\ProcessTracker\app-<version>\` (the service exe plus
`bin\proctrack.exe`), and its task is `\ProcessTracker\User-<SID>`.

**What user-only mode cannot see.** Other users' processes and elevated processes of your own are not attributed to
any session. They still count toward the machine's load, but show up as unattributed. The "User last active ... ago" line
in the hooks is only available in user and development mode. In service mode it says "unknown".

**It serves only its owner.** On a machine with several users, the first user's instance holds the port. Everyone else
who asks it (other users' hooks and MCP clients) gets `403` with a fixed message saying it belongs to another user and
that an admin should install the machine-wide service. Use the MSI on shared machines.

**The machine service always wins.** If the service is installed, user mode exits at start (exit code 4) and skips
self-updates, so a leftover user-only copy does nothing. Installing the MSI stops running user-only instances. After you
uninstall the MSI, a user-only copy takes over again at its owner's next logon.

### Self-update (user-only)

The user-only tracker updates itself, with no admin rights, 10 minutes after it starts and then every 24 hours:

1. It reads the latest GitHub release and stops if that is not newer than the running version.
2. It downloads the zip and `SHA256SUMS.txt` from that release and checks the hash. A mismatch aborts.
3. It unpacks the new version next to the old one (`app-<version>\`), stops the running instance, starts the new one
   and waits for it to report healthy.
4. If the new version does not become healthy, it rolls back to the previous one. That version's folder is kept for
   this, older ones are deleted.

The log is `%LOCALAPPDATA%\ProcessTracker\logs\processtracker.log`. It records every update, skipped update and
rollback:

```powershell
Get-Content "$env:LOCALAPPDATA\ProcessTracker\logs\processtracker.log" -Tail 50
```

One residual risk: if a new version crashes hard before it is healthy, nothing runs until your next logon, because the
logon task still points at the previous version.

### Trust and updates

Automatic updates, both the user-only self-update and the MSI's GitHub fallback (which runs as SYSTEM), install a
GitHub release after checking only its **SHA-256 against `SHA256SUMS.txt` from the same release**. Releases are
unsigned. So whoever controls the GitHub repository or its release workflow can run code as SYSTEM on every MSI machine
that uses the fallback, and as the user on every user-only machine, within a day. This was a deliberate choice to keep
updates working without winget and without a paid certificate.

What limits it: downloads come only from `https://github.com/worc4021/ProcessTracker/releases/`, redirects are allowed
only to GitHub's release asset host, the hash is checked before anything is unpacked or run, and an update must be a
strictly newer version, so there is no downgrade.

If you maintain the repository, enable two-factor authentication on the account, protect `main` with branch
protection, and keep the release workflow's permissions minimal. When code signing is switched on (see
[Releasing](#releasing)), both updaters will additionally require a valid signature, and the hash-only path goes away.

## Ports, data and settings

- The service listens on `127.0.0.1` only, on port **47811** (dev: **47812**). REST is under `/api` and MCP is at
  `/mcp`. Requests whose `Host` or `Origin` is not loopback on that port are refused (DNS-rebinding guard).
- Data lives in `%ProgramData%\ProcessTracker` (machine-wide; the user-only copy uses `%LOCALAPPDATA%\ProcessTracker`):
  - `state.db` (SQLite): process records, sessions, live counters, kill audit;
  - `history\*.pts`: 5-minute segments, kept for `RetentionHours`;
  - `appsettings.json`: settings.

  The folder is owned by `BUILTIN\Administrators`, does not inherit permissions, gives full control to SYSTEM and
  Administrators and read & execute to Users. The installer refuses to proceed if the folder (or `history\`) is a
  junction or symbolic link.
- Settings in `appsettings.json` in the data folder:

  | Setting | Default | Notes |
  |---|---|---|
  | `RetentionHours` | 24 | Minimum 2 |
  | `OrphanIdleAlertMinutes` | 30 | |
  | `HeavyCommandPatterns` | built-in list | Optional list of regular expressions that mark a command as heavy for the [hooks](#hooks) |
  | `UserProfilePath` | | Optional override. By default the service resolves each session's user profile itself; set this only to force one profile |

  A `Port` set there is ignored, because `deploy.ps1` and the CLI both use 47811. Only `--Port` on the
  command line changes the port, which is meant for development and tests.

## Logs

As a machine-wide service, ProcessTracker logs Information and above to the Windows **Application** event log under the source
`ProcessTracker`:

```powershell
Get-WinEvent -ProviderName ProcessTracker -MaxEvents 50
```

In user-only mode there is no event log (creating its source needs admin). It writes a rolling log file instead,
`%LOCALAPPDATA%ProcessTrackerlogsprocesstracker.log`. In console mode, logs go to the console.

## proctrack

```text
proctrack load                      machine load and the sessions using the most
proctrack sessions                  Claude Code sessions with their totals
proctrack orphans                   orphaned processes, alertable first (a trailing ! marks an alert)
proctrack tree [unit]               a session's process tree (default: the calling session)
proctrack history --unit <id> | --key <pid@ticks> [--minutes 30]
proctrack kill <pid> [--tree] [-y]  kill a tracked process (and its descendants)
proctrack mcp register|unregister   add or remove the Claude Code MCP entry (user scope)
proctrack upgrade-task enable|disable|status   the MSI's winget task (changing it needs elevation)
proctrack user install|uninstall [--purge-data]|status [--json]   the user-only copy (no admin)
```

`load`, `sessions`, `orphans`, `tree`, `history` and `user status` accept `--json`; `kill` does not. You normally do not
run `user install` yourself: `/process-tracker:install` does the whole install. Use `--dev` to talk to the dev
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

The plugin registers the MCP server (`plugin:process-tracker:process-tracker`, from `plugin/.mcp.json`). Without the
plugin, `proctrack mcp register` adds it for the current user from a normal terminal, and `proctrack mcp unregister`
removes it. The command it runs is:

```powershell
claude mcp add --transport http --scope user process-tracker http://127.0.0.1:47811/mcp --header 'X-Claude-Session: ${CLAUDE_CODE_HOST_SESSION_ID:-}'
```

Claude Code expands the header per session. Terminal sessions do not set `CLAUDE_CODE_HOST_SESSION_ID`, so for
them the header is empty and ignored. The caller is then identified from the TCP connection's owning process.

The tools are all read-only. There are no kill tools.

### Checking the load before heavy work

The server sends **instructions** when a session connects, and Claude Code adds them to that session's system prompt.
So every session with the MCP entry is told, with no CLAUDE.md change, to call `get_machine_load` before builds,
test suites, simulations, MATLAB or Dymola engines, parallel jobs or subagents doing these. The policy is to **warn,
not to block**:

| Pressure | What the session does |
|---|---|
| Low, Moderate | Runs at most `suggestedParallelism` tasks at once |
| High, Critical | Knows it is close to making the machine unusable for you, weighs what you have told it about how you use the machine (foreground work, or a remote or dedicated box) and scales down accordingly. At Critical, if it does not know, it asks you |

The exact text is `ProcessTrackerTools.ServerInstructions` in
[src/ProcessTracker.Service/Mcp/ProcessTrackerTools.cs](src/ProcessTracker.Service/Mcp/ProcessTrackerTools.cs).
Sessions that were already running when the tracker was upgraded get it on their next start.

Instructions from an MCP server carry less weight than your own memory file. To make the rule stronger, or to cover
sessions without the MCP entry, add this to `%USERPROFILE%\.claude\CLAUDE.md`:

```markdown
# Machine load

- Before starting heavy local work (builds, test suites, simulations, MATLAB/Dymola engines, parallel jobs, or
  subagents that will do these), check the load with the process-tracker MCP tool `get_machine_load`
  (or `proctrack load --json` if the MCP is unavailable).
- Low/Moderate: run at most `suggestedParallelism` tasks at once. High/Critical: the machine is close to unusable for
  me, so scale down based on what I have told you about how I use it (foreground work, or a remote or dedicated box).
  If you don't know and it is Critical, ask me.
- Shut down engines and servers you started once you are done with them.
```

| Tool | Returns |
|---|---|
| `get_machine_load` | Pressure level, free cores, memory and commit headroom, suggested parallelism, the caller's own usage, the top sessions |
| `get_my_session` | The calling session with its live process tree |
| `list_sessions` | Sessions that are live or recently ended, with their totals |
| `list_orphans` | Orphaned processes, alertable ones first |
| `get_process_history` | One process's usage over the last minutes, by `pid@ticks` key |

## Hooks

The plugin has two hooks (`plugin/hooks/hooks.json`). Neither blocks anything, and both stay silent when the tracker is
not running.

- **SessionStart** adds a short paragraph to the session: the mode, the pressure and suggested parallelism, the top
  sessions by memory, how long ago you last used the machine, and a one-line rule. If the tracker is not running, it
  tells the agent to offer `/process-tracker:install` once. If the tracker is older than the plugin, it says an update is
  due.
- **PreToolUse** runs before `Bash`, `PowerShell`, `Task` and `Agent` calls. The service decides whether the command is
  heavy (builds, test runs, MATLAB or Dymola launches, explicit parallelism, and subagent launches). Other commands, and
  any command at Low pressure, get no output.

What the agent sees for a heavy command:

| Pressure | Added to the agent's context |
|---|---|
| Moderate | One line: the machine is at Moderate, run at most N tasks in parallel |
| High, Critical | A full block: pressure, CPU busy, commit headroom, suggested parallelism, the top sessions with CPU and memory, and "User last active ... ago" (user-only and development mode; "unknown" in service mode). It leaves the decision to the agent, based on what you told it about the machine, and asks you if it is unsure at Critical |

A session gets the full block at most once per 60 s, and the one-liner in between. Nothing is ever blocked or denied.
To change which commands count as heavy, set `HeavyCommandPatterns` in `appsettings.json`
(see [Ports, data and settings](#ports-data-and-settings)).

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

`--console --dev` runs the same code as a console app. The service exe is a GUI-subsystem app (so user mode shows no
window at logon). `--console` attaches to the parent console, but cmd and PowerShell return the prompt immediately
when you run the built exe directly, so use `dotnet run` as above to see its output. It listens on port 47812, keeps its data in
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
MSI, uploads `ProcessTracker-<version>-x64.msi`, `ProcessTracker-<version>-x64.zip` and `SHA256SUMS.txt` to the GitHub
release and, once enabled, submits the new version to winget. `release-please` also bumps the plugin's `plugin.json`
version. As its last step, after publishing, the workflow moves the plugin marketplace's `ref` to the new tag, so the
plugin never gets ahead of a downloadable release. Nothing is released by hand.

### Publishing to winget

**How winget finds a package.** `winget install` does not search GitHub. Its default source, `winget`, is an index
built from the [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) repository, where every package
version is a folder of YAML manifests (`manifests/w/worc4021/ProcessTracker/<version>/`). The manifests give the
package id, name, publisher, license, and the installer URL and SHA256. So to make `worc4021.ProcessTracker` known,
someone opens a pull request to winget-pkgs adding those manifests. The PR is checked by Microsoft's validation
pipeline: it downloads the MSI, checks the hash, scans it, and installs it in a VM. A moderator then reviews it, and
a **new** package gets a human review. That usually takes from a few hours to a few days. Once merged, the index is
republished and clients see the package within a few hours (`winget source update` forces a refresh).

[`wingetcreate`](https://github.com/microsoft/winget-create) writes the manifests and opens that PR. It uses a
GitHub token to fork winget-pkgs into your account (if you have no fork) and to open the PR from it.

**One-time setup (by hand, once):**

1. Create a **classic** personal access token with the `public_repo` scope. Fine-grained tokens cannot open PRs
   against repositories you are not a member of. Store it as this repository's secret `WINGET_TOKEN`.
2. Install `wingetcreate` (`winget install Microsoft.WingetCreate`) and submit the first version:

   ```powershell
   wingetcreate new https://github.com/worc4021/ProcessTracker/releases/download/v0.1.0/ProcessTracker-0.1.0-x64.msi --token <PAT>
   ```

   It downloads the MSI, reads the ProductCode and installer type from it, and prompts for the rest:

   | Prompt | Answer |
   |---|---|
   | PackageIdentifier | `worc4021.ProcessTracker` (every later release uses this id) |
   | PackageVersion | `0.1.0` |
   | Publisher | `worc4021` (the MSI's Manufacturer) |
   | PackageName | `ProcessTracker` |
   | License | `MIT` |
   | ShortDescription | `Tracks process resource use per Claude Code session and finds orphaned processes.` |
   | Scope | `machine` |

   Before submitting, open the generated `*.installer.yaml` and check that it has an `AppsAndFeaturesEntries` entry
   with `UpgradeCode: '{69B0FAFC-2873-43F9-8377-3B554A10091D}'` (the MSI's fixed UpgradeCode), and add it if it is
   missing. Every version has a new ProductCode, so the UpgradeCode is what lets winget recognise copies installed
   from a downloaded MSI, or by `deploy.ps1`, as this package and upgrade them. To edit first, decline the submit
   prompt, edit, then run `wingetcreate submit <manifest folder> --token <PAT>`. When submitting, it prints the URL
   of the PR it opened in winget-pkgs.
3. Wait for that PR to be **merged**. If validation adds a label such as `Needs-Author-Feedback`, reply on the PR.
   Unsigned installers are accepted, but the scan may take longer.
4. Set this repository's variable `WINGET_PUBLISHED` to `true`.

**From then on it is automatic.** Each release runs
`wingetcreate update worc4021.ProcessTracker --version <v> --urls <msi url>|x64 --submit`, which copies the
previous manifests, updates the version, URL and hash, and opens the next PR. That PR is validated and merged
without you (an update to an existing package needs no human review unless validation flags it). On every
installed machine, the `\ProcessTracker\AutoUpgrade` task's next `winget upgrade` then picks the new version up.
Until step 4 is done, the release workflow skips the winget step with a notice, and the AutoUpgrade task falls back to
the GitHub release (event 1004), or logs event 1002 if that fails too.

Code signing (optional, off by default): set the repository variable `SIGNING_ENABLED` to `true` and add the Azure
Trusted Signing secrets the `release` workflow reads (`AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`,
`SIGNING_ENDPOINT`, `SIGNING_ACCOUNT`, `SIGNING_PROFILE`). Until then releases are unsigned and show the SmartScreen
warning described under [Install](#install).

## License

MIT — see [LICENSE](LICENSE). The software is provided "as is", without warranty of any kind, and the authors
accept no liability for any damage arising from its use.
