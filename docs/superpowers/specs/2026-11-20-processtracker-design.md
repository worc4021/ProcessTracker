# ProcessTracker — Design

Date: 2026-11-20 · Status: draft for approval

## 1. Intent

Processes launched by Claude Code sessions (shells, builds, MCP servers and, above all, the MATLAB and
Dymola engines those MCP servers start) have repeatedly brought this workstation to its knees. When
that happens there is no way to tell which session owns the offending process, and stale engines from
sessions that ended long ago keep accumulating.

ProcessTracker is a single, always-on Windows service that:

1. records every process's resource use, in the spirit of Sysinternals Process Explorer / Process Monitor;
2. attributes each process to the **Claude Code session** that launched it, even after its parent died;
3. identifies **orphans** (processes whose owning session is gone) and how long they have been dormant;
4. keeps a **rolling history** (≥ 2 h, on disk) so a post-mortem is possible after a freeze or hard reset;
5. lets agent sessions **ask how loaded the machine is** and **book resources** before launching heavy work;
6. can be **upgraded in place** without losing what it is tracking.

## 2. Goals, non-goals, anti-goals

**Goals**
- Answer "which session started this, and what has it been doing?" for any process, live or from the last 2 h.
- Surface orphaned MATLAB / Dymola / dymosim processes with their owner session and idle time.
- One machine-wide service that every Claude session queries over MCP without spawning anything per session.
- Upgrade by stopping, swapping, and restarting, with no loss of tracking state and automatic rollback.

**Non-goals (for now)**
- Automatically killing anything. Kills happen only on an explicit user action (tray UI or CLI).
- Remote / multi-machine monitoring; CI-built releases (GitHub Actions comes later, see §13).
- Hard enforcement of resource bookings (bookings are cooperative; job-object caps are a later phase).

**Anti-goals: it has failed if…**
- …the monitor itself is one of the things using noticeable CPU/memory (budget: < 1 % of one core
  averaged, < 200 MB private bytes).
- …it stops answering when the machine is under memory pressure, which is exactly when it is needed.
- …it gives any local process a way to kill arbitrary processes as SYSTEM.
- …an upgrade loses history, attributions, or bookings.

## 3. Constraints and verified facts

- Windows 11 Pro single-user workstation; the user is the only interactive user.
- Claude Code runs mostly through the Claude desktop app; terminal `claude` sessions also occur.
- Local deployment only for now (§13).
- MATLAB / Dymola engines are **never shared between sessions** (confirmed by the user). The session that
  started an engine is the only one using it, so "started by" can stand in for "used by". No connection
  tracking between engines and MCP servers is needed.

Verified on this machine on 2026-11-20 by reading other processes' environment blocks (PEB) from PowerShell:

- A desktop session's own `claude.exe` (child of the desktop app's main `claude.exe`) carries
  `CLAUDE_CODE_HOST_SESSION_ID=local_<guid>`.
- Every process that session starts (shells, `uv`/`uvx`, MCP server Python processes three levels down)
  carries both `CLAUDE_CODE_HOST_SESSION_ID` and `CLAUDE_CODE_SESSION_ID=<guid>`.
- `CLAUDE_CODE_SESSION_ID` equals the transcript file name `~/.claude/projects/<cwd-slug>/<id>.jsonl`.
- The desktop app's main `claude.exe` carries neither; some processes it starts (e.g. a `uv.exe` with a
  dead parent) also carry neither and belong to the app rather than to a session.
- Each session starts its **own** set of MCP servers (≈ 30 `uv`/`python` processes per session here),
  which in turn start MATLAB / Dymola engines. A session that ends uncleanly leaves the engine behind.
- Not yet verified: that MATLAB.exe / Dymola.exe started by those MCP servers inherit the variables (no engine
  was running). Expected, because they are started as ordinary child processes; the builder must confirm.
- Not yet verified: what terminal (non-desktop) `claude` sessions put in the environment.

## 4. Approaches considered

