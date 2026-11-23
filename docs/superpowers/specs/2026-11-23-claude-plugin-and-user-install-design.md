# Claude Code plugin and user-only install: design

Date: 2026-11-23. Status: draft for approval.

## Intent

ProcessTracker is for **anyone** who runs heavy computational work through Claude Code agents on a Windows machine.
It is not tied to any company. Agents do not pace themselves. They start builds, simulations and engines in parallel
until the machine is unusable for anything else, and the user cannot tell which session owns what. ProcessTracker
gives the user **visibility** (which session runs what, at what cost) and gives agents the information to **pace
themselves**.

Today you get it through an MSI (winget pending), and the Claude side is wired up by the installer. This design makes
a **Claude Code plugin** the main way in. The user adds one marketplace and installs one plugin. From then on they
have the tracker, the MCP tools, and load warnings at the moment agents start heavy work. Everything stays current
without them doing anything.

Trust is a first-class requirement. This is a tool people download from the internet that reads system counters and
other processes' environments. So the default install must not ask for admin rights. It runs as the user, lives in
the user's profile, and can be removed by the user.

## Goals

1. **One-step setup from Claude Code:**
   - `/plugin marketplace add worc4021/ProcessTracker`
   - `/plugin install process-tracker@processtracker`
   - `/process-tracker:install`

   That gives a working tracker without an admin prompt.
2. **User-only mode (new).** The existing service binary runs as the logged-in user, starts at logon and updates itself
   from GitHub Releases. It needs no admin rights at any point.
