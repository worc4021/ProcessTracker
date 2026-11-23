# Claude Code Plugin and User-Only Install Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship a Claude Code plugin that installs a no-admin, self-updating user-mode ProcessTracker. The plugin warns
agents about machine load through hooks, and works with the existing machine-wide service too.

**Architecture:**
- The service exe gains a `--user` run mode: same binary, `%LOCALAPPDATA%` data, owner-only API, a daily self-update
  from GitHub Releases.
- The hook logic lives in the service as two new endpoints. The plugin stays thin: `.mcp.json`, `hooks.json` (curl for
  PreToolUse, a small PowerShell script for SessionStart), three slash commands and an install script.
- The MSI drops its MCP registration, stops user-mode instances before starting the service, and falls back to GitHub
  Releases when winget can't upgrade.

**Tech Stack:**
- .NET 10 (SDK pinned in `global.json`), ASP.NET Core minimal APIs, `ModelContextProtocol.AspNetCore` 2.4.0,
  System.CommandLine 2.0.15, xUnit v3 (MTP);
- WiX v6;
- PowerShell 5.1-compatible scripts;
- GitHub Actions; Claude Code plugin format.

**Spec:** `docs/superpowers/specs/2026-11-23-claude-plugin-and-user-install-design.md`

## Global Constraints

- **Ports.** Production port is `47811` for both service and user mode; dev is `47812`. Nothing else listens, and
  only on loopback. The existing `LoopbackGuard` stays in force.
- **Single instance.** The service and user mode share the mutex `Global\ProcessTracker.Service`. Dev mode takes no
  mutex.
- **The machine service always wins.**
  - User mode exits at start if a `ProcessTracker` Windows service is installed.
  - User mode skips self-update when one is installed.
- **User mode serves only its owner.** Every request except `GET /api/health` must come from a process running as the
  instance's user SID, otherwise `403` with body exactly:
  `this ProcessTracker belongs to another user; ask an admin to install the machine-wide service`.
- **Warn, never block.** No hook response ever contains `permissionDecision`. A hook failure never blocks a tool
  call: no `onFailure: "block"`.
- **The plugin never installs, updates or elevates on its own.** Only `/process-tracker:install` and
  `/process-tracker:uninstall` change software. The SessionStart hook may *start* an existing user-mode install of the
  same user.
- **Downloads.**
  - Releases come only from `https://api.github.com/repos/worc4021/ProcessTracker/releases/latest` and its assets.
  - Redirects are allowed only to `github.com`, `objects.githubusercontent.com` and
    `release-assets.githubusercontent.com`.
  - The SHA-256 must match the release's `SHA256SUMS.txt` before anything is extracted or run.
  - A release is used only if its version is strictly newer than the installed one.
  - GitHub API requests send `User-Agent: ProcessTracker/<version>`.
- **Release assets:** `ProcessTracker-<X.Y.Z>-x64.msi`, `ProcessTracker-<X.Y.Z>-x64.zip` and `SHA256SUMS.txt`, with
  one line per asset in the `<hex>  <name>` format.
- **API changes are additive only.** New fields and endpoints; nothing renamed or removed.
- **User-mode layout:**
  - binaries in `%LOCALAPPDATA%\Programs\ProcessTracker\app-<X.Y.Z>\` (service exe at the root, `bin\proctrack.exe`);
  - data in `%LOCALAPPDATA%\ProcessTracker\`, logs in `%LOCALAPPDATA%\ProcessTracker\logs\`;
  - the per-user task is `\ProcessTracker\User-<SID>`.
- **Exit event.** The user-mode exit event is `Global\ProcessTracker.UserMode.Exit`. Its DACL grants access to the
  owner, SYSTEM and Administrators only.
- **Plugin naming:**
  - marketplace name `processtracker`, plugin name `process-tracker`;
  - MCP server key `process-tracker`, URL `http://127.0.0.1:47811/mcp`, header
    `X-Claude-Session: ${CLAUDE_CODE_HOST_SESSION_ID:-}`.
- **Tests and conventions:**
  - Tests run with `dotnet test --solution ProcessTracker.slnx -- --filter-not-trait "Category=Perf"`.
  - Commits follow conventional commits and end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- **Spec deviations (decided here):**
  - Self-update runs **inside** the user-mode service (a background worker), not as `proctrack self-update`. A daily
    console process would flash a window.
  - Graceful stop uses the named exit event, not a `POST /api/admin/shutdown`.
  - `current.txt` is dropped: the per-user task's action path is the pointer to the active version.
  - The uninstall command is `proctrack user uninstall`, not `proctrack uninstall-user`.
  - **Not in this plan:** the spec's "require a valid signature once signing is enabled". Releases are unsigned today,
    so there is nothing to verify against. It becomes a follow-up when `SIGNING_ENABLED` is turned on. Until then,
    `ReleaseClient.DownloadVerifiedAsync` is the single place where that check will be added.

## Review Focus

1. **Takeover failure.** The new version crashes before its first tick during self-update. Expected: the previous
   version is running again within 60 s and the task still points at it. Pinned by
   `Takeover_NewVersionFailsBeforeHealthy_RestartsPrevious` (Task 6).
2. **Two users on one machine.** User B's Claude sends hook payloads to user A's instance. Expected: 403 with the
   exact message, and B's command text is never processed or logged. Pinned by
   `OwnerGate_OtherUser_Gets403AndBodyNotRead` (Task 2).
3. **GitHub returns odd data:** no assets, a missing `SHA256SUMS.txt`, a sums file without our asset, a tag
   `v0.3.0-rc1`, or a rate-limit 403. Expected: no download, no change, one log line. Pinned by the
   `ReleaseClient_*` tests (Task 4).