| Decision | Chosen | Rejected and why |
|---|---|---|
| Stack | **.NET 10** worker service (`Microsoft.Extensions.Hosting.WindowsServices`), C# | PowerShell/Python: too heavy and slow to run continuously and to stay responsive under pressure. Rust: viable, but .NET gives the WPF UI, the official C# MCP SDK and P/Invoke in one ecosystem. |
| Hosting | **One Windows service, LocalSystem**, single instance | Per-user tray app as collector: dies with logoff, cannot read every process or use ETW. |
| Session attribution | **Environment variables read from each process's PEB**, falling back to recorded ancestry | Parent-PID walking alone: PIDs are reused and parents die, so orphan lineage is lost. |
| Collection | **Snapshot polling first** (`NtQuerySystemInformation(SystemProcessInformation)`), ETW added later | ETW-first: more work up front. Env-based attribution means polling loses almost nothing; ETW adds short-lived processes and file/network detail later. PDH performance counters: slow, instance-name problems. |
| Agent interface | **MCP over HTTP hosted inside the service** (`"type": "http"` in `.mcp.json`) | Per-session stdio MCP shim: adds processes per session, which is the very problem being solved. |
| Agent load control | **Advisory reads plus cooperative bookings** | Advisory only: two sessions both see "low" and both launch, and the machine still goes down. |
| Upgrade | **Stop → swap `current` junction → start → health check → rollback on failure** | Split "stable collector + upgradable host" process pair: no tracking gap, but two processes plus IPC. Not worth it while a restart loses so little (§12); the on-disk store is a clean seam if it is ever needed. Zero-gap side-by-side handover: conflicts with "exactly one instance". |
| Deployment | **Local `deploy.ps1`** | Azure Pipelines / Artifacts pull: not wanted now; GitHub Actions releases later. |

## 5. Architecture

```
ProcessTracker.Service  (Windows service, LocalSystem, one per machine)
├─ Collector     every 2 s: one system snapshot → per-process deltas; env read for new processes
├─ Attribution   process → session / app / unattributed; session liveness and metadata
├─ Store         in-memory ring (live) + 5-min segment files (history) + SQLite (state)
├─ Broker        load assessment, suggested parallelism, cooperative bookings
├─ Alerts        threshold rules → events for the tray UI (toast)
└─ API           Kestrel bound to 127.0.0.1:<port> only
     ├─ /api/…   REST (JSON)   ← tray UI, `proctrack` CLI, hooks, deploy health check
     └─ /mcp     MCP (stateless streamable HTTP)  ← every Claude Code session

ProcessTracker.Tray     WPF tray app, per user; pure client of the REST API
proctrack.exe           CLI; pure client of the REST API
ProcessTracker.Updater  small exe that performs stop/swap/start/verify/rollback
```