3. **The machine-wide service stays.** It is optional, for full visibility (elevated and other users' processes). It is
   installed with the MSI or `winget install worc4021.ProcessTracker`. The plugin works with either install type.
4. **Warn, never block.** A PreToolUse hook tells the agent about the machine state when it is about to start heavy
   work. The agent decides, weighing what the user told it (for example "this is a remote build box" vs "I'm working
   on this machine"). The hook includes how long ago the user last touched keyboard or mouse, where available, as the
   foreground/remote signal.
5. **Session context at start.** A SessionStart hook gives each session a one-paragraph load summary and the
   load-check rule. If no tracker is running, it tells the agent to offer `/process-tracker:install` to the user.
6. **Self-updating everywhere:**
   - user-only installs update from GitHub Releases;
   - the MSI's daily task tries winget first and falls back to GitHub Releases, so it works even if winget never lists
     the package or policy blocks it;
   - the plugin itself updates through its marketplace.

## Non-goals

- Blocking or asking before commands (decided: warn only; the agent is responsible).
- Attributing elevated or other users' processes in user-only mode (they still count toward the load, unattributed).
- A private winget source (decided against: needs a hosted search API and admin `winget source add`, and company policy
  often blocks extra sources).
- Submitting to Anthropic's official plugin directory (later).
- macOS/Linux; agents other than Claude Code (they keep the MCP server instructions and `proctrack load --json`).
- Compacting MCP outputs (`get_my_session` ~52 KB). That is tracked separately.

## Anti-goals (failure even if it works)

- The plugin installs or updates software, or triggers a UAC prompt, **without the user running a plugin command**.
  The SessionStart hook only *tells*. The exception is the self-update of an install the user already made.
- Any process or session is killed or slowed by ProcessTracker. It stays read-only towards other processes. Kills stay
  the user's, via `proctrack kill`.
- A hook adds noticeable latency or noise to ordinary commands (`git status`, `ls`), or fails a tool call because the
  tracker is down.
- Anything leaves the machine other than downloading releases from GitHub.

## Constraints and facts (from the Claude Code docs, checked 2026-11-23)

- **Plugin layout.** Everything sits at the plugin root:
  - `.claude-plugin/plugin.json` (optional manifest);
  - `.mcp.json`;
  - `hooks/hooks.json`;
  - `commands/*.md`, `skills/`, `bin/`.
- **Plugin paths.** `${CLAUDE_PLUGIN_ROOT}` is the installed version's folder and changes on every update, so nothing
  persistent goes there. `${CLAUDE_PLUGIN_DATA}` (`%USERPROFILE%\.claude\plugins\data\<id>\`) persists across updates.
- **Marketplace.** A repo with `.claude-plugin/marketplace.json` (`name`, `owner`, `plugins[]` with `source`).
  - Third-party marketplaces are **not** auto-updated by default. The user enables it per marketplace in `/plugin` →
    Marketplaces.
  - When enabled, updates are fetched up to ~10 minutes after the first message of a session, and apply after
    `/reload-plugins` or a restart.
  - The plugin `version` in `plugin.json` controls updates: users get one only when the string changes.
- **No plugin install or update hook exists.** SessionStart is the earliest code a plugin runs.
- **Hooks.**
  - SessionStart stdout (exit 0) or `hookSpecificOutput.additionalContext` is added to the session's context.
  - PreToolUse can return `hookSpecificOutput.additionalContext`.
  - A failed or timed-out PreToolUse hook does not block the call unless `onFailure: "block"`.
  - Hooks support exec form (`command` + `args`, no shell) and `"shell": "powershell"`.
  - Default timeout is 600 s, configurable.
- **Plugin `.mcp.json`.**
  - It supports `{"type":"http","url":...,"headers":{...}}` with `${VAR:-default}` expansion in `url` and `headers`.
  - The server is named `plugin:process-tracker:process-tracker`, and its tools
    `mcp__plugin_process-tracker_process-tracker__*`.
- **Unprivileged access is enough.**
  - `NtQuerySystemInformation(SystemProcessInformation)`, used by the collector, returns every process's CPU, memory
    and I/O counters to an unprivileged user.
  - Reading another process's PEB environment needs `PROCESS_VM_READ`. A normal user has it for their own
    non-elevated processes, which covers everything a Claude session starts unless an agent elevates.
  - `GetExtendedTcpTable` (caller identity) works unprivileged.
- **Existing single-instance guard.** The service takes the mutex `Global\ProcessTracker.Service` (not in `--dev`). An
  unprivileged process can create and hold a `Global\` mutex.
- **Dev mode is already close.** `--console --dev` runs the service unelevated with data in
  `%LOCALAPPDATA%\ProcessTracker-dev`. User-only mode is close to that, on the production port.

## Approaches considered

| | Approach | Trade-off |
|---|---|---|
| **A (chosen)** | User-only install by default (plugin command, no admin); machine-wide service optional via MSI/winget; plugin works with whichever runs | Two install types to maintain, but the user-only one is much simpler than the MSI (no SYSTEM, no protected data folder, no installer) |
| B | Machine-wide service by default; user-only only if the user declines admin | Leads with the admin prompt the user population is uncomfortable with |
| C | User-only only; drop MSI/service | Simplest, but throws away the finished service/installer and full visibility, and the pending winget submission |

On enforcement, the options were warn, ask the user at Critical, or block at Critical. **Warn** was chosen: the agent
is the one doing the work and is best placed to weigh the user's stated needs. The hook's job is to make sure it
knows.

## Design

### 1. One binary, three run modes

`ProcessTracker.Service.exe` gains a **user mode** next to service mode and console/dev mode:

| | Service (MSI) | User (new) | Dev (`--console --dev`) |
|---|---|---|---|
| Runs as | LocalSystem | the logged-in user | the developer |
| Started by | SCM, automatic | per-user logon task | by hand |
| Port | 47811 | 47811 | 47812 |
| Data | `%ProgramData%\ProcessTracker` | `%LOCALAPPDATA%\ProcessTracker` | `%LOCALAPPDATA%\ProcessTracker-dev` |
| Settings file | `%ProgramData%\ProcessTracker\appsettings.json` | `%LOCALAPPDATA%\ProcessTracker\appsettings.json` | none |
| Single-instance mutex | `Global\ProcessTracker.Service` | the same mutex | none |
| Logs | Application event log | a rolling log file in the data folder (the event source needs admin to create) | console |
| Window | none | **none**: must not show a console window at logon | console |
| Attribution | all processes | the user's own non-elevated processes; others count toward load as unattributed | as user |
| Last user input | not available (session 0) | `GetLastInputInfo` | `GetLastInputInfo` |

The shared mutex makes the two production modes mutually exclusive machine-wide: one instance serves port 47811.

**The machine service always wins.** *(Builder-check answers; my calls.)*
- **User mode yields.** It checks the SCM, which needs no admin, for an installed `ProcessTracker` service:
  - at start: if the service is installed, it exits quietly and logs it;
  - before each self-update: if the service is installed, it skips the update.

  A dormant user-only install therefore neither runs nor downloads anything.
- **The MSI stops user-mode instances before starting the service.** Install (and `upgrade.ps1`) signal every running
  user-mode instance, of any user, to exit through a named event `Global\ProcessTracker.UserMode.Exit`. Each instance
  waits on it and persists state on exit. The MSI waits up to 10 s for the mutex to come free, then starts the service
  as today (`Vital`, rollback on failure).
- **The MSI does not delete user-only installs** (they belong to the users). It doesn't need to, because they yield.
- **`/process-tracker:install`, when it finds the service:** reports that the machine service is in use and offers to
  remove a leftover user-only install with `proctrack uninstall-user`.
- **After an MSI uninstall:** a remaining user-only install takes over again at its owner's next logon. That is the
  intended fallback.

**User mode serves only its owner.** On a machine with several users (RDP, a shared build box), the first user's
instance holds the port. Other users' hook payloads and MCP calls would otherwise reach a process running as someone
else, which would leak their command lines and give them a view without their own sessions. So in user mode, every
`/api` and `/mcp` request is checked: the caller's TCP owner process (already resolved by `CallerResolver`) must run
as the same user SID as the instance. Anything else gets `403` with a plain message: "this ProcessTracker belongs to
another user; ask an admin to install the machine-wide service". The SessionStart hook turns that 403 into one line of
context. Multi-user machines are what the machine-wide service is for.

`/api/health` reports the mode (`service` | `user` | `dev`) and, in user mode, the owner's user name, so the plugin and
CLI can tell which one answers. `/api/health` itself stays readable by everyone, so another user's SessionStart can
explain the 403.

### 2. User-only install layout and lifecycle

- **Files** go in `%LOCALAPPDATA%\Programs\ProcessTracker\`:
  - versioned folders `app-<version>\`, each a copy of the release zip (service exe and `bin\proctrack.exe`);
  - a small `current.txt` naming the active version.

  No junctions: the phase 1 junction updater was dropped for good reasons.
- **Start at logon:** a per-user scheduled task `\ProcessTracker\User-<SID>`. A non-admin user can create a task that
  runs as themselves (interactive token) without elevation. It has two triggers:
  - at logon, which starts user mode;
  - daily, plus 10 minutes after logon, which runs `proctrack self-update`.
- **`proctrack self-update`** (new command, user mode only):
  1. Read the latest GitHub release (REST API, unauthenticated).
  2. Compare versions. Nothing to do if this version is current.
  3. Download `ProcessTracker-<v>-x64.zip` and `SHA256SUMS.txt`. Verify the hash; any mismatch means abort.
  4. Extract to `app-<v>\`.
  5. Ask the running instance to shut down gracefully. Persisting state on graceful stop already exists. User mode
     needs a shutdown path that does not need admin; see below.
  6. Switch `current.txt`, start the new version, and wait for `/api/health` to report it.
  7. Keep the previous `app-*` folder for one rollback and delete older ones.
  8. On a failed health check, switch back.

  The outcome is logged to the data folder's log file.
- **Graceful shutdown in user mode:** a loopback-only `POST /api/admin/shutdown` that is honoured **only in user mode**
  and only when the caller's TCP owner process runs as the same user. The machine service never exposes it. A simpler
  equivalent is acceptable, for example a named event the instance waits on.
- **Uninstall** (`proctrack uninstall-user`, also behind the plugin command):
  - stop the instance and delete the task and the `Programs\ProcessTracker` folder;
  - keep `%LOCALAPPDATA%\ProcessTracker` unless `--purge-data`.

### 3. Release changes

- Each release additionally uploads `ProcessTracker-<v>-x64.zip`: the same self-contained publish the MSI is built
  from, with `bin\proctrack.exe`. It is listed in `SHA256SUMS.txt`.
- The release smoke test also covers the zip:
  - extract, start in user mode, check `/api/health` reports mode `user`;
  - `proctrack self-update` against a fake "latest" (local file URL or mock) verifies hash checking and the version
    switch.
- **The marketplace serves only released plugin content.** *(Builder-check answer; my call.)*
  `.claude-plugin/marketplace.json` names the plugin by a git source pinned to a release tag: `github`/`git-subdir` with
  `ref: v<version>` and path `plugin`. It does not use `./plugin` from the default branch, so unreleased hook changes
  on main never reach users.
  - The release workflow, **as its last step, after the zip and MSI are uploaded**, commits the new tag into
    `marketplace.json` on main. It does this with the workflow token, with a `chore:` message that release-please
    ignores for versioning.
  - `plugin.json` `version` is set to the release version in that same commit, not by release-please. So the plugin
    can never be ahead of a downloadable release, and a failed release job leaves the plugin on the previous release.
- **Version skew is tolerated, not prevented:**
  - New service API is **additive only**. A field is never removed or renamed in an incompatible way.
  - A plugin talking to an older tracker degrades silently: `curl -f` gets a 404 for an unknown hook endpoint, so no
    context is added; SessionStart treats a missing `mode` as `service`.
  - A tracker newer than the plugin is normal, because third-party marketplace auto-update is off by default.
    SessionStart then adds one line suggesting the user enable auto-update for the marketplace.
  - Tests cover the hook endpoints with the previous release's payloads.

### 4. MSI auto-upgrade gets a GitHub fallback

`upgrade.ps1` (SYSTEM, daily):

1. Try winget as today.
2. If winget is missing, or knows no `worc4021.ProcessTracker` package (event 1003, or the no-package code), fall back
   to the latest GitHub release: download the MSI and `SHA256SUMS.txt`, verify, then
   `msiexec /i … /qn /norestart`.
3. Log a new event id **1004** "upgraded from GitHub release", or 1002 on failure.

A machine installed from the MSI therefore stays current even if winget never lists the package. Same-version
reinstalls are skipped by comparing the installed `ProductVersion`.

**Trust decision (yours, 2026-11-23): automatic updates trust a GitHub release everywhere, including the SYSTEM
fallback.**
- **The only check** is the SHA-256 against `SHA256SUMS.txt` from the same release, and releases are unsigned. So
  whoever controls the GitHub repo or its release workflow can run code as SYSTEM on every MSI machine within a day,
  and as the user on every user-only machine.
- **Alternatives considered:**
  - trust GitHub only for user-only installs;
  - require Authenticode signing for every automatic update.

  Accepted to keep updates working without winget and without a paid certificate.
- **Mitigations the builder must keep:**
  - downloads only from `https://github.com/worc4021/ProcessTracker/releases/` (the URL is hard-coded; redirects are
    allowed only to GitHub's release asset host);
  - the hash is checked before anything is extracted or run;
  - the version must be strictly newer than the installed one, so no downgrades through the fallback;
  - when signing is later switched on (`SIGNING_ENABLED`), both updaters additionally require a valid signature from
    the pinned publisher. That turns the hash-only path off for good.
- Protect the repo accordingly: 2FA on the account, branch protection on `main`, and release workflow permissions kept
  minimal.

### 5. The plugin

Lives in this repo:

```
.claude-plugin/marketplace.json        marketplace "processtracker", one plugin, source ./plugin
plugin/.claude-plugin/plugin.json      name "process-tracker", version = release version
plugin/.mcp.json                       process-tracker → http://127.0.0.1:47811/mcp, header X-Claude-Session: ${CLAUDE_CODE_HOST_SESSION_ID:-}
plugin/hooks/hooks.json                SessionStart + PreToolUse
plugin/commands/install.md             /process-tracker:install
plugin/commands/uninstall.md           /process-tracker:uninstall
plugin/commands/status.md              /process-tracker:status
plugin/scripts/*.ps1                   install/uninstall/session-start helpers
```

- **MCP.** The plugin's `.mcp.json` is the one MCP entry. To avoid duplicate tools:
  - the MSI **stops registering** the user-scope `process-tracker` MCP entry on first install;
  - `proctrack mcp register` stays for people who don't use the plugin;
  - `/process-tracker:install` and SessionStart detect a leftover user-scope `process-tracker` entry and tell the user
    how to remove it (`proctrack mcp unregister`).

  The MCP server instructions from PR #4 stay the rule for any MCP client, but **they are rewritten to the warn-only
  policy** so the plugin's hook text and the server instructions never contradict each other. *(Builder-check answer;
  follows your "we warn the agent" decision.)* The new wording:
  - Low/Moderate: pace to `suggestedParallelism`.
  - High/Critical: you are close to making this machine unusable. Weigh what the user has told you about using it
    (foreground work vs a remote or dedicated box). If you don't know and it's Critical, ask.

  `ProcessTrackerTools.ServerInstructions` and its test change accordingly.
- **`/process-tracker:install`** (a command prompt that has Claude run `plugin/scripts/install-user.ps1` and report):
  1. If the machine service already answers on 47811 (`/api/health` mode `service`), do nothing but report it.
  2. Otherwise download and verify the latest zip into the layout above, create the per-user task, start user mode,
     wait for health, and report the version.

  The script needs no admin rights and must refuse to run elevated: a user install made from an elevated shell would
  land in the wrong context. Re-running it repairs or updates the install.
- **`/process-tracker:uninstall`** runs `proctrack uninstall-user` and reports. It never touches an MSI install; for
  that it tells the user to use winget or Apps & Features.
- **`/process-tracker:status`** shows mode, version, pressure, and the top sessions (`proctrack load`).
- **SessionStart hook.** PowerShell via `"shell": "powershell"` or exec form; once per session, so ~0.5 s of startup
  cost is acceptable. Timeout 10 s.
  - Tracker reachable and at least as new as the plugin's version: print a short context block with:
    - the mode, the pressure and suggested parallelism;
    - the top 3 sessions by memory;
    - the user's last input age;
    - one line of the rule ("before heavy local work check `get_machine_load` and pace to `suggestedParallelism`;
      the PreToolUse hook will warn you at High/Critical").
  - Reachable but older than the plugin: the same block plus a line saying an update is due. User mode updates on its
    next daily run; the user can run `/process-tracker:install` now.
  - Not reachable: one line telling the agent that ProcessTracker is not running and to offer the user
    `/process-tracker:install` once. If the user-mode install exists but isn't running, the hook may start it, since
    that is the user's own install.
  - Never installs or elevates.
- **PreToolUse hook**, matcher `Bash|PowerShell|Task|Agent`. It must be fast on every call, so: exec form
  `curl.exe` (in-box on Windows 10 1803+) posting the hook's stdin JSON to `POST /api/hooks/pre-tool-use`. Timeout 3 s;
  a failure (tracker down) is silently non-blocking, never `onFailure: block`. The **service** decides, so the logic is
  tested C#:
  - Classify the command as **heavy** or not, using a built-in pattern list:
    - builds: `cmake --build`, `msbuild`, `dotnet build|test|publish`, `make`, `ninja`, `cargo build|test`, `npm|pnpm|yarn run build|test`;
    - simulation and engine launches: `matlab`, `dymola`, `dymosim`, `python … ` running scripts;
    - explicit parallelism: `-j`, `-m:`, `--parallel`, `Start-Job`, `ForEach-Object -Parallel`;
    - `Task`/`Agent` tool calls, because they spawn more work.

    The list is configurable in the settings file.
  - Non-heavy, or pressure Low: respond `{}`. No output means no noise.
  - Heavy and Moderate: `additionalContext`, one line: "machine at Moderate; run at most N parallel tasks".
  - Heavy and High or Critical: `additionalContext` with:
    - pressure, CPU busy, commit headroom;
    - suggested parallelism (1);
    - the top sessions (title, CPU, memory), excluding none (the caller is marked);
    - the user's last input age (or "unknown" in service mode);
    - "decide based on what the user has told you about using this machine; if unsure at Critical, ask them."

    Never `permissionDecision`.
  - Rate limit: the same session gets the full High/Critical block at most once per 60 s, and a one-liner in between,
    so a burst of commands does not flood the context.
- The PreToolUse endpoint is unauthenticated loopback like the rest of the API. It only reads the posted payload and
  returns text, with the same Host/Origin guard.

### 6. Docs

- The README leads with the plugin route. MSI/winget is described as the "full visibility" option.
- The README keeps the winget publishing section and adds:
  - enabling marketplace auto-update (`/plugin` → Marketplaces → process-tracker → auto-update);
  - the user-only layout;
  - `self-update`, uninstall;
  - what user-only mode can and cannot see.

## Left to the builder

- Exact JSON field names of the new endpoints and the hook payload parsing (follow the Claude Code hook input schema:
  `tool_name`, `tool_input.command`, `session_id`).
- Rolling log file format and size cap.
- How the windowless user-mode start is achieved:
  - an `OutputType=WinExe` variant;
  - or a task action that starts hidden.

  It must not flash a console at logon.
- The final heavy-command pattern list and its matching (case-insensitive, first token plus flags). Keep false
  positives cheap: they only add one line of context.
- Whether `/process-tracker:install` downloads the zip through `proctrack` (if present) or the PowerShell script (a
  first install has no `proctrack`).
- Test strategy details. At minimum:
  - unit tests for heavy-command classification and the rate limiter;
  - hook endpoint integration tests per pressure;
  - user-mode health/mode tests;
  - self-update hash-mismatch and rollback tests;
  - `upgrade.ps1` fallback under `-WhatIf`.