4. **Tracker down or old while hooks run.** Expected: tool calls proceed with no added latency beyond curl's 2 s
   connect cap, and no error text in the transcript. Pinned by a plugin check in Task 8 (`curl` against a closed port
   exits non-zero, silently) and by `PreToolUse_UnknownPayload_ReturnsEmptyObject` (Task 7).
5. **Install from an elevated shell.** `/process-tracker:install` run from an admin terminal. Expected: refused with
   a clear message, nothing written. Pinned by `UserInstall_Elevated_Refuses` (Task 5) and by the script's own
   elevation check (Task 8).

---

### Task 1: User run mode, mode in health, no console window

**Files:**
- Modify: `src/ProcessTracker.Service/ServiceOptions.cs`, `src/ProcessTracker.Service/Program.cs`,
  `src/ProcessTracker.Service/ProcessTracker.Service.csproj`, `src/ProcessTracker.Core/Contracts/Dtos.cs`
  (`HealthDto`), `src/ProcessTracker.Service/Api/RestEndpoints.cs` (`Health`)
- Create: `src/ProcessTracker.Service/RunMode.cs`, `src/ProcessTracker.Service/Logging/FileLoggerProvider.cs`,
  `src/ProcessTracker.Native/ServiceProbe.cs`
- Test: `tests/ProcessTracker.Service.Tests/RunModeTests.cs`, `tests/ProcessTracker.Service.Tests/RestEndpointsTests.cs`

**Interfaces:**
- Produces:
  - `public enum RunMode { Service, User, Dev }`
  - `ServiceOptions.Mode` (RunMode)
  - `ServiceOptions.Resolve(IConfiguration config, IConfiguration commandLine, RunMode mode)`, replacing the
    `bool dev` parameter. All callers are updated.
  - `HealthDto(string Version, DateTime StartedUtc, DateTime? LastTickUtc, double? TickAgeSeconds, string Mode, string? Owner)`.
    `Mode` is `"service" | "user" | "dev"`; `Owner` is `DOMAIN\user` in user mode, otherwise null.
  - `ServiceProbe.IsMachineServiceInstalled() : bool`, which opens the SCM read-only and looks for service
    `ProcessTracker`.
  - Exit code `4` = "user mode refused: machine service installed".

- [ ] **Step 1: Write failing tests** in `RunModeTests.cs`:
  - `Resolve_UserMode_UsesLocalAppDataAndProductionPort`: `Port == 47811`;
    `DataDir == Path.Combine(LocalApplicationData, "ProcessTracker")`.
  - `Resolve_ServiceMode_UsesProgramData`: `DataDir == Path.Combine(CommonApplicationData, "ProcessTracker")`.
  - `Resolve_DevMode_Unchanged`: port 47812, `ProcessTracker-dev`.
  - `ParseMode_UserFlag`: args `["--user"]` give `RunMode.User`; `["--console","--dev"]` give `Dev`; `[]` gives
    `Service`.
  - `ParseMode_UserAndDev_IsError`: `["--user","--dev"]` throws `InvalidOperationException`.

  In `RestEndpointsTests.cs`, `Health_ReportsMode`: TestApp (dev) returns `"mode":"dev"` and no `owner`.
- [ ] **Step 2:** Run `dotnet test --project tests/ProcessTracker.Service.Tests`. Expected: compile errors or failures
  on the new names.
- [ ] **Step 3: Implement.**
  - **`RunMode` parsing:** `--user` maps to `--User=true`, like the existing `--dev` mapping. Add
    `static RunMode ParseMode(IConfiguration mode)`.
  - **Settings file:** in user mode, the second settings file is `%LOCALAPPDATA%\ProcessTracker\appsettings.json`.
  - **Mutex:** taken in Service and User modes.
  - **User-mode startup:** before building the host, if `ServiceProbe.IsMachineServiceInstalled()` is true, log one
    line to the file log and `return 4`.
  - **Logging:** user mode logs through `FileLoggerProvider` to `logs\processtracker.log`, Information and above,
    rolling at 5 MB and keeping 2 files. No `AddWindowsService` and no EventLog in user mode.
  - **No console window:** set `<OutputType>WinExe</OutputType>`. In `--console`/dev mode, call
    `AttachConsole(ATTACH_PARENT_PROCESS)`; if that fails, call `AllocConsole()` before logging is configured.
  - **Health:** `Health()` fills `Mode` and `Owner` (`WindowsIdentity.GetCurrent().Name` in user mode).
- [ ] **Step 4:** Run the service tests again. Expected: PASS. Also run
  `dotnet run --project src/ProcessTracker.Service -- --console --dev` by hand. Expected: log lines still appear in
  the terminal.
- [ ] **Step 5: Commit** `feat: user run mode (--user) with mode in /api/health`

### Task 2: Owner-only access in user mode

**Files:**
- Create: `src/ProcessTracker.Service/OwnerGate.cs`, `src/ProcessTracker.Native/ProcessOwner.cs`
- Modify: `src/ProcessTracker.Service/Program.cs` (middleware after the LoopbackGuard middleware),
  `tests/ProcessTracker.Service.Tests/Fakes/TestApp.cs` (allow mode and gate overrides)
- Test: `tests/ProcessTracker.Service.Tests/OwnerGateTests.cs`

**Interfaces:**
- Consumes: `ServiceOptions.Mode` (Task 1); `TcpOwnerLookup.OwnerKeyOfLoopbackClient` (existing).
- Produces:
  - `public interface IRequestOwnerGate { bool IsOwner(HttpContext ctx); }`
  - `OwnerGate` (production): resolves the TCP owner PID, then `ProcessOwner.SidOf(int pid) : SecurityIdentifier?`,
    and compares it with the current user's SID. An unknown owner means not the owner.
  - `OwnerGate.ForbiddenMessage` (const string, exact text in Global Constraints).