Services run in session 0 and cannot show UI, so the tray app is a separate process. Closing it stops
nothing. Single instance is guaranteed by the Service Control Manager plus a `Global\` named mutex taken
by the service host (a dev instance does not take it; §14).

## 6. Data model

- **ProcessKey** = (PID, creation time). PIDs are reused quickly; PID alone must never key anything.
- **ProcessRecord**: key, parent key (as observed at first sight), image name, full path, command line,
  owner user, first seen, exited at, attribution (see §7), the four activity timestamps (§8), last
  cumulative counters (for delta computation across restarts).
- **Sample**: key, timestamp, CPU % (delta of kernel+user time), private bytes, working set,
  read/write/other operation counts and bytes (deltas), context switches (delta, summed over threads),
  handle count, thread count.
- **Session**: `CLAUDE_CODE_SESSION_ID` (primary), host session id (desktop), anchor `claude.exe`
  ProcessKey, cwd and title (from transcript), started, last transcript activity, ended at, state.
  A host session can map to several session ids over its life (e.g. after `/clear`). Keep the mapping
  many-to-one, not one-to-one.
- **SessionAggregate**: per session per tick totals (CPU, private bytes, IO) so per-session history is cheap.
- **Booking**: id, session, cores, memory GB, purpose, created, expires, released.
- **SystemSample**: per tick: CPU total and per core, commit charge / commit limit, available memory,
  system disk throughput.

## 7. Attribution

On first sight of a process, in order:

1. Read its environment block (PEB). `CLAUDE_CODE_SESSION_ID` present → owned by that session.
   Only `CLAUDE_CODE_HOST_SESSION_ID` present → owned by the session(s) of that host id (this is the
   session's own `claude.exe`, which becomes the session's **anchor**).
2. Else inherit the attribution of the nearest recorded ancestor (parent key as observed at first sight).
3. Else, if it descends from the desktop app's main `claude.exe` → **Claude app (no session)**.
4. Else → **unattributed** (ordinary non-Claude processes).

Attribution is computed once and stored; it never changes later, even if PIDs are reused.

Reading the PEB needs `PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ`; LocalSystem can do this
for the user's processes. Protected processes will fail. Treat them as unattributed. 32-bit (WOW64)
processes need the WOW64 PEB path. Support it, or treat them as unattributed and log it.

**What the user calls "a session"** is a desktop session row, identified by `CLAUDE_CODE_HOST_SESSION_ID`.
Its `CLAUDE_CODE_SESSION_ID`s (one per `/clear` or restart) are sub-sessions listed under it. Terminal
sessions have no host id. Their unit is the anchor `claude.exe`, and the builder confirms what such sessions
put in the environment. All views, bookings and `get_my_session` work at this unit.

**Session liveness:** a session is *live* while its anchor `claude.exe` is running. A terminal session's
anchor is the `claude.exe` that is the nearest ancestor of its tagged processes.
**Session metadata:** cwd and title from the transcript `.jsonl` under the interactive user's
`%USERPROFILE%\.claude\projects\`, using the first user prompt, truncated. If the desktop app's own
session titles can be found in its local data, prefer them. That is left to the builder to investigate.

## 8. Activity ("last activity")

All signals come from the same snapshot used for sampling, so they cost nothing extra.

- `lastCpuAt`: CPU time grew by more than the threshold over the window.
- `lastIoAt`: read or other IO operations grew.
- `lastWriteAt`: write operations grew.
- `lastWakeAt`: context switches grew by more than the threshold.

A process is **active** in a tick if any signal is above its threshold. Thresholds are needed because idle
engines are never at exactly zero (MATLAB's JVM housekeeping). Defaults are a builder call, configurable,
with per-image overrides (MATLAB, Dymola).

**Tree roll-up:** a process's *effective* last activity = max over itself and its live descendants, so an
idle Dymola with a busy `dymosim` child counts as in use.

## 9. Orphans

A process is an **orphan** when it is attributed to a session that has ended (its anchor is gone).

A dead parent alone does **not** make a process an orphan. Launchers hand off and exit (e.g. a `matlab.exe`
stub starting `bin\win64\MATLAB.exe`), and attribution comes from the environment, not from the parent
chain. A process in a live session whose parent has died is shown as **detached**: informational only, with
no alert.

The orphan view shows owner session (title, cwd, end time), image, age, effective idle time, and
current/peak memory. **Watched images** (configurable list, default `MATLAB.exe`, `Dymola.exe`, `dymosim*.exe`)
are highlighted and drive an alert when orphaned and idle for longer than N minutes (default 30).

## 10. Store

- **Live ring (memory):** last 10 min at full 2 s resolution for the UI and API.
- **History (disk):** append-only segment files, one per 5 minutes, under `%ProgramData%\ProcessTracker\history\`.
  - Active processes get a row every tick. Idle ones get a heartbeat row every 60 s.
  - Estimated size: ~15 MB per 2 h.
  - Retention is configurable. The default is 24 h; the minimum is 2 h.
  - Files older than the retention window are deleted (FIFO).
- **Segment format:** compact binary, with a header holding a magic number, a format version and a field
  table. Changes are additive only, so an older reader skips unknown fields. This is what makes rollback
  safe.
- **State (SQLite, WAL mode):** process records, sessions, aggregates index, bookings, last counter values.
  Written incrementally, so a crash loses at most the last tick.
- **Process records** are kept as long as any history references them.

## 11. Interfaces

### REST (`/api`)
`GET /health` (version, uptime, last collector tick age); `GET /load`; `GET /sessions`;
`GET /sessions/{id}` (process tree + aggregates); `GET /orphans`; `GET /processes/{pid}?created=…`;
`GET /history?session=…|process=…&from=…&to=…&metrics=…`; `POST /bookings`; `DELETE /bookings/{id}`;
`POST /audit/kill` (record of a kill done by a client); `GET /events` (SSE stream for the tray app).

**Kills are executed by the client, not by the service.** The tray app or CLI asks the service for the
target's ProcessKey/tree, re-checks the creation time (guards against PID reuse), and terminates with the
user's own rights. It then posts an audit record. The SYSTEM service has no kill capability at all.

**`proctrack kill` run from inside a Claude session** (the CLI's own attribution says so) may only target that
caller's own session's processes. Killing another session's processes or orphans requires the tray UI or a
terminal outside Claude. This is a guard against accidents, not a security boundary, because an agent can
always run `Stop-Process`.

### MCP (`/mcp`, stateless)
- `get_machine_load`: pressure level, free cores, available memory, commit headroom, suggested
  parallelism, the caller's own session usage, top sessions.
- `get_my_session`: the caller's process tree with usage and last activity.
- `list_sessions`, `list_orphans`, `get_process_history`.
- `request_resources(cores, memoryGB, minutes, purpose)` → granted / partial (with what is available) / wait.
- `release_resources(bookingId)`.

No kill tools over MCP (user-only kills).

**Caller identity:** the service maps the TCP connection's remote port to its owning PID
(`GetExtendedTcpTable`) and resolves that process's attribution. This gives the session at the host-session
unit (§7). That is the unit agents see, so it is sufficient.

Phase 1 must verify that the connecting process is the session's own `claude.exe` and not a desktop-app
proxy. If it is a proxy, fall back to an `X-Claude-Session` header set in the MCP config. Bookings are
cooperative and MCP has no kill tools, so trusting a caller-supplied header costs little.

**Load assessment:**
- Pressure is driven primarily by **commit headroom** (commit limit − commit charge), then CPU.
- Windows refuses allocations at the commit limit even when "available" memory looks fine.
- A booking records the owning session's usage at the moment it is made (its baseline). Its outstanding
  amount is the booked amount minus the session's growth above that baseline, never below zero. Booked work
  that is actually running is therefore not counted twice.
- Outstanding amounts are subtracted from headroom before answering any other session.
- There is no queue. "wait" carries a retry-after estimate, based on the expiry of the oldest blocking
  booking.
- Bookings end on explicit release, expiry, or end of the owning session.

### CLI
`proctrack load | sessions | orphans | tree [session] | history … | kill <pid> [--tree]`, with `--json` everywhere.

### Hooks (later phase)
A `PreToolUse` hook script for Bash/PowerShell commands that look heavy (builds, `matlab -batch`, dymosim
sweeps) calls `/api/load` and injects a warning when pressure is high.

## 12. Resilience, security, resource budget

- **Under pressure:** buffers are preallocated, GC heap growth is bounded (`GCHeapHardLimit`), the service
  runs at above-normal priority, and the collector never blocks on disk (a separate writer drains the queue).
- **Security:**
  - Kestrel binds to 127.0.0.1 only.
  - The service has no kill capability. Kills run in the tray app or CLI with the user's rights (§11), so
    they can only reach processes the user could kill anyway.
  - Every kill is recorded through `/api/audit/kill`.
- **Restart cost:** CPU time and IO counters are cumulative since process start. After a restart the service
  computes deltas against the persisted last values, so the work done during the gap is kept (as one lump).
  Processes started during the gap are attributed from their environment. Only processes that both started
  and exited inside the gap are lost.

## 13. Upgrade and deployment (local only)

Layout:
```
C:\Program Files\ProcessTracker\versions\<semver>\…
C:\Program Files\ProcessTracker\current  → junction to the active version
```
The service's ImagePath points at `current\ProcessTracker.Service.exe`.

`deploy.ps1` (run elevated from the checkout):
1. Build, run tests, `dotnet publish` (self-contained, win-x64) into `versions\<semver>`.
2. Invoke the updater, which:
   1. stops the service (graceful stop persists state, timeout 10 s);
   2. repoints `current`;
   3. starts the service;
   4. polls `/api/health` until it reports the new version with a collector tick < 5 s old (timeout 15 s).
   5. On failure it repoints to the previous version, restarts it and reports.
3. Keep the last 3 versions and prune the rest.

`install.ps1` registers the service (auto start, recovery: restart on failure), creates `%ProgramData%`
folders and adds the MCP entry to the user's Claude Code config (`"type": "http"`, `http://127.0.0.1:<port>/mcp`).

