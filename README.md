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

## Prerequisites

- Windows x64 and the .NET 10 SDK, used to test and publish from this checkout.
- An **elevated** PowerShell (Windows PowerShell 5.1 or PowerShell 7), started as **your own account**. The install
  configures the PATH, the transcript folder (`%USERPROFILE%\.claude\projects`) and the Claude Code config of the
  account that runs it.

## Install, upgrade, uninstall

Run these from the root of the checkout.

| Script | What it does | Exit codes |
|---|---|---|
| `scripts\install.ps1` | Runs the tests and publishes to `C:\Program Files\ProcessTracker\versions\<version>`, then points the `current` junction at it. Registers the `ProcessTracker` service (automatic start, restart on failure), creates the data folder and the `ProcessTracker` event source, and adds `current` to your user PATH. Starts the service, waits for it to report healthy, then registers the MCP server with Claude Code. | 0 installed, 1 failed (run `uninstall.ps1` to clean up), 3 nothing changed |
| `scripts\deploy.ps1` | Upgrades in place. It runs the tests (except the timing-sensitive `Category=Perf` ones) and publishes a new version folder. The updater then stops the service, repoints `current`, starts it and polls `/api/health` until the new version reports a fresh collector tick (15 s timeout). If the new version is unhealthy, the updater points back to the previous version and restarts it. The 3 newest versions are kept. `-SimulateUnhealthy` builds a version that always reports unhealthy, which exercises the rollback. | 0 Upgraded, 1 RolledBack, 2 RollbackFailed, 3 nothing changed |
| `scripts\uninstall.ps1` | Stops and deletes the service, then removes the MCP entry, the PATH entry and the installed versions. The data folder and the event source are kept unless you pass `-PurgeData`. | 0 uninstalled, 1 failed, 3 nothing changed |

A graceful stop (including the one during an upgrade) persists state. After a restart, CPU and I/O deltas
continue from the persisted counters, so work done while the service was down is not lost.

## Ports, data and settings

- The service listens on `127.0.0.1` only, on port **47811** (dev: **47812**). REST is under `/api` and MCP is at
  `/mcp`. Requests whose `Host` or `Origin` is not loopback on that port are refused (DNS-rebinding guard).
- Data lives in `%ProgramData%\ProcessTracker`:
  - `state.db` (SQLite): process records, sessions, live counters, kill audit;
  - `history\*.pts`: 5-minute segments, kept for `RetentionHours`;
  - `appsettings.json`: settings.

  The folder is owned by `BUILTIN\Administrators`, does not inherit permissions, and gives full control to SYSTEM
  and Administrators and read access to the installing user. `install.ps1` refuses to run if the folder is a
  junction or symbolic link.
- Settings in `%ProgramData%\ProcessTracker\appsettings.json`:

  | Setting | Default | Notes |
  |---|---|---|
  | `RetentionHours` | 24 | Minimum 2 |
  | `OrphanIdleAlertMinutes` | 30 | |
  | `UserProfilePath` | | Written by `install.ps1` |

  A `Port` set there is ignored, because the updater, scripts and CLI all use 47811. Only `--Port` on the command
  line changes the port, which is meant for development and tests.

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
```

Every command accepts `--json`. Use `--dev` to talk to the dev instance, or set `PROCTRACK_URL` to use any other
address.

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

`install.ps1` registers the server at user scope:

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

## Development mode

```powershell
dotnet test --solution ProcessTracker.slnx
dotnet run --project src\ProcessTracker.Service -- --console --dev
proctrack --dev sessions
```

`--console --dev` runs the same code as a console app. It listens on port 47812, keeps its data in
`%LOCALAPPDATA%\ProcessTracker-dev`, takes no single-instance mutex and does not read the ProgramData settings,
so it can run next to the installed service. `--Port <n>` and `--DataDir <path>` override the port and data folder.

## License

MIT — see [LICENSE](LICENSE). The software is provided "as is", without warranty of any kind, and the authors
accept no liability for any damage arising from its use.