- [ ] **Step 1: Write failing tests** with TestApp in user mode and a fake gate:
  - `OwnerGate_OtherUser_Gets403AndBodyNotRead`: POST `/api/hooks/pre-tool-use` with a body. Expect 403, body ==
    `ForbiddenMessage`, and the fake hook handler records zero calls. It must use the Task 7 endpoint name; until
    Task 7 exists, use `GET /api/load` and retarget in Task 7.
  - `OwnerGate_OtherUser_HealthStillReadable`: `GET /api/health` returns 200 with `"mode":"user"`.
  - `OwnerGate_OtherUser_McpForbidden`: POST `/mcp` initialize returns 403.
  - `OwnerGate_Owner_Passes`: `GET /api/load` returns 200.
  - `OwnerGate_ServiceMode_NotApplied`: a fake gate returning false in service mode still lets `/api/load` return 200.
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3: Implement.**
  - **Middleware:** registered only when `Mode == User`. It runs before routing reads the body. It skips the gate
    only for `GET /api/health`.
  - **`ProcessOwner.SidOf`:** `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)`, then `OpenProcessToken(TOKEN_QUERY)`,
    then `GetTokenInformation(TokenUser)`.
- [ ] **Step 4:** Run. Expected: PASS.
- [ ] **Step 5: Commit** `feat: user mode serves only the user that runs it`

### Task 3: Exit event, takeover start, stop-user-instances

**Files:**
- Create: `src/ProcessTracker.Native/UserModeSignal.cs`, `src/ProcessTracker.Service/UserModeExitWatcher.cs`
- Modify: `src/ProcessTracker.Service/Program.cs` (`SingleInstance.TryAcquire(TimeSpan wait)`),
  `src/ProcessTracker.Cli/Commands/AdminCommand.cs`
- Test: `tests/ProcessTracker.Native.Tests/UserModeSignalTests.cs`,
  `tests/ProcessTracker.Cli.Tests/Admin/StopUserInstancesTests.cs`

**Interfaces:**
- Produces:
  - `UserModeSignal.CreateForInstance() : EventWaitHandle`. Creates `Global\ProcessTracker.UserMode.Exit` (manual
    reset) with DACL owner+SYSTEM+Administrators, and resets it on creation.
  - `UserModeSignal.TrySignal() : bool`. Opens the event if it exists and sets it; false if there is none.
  - `UserModeExitWatcher : BackgroundService` (user mode only). It waits on the event and calls
    `IHostApplicationLifetime.StopApplication()`; state persists through the existing graceful-stop path.
  - `SingleInstance.TryAcquire(TimeSpan wait) : Mutex?`. The existing call passes `TimeSpan.Zero`.
  - Command-line flag `--takeover <previousAppDir>` (user mode): signal the exit event, then `TryAcquire(30 s)`.
  - `proctrack admin stop-user-instances`: `TrySignal()`, then wait until `Global\ProcessTracker.Service` can be
    acquired and released, up to 10 s. Exit 0 if free or no event existed; exit 1 on timeout.

- [ ] **Step 1: Write failing tests:**
  - `Signal_SetsEventCreatedByInstance`: create the event, `TrySignal()` returns true, the handle is signalled.
  - `Signal_NoInstance_ReturnsFalse`.
  - `CreateForInstance_ResetsStaleSignal`: a set event is reset by a new `CreateForInstance`.
  - `StopUserInstances_NoEvent_ExitsZero`.
  - `StopUserInstances_MutexHeld_TimesOut`: a test-held mutex under a test-specific name injected through a
    constructor parameter gives exit 1 after the short injected timeout.
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3: Implement** the signal (`EventWaitHandleAcl.Create` with an `EventWaitHandleSecurity`), the watcher,
  the takeover flag, and the admin command. The names are injectable for tests and the defaults are the constants
  above.
- [ ] **Step 4:** Run. Expected: PASS.
- [ ] **Step 5: Commit** `feat: user-mode exit signal, takeover start and admin stop-user-instances`

### Task 4: GitHub release client with verified downloads

**Files:**
- Create: `src/ProcessTracker.Core/Updates/ReleaseClient.cs`, `src/ProcessTracker.Core/Updates/Sha256Sums.cs`,
  `src/ProcessTracker.Core/Updates/ReleaseInfo.cs`
- Test: `tests/ProcessTracker.Core.Tests/Updates/ReleaseClientTests.cs`,
  `tests/ProcessTracker.Core.Tests/Updates/Sha256SumsTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record ReleaseInfo(Version Version, string Tag, IReadOnlyDictionary<string, Uri> Assets)`
  - `public sealed class ReleaseClient(HttpMessageHandler handler, string userAgentVersion)`:
    - `Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)`: null on 403/404/5xx, a missing tag, or a tag not
      matching `^v\d+\.\d+\.\d+$`.
    - `Task<string?> DownloadVerifiedAsync(ReleaseInfo r, string assetName, string targetDir, CancellationToken ct)`:
      downloads `SHA256SUMS.txt` and the asset into `targetDir`. It returns the asset path only when the hash matches,
      otherwise deletes both files and returns null.
  - `public static class Sha256Sums { static IReadOnlyDictionary<string,string> Parse(string text); }`: lower-case hex
    by file name; ignores blank lines; throws `FormatException` on a malformed line.
  - `public static bool IsNewer(Version candidate, Version installed)`: strict `>`.
  - Redirects are followed manually (handler with `AllowAutoRedirect = false`), at most 5, and only to the allowlisted
    hosts in Global Constraints.