Later: GitHub repository plus a GitHub Actions workflow that builds tagged releases. `deploy.ps1` gains a
`-FromRelease <tag>` switch. Nothing in this design depends on that.

## 14. Development mode

`ProcessTracker.Service.exe --console --dev` runs the same code as a console app with its own port and
data folder, kill disabled, and no global mutex, alongside the production service. Read-only collection
from two instances is harmless.

## 15. Phases

1. **Core service:**
   - collector, attribution, sessions, orphans and activity;
   - store (ring + segments + SQLite);
   - REST, MCP read tools and CLI;
   - `install.ps1` / `deploy.ps1` / updater with health check and rollback.
2. **Tray UI:**
   - session → process tree (sortable by CPU/memory/IO/idle);
   - orphan tab;
   - per-session and per-process history charts over the retention window;
   - kill / kill-tree;
   - alerts as toasts.
3. **Broker:** bookings and `request_resources`; the `PreToolUse` hook; suggested parallelism.
4. **ETW:** process start/stop for short-lived processes and exact lineage; per-process file and network
   activity (Process Monitor-style detail).
5. **Containment (needs a separate decision):** optional per-session job objects with memory caps;
   job-object kill-on-close guidance for the `boatmodel-mcp` servers.

## 16. Left to the builder

- Activity thresholds, pressure-level formula and suggested-parallelism formula (configurable defaults).
- Segment binary layout, and the choice of SQLite library.
- Default port (configurable).
- Chart library for the tray UI.
- Whether the desktop app's session titles are readable.

## 17. Calls made by Claude (not explicitly decided by the user)

- Default retention 24 h on disk (user asked for ≥ 2 h).
- 2 s sampling; 10 min full-resolution in-memory ring.
- Watched-image orphan alert after 30 min idle.
- Kill only from tray/CLI; no kill over MCP. Kills run in the client with the user's rights, so the service
  has none. The CLI inside a Claude session is limited to its own session.
- "Session" = desktop session row (host session id); Claude Code session ids are sub-sessions.
- Orphan = owning session ended. A dead parent alone only marks a process "detached".
- Bookings are consumed by the session's growth above its baseline; no queue, "wait" with retry-after.
- Commit headroom as the primary pressure signal.
- Self-contained publish; keep last 3 versions.
- Stop/swap/restart upgrade rather than a split collector.