- [ ] **Step 1: Write failing tests** using a fake `HttpMessageHandler` that serves canned responses:
  - `ReleaseClient_LatestParsesTagAndAssets`: tag `v0.3.0` gives `Version(0,3,0)` and three assets.
  - `ReleaseClient_PrereleaseTag_ReturnsNull`: `v0.3.0-rc1`.
  - `ReleaseClient_RateLimited403_ReturnsNull`.
  - `ReleaseClient_SendsUserAgent`: the request has `User-Agent` starting `ProcessTracker/`.
  - `Download_HashMatches_ReturnsPath`.
  - `Download_HashMismatch_ReturnsNullAndLeavesNoFiles`: `Directory.GetFiles(targetDir)` is empty.
  - `Download_SumsMissingAsset_ReturnsNull`.
  - `Download_RedirectToForeignHost_ReturnsNull`: a 302 to `https://evil.example/x.zip`.
  - `Download_RedirectToReleaseAssetsHost_Followed`.
  - `IsNewer_StrictlyGreater`: `0.3.0 > 0.2.9` true; `0.2.0 > 0.2.0` false.
  - `Sha256Sums_ParsesTwoSpaceFormat`.
  - `Sha256Sums_MalformedLine_Throws`.
- [ ] **Step 2:** Run `dotnet test --project tests/ProcessTracker.Core.Tests`. Expected: FAIL.
- [ ] **Step 3: Implement** with `System.Text.Json` against the GitHub fields `tag_name`, `assets[].name` and
  `assets[].browser_download_url`.
- [ ] **Step 4:** Run. Expected: PASS.
- [ ] **Step 5: Commit** `feat: GitHub release client with hash-verified downloads`

### Task 5: `proctrack user install|uninstall|status`

**Files:**
- Create: `src/ProcessTracker.Native/UserTask.cs` (in Native, because the service uses it too, in Task 6),
  `src/ProcessTracker.Cli/User/UserInstall.cs`, `src/ProcessTracker.Cli/Commands/UserCommand.cs`
- Modify: `src/ProcessTracker.Cli/Program.cs` (add `UserCommand.Create()`)
- Test: `tests/ProcessTracker.Native.Tests/UserTaskTests.cs`, `tests/ProcessTracker.Cli.Tests/User/UserInstallTests.cs`

**Interfaces:**
- Consumes: the `UpgradeTask` pattern (existing, schtasks at `%SystemRoot%\System32\schtasks.exe`);
  `UserModeSignal.TrySignal` (Task 3).
- Produces:
  - `UserTask(Func<string, IReadOnlyList<string>, TimeSpan, (int ExitCode, string Output)> run, string sid)`. The
    CLI passes `new ProcessRunner().Run`; tests pass a fake.
    - `TaskName == $@"\ProcessTracker\User-{sid}"`;
    - `static string BuildXml(string appDir, string sid)`;
    - `int Register(string appDir)` (`schtasks /Create /XML … /F`);
    - `int Remove()`;
    - `string? RegisteredAppDir()`, which parses the action path from `schtasks /Query /XML`.
  - Task XML:
    - `LogonTrigger` with `UserId` = SID;
    - `Principal` with `LogonType=InteractiveToken`, `RunLevel=LeastPrivilege`;
    - `Settings` with `MultipleInstancesPolicy=IgnoreNew`, `DisallowStartIfOnBatteries=false`,
      `ExecutionTimeLimit=PT0S`;
    - one `Exec` action, `<appDir>\ProcessTracker.Service.exe --user`.
  - `UserInstall`:
    - `int Install(string appDir)`: refuse if elevated (exit 1, message
      `run this from a normal, non-administrator terminal`); refuse if the machine service is installed (exit 1, say
      so); otherwise register the task, start the exe detached, and wait up to 30 s for `/api/health` to report
      `mode=user`.
    - `int Uninstall(bool purgeData)`: signal exit, remove the task, delete `%LOCALAPPDATA%\Programs\ProcessTracker`,
      and delete the data folder only with `--purge-data`.
    - `UserStatus Status()`.
  - `proctrack user install --app-dir <dir>`, `proctrack user uninstall [--purge-data]`, `proctrack user status [--json]`.

- [ ] **Step 1: Write failing tests:**
  - `BuildXml_UsesInteractiveTokenAndLeastPrivilege`: XPath assertions on `LogonType`, `RunLevel`, `UserId`, and the
    `Command` ending in `ProcessTracker.Service.exe` with `Arguments` `--user`.
  - `BuildXml_EscapesPathWithAmpersand`: an `appDir` containing `&` still yields well-formed XML.
  - `UserInstall_Elevated_Refuses`: injected `isElevated: () => true` gives exit 1, and the fake runner received no
    schtasks call.
  - `UserInstall_MachineServicePresent_Refuses`.
  - `Uninstall_KeepsDataWithoutPurge`.
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3: Implement.** Elevation, machine-service probe, process start and health polling are injected for
  tests.
- [ ] **Step 4:** Run. Expected: PASS.
- [ ] **Step 5: Commit** `feat: proctrack user install, uninstall and status`

### Task 6: Self-update and takeover in user mode

**Files:**
- Create: `src/ProcessTracker.Service/Updates/SelfUpdateWorker.cs`, `src/ProcessTracker.Service/Updates/Takeover.cs`
- Modify: `src/ProcessTracker.Service/Program.cs` (register the worker in user mode; run `Takeover` when
  `--takeover` is given)
- Test: `tests/ProcessTracker.Service.Tests/Updates/SelfUpdateWorkerTests.cs`,
  `tests/ProcessTracker.Service.Tests/Updates/TakeoverTests.cs`

**Interfaces:**
- Consumes: `ReleaseClient`, `IsNewer` (Task 4); `ServiceProbe` (Task 1); `UserTask` (Native, Task 5);
  `SingleInstance.TryAcquire(TimeSpan)` (Task 3).
- Produces:
  - `SelfUpdateWorker : BackgroundService`. It first checks 10 min after start, then every 24 h:
    1. Skip if the machine service is installed.
    2. Skip if `GetLatestAsync` gives null or a version that isn't newer.
    3. Otherwise `DownloadVerifiedAsync(zip)` into `%TEMP%\ProcessTracker-update-<v>` and extract to
       `…\Programs\ProcessTracker\app-<v>`. If that folder already exists, delete it and extract again.
    4. Start `app-<v>\ProcessTracker.Service.exe --user --takeover <currentAppDir>` detached.

    The worker does not exit itself: the exit event the new process sets does that.
  - `Takeover.RunAsync(string previousAppDir, Func<Task<bool>> becameHealthy, …)` in the new process. It runs after
    the mutex is acquired. On the first completed tick it calls `UserTask.Register(newAppDir)` and deletes `app-*`
    folders other than the new and the previous. If the host fails to start, or no tick completes within 60 s, it
    starts `<previousAppDir>\ProcessTracker.Service.exe --user`, logs, and exits non-zero.

- [ ] **Step 1: Write failing tests** (all with fakes, no network or processes):
  - `Worker_MachineServiceInstalled_DoesNothing`.
  - `Worker_NotNewer_DoesNothing`.
  - `Worker_Newer_ExtractsAndStartsTakeover`: the fake starter records the exe and args
    `--user --takeover <current>`.
  - `Worker_DownloadFails_NoExtractNoStart`.
  - `Takeover_Healthy_RegistersTaskAndPrunes`: folders `app-0.1.0`, `app-0.2.0`, `app-0.3.0` (new) leave
    `0.2.0` and `0.3.0`.
  - `Takeover_NewVersionFailsBeforeHealthy_RestartsPrevious`: `becameHealthy` returns false. The starter received
    `<previous>\ProcessTracker.Service.exe --user`, and `Register` was not called.
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3: Implement.** Use `ZipFile.ExtractToDirectory`, reject zip entries that escape the target directory,
  and start processes with `UseShellExecute=false`, `CreateNoWindow=true`.
- [ ] **Step 4:** Run. Expected: PASS.
- [ ] **Step 5: Commit** `feat: user mode updates itself from GitHub releases with rollback`

### Task 7: Hook endpoints, last input, warn-only instructions

**Files:**
- Create: `src/ProcessTracker.Core/Hooks/HeavyCommandClassifier.cs`, `src/ProcessTracker.Core/Hooks/HookWarningLimiter.cs`,
  `src/ProcessTracker.Core/Hooks/HookAdvisor.cs`, `src/ProcessTracker.Native/LastInputProbe.cs`,
  `src/ProcessTracker.Service/Api/HookEndpoints.cs`
- Modify: `src/ProcessTracker.Service/Program.cs` (`app.MapHookEndpoints()`; `IUserActivity` registration),
  `src/ProcessTracker.Service/Mcp/ProcessTrackerTools.cs` (`ServerInstructions` text),
  `src/ProcessTracker.Service/ServiceOptions.cs` (`HeavyCommandPatterns` string[] setting, extra regexes)
- Test: `tests/ProcessTracker.Core.Tests/Hooks/HeavyCommandClassifierTests.cs`,
  `tests/ProcessTracker.Core.Tests/Hooks/HookWarningLimiterTests.cs`, `tests/ProcessTracker.Core.Tests/Hooks/HookAdvisorTests.cs`,
  `tests/ProcessTracker.Service.Tests/HookEndpointsTests.cs`, `tests/ProcessTracker.Service.Tests/McpToolsTests.cs`

**Interfaces:**
- Consumes: `LoadAssessor`/`LoadReport` (existing), `SessionInfo.ClaudeSessionIds` (existing), `ServiceOptions.Mode`.
- Produces:
  - `HeavyCommandClassifier.IsHeavy(string toolName, string? command, IReadOnlyList<Regex> extra) : bool`.
    - `Task` and `Agent` are always heavy.
    - For `Bash` and `PowerShell`, matching is case-insensitive on the command text, and any one of these makes it
      heavy:
      - `cmake --build`;
      - `msbuild`;
      - `dotnet (build|test|publish)`;
      - `\bmake\b`;
      - `ninja`;
      - `cargo (build|test)`;
      - `(npm|pnpm|yarn) (run )?(build|test)`;
      - `\bmatlab\b`;
      - `dymola`;
      - `dymosim`;
      - `\bpython(3)?(\.exe)?\s+\S+\.py`;
      - ` -j\s*\d*`;
      - ` -m:\d+`;
      - `--parallel`;
      - `Start-Job`;
      - `ForEach-Object\s+-Parallel`.
  - `HookWarningLimiter(TimeProvider time)`: `bool TakeFull(string sessionKey)` is true at most once per 60 s per key.
  - `IUserActivity { TimeSpan? SinceLastInput(); }`: `LastInputProbe` (`GetLastInputInfo`) in User/Dev modes; a null
    implementation in Service mode.
  - `HookAdvisor.PreToolUse(LoadReport load, bool heavy, bool full, TimeSpan? sinceInput, string? callerTitle) : string?`
    returns null when it should say nothing. Text rules:
    - **Moderate:** one line,
      `ProcessTracker: machine at Moderate; run at most {N} heavy tasks in parallel.`
    - **High/Critical, full:** a block starting `ProcessTracker: machine at {P}` with:
      - CPU busy %, commit headroom GB, suggested parallelism;
      - the top sessions (title, CPU cores, private GB, with ` (this session)` on the caller);
      - `User last active {age} ago` or `User activity unknown`;
      - then exactly:
        `Decide based on what the user has told you about using this machine (foreground work vs a remote or dedicated box). If you don't know and the machine is at Critical, ask them.`
    - **High/Critical, not full:**
      `ProcessTracker: machine still at {P}; suggested parallelism {N}.`
  - `HookAdvisor.SessionStart(LoadReport load, string mode, TimeSpan? sinceInput, Version? pluginVersion, Version trackerVersion) : string`
    gives the context paragraph:
    - mode, pressure, suggested parallelism;
    - the top 3 sessions;
    - last input;
    - the rule line
      `Before heavy local work, check get_machine_load and pace to suggestedParallelism; the ProcessTracker hook warns you at Moderate and above.`

    It then adds at most one of these:
    - tracker older than the plugin:
      `ProcessTracker {t} is older than the plugin ({p}); it updates itself daily, or the user can run /process-tracker:install now.`
    - plugin older than the tracker:
      `The process-tracker plugin ({p}) is older than the tracker ({t}); suggest the user enable auto-update for the processtracker marketplace in /plugin.`
    - a null `pluginVersion` adds neither.
  - `POST /api/hooks/pre-tool-use`:
    - The body is the Claude Code hook input (`session_id`, `tool_name`, `tool_input.command`); unknown fields are
      ignored.
    - The response is `{}` or `{"hookSpecificOutput":{"hookEventName":"PreToolUse","additionalContext":"…"}}`.
    - The caller session is found by matching `session_id` against `SessionInfo.ClaudeSessionIds`; the limiter key is
      `session_id`.
  - `POST /api/hooks/session-start`:
    - The body is the hook input plus optional `plugin_version` (the script adds it).
    - The response is
      `{"hookSpecificOutput":{"hookEventName":"SessionStart","additionalContext":"…"}}`.
  - New `ServerInstructions` text (replace the whole constant; keep it under 1,500 characters):
    - Before heavy local work (builds, test suites, simulations, MATLAB or Dymola engines, parallel jobs, or subagents
      doing these), call get_machine_load.
    - Low/Moderate: run at most suggestedParallelism tasks at once.
    - High/Critical: you are close to making this machine unusable for the user. Weigh what the user has told you
      about using it (foreground work vs a remote or dedicated box) and scale down accordingly. If you don't know and
      it is Critical, ask.
    - Shut down engines and servers you started once done. `proctrack load --json` gives the same report if this
      server is unreachable.

- [ ] **Step 1: Write failing tests:**
  - **Classifier:** `IsHeavy_Builds` (6 positive cases); `IsHeavy_Trivial` (`git status`, `ls`, `Get-ChildItem`,
    `make-dir`: all false); `IsHeavy_TaskTool_True`; `IsHeavy_ExtraPattern`.
  - **Limiter:** `TakeFull_OncePer60s`, using `ManualTimeProvider`: at t=0 true, t=59 false, t=60 true; different keys
    are independent.
  - **Advisor:**
    - `PreToolUse_LowOrNotHeavy_ReturnsNull`;
    - `PreToolUse_Moderate_OneLine` (exact string with N=3);
    - `PreToolUse_Critical_FullBlockEndsWithDecideLine`;
    - `PreToolUse_MarksCallerSession`;
    - `PreToolUse_UnknownInput_SaysUnknown`;
    - `SessionStart_IncludesRuleLine`;
    - `SessionStart_OlderTracker_AddsUpdateLine`;
    - `SessionStart_OlderPlugin_SuggestsAutoUpdate`;
    - `SessionStart_NoPluginVersion_NoSkewLine`.
  - **Endpoints:**
    - `PreToolUse_HeavyAtCritical_ReturnsAdditionalContextNoPermissionDecision`: the response JSON has no
      `permissionDecision` anywhere;
    - `PreToolUse_UnknownPayload_ReturnsEmptyObject`: body `{"foo":1}` gives `{}`;
    - `PreToolUse_MalformedJson_Returns400`;
    - `SessionStart_ReturnsContext`.
  - **MCP:** update `Mcp_Initialize_SendsLoadCheckInstructions` to assert `"Weigh what the user has told you"` and
    that `"do not start"` is absent.
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3: Implement.** Then retarget `OwnerGate_OtherUser_Gets403AndBodyNotRead` (Task 2) to
  `/api/hooks/pre-tool-use`.
- [ ] **Step 4:** Run the full suite. Expected: PASS.
- [ ] **Step 5: Commit** `feat: hook endpoints warn agents at Moderate and above; warn-only MCP instructions`

### Task 8: The plugin and its marketplace

**Files:**
- Create:
  - `.claude-plugin/marketplace.json`
  - `plugin/.claude-plugin/plugin.json`
  - `plugin/.mcp.json`
  - `plugin/hooks/hooks.json`
  - `plugin/commands/install.md`, `plugin/commands/uninstall.md`, `plugin/commands/status.md`
  - `plugin/scripts/install-user.ps1`, `plugin/scripts/session-start.ps1`
  - `tests/plugin.tests.ps1`

**Interfaces:**
- Consumes: `/api/health` with mode and owner (Task 1); `/api/hooks/*` (Task 7); `proctrack user install`,
  `uninstall` and `status` (Task 5).
- Produces:
  - **`marketplace.json`:** name `processtracker`, owner `worc4021`, one plugin `process-tracker`. Its source is a git
    subdirectory of `https://github.com/worc4021/ProcessTracker.git`, path `plugin`, `ref` `v<version>`. Before
    writing it, check the exact source schema against https://code.claude.com/docs/en/plugins/create-marketplace.
    Until the first release containing the plugin exists, set `ref` to the release this PR will produce. Task 9's
    workflow keeps it current afterwards.
  - **`plugin.json`:** `name` `process-tracker`, `version` = `version.txt`, a description, `homepage` = the repo URL,
    and `license` `MIT`.
  - **`.mcp.json`:** `{"mcpServers":{"process-tracker":{"type":"http","url":"http://127.0.0.1:47811/mcp","headers":{"X-Claude-Session":"${CLAUDE_CODE_HOST_SESSION_ID:-}"}}}}`
  - **`hooks.json`:**
    - **SessionStart:** exec form, `powershell.exe` with args
      `-NoProfile -ExecutionPolicy Bypass -File ${CLAUDE_PLUGIN_ROOT}/scripts/session-start.ps1`, timeout 10.
    - **PreToolUse:** matcher `Bash|PowerShell|Task|Agent`, exec form `curl.exe` with args
      `-s -f --connect-timeout 1 --max-time 2 -H "Content-Type: application/json" --data-binary @- http://127.0.0.1:47811/api/hooks/pre-tool-use`,
      timeout 3.
  - **`session-start.ps1`:**
    - Read stdin, add `plugin_version` from `plugin.json`, POST to `/api/hooks/session-start` with a 3 s timeout, and
      write the response body to stdout.
    - On 403, print `ProcessTracker on this machine belongs to another user; ask an admin to install the machine-wide service.`
    - On connection failure:
      - if a `\ProcessTracker\User-<SID>` task exists, run it (`schtasks /Run`) and print one line saying it was
        started;
      - otherwise print
        `ProcessTracker is not running. Offer the user /process-tracker:install once; it installs a user-only copy without admin rights.`
    - Always exit 0.
  - **`install-user.ps1`:**
    1. Refuse if elevated.
    2. If `/api/health` answers with mode `service`, print that the machine-wide service is in use and stop. If a
       `\ProcessTracker\User-<SID>` task also exists, add that `proctrack user uninstall` removes the now-unused
       user-only copy.
    3. Read the latest release JSON, download the zip and `SHA256SUMS.txt` to `%TEMP%`, verify, and extract to
       `%LOCALAPPDATA%\Programs\ProcessTracker\app-<v>`.
    4. Run `app-<v>\bin\proctrack.exe user install --app-dir <that dir>`, print its output, and exit with its code.
    5. Also run `proctrack mcp unregister`, so a leftover user-scope MCP entry doesn't duplicate the plugin's.

    Downloads follow the same rules as Task 4: same URLs and hosts, PowerShell 5.1 compatible.
  - **`commands/install.md`:** a prompt telling Claude to run
    `powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_PLUGIN_ROOT}/scripts/install-user.ps1"`
    and report the outcome verbatim, without retrying elevated.
  - **`uninstall.md`:** runs `proctrack user uninstall`, and only adds `--purge-data` if the user asked.
  - **`status.md`:** runs `proctrack user status` and `proctrack load`.

- [ ] **Step 1: Write failing Pester tests** in `tests/plugin.tests.ps1`, matching the style of
  `tests/upgrade-script.tests.ps1`:
  - `marketplace.json and plugin.json parse and names match`.
  - `marketplace ref equals v + plugin.json version`. Not "equals version.txt": release-please bumps `version.txt`
    in the release PR, but the plugin version only moves in the release workflow's final commit, by design.
  - `hooks.json PreToolUse has no onFailure block and uses curl.exe`.
  - `install-user.ps1 refuses when elevated`: dot-source with a mocked `Test-Elevated` returning true; expect a throw
    or a non-zero exit and no download call.
  - `install-user.ps1 rejects hash mismatch`: mock `Invoke-WebRequest` to write files whose hash doesn't match; expect
    no extract.
  - `session-start.ps1 prints install offer when unreachable`: mock the POST to throw a connection error; the output
    contains `/process-tracker:install`.
  - `curl against closed port is silent`: run the exact PreToolUse curl args against `127.0.0.1:1`; exit code is
    non-zero and stdout is empty.
- [ ] **Step 2:** Run `Invoke-Pester tests/plugin.tests.ps1`. Expected: FAIL.
- [ ] **Step 3: Implement** the files. Scripts dot-source safely, using the same
  `if ($MyInvocation.InvocationName -eq '.') { return }` guard as `upgrade.ps1`.
- [ ] **Step 4:** Run Pester. Expected: PASS. Then check by hand on a dev build:
  1. `claude plugin marketplace add ./` from the repo root (a local path), then
     `claude plugin install process-tracker@processtracker`.
  2. Start a session.
  3. Confirm `/mcp` lists `plugin:process-tracker:process-tracker` and that the session-start context appears.
  4. Run a `dotnet build` in a session while the tracker reports a forced pressure (dev setting), and confirm the
     context line appears.
- [ ] **Step 5: Commit** `feat: Claude Code plugin with install command, MCP entry and load hooks`

### Task 9: Release zip, smoke test and marketplace bump

**Files:**
- Modify: `scripts/build-msi.ps1` (also write `<OutDir>\ProcessTracker-<v>-x64.zip` from the stage, excluding
  `upgrade.ps1`), `.github/workflows/release.yml`, `.github/workflows/ci.yml`
- Create: `scripts/user-smoke.ps1`

**Interfaces:**
- Consumes: the stage layout (existing), `--user` (Task 1), `proctrack user install|uninstall` (Task 5).
- Produces:
  - **Release assets:** the zip next to the MSI. `SHA256SUMS.txt` lists both, MSI first.
  - **`scripts/user-smoke.ps1 -Zip <path>`**, run as a non-admin user on CI via a `runas`-free path: the runner is
    admin, so the smoke runs `proctrack user install` through a scheduled task as a freshly created local standard
    user. If that proves impractical on GitHub runners, test the binaries with `--user --DataDir` in-process and
    record the gap.
    1. Extract the zip.
    2. Start `ProcessTracker.Service.exe --user --DataDir <temp>`.
    3. Expect `/api/health` mode `user` within 30 s.
    4. Send the exit event and expect the process to exit within 10 s.
  - **Final step of `release.yml`**, after upload and winget:
    1. Check out `main`.
    2. Set `.claude-plugin/marketplace.json` `ref` to `$env:TAG` and `plugin/.claude-plugin/plugin.json` `version` to
       `$env:VERSION`.
    3. Commit `chore: point the plugin marketplace at <tag>` and push with the workflow token
       (`permissions: contents: write`, already present).

    Skip it if nothing changed. If branch protection rejects the push, fail with the message
    `allow github-actions to push marketplace bumps to main`.

- [ ] **Step 1:** Add the zip step to `build-msi.ps1`. Run `./scripts/build-msi.ps1 -SkipTests` (or the existing
  equivalent). Expected: `artifacts\ProcessTracker-<v>-x64.zip` exists, containing `ProcessTracker.Service.exe` and
  `bin\proctrack.exe`.
- [ ] **Step 2:** Write `user-smoke.ps1` and run it locally against that zip. Expected: `user smoke: OK`.
- [ ] **Step 3:** Update `release.yml`:
  - the sums cover both files;
  - upload both;
  - run the user smoke;
  - add the marketplace bump step.

  Update `ci.yml` to run `user-smoke.ps1` and `Invoke-Pester tests/plugin.tests.ps1`.
- [ ] **Step 4:** Push the branch and confirm CI is green: build-test, msi-smoke, user-smoke, plugin tests.
- [ ] **Step 5: Commit** `ci: release the user-mode zip and keep the plugin marketplace on the latest tag`

### Task 10: MSI side: no MCP registration, stop user instances, GitHub fallback

**Files:**
- Modify: `installer/Package.wxs`, `installer/upgrade.ps1`, `scripts/msi-smoke.ps1`, `tests/upgrade-script.tests.ps1`,
  `src/ProcessTracker.Cli/Commands/AdminCommand.cs`
- Create: `src/ProcessTracker.Cli/Admin/FetchRelease.cs`
- Test: `tests/ProcessTracker.Cli.Tests/Admin/FetchReleaseTests.cs`

**Interfaces:**
- Consumes: `ReleaseClient` (Task 4), `admin stop-user-instances` (Task 3).
- Produces:
  - `proctrack admin fetch-release --asset msi --out <dir>`:
    - prints the verified MSI path and exits 0;
    - exits 3 when the latest version is not newer than proctrack's own;
    - exits 1 on any failure.
  - **MSI:**
    - remove `McpRegister` and `SetMcpRegisterCmdLine`; keep `McpUnregister` to clean up older installs;
    - add a deferred, `Impersonate="no"`, `Return="check"` action `StopUserInstances` that runs
      `"[INSTALLFOLDER]bin\proctrack.exe" admin stop-user-instances`, scheduled `Before="StartServices"` with
      condition `NOT REMOVE~="ALL"`.
  - **`upgrade.ps1`:** when winget is not found (1003) or returns the no-package code, call `fetch-release`.
    - On exit 0, run `msiexec /i <path> /qn /norestart`. Write event **1004**
      `ProcessTracker upgraded from GitHub release <v>.` on success, or 1002 with the msiexec code on failure.
    - On exit 3, write 1000 "up to date".
    - `-WhatIf` prints the fallback command without running it.

- [ ] **Step 1: Write failing tests:**
  - `FetchRelease_NotNewer_Exits3` (fake client).
  - `FetchRelease_Verified_PrintsPath`.
  - Pester: `upgrade.ps1 falls back to fetch-release when winget is missing` (mock `Find-Winget` returning no path;
    `-WhatIf` output contains `admin fetch-release`).
  - Pester: `upgrade.ps1 writes 1004 after a successful fallback` (mock `Invoke-Native`).
- [ ] **Step 2:** Run the CLI tests and Pester. Expected: FAIL.
- [ ] **Step 3: Implement.** Update `msi-smoke.ps1`:
  - replace the two `McpRegister` assertions with
    `Assert-That (... -notmatch 'McpRegister') 'the install no longer registers MCP'`;
  - add `Assert-That (... -match 'Action start \d+:\d+:\d+: StopUserInstances\.') 'the install stopped user-mode instances'`.
- [ ] **Step 4:** Run the tests and Pester. Expected: PASS. Push and confirm the `msi-smoke` CI job is green.
- [ ] **Step 5: Commit** `feat: MSI stops user-mode instances, drops MCP registration and falls back to GitHub releases`

### Task 11: README

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Rewrite the Install section** to lead with the plugin route:

  ```
  /plugin marketplace add worc4021/ProcessTracker
  /plugin install process-tracker@processtracker
  /process-tracker:install
  ```

  then enabling auto-update for the marketplace (`/plugin` → Marketplaces → processtracker → Enable auto-update).

  Then add a section **User-only vs machine-wide** covering:
  - the table from the spec section 1;
  - what user-only mode can't see;
  - which one wins;
  - uninstall for each.
- [ ] **Step 2:** Document:
  - the self-update behaviour and the logs location;
  - the hooks (what agents see at Moderate, High and Critical; that nothing is blocked);
  - the trust decision, including the GitHub-release risk and the recommendation to enable 2FA and branch protection;
  - the MSI's fallback event 1004.

  Remove the CLAUDE.md snippet's "don't start at High" wording so it matches the warn-only rule.
- [ ] **Step 3:** Check every command in the README against `proctrack --help` and the plugin files. Expected: no
  stale names.
- [ ] **Step 4: Commit** `docs: plugin-first install, user-only mode and hooks`
