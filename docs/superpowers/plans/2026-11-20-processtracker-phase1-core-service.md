# ProcessTracker Phase 1 (Core Service) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A single always-on Windows service that samples every process, attributes each to the Claude Code
session that launched it, flags orphans with their idle time, keeps a rolling on-disk history, and serves all
of this over REST, MCP and a CLI, upgradable in place with health check and rollback.

**Architecture:** `ProcessTracker.Core` holds all logic behind two seams: `ISnapshotSource` (raw counters)
and `IProcessInspector` (environment / path / command line). `ProcessTracker.Native` implements those seams
with Win32/NT calls. A `Tracker` runs one tick every 2 s and publishes an immutable `TrackerView`, which the
service's REST and MCP endpoints read without locking. History goes to append-only segment files; state goes
to SQLite (WAL), so a restart resumes where it left off.

**Tech Stack:** .NET 10 (SDK 10.0.101 present), C#, ASP.NET Core minimal APIs, `Microsoft.Extensions.Hosting.WindowsServices`,
`Microsoft.Data.Sqlite`, `ModelContextProtocol.AspNetCore`, `System.CommandLine`, xUnit v3.

**Spec:** `docs/superpowers/specs/2026-11-20-processtracker-design.md`. This plan is Phase 1 of §15. Phases 2–5
(tray UI, broker/bookings, ETW, containment) each get their own plan. Deferred out of this plan:
`GET /api/events` and alert toasts (Phase 2, its only consumer is the tray); bookings and `request_resources` (Phase 3).
This plan does compute the `Alertable` flag, so Phase 2 only has to display it.

## Global Constraints

- Target `net10.0-windows`, runtime `win-x64` only; `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`.
- Central package management (`Directory.Packages.props`); latest stable versions at implementation time; no packages beyond the Tech Stack without a stated reason.
- Service name `ProcessTracker`; single-instance mutex `Global\ProcessTracker.Service` (not taken in `--dev`).
- Kestrel binds **127.0.0.1 only**; port **47811** (dev **47812**); MCP at `/mcp`, REST under `/api`.
- Install root `C:\Program Files\ProcessTracker\versions\<version>\`, junction `C:\Program Files\ProcessTracker\current`.
- Data `%ProgramData%\ProcessTracker\` (`history\`, `state.db`, `logs\`, `appsettings.json`); dev data `%LOCALAPPDATA%\ProcessTracker-dev\`.
- Sampling every **2 s**; in-memory ring **10 min**; idle-process heartbeat **60 s**; segment length **5 min**; retention default **24 h**, minimum **2 h**.
- Budget: < 1 % of one core averaged, < 200 MB private bytes; `System.GC.HeapHardLimit` = 192 MB; process priority AboveNormal.
- **Only** `CLAUDE_CODE_SESSION_ID`, `CLAUDE_CODE_HOST_SESSION_ID`, `CLAUDE_CODE_ENTRYPOINT` may be extracted from a process environment. Never store or log any other variable; the environment contains tokens.
- Every process identity is `ProcessKey(Pid, CreateTimeTicks)` (FILETIME UTC ticks). Never key anything by PID alone.
- The service has **no** kill capability; kills run in the CLI with the user's rights.
- Stored formats change **additively only**: segment records are length-prefixed; SQLite only gains columns or tables.
- Watched images: `MATLAB.exe`, `Dymola.exe`, `dymosim*.exe` (case-insensitive); orphan alert after **30 min** effective idle.
- Desktop app processes: image path contains `\WindowsApps\Claude_`. Desktop session `claude.exe` lives under `Claude-Data\…\claude-code\`; terminal CLI at `%USERPROFILE%\.local\bin\claude.exe`.

## Review Focus

1. **Process exits between snapshot and inspection**: inspection returns `Gone` and the tracker drops the process for that tick without throwing (Task 4 and Task 10 tests).
2. **PID reused while the service was down**: a persisted record with the same PID but a different create time is treated as exited, and the new process is attributed fresh (Task 10 test `Restore_PidReusedWithDifferentCreateTime_TreatsAsNewProcess`).
3. **Inspection denied (protected, elevated or WOW64 process)**: attributed by ancestry or as Unattributed, logged once, and not re-inspected every tick (Task 10 test `Tick_AccessDenied_InspectedOnlyOnce`).
4. **Sleep/resume gap or counter regression**: CPU is computed over the real elapsed time, and a negative delta clamps to 0 (Task 2 tests).
5. **Truncated last segment after a hard reset, or disk write failure**: the reader returns every complete record and stops at the torn tail; a write failure is logged and the collector keeps ticking (Task 8 tests).
6. **Orphans that predate the service** (anchor never observed): the session counts as ended, so its processes are orphans on the first tick (Task 7 test `IsLive_AnchorNeverObserved_False`).

---

## File Structure

```
ProcessTracker.slnx
Directory.Build.props            net10.0-windows, nullable, warnings-as-errors, VersionPrefix 0.1.0
Directory.Packages.props         central package versions
.gitignore                       standard dotnet + publish/ + *.db
src/ProcessTracker.Core/
  Model/ProcessKey.cs, RawProcessCounters.cs, SystemCounters.cs, Snapshot.cs, ProcessSample.cs,
        SystemSample.cs, Attribution.cs, InspectResult.cs, ProcessRecord.cs
  Collection/ISnapshotSource.cs, IProcessInspector.cs, SampleCalculator.cs
  Attribution/Attributor.cs
  Activity/ActivityThresholds.cs, ActivityState.cs, ActivityTracker.cs
  Sessions/SessionInfo.cs, SessionRegistry.cs, TranscriptReader.cs
  Orphans/ProcessState.cs, OrphanClassifier.cs
  Storage/TickFrame.cs, SampleRing.cs, SegmentFormat.cs, SegmentWriter.cs, SegmentReader.cs, StateStore.cs
  Tracking/TrackerOptions.cs, ProcessTable.cs, TrackerView.cs, Tracker.cs
  Load/LoadOptions.cs, LoadReport.cs, LoadAssessor.cs
  Contracts/Dtos.cs              REST/MCP DTOs shared by Service and Cli
src/ProcessTracker.Native/
  NtSnapshotSource.cs, PebProcessInspector.cs, TcpOwnerLookup.cs, NativeMethods.cs
src/ProcessTracker.Service/
  Program.cs, ServiceOptions.cs, CollectorWorker.cs, TrackerHost.cs, CallerResolver.cs,
  Api/RestEndpoints.cs, Api/Dtos.cs, Mcp/ProcessTrackerTools.cs, appsettings.json
src/ProcessTracker.Cli/          Program.cs, ApiClient.cs, Commands/*.cs, KillGuard.cs
src/ProcessTracker.Updater/      Program.cs, UpgradeOrchestrator.cs, IServiceControl.cs, IJunction.cs, IHealthProbe.cs, Windows*.cs
tests/ProcessTracker.Core.Tests/       fast unit tests (fakes for both seams)
tests/ProcessTracker.Native.Tests/     integration tests against the real OS
tests/ProcessTracker.Service.Tests/    WebApplicationFactory tests with a fake TrackerHost
tests/ProcessTracker.Updater.Tests/    orchestrator tests with fakes
tests/ProcessTracker.Cli.Tests/        kill guard tests
scripts/install.ps1, deploy.ps1, uninstall.ps1
```

---

### Task 1: Repository and solution scaffold, ProcessKey

**Files:**
- Create: `.gitignore`, `Directory.Build.props`, `Directory.Packages.props`, `ProcessTracker.slnx`, all `src/*` and `tests/*` `.csproj` files from the File Structure
- Create: `src/ProcessTracker.Core/Model/ProcessKey.cs`
- Test: `tests/ProcessTracker.Core.Tests/Model/ProcessKeyTests.cs`

**Interfaces:**
- Produces: `public readonly record struct ProcessKey(int Pid, long CreateTimeTicks)` with `override string ToString() => $"{Pid}@{CreateTimeTicks}"` and `static bool TryParse(string s, out ProcessKey key)`.

- [ ] **Step 1:** `git init`. Create the scaffold. Service project uses `Microsoft.NET.Sdk.Web`; Cli has `<AssemblyName>proctrack</AssemblyName>`; every test project references xunit.v3. Commit the existing `docs/` as-is in the first commit.
- [ ] **Step 2: Write the failing test**

```csharp
[Fact] public void SamePidDifferentCreateTime_AreDifferentProcesses()
    => Assert.NotEqual(new ProcessKey(42, 1000), new ProcessKey(42, 2000));
[Fact] public void RoundTripsThroughString()
{
    var k = new ProcessKey(42, 133_000_000_000_000_000);
    Assert.True(ProcessKey.TryParse(k.ToString(), out var parsed));
    Assert.Equal(k, parsed);
}
[Fact] public void TryParse_Garbage_False() => Assert.False(ProcessKey.TryParse("42", out _));
```

- [ ] **Step 3:** Run `dotnet test tests/ProcessTracker.Core.Tests`. Expected: FAIL (ProcessKey missing).
- [ ] **Step 4:** Implement `ProcessKey`.
- [ ] **Step 5:** Run `dotnet build ProcessTracker.slnx -warnaserror` and `dotnet test`. Expected: build clean, 3 passed.
- [ ] **Step 6:** Commit `chore: scaffold solution and ProcessKey`.

---

### Task 2: Core models and SampleCalculator

**Files:**
- Create: `src/ProcessTracker.Core/Model/{RawProcessCounters,SystemCounters,Snapshot,ProcessSample,SystemSample}.cs`, `Collection/{ISnapshotSource,SampleCalculator}.cs`
- Test: `tests/ProcessTracker.Core.Tests/Collection/SampleCalculatorTests.cs`

**Interfaces:**
- Produces:
  - `record RawProcessCounters(ProcessKey Key, int ParentPid, string ImageName, long KernelTime, long UserTime, long PrivateBytes, long WorkingSet, long ReadOps, long WriteOps, long OtherOps, long ReadBytes, long WriteBytes, long OtherBytes, long ContextSwitches, int HandleCount, int ThreadCount)`. Times are in 100 ns units.
  - `record SystemCounters(int LogicalCpuCount, long IdleTime, long KernelTime, long UserTime, long CommitChargeBytes, long CommitLimitBytes, long AvailablePhysicalBytes, long TotalPhysicalBytes)`. Kernel time **includes** idle time, as `GetSystemTimes` reports it.
  - `record Snapshot(DateTime TimestampUtc, IReadOnlyList<RawProcessCounters> Processes, SystemCounters System)`.
  - `interface ISnapshotSource { Snapshot Take(); }`
  - `readonly record struct ProcessSample(ProcessKey Key, DateTime TimestampUtc, float CpuCores, long PrivateBytes, long WorkingSet, long ReadOpsDelta, long WriteOpsDelta, long OtherOpsDelta, long ReadBytesDelta, long WriteBytesDelta, long OtherBytesDelta, long ContextSwitchesDelta, int HandleCount, int ThreadCount)`. `CpuCores` = cores used on average (2.5 = two and a half cores).
  - `readonly record struct SystemSample(DateTime TimestampUtc, float CpuBusyPercent, float BusyCores, int LogicalCpuCount, long CommitChargeBytes, long CommitLimitBytes, long AvailablePhysicalBytes, long TotalPhysicalBytes)`
  - `static class SampleCalculator { static ProcessSample Compute(RawProcessCounters prev, DateTime prevTs, RawProcessCounters cur, DateTime curTs); static ProcessSample First(RawProcessCounters cur, DateTime ts); static SystemSample ComputeSystem(SystemCounters prev, SystemCounters cur, DateTime ts); }`

- [ ] **Step 1: Write the failing tests**

```csharp
// helper: Raw(cpu100ns, readOps, ctx) builds RawProcessCounters for key (1, 1) with other fields 0
[Fact] public void CpuCores_IsCpuTimeOverElapsed()
{   // 2 s elapsed, 3 s of CPU time (kernel+user) -> 1.5 cores
    var s = SampleCalculator.Compute(Raw(0,0,0), T0, Raw(30_000_000,0,0), T0.AddSeconds(2));
    Assert.Equal(1.5f, s.CpuCores, 3);
}
[Fact] public void LongGapAfterResume_UsesRealElapsed()
{   // 1 hour gap, 36 s CPU -> 0.01 cores, not 18 cores
    var s = SampleCalculator.Compute(Raw(0,0,0), T0, Raw(360_000_000,0,0), T0.AddHours(1));
    Assert.Equal(0.01f, s.CpuCores, 4);
}
[Fact] public void CounterRegression_ClampsToZero()
{
    var s = SampleCalculator.Compute(Raw(50,10,10), T0, Raw(40,5,5), T0.AddSeconds(2));
    Assert.Equal(0f, s.CpuCores); Assert.Equal(0, s.ReadOpsDelta); Assert.Equal(0, s.ContextSwitchesDelta);
}
[Fact] public void ZeroElapsed_GivesZeroCpu()
    => Assert.Equal(0f, SampleCalculator.Compute(Raw(0,0,0), T0, Raw(100,0,0), T0).CpuCores);
[Fact] public void First_HasNoDeltas()
{
    var s = SampleCalculator.First(Raw(999,999,999), T0);
    Assert.Equal(0f, s.CpuCores); Assert.Equal(0, s.ReadOpsDelta);
}
[Fact] public void System_BusyPercent_ExcludesIdle()
{   // 4 CPUs, 2 s: total = kernel+user deltas = 80M (kernel includes idle 60M) -> busy 25%, 1 busy core
    var a = Sys(idle:0, kernel:0, user:0);
    var b = Sys(idle:60_000_000, kernel:70_000_000, user:10_000_000);
    var s = SampleCalculator.ComputeSystem(a, b, T0);
    Assert.Equal(25f, s.CpuBusyPercent, 2); Assert.Equal(1f, s.BusyCores, 2);
}
```

- [ ] **Step 2:** Run `dotnet test --filter SampleCalculatorTests`. Expected: FAIL (types missing).
- [ ] **Step 3:** Implement the models and `SampleCalculator`. Busy cores = busyPercent/100 × LogicalCpuCount.
- [ ] **Step 4:** Run the tests. Expected: 6 passed.
- [ ] **Step 5:** Commit `feat(core): models and sample delta computation`.

---

### Task 3: Native snapshot source

**Files:**
- Create: `src/ProcessTracker.Native/{NativeMethods,NtSnapshotSource}.cs`
- Test: `tests/ProcessTracker.Native.Tests/NtSnapshotSourceTests.cs`

**Interfaces:**
- Consumes: `ISnapshotSource`, `Snapshot`, `RawProcessCounters`, `SystemCounters` (Task 2).
- Produces: `public sealed class NtSnapshotSource : ISnapshotSource`

**Data sources:**
- `NtQuerySystemInformation(SystemProcessInformation = 5)`. Grow the buffer on `STATUS_INFO_LENGTH_MISMATCH` (0xC0000004), keep it between calls, and walk the entries by `NextEntryOffset`.
- `ContextSwitches` = the sum of `SYSTEM_THREAD_INFORMATION.ContextSwitches` over the process's threads.
- `CreateTimeTicks` = `CreateTime` as returned (FILETIME UTC).
- The IO counters come from the entry's `IO_COUNTERS`.
- `PrivateBytes` = `PrivatePageCount`.
- Skip PID 0 (Idle).
- System counters: `GetSystemTimes` and `GetPerformanceInfo`. Commit values are pages × `PageSize`.
- `LogicalCpuCount` = `Environment.ProcessorCount`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void ContainsCurrentProcess_WithMatchingCreateTime()
{
    var snap = new NtSnapshotSource().Take();
    using var me = Process.GetCurrentProcess();
    var row = Assert.Single(snap.Processes, p => p.Key.Pid == me.Id);
    Assert.Equal(me.StartTime.ToUniversalTime().ToFileTimeUtc(), row.Key.CreateTimeTicks);
    Assert.InRange(row.ThreadCount, me.Threads.Count - 4, me.Threads.Count + 4);
    Assert.True(row.PrivateBytes > 0 && row.KernelTime + row.UserTime > 0);
}
[Fact] public void ParentPidOfChild_IsUs()
{
    using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 5 127.0.0.1 >nul") { CreateNoWindow = true })!;
    var row = new NtSnapshotSource().Take().Processes.Single(p => p.Key.Pid == child.Id);
    Assert.Equal(Environment.ProcessId, row.ParentPid);
    child.Kill();
}
[Fact] public void SystemCounters_AreSane()
{
    var s = new NtSnapshotSource().Take().System;
    Assert.True(s.CommitLimitBytes > s.CommitChargeBytes && s.CommitChargeBytes > 0);
    Assert.True(s.TotalPhysicalBytes >= s.AvailablePhysicalBytes);
}
[Fact] public void TwoConsecutiveSnapshots_Under50ms()   // budget guard
{
    var src = new NtSnapshotSource(); src.Take();
    var sw = Stopwatch.StartNew(); src.Take();
    Assert.True(sw.ElapsedMilliseconds < 50, $"{sw.ElapsedMilliseconds} ms");
}
```

- [ ] **Step 2:** Run `dotnet test tests/ProcessTracker.Native.Tests`. Expected: FAIL.
- [ ] **Step 3:** Implement. Use `LibraryImport` with blittable structs, and read the entries with `MemoryMarshal` over a pinned `byte[]`.
- [ ] **Step 4:** Run the tests. Expected: 4 passed.
- [ ] **Step 5:** Commit `feat(native): NtQuerySystemInformation snapshot source`.

---

### Task 4: Native process inspector (PEB environment, image path, command line)

**Files:**
- Create: `src/ProcessTracker.Core/Model/InspectResult.cs`, `src/ProcessTracker.Core/Collection/IProcessInspector.cs`, `src/ProcessTracker.Native/PebProcessInspector.cs`
- Test: `tests/ProcessTracker.Native.Tests/PebProcessInspectorTests.cs`

**Interfaces:**
- Produces:
  - `enum InspectStatus { Ok, AccessDenied, Gone, Unsupported }`
  - `sealed record InspectResult(InspectStatus Status, string? SessionId, string? HostSessionId, string? Entrypoint, string? ImagePath, string? CommandLine)`
  - `interface IProcessInspector { InspectResult Inspect(ProcessKey key); }`
  - `sealed class PebProcessInspector : IProcessInspector`

**Approach:**
- Open the process with `PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ`.
- Verify the handle's creation time (`GetProcessTimes`) equals `key.CreateTimeTicks`. If it differs, return `Gone` (the PID was reused).
- If `IsWow64Process2` reports a 32-bit process, return `Unsupported` with the image path only.
- Read the PEB: `NtQueryInformationProcess(ProcessBasicInformation)`, then `PEB+0x20` gives ProcessParameters. In `RTL_USER_PROCESS_PARAMETERS`: `CommandLine` UNICODE_STRING at `+0x70`, `Environment` at `+0x80`, `EnvironmentSize` at `+0x3F0`.
- Parse the environment block and pick out **only** the three allowlisted names (Global Constraints).
- Image path comes from `QueryFullProcessImageName`.
- Map access-denied errors to `AccessDenied`, and `ERROR_INVALID_PARAMETER` to `Gone`.
- Truncate the command line to 2048 characters.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void ReadsAllowlistedClaudeVariablesOfChild()
{
    var psi = new ProcessStartInfo("cmd.exe", "/c ping -n 5 127.0.0.1 >nul") { CreateNoWindow = true };
    psi.Environment["CLAUDE_CODE_SESSION_ID"] = "sid-123";
    psi.Environment["CLAUDE_CODE_HOST_SESSION_ID"] = "local_abc";
    psi.Environment["CLAUDE_CODE_MESSAGING_TOKEN"] = "secret";
    using var child = Process.Start(psi)!;
    var key = KeyOf(child);   // helper using NtSnapshotSource
    var r = new PebProcessInspector().Inspect(key);
    Assert.Equal(InspectStatus.Ok, r.Status);
    Assert.Equal("sid-123", r.SessionId); Assert.Equal("local_abc", r.HostSessionId);
    Assert.EndsWith(@"\cmd.exe", r.ImagePath, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("ping", r.CommandLine);
    Assert.DoesNotContain("secret", r.ToString());
    child.Kill();
}
[Fact] public void WrongCreateTime_IsGone()
    => Assert.Equal(InspectStatus.Gone, new PebProcessInspector().Inspect(new ProcessKey(Environment.ProcessId, 1)).Status);
[Fact] public void ExitedProcess_IsGone()
{
    using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true })!;
    var key = KeyOf(child); child.WaitForExit(); Thread.Sleep(100);
    Assert.Equal(InspectStatus.Gone, new PebProcessInspector().Inspect(key).Status);
}
[Fact] public void SystemProcess_IsAccessDeniedOrUnsupported_NotThrow()
{
    var r = new PebProcessInspector().Inspect(KeyOfPid(4));
    Assert.NotEqual(InspectStatus.Ok, r.Status);
}
```

`KeyOf` must capture the key **before** the child exits. Have it take the snapshot right after `Process.Start`. For `ExitedProcess_IsGone`, start a child that sleeps 1 s, take the key, then wait for it to exit.

- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the tests. Expected: 4 passed.
- [ ] **Step 5:** Commit `feat(native): PEB inspector with env allowlist`.

---

### Task 5: Attributor

**Files:**
- Create: `src/ProcessTracker.Core/Model/Attribution.cs`, `src/ProcessTracker.Core/Attribution/Attributor.cs`
- Test: `tests/ProcessTracker.Core.Tests/Attribution/AttributorTests.cs`

**Interfaces:**
- Consumes: `RawProcessCounters`, `InspectResult`.
- Produces:
  - `enum AttributionKind { Session, ClaudeApp, Unattributed }`
  - `sealed record Attribution(AttributionKind Kind, string? UnitId, string? ClaudeSessionId, bool IsAnchor)`, with `static Attribution Unattributed`.
  - `sealed class Attributor { Attribution Attribute(RawProcessCounters proc, InspectResult inspect, Attribution? parent); }`

**Rules, first match wins (spec §7, with the verified paths):**
1. `HostSessionId` set → `Session("host:" + HostSessionId, SessionId, IsAnchor: image is claude.exe && SessionId == null)`.
2. `SessionId` set (terminal child) → if `parent` is Session, inherit `UnitId` and set `ClaudeSessionId = SessionId`; else `Session("sid:" + SessionId, SessionId, false)`.
3. Image `claude.exe` and the image path does **not** contain `\WindowsApps\Claude_` → terminal anchor `Session($"term:{Key}", null, true)`.
4. Image path contains `\WindowsApps\Claude_` → `ClaudeApp`.
5. `parent` not null and not Unattributed → inherit `Kind`, `UnitId` and `ClaudeSessionId`, with `IsAnchor: false`.
6. Otherwise → `Unattributed`.

When `inspect.Status != Ok`, only rules 5 and 6 apply.

- [ ] **Step 1: Write the failing tests**, one per rule plus the inspect-failed case:
  - `HostId_OnClaudeExe_WithoutSessionId_IsAnchor`
  - `HostAndSessionId_OnPython_IsSessionNotAnchor` (UnitId `host:local_x`, ClaudeSessionId `sid1`)
  - `SessionIdOnly_InheritsParentUnit`
  - `SessionIdOnly_NoParent_UsesSidUnit` (`sid:sid1`)
  - `TerminalClaudeExe_IsTerminalAnchor` (path `C:\Users\u\.local\bin\claude.exe`, UnitId starts with `term:`)
  - `DesktopElectron_IsClaudeApp` (path `C:\Program Files\WindowsApps\Claude_6.6395.0.0_x64__pzs8sxrjxfjjc\app\Claude.exe`)
  - `NoVars_ChildOfClaudeApp_IsClaudeApp`
  - `NoVars_ChildOfSession_InheritsSession`
  - `AccessDenied_WithSessionParent_Inherits`
  - `AccessDenied_NoParent_Unattributed`
  - `PlainProcess_Unattributed`
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement. Compare image names and paths case-insensitively.
- [ ] **Step 4:** Run the tests. Expected: 11 passed.
- [ ] **Step 5:** Commit `feat(core): session attribution rules`.

---

### Task 6: Activity tracking with tree roll-up

**Files:**
- Create: `src/ProcessTracker.Core/Activity/{ActivityThresholds,ActivityState,ActivityTracker}.cs`, `src/ProcessTracker.Core/Tracking/ProcessTable.cs`
- Test: `tests/ProcessTracker.Core.Tests/Activity/ActivityTrackerTests.cs`

**Interfaces:**
- Produces:
  - `sealed record ActivityThresholds(float CpuCores = 0.01f, long IoOps = 1, long WriteOps = 1, long ContextSwitches = 100)`
  - `sealed class ActivityOptions { ActivityThresholds Default; Dictionary<string, ActivityThresholds> PerImage; }`. The default per-image entries are `MATLAB.exe` → (0.05, 1, 1, 2000) and `Dymola.exe` → (0.05, 1, 1, 2000). Lookup is case-insensitive.
  - `readonly record struct ActivityState(DateTime? LastCpuAt, DateTime? LastIoAt, DateTime? LastWriteAt, DateTime? LastWakeAt)` with a `DateTime? LastActiveAt` property (the maximum of the four).
  - `sealed class ProcessTable { void SetLive(IEnumerable<(ProcessKey Key, ProcessKey? Parent)> live); IEnumerable<ProcessKey> Children(ProcessKey k); IEnumerable<ProcessKey> Descendants(ProcessKey k); bool IsLive(ProcessKey k); ProcessKey? ParentOf(ProcessKey k); }`
  - `sealed class ActivityTracker { ActivityTracker(ActivityOptions o); void Update(in ProcessSample s, string imageName); ActivityState Get(ProcessKey k); void Seed(ProcessKey k, ActivityState s); void Forget(ProcessKey k); DateTime? EffectiveLastActive(ProcessKey k, ProcessTable t); }`

**Rules:**
- CPU fires when `CpuCores > t.CpuCores`.
- Wake fires when `ContextSwitchesDelta > t.ContextSwitches`.
- IO fires when `ReadOpsDelta + OtherOpsDelta >= t.IoOps`.
- Write fires when `WriteOpsDelta >= t.WriteOps`.
- `EffectiveLastActive` = max over the process and all live descendants.

- [ ] **Step 1: Write the failing tests**
  - `CpuAboveThreshold_SetsLastCpuAt`
  - `CpuBelowThreshold_LeavesNull`
  - `WriteOps_SetsWriteAndNotIo`
  - `MatlabJvmNoise_NotActive` (MATLAB.exe, CpuCores 0.03, ctx 1500 → LastActiveAt null)
  - `ImageLookupIsCaseInsensitive` (`matlab.EXE`)
  - `EffectiveLastActive_TakesBusyGrandchild` (idle Dymola, busy dymosim grandchild at T+5 → T+5)
  - `EffectiveLastActive_IgnoresDeadDescendants`
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the tests. Expected: 7 passed.
- [ ] **Step 5:** Commit `feat(core): activity signals and tree roll-up`.

---

### Task 7: Sessions, transcript metadata, orphan classification

**Files:**
- Create: `src/ProcessTracker.Core/Sessions/{SessionInfo,SessionRegistry,TranscriptReader}.cs`, `src/ProcessTracker.Core/Orphans/{ProcessState,OrphanClassifier}.cs`, `src/ProcessTracker.Core/Model/ProcessRecord.cs`
- Test: `tests/ProcessTracker.Core.Tests/Sessions/SessionRegistryTests.cs`, `TranscriptReaderTests.cs`, `tests/ProcessTracker.Core.Tests/Orphans/OrphanClassifierTests.cs`

**Interfaces:**
- Produces:
  - `sealed record ProcessRecord(ProcessKey Key, ProcessKey? Parent, string ImageName, string? ImagePath, string? CommandLine, Attribution Attribution, DateTime FirstSeenUtc, DateTime? ExitedUtc)`
  - `sealed record SessionInfo(string UnitId, string? HostSessionId, IReadOnlyList<string> ClaudeSessionIds, ProcessKey? Anchor, DateTime FirstSeenUtc, DateTime? EndedUtc, string? Cwd, string? Title, DateTime? LastTranscriptWriteUtc)`
  - `sealed class SessionRegistry`, with:
    - `void Observe(ProcessRecord r)` adds the unit, records the anchor and adds the ClaudeSessionId;
    - `void ProcessExited(ProcessKey k, DateTime now)` sets `EndedUtc` when `k` is the anchor;
    - `bool IsLive(string unitId)`, which is true only when an anchor has been observed and has not exited;
    - `SessionInfo? Get(string unitId)`, `IReadOnlyList<SessionInfo> All()`, `void Restore(IEnumerable<SessionInfo>)`;
    - `void ApplyTranscript(string unitId, TranscriptInfo t)`.
  - `sealed record TranscriptInfo(string? Cwd, string? Title, DateTime LastWriteUtc)`
  - `sealed class TranscriptReader { TranscriptReader(string userProfilePath); TranscriptInfo? Read(string claudeSessionId); }`
    - Finds `<profile>\.claude\projects\*\<id>.jsonl`.
    - Reads lines until both values are found, or 256 KB have been read.
    - `Cwd` = the first non-empty `"cwd"` field.
    - `Title` = the first record with `"type":"user"` whose `message.content` is a string (or whose first content item has type `text`), collapsed to one line and truncated to 80 characters.
  - `enum ProcessState { Normal, Detached, Orphan }`
  - `static class OrphanClassifier`, with:
    - `ProcessState Classify(ProcessRecord r, SessionRegistry s, ProcessTable t)`;
    - `bool IsAlertable(ProcessRecord r, ProcessState st, DateTime? effectiveLastActive, DateTime now, TimeSpan idle)`. This is true for `Orphan` with a watched image (Global Constraints) and either idle ≥ `idle` or never active since first seen ≥ `idle` ago.

**Classification:**
- `Orphan`: attribution Session and `!IsLive(unit)`.
- `Detached`: session live, not anchor, and the parent is null or not live.
- Otherwise `Normal`.
- `ClaudeApp` and `Unattributed` are always `Normal`.

- [ ] **Step 1: Write the failing tests**
  - Registry: `IsLive_AnchorObservedAndRunning_True`, `IsLive_AnchorExited_False`, `IsLive_AnchorNeverObserved_False`, `MultipleClaudeSessionIds_CollectUnderOneHostUnit`.
  - Transcript: `Read_TakesFirstCwdAndFirstUserText` (fixture lines copied from the real format: a `bridge-session` line without cwd, an `attachment` line with cwd, a `user` line with string content), `Read_MissingFile_Null`, `Read_TitleTruncatedTo80`.
  - Orphans: `EndedSession_IsOrphan`, `LiveSession_DeadParent_IsDetached`, `LiveSession_Anchor_IsNormal`, `Unattributed_IsNormal`, `Alertable_WatchedOrphanIdle31Min_True`, `Alertable_WatchedOrphanIdle10Min_False`, `Alertable_NonWatchedImage_False`, `Alertable_DymosimWildcard_True` (`dymosim_sweep.exe`).
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement. `TranscriptReader` uses `Utf8JsonReader` per line and skips lines it can't parse.
- [ ] **Step 4:** Run the tests. Expected: 15 passed.
- [ ] **Step 5:** Commit `feat(core): sessions, transcript metadata, orphan rules`.

---

### Task 8: History store (ring and segment files)

**Files:**
- Create: `src/ProcessTracker.Core/Storage/{TickFrame,SampleRing,SegmentFormat,SegmentWriter,SegmentReader}.cs`
- Test: `tests/ProcessTracker.Core.Tests/Storage/{SampleRingTests,SegmentTests}.cs`

**Interfaces:**
- Produces:
  - `sealed record TickFrame(DateTime TimestampUtc, SystemSample System, IReadOnlyList<ProcessSample> Samples, IReadOnlyList<SessionAggregate> Sessions)`
  - `readonly record struct SessionAggregate(string UnitId, DateTime TimestampUtc, float CpuCores, long PrivateBytes, long ReadBytesDelta, long WriteBytesDelta, int ProcessCount)`. This is the per-session total for one tick (spec §6), written on every tick so session history needs no re-summing.
  - `sealed class SampleRing { SampleRing(int capacity); void Add(TickFrame f); IReadOnlyList<TickFrame> Since(DateTime fromUtc); TickFrame? Latest { get; } }`. Capacity is 300 (10 min at 2 s). The storage array is allocated once.
  - `sealed class SegmentWriter : IDisposable`:
    - `SegmentWriter(string dir, TimeSpan retention, ILogger log)`;
    - `void Enqueue(TickFrame f, IReadOnlyList<ProcessRecord> newProcesses)`, which never blocks (bounded `Channel`, capacity 64, drop-oldest);
    - `Task FlushAsync()` (for tests), `void EnforceRetention(DateTime nowUtc)`.
  - `static class SegmentReader { IEnumerable<SegmentRecord> Read(string dir, DateTime fromUtc, DateTime toUtc); }`, where `SegmentRecord` is `SystemRec(SystemSample)`, `ProcessRec(ProcessSample)`, `ProcessInfoRec(ProcessRecord)` or `SessionRec(SessionAggregate)`.

**Format (`SegmentFormat`):**
- File name `yyyyMMdd-HHmm.pts` (UTC, floored to 5 min).
- Header: ASCII `PTSG`, then `u16 version = 1`.
- Then records: `[u8 type][u16 payloadLength][payload]`. Types: 1 = System, 2 = Process sample, 3 = ProcessInfo, 4 = SessionAggregate. Fields are little-endian, in the order of the record struct; strings are a u16 length followed by UTF-8.
- The reader reads the fields it knows and skips the rest of the payload, and skips unknown types. That is what makes the format additive.
- The writer writes a process sample only when the process was active this tick (any delta signal non-zero, or `CpuCores > 0`) or its last written sample is ≥ 60 s old. Sessions are not written here.
- Retention deletes files whose start time + 5 min is earlier than now − retention. Retention below 2 h is clamped to 2 h.

- [ ] **Step 1: Write the failing tests**
  - `Ring_KeepsLast300_InOrder`
  - `Ring_Since_FiltersByTime`
  - `Segment_RoundTripsAllRecordTypes`
  - `Segment_RollsFileEvery5Minutes` (frames at 12:04:58 and 12:05:00 → two files)
  - `Reader_TruncatedTail_ReturnsCompleteRecordsOnly` (chop the last 3 bytes off the file)
  - `Reader_UnknownRecordType_Skipped`, and `Reader_LongerPayloadThanKnown_SkipsExtraBytes` (append 8 bytes inside the payload and bump the length)
  - `Writer_IdleProcess_WritesHeartbeatEvery60s` (30 idle ticks at 2 s → 1 sample row)
  - `Retention_DeletesOldFiles_ClampsTo2h`
  - `Writer_DirectoryUnwritable_LogsAndDoesNotThrow` (point it at a file path instead of a dir)
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement. A single background task drains the channel.
- [ ] **Step 4:** Run the tests. Expected: 10 passed.
- [ ] **Step 5:** Commit `feat(core): ring buffer and versioned segment history`.

---

### Task 9: State store (SQLite)

**Files:**
- Create: `src/ProcessTracker.Core/Storage/StateStore.cs`
- Test: `tests/ProcessTracker.Core.Tests/Storage/StateStoreTests.cs`

**Interfaces:**
- Consumes: `ProcessRecord`, `SessionInfo`, `ActivityState`, `RawProcessCounters`.
- Produces: `sealed class StateStore : IDisposable` with:
  - `StateStore(string dbPath)`. Opens the database, sets `PRAGMA journal_mode=WAL`, `synchronous=NORMAL`, and runs migrations.
  - `void SaveTick(DateTime ts, IReadOnlyList<ProcessRecord> upserts, IReadOnlyList<(ProcessKey, ActivityState, RawProcessCounters)> live, IReadOnlyList<SessionInfo> sessions)`, in one transaction.
  - `PersistedState Load()`, returning `PersistedState(DateTime? LastTickUtc, IReadOnlyList<ProcessRecord> Processes, IReadOnlyDictionary<ProcessKey, (ActivityState, RawProcessCounters)> Live, IReadOnlyList<SessionInfo> Sessions)`.
  - `void RecordKill(KillAudit a)`, with `record KillAudit(DateTime TsUtc, ProcessKey Key, string ImageName, string? UnitId, string CallerImage, bool Tree, string Result)`.
  - `void PruneExitedBefore(DateTime utc)`.

**Schema v1:**
- Tables `meta(key PK, value)`, `processes` (PK `pid, create_ticks`), `live_counters` (PK `pid, create_ticks`; cleared and rewritten each tick), `sessions` (PK `unit_id`), `session_ids(unit_id, claude_session_id)`, `kills`.
- Migrations are an ordered list of SQL steps keyed by `meta.schema_version`. **Only `CREATE TABLE` / `ALTER TABLE ADD COLUMN` are allowed.**
- An older binary seeing a higher `schema_version` logs it and continues; additive changes keep its queries valid.

- [ ] **Step 1: Write the failing tests**
  - `SaveThenLoad_RoundTripsEverything`
  - `Reopen_IsWalMode`
  - `OpeningDbWithHigherSchemaVersion_DoesNotThrow` (set `schema_version=99` and add an extra column, then reopen)
  - `PruneExitedBefore_RemovesOnlyExited`
  - `SaveTick_1000Processes_Under30ms`
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement with `Microsoft.Data.Sqlite` and prepared commands reused across ticks.
- [ ] **Step 4:** Run the tests. Expected: 5 passed.
- [ ] **Step 5:** Commit `feat(core): SQLite state store with additive migrations`.

---

### Task 10: Tracker orchestrator and restart continuity

**Files:**
- Create: `src/ProcessTracker.Core/Tracking/{TrackerOptions,TrackerView,Tracker}.cs`
- Test: `tests/ProcessTracker.Core.Tests/Tracking/TrackerTests.cs` (with `FakeSnapshotSource` and `FakeInspector` in `tests/.../Fakes/`)

**Interfaces:**
- Consumes: everything in Tasks 2 and 4–9.
- Produces:
  - `sealed class TrackerOptions { string DataDir; TimeSpan Retention = 24h; TimeSpan OrphanIdleAlert = 30min; string UserProfilePath; ActivityOptions Activity; }`
  - `sealed record ProcessView(ProcessRecord Record, ProcessSample Latest, ActivityState Activity, DateTime? EffectiveLastActive, ProcessState State, bool Alertable)`
  - `sealed record SessionView(SessionInfo Info, bool IsLive, float CpuCores, long PrivateBytes, int ProcessCount)`
  - `sealed record TrackerView(DateTime TimestampUtc, SystemSample System, IReadOnlyDictionary<ProcessKey, ProcessView> Processes, IReadOnlyList<SessionView> Sessions)` with `static TrackerView Empty`.
  - `sealed class Tracker : IDisposable`:
    - `Tracker(ISnapshotSource, IProcessInspector, TrackerOptions, ILogger<Tracker>, TimeProvider)`;
    - `void Restore()`, `TrackerView Tick()`, `TrackerView View { get; }` (published with `Volatile.Write`);
    - `SampleRing Ring { get; }`, `StateStore State { get; }`.

**Tick sequence:**
1. Take a snapshot.
2. Diff it against the known live keys.
3. **New** processes, ordered by create time so parents come first:
   - resolve the parent key (a live process with `ParentPid` whose create time ≤ the child's);
   - inspect, then attribute using the parent's attribution;
   - `SessionRegistry.Observe`;
   - queue a transcript read for unit sessions with a new ClaudeSessionId (at most 5 reads per tick).
4. Processes whose inspection returned `AccessDenied` or `Unsupported` are recorded and **never re-inspected**. Processes that returned `Gone` are skipped this tick and not recorded.
5. Exited processes: set `ExitedUtc`, call `SessionRegistry.ProcessExited` and `ActivityTracker.Forget`.
6. Compute samples (`SampleCalculator.Compute` against the previous counters, or `First`), update activity, refresh the `ProcessTable`, classify, and build the views.
7. Build one `SessionAggregate` per unit with live processes. Send the frame to the ring and the segment writer; `StateStore.SaveTick`.
8. Publish the view.
9. Once an hour, `SegmentWriter.EnforceRetention(now)` and `StateStore.PruneExitedBefore(now − Retention)`.

**Restore:**
- Load `PersistedState`.
- Keys in the first snapshot that are present in persisted `Live` reuse their record, activity, and counters. Their previous timestamp is `LastTickUtc`, so the first delta covers the downtime.
- Persisted live keys not in the snapshot get `ExitedUtc` = restore time.
- Sessions are restored before the first tick.

- [ ] **Step 1: Write the failing tests**
  - `Tick_NewSessionProcess_AppearsWithAttribution`
  - `Tick_ChildInheritsFromParentInSameSnapshot`
  - `Tick_AnchorExits_ChildrenBecomeOrphans`
  - `Tick_Gone_SkippedAndRetriedNextTick`
  - `Tick_AccessDenied_InspectedOnlyOnce` (FakeInspector counts calls)
  - `Restore_ContinuesDeltasAcrossDowntime` (counters persisted at T, restored at T+5 s with +5 s of CPU → CpuCores≈1.0 on the first tick)
  - `Restore_PidReusedWithDifferentCreateTime_TreatsAsNewProcess`
  - `Restore_ProcessGoneDuringDowntime_MarkedExited`
  - `Tick_PreexistingOrphan_AnchorNeverSeen_IsOrphanFirstTick`
  - `View_IsReplacedNotMutated` (a view captured before tick 2 is unchanged after it)
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the tests. Expected: 10 passed.
- [ ] **Step 5:** Commit `feat(core): tracker tick pipeline and restart continuity`.

---

### Task 11: Load assessment

**Files:**
- Create: `src/ProcessTracker.Core/Load/{LoadOptions,LoadReport,LoadAssessor}.cs`
- Test: `tests/ProcessTracker.Core.Tests/Load/LoadAssessorTests.cs`

**Interfaces:**
- Produces:
  - `enum Pressure { Low, Moderate, High, Critical }`
  - `sealed record SessionUsage(string UnitId, string? Title, string? Cwd, float CpuCores, double PrivateGb, int ProcessCount)`
  - `sealed record LoadReport(DateTime TimestampUtc, Pressure Pressure, float CpuBusyPercent, float FreeCores, double AvailableGb, double CommitHeadroomGb, int SuggestedParallelism, SessionUsage? Caller, IReadOnlyList<SessionUsage> TopSessions)`
  - `sealed record LoadOptions(double PerTaskGb = 2.0, int TopSessions = 5)`
  - `static class LoadAssessor { LoadReport Assess(TrackerView view, IReadOnlyList<TickFrame> recent, string? callerUnitId, LoadOptions o); }`

**Formula (default values for spec §16; keep them as named constants):**
- `cpu` = mean `CpuBusyPercent` over `recent` (the last 5 frames).
- `h` = `(CommitLimit − CommitCharge) / CommitLimit`.
- `headroomGb` = `(CommitLimit − CommitCharge) / 2³⁰`.
- Pressure:
  - **Critical** if `h < 0.05 || headroomGb < 2`;
  - **High** if `h < 0.15 || cpu > 90`;
  - **Moderate** if `h < 0.30 || cpu > 70`;
  - otherwise **Low**.
- `FreeCores` = `LogicalCpuCount × (1 − cpu/100)`.
- `SuggestedParallelism` = 1 if High or Critical, else `max(1, min(floor(FreeCores), floor(headroomGb / PerTaskGb)))`.
- `TopSessions` = live sessions ordered by `PrivateBytes`, descending.

- [ ] **Step 1: Write the failing tests**
  - `LowLoad_ParallelismLimitedByCoresAndMemory` (16 cores at 10 %, headroom 9 GB → 4)
  - `CommitBelow5Percent_IsCritical_Parallelism1`
  - `CpuSpikeInOneFrame_AveragedOut_NotHigh` (one frame at 100 %, four at 20 %)
  - `Caller_IsFilledFromUnit`
  - `TopSessions_OrderedByMemory_ExcludesEnded`
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the tests. Expected: 5 passed.
- [ ] **Step 5:** Commit `feat(core): load assessment`.

---

### Task 12: Service host, collector worker, REST API

**Files:**
- Create: `src/ProcessTracker.Service/{Program,ServiceOptions,TrackerHost,CollectorWorker}.cs`, `Api/RestEndpoints.cs`, `appsettings.json`, `src/ProcessTracker.Core/Contracts/Dtos.cs`
- Test: `tests/ProcessTracker.Service.Tests/RestEndpointsTests.cs`

**Interfaces:**
- Consumes: `Tracker`, `TrackerView`, `LoadAssessor`, `SegmentReader`, `StateStore.RecordKill`.
- Produces:
  - `interface ITrackerHost { TrackerView View { get; } IReadOnlyList<TickFrame> Recent(int n); IEnumerable<SegmentRecord> History(DateTime from, DateTime to); void RecordKill(KillAudit a); DateTime StartedUtc { get; } }`, implemented by `TrackerHost`. Tests use `FakeTrackerHost`.
  - `ICallerResolver`, defined in Task 13. In this task, register a stub that returns `null`.
  - DTOs in `ProcessTracker.Core.Contracts`: `ProcessViewDto`, `SessionViewDto`, `SessionTreeDto`, `HistorySeriesDto`, `HealthDto`, `AttributionDto`, `KillAuditDto`. These are plain records mirroring the views, with `ProcessKey` rendered as a string (`pid@ticks`) and timestamps in ISO-8601 UTC. `LoadReport` is serialized as-is.

**Behaviour:**
- `Program`:
  - `--console` runs as a console app; otherwise `UseWindowsService(o => o.ServiceName = "ProcessTracker")`.
  - `--dev` selects port 47812, the dev data dir, no mutex, and no auto-registration.
  - Otherwise take the `Global\ProcessTracker.Service` mutex, and exit with code 2 if it is already held.
  - Configuration: `appsettings.json` next to the exe, then `%ProgramData%\ProcessTracker\appsettings.json`, then the command line.
  - `Kestrel.Listen(IPAddress.Loopback, port)` only.
  - Process priority AboveNormal. The runtimeconfig sets `System.GC.HeapHardLimit` to `0xC000000` and `System.GC.Concurrent` to false (in the csproj via `RuntimeHostConfigurationOption`).
- `CollectorWorker`:
  - a `PeriodicTimer` at 2 s; calls `Tracker.Restore()` once, then `Tick()` in a loop;
  - catches and logs per-tick exceptions and continues;
  - logs a warning when a tick takes more than 500 ms;
  - on stop, flushes the segment writer and the state store (the graceful-stop half of spec §13).
- Endpoints:
  - `GET /api/health` → `{ version: <InformationalVersion>, startedUtc, lastTickUtc, tickAgeSeconds }`
  - `GET /api/load` → `LoadReport`
  - `GET /api/sessions` → `SessionView[]`
  - `GET /api/sessions/{unitId}` → the session plus its process tree (nested children)
  - `GET /api/orphans` → `ProcessView[]` where State is Orphan, ordered by `Alertable` then private bytes
  - `GET /api/processes/{pid}` → live processes with that PID (0 or 1)
  - `GET /api/processes/{key}` → the view for that `pid@ticks` key
  - `GET /api/history?unit=&key=&from=&to=` → series. `from`/`to` default to the last 30 min, and the range is clamped to the retention window.
  - `GET /api/caller` → the caller's `Attribution` via `ICallerResolver`
  - `POST /api/audit/kill` → 204
  - Unknown unit or key → 404.

- [ ] **Step 1: Write the failing tests** (WebApplicationFactory with `FakeTrackerHost`):
  - `Health_ReportsVersionAndTickAge`
  - `Orphans_OnlyOrphansAlertableFirst`
  - `SessionTree_NestsChildren`
  - `History_DefaultsToLast30Minutes`
  - `UnknownSession_404`
  - `AuditKill_IsRecorded`
  - `ListensOnLoopbackOnly`. This one starts the real host with `--dev`, then asserts that a connection to the machine's LAN IP on 47812 is refused, and that 127.0.0.1 answers.
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the tests. Expected: 7 passed. Then run `dotnet run --project src/ProcessTracker.Service -- --console --dev` and `curl http://127.0.0.1:47812/api/sessions`. Expected: this Claude session appears with UnitId `host:local_…`, live, and with a non-zero process count.
- [ ] **Step 5:** Commit `feat(service): host, collector loop, REST API`.

---

### Task 13: Caller resolution and MCP endpoint

**Files:**
- Create: `src/ProcessTracker.Native/TcpOwnerLookup.cs`, `src/ProcessTracker.Service/CallerResolver.cs`, `src/ProcessTracker.Service/Mcp/ProcessTrackerTools.cs`
- Test: `tests/ProcessTracker.Native.Tests/TcpOwnerLookupTests.cs`, `tests/ProcessTracker.Service.Tests/{CallerResolverTests,McpToolsTests}.cs`

**Interfaces:**
- Produces:
  - `static class TcpOwnerLookup { int? OwnerOfLoopbackClient(int clientPort, int serverPort); }`. Uses `GetExtendedTcpTable(AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS)` and finds the row whose local port is `clientPort` and remote port is `serverPort`.
  - `interface ICallerResolver { Attribution? Resolve(HttpContext ctx); }`
  - `CallerResolver`:
    - PID from `TcpOwnerLookup`, then the live `ProcessView` with that PID, then its `Attribution`;
    - if that is not `Session` and the request has an `X-Claude-Session` header, return `Session("host:" + header, null, false)`;
    - otherwise return whatever was found.
  - MCP tools on `ProcessTrackerTools` (`[McpServerToolType]`), all read-only, returning the same DTOs as REST:
    - `get_machine_load()`, `get_my_session()`, `list_sessions()`, `list_orphans()`;
    - `get_process_history(string key, int minutes = 30)`.
    - Each tool's `[Description]` states its units: cores, GB, UTC.
- Registration: `AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithTools<ProcessTrackerTools>()`, then `app.MapMcp("/mcp")`.

- [ ] **Step 1: Write the failing tests**
  - `TcpOwnerLookup_FindsOurOwnClientSocket` (open a `TcpListener` on loopback and connect with a `TcpClient` → owner = our PID)
  - `Resolver_TcpOwnerIsSessionProcess_ReturnsIt`
  - `Resolver_TcpOwnerIsClaudeApp_FallsBackToHeader`
  - `Resolver_NoMatch_NoHeader_Null`
  - `Mcp_ListsFiveTools_AllReadOnly` (MCP client over the test server: `ListToolsAsync`, then assert the names, and that no name contains "kill")
  - `Mcp_GetMachineLoad_ReturnsPressure`
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the tests. Expected: 6 passed.
- [ ] **Step 5: Manual verification (spec §11 Phase 1 check).**
  1. Run the dev host.
  2. `claude mcp add --transport http --scope user process-tracker-dev http://127.0.0.1:47812/mcp`
  3. In a fresh desktop session, ask it to call `get_my_session`.
  4. Expected: the UnitId equals that session's `CLAUDE_CODE_HOST_SESSION_ID`.
  5. If instead the resolved PID is the desktop Electron app, record that in the spec (§11) and confirm the header fallback works.
  6. Remove the dev registration afterwards.
- [ ] **Step 6:** Commit `feat(service): caller identity and stateless MCP endpoint`.

---

### Task 14: `proctrack` CLI with kill guard

**Files:**
- Create: `src/ProcessTracker.Cli/{Program,ApiClient,KillGuard}.cs`, `Commands/{Load,Sessions,Orphans,Tree,History,Kill}Command.cs`
- Test: `tests/ProcessTracker.Cli.Tests/KillGuardTests.cs`

**Interfaces:**
- Consumes: REST DTOs (Task 12), `/api/caller`, `/api/audit/kill`.
- Produces:
  - `static class KillGuard { KillDecision Check(Attribution? caller, ProcessViewDto target, bool tree, IReadOnlyList<ProcessViewDto> treeMembers); }`
  - `record KillDecision(bool Allowed, string? Reason)`

**Behaviour:**
- Base URL: `PROCTRACK_URL`, or `http://127.0.0.1:47811`, or `:47812` with `--dev`.
- Commands: `load`, `sessions`, `orphans`, `tree [unit]`, `history (--unit U | --key K) [--minutes 30]`, `kill <pid> [--tree] [--yes]`. All except `kill` accept `--json`. Tables use fixed-width columns: unit/title, CPU (cores), memory (GB), IO, idle, state.
- `kill`:
  1. Fetch the target by PID.
  2. Re-read the live create time with `Process.GetProcessById(pid).StartTime.ToUniversalTime().ToFileTimeUtc()` and abort if it differs from the key.
  3. Apply `KillGuard`. When the caller is a `Session`, every target (and every tree member) must have the same UnitId. A caller that is `ClaudeApp` or not attributed is allowed.
  4. Confirm (y/N) unless `--yes`.
  5. `Process.Kill(entireProcessTree: tree)`.
  6. Post the audit record with the result.
- Exit codes: 0 ok, 1 refused, 2 service unreachable.

- [ ] **Step 1: Write the failing tests**
  - `SessionCaller_OwnProcess_Allowed`
  - `SessionCaller_OtherSessionOrphan_Refused` (Reason mentions the tray or a terminal outside Claude)
  - `SessionCaller_TreeContainsForeignProcess_Refused`
  - `TerminalOutsideClaude_Allowed`
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement the guard and the commands.
- [ ] **Step 4:** Run the tests. Expected: 4 passed. Against the dev host:
  - `dotnet run --project src/ProcessTracker.Cli -- orphans --dev` prints a table;
  - `... kill <pid of a cmd.exe started from another terminal> --dev` from inside this session → refused, exit code 1.
- [ ] **Step 5:** Commit `feat(cli): proctrack with session kill guard`.

---

### Task 15: Updater, install, deploy, uninstall

**Files:**
- Create: `src/ProcessTracker.Updater/{Program,UpgradeOrchestrator,IServiceControl,IJunction,IHealthProbe,WindowsServiceControl,CmdJunction,HttpHealthProbe}.cs`, `scripts/{install,deploy,uninstall}.ps1`
- Test: `tests/ProcessTracker.Updater.Tests/UpgradeOrchestratorTests.cs`

**Interfaces:**
- Produces:
  - `interface IServiceControl { void Stop(TimeSpan timeout); void Start(); }`
  - `interface IJunction { string? Target(string link); void Point(string link, string target); }`. `CmdJunction` uses `cmd /c rmdir` then `cmd /c mklink /J`.
  - `interface IHealthProbe { Task<HealthResult?> GetAsync(); }`, with `record HealthResult(string Version, double TickAgeSeconds)`.
  - `sealed class UpgradeOrchestrator { Task<UpgradeOutcome> RunAsync(string installRoot, string newVersion, TimeSpan healthTimeout); }`, with `enum UpgradeOutcome { Upgraded, RolledBack, RollbackFailed }`.
  - Updater CLI: `ProcessTracker.Updater.exe --root "C:\Program Files\ProcessTracker" --version <v> [--timeout 15]`. Exit codes 0 / 1 / 2 match the outcomes.

**Orchestration:**
1. Remember the previous target.
2. `Stop(10 s)`.
3. Point `current` at `versions\<new>`.
4. `Start()`.
5. Poll health every 500 ms until `Version == new && TickAgeSeconds < 5`, within the timeout.
6. On failure: stop, point back to the previous target, start, poll health for the old version, and return `RolledBack` (or `RollbackFailed`).
7. On success: prune `versions\` to the 3 newest by directory creation time, never deleting the current or previous target.

**Scripts** (each checks for elevation and exits with a clear message if not elevated):
- `deploy.ps1`:
  1. Run `dotnet test` and stop on failure.
  2. Version = `VersionPrefix` + `-local.yyyyMMddHHmm`.
  3. `dotnet publish src/ProcessTracker.Service -c Release -r win-x64 --self-contained -p:Version=<v> -o "<root>\versions\<v>"`; publish the CLI and the Updater into the same folder.
  4. Copy the Updater to `%TEMP%` and run it from there, so it is not inside a folder it swaps.
  5. Print the outcome.
- `install.ps1`:
  1. Do everything up to the publish step as in `deploy.ps1`, then create the junction.
  2. `New-Service -Name ProcessTracker -BinaryPathName '"C:\Program Files\ProcessTracker\current\ProcessTracker.Service.exe"' -StartupType Automatic`.
  3. `sc.exe failure ProcessTracker reset= 86400 actions= restart/5000/restart/5000/restart/30000`.
  4. Write `%ProgramData%\ProcessTracker\appsettings.json` with `UserProfilePath` = `$env:USERPROFILE`.
  5. Add `<root>\current` to the user PATH (so `proctrack` resolves).
  6. Start the service and wait for health.
  7. `claude mcp add --transport http --scope user process-tracker http://127.0.0.1:47811/mcp --header "X-Claude-Session: ${CLAUDE_CODE_HOST_SESSION_ID}"`. Pass the header literally, unexpanded; Claude Code expands it.
- `uninstall.ps1`: stop and delete the service, `claude mcp remove process-tracker --scope user`, remove the PATH entry. Keep `%ProgramData%` unless `-PurgeData` is given.

- [ ] **Step 1: Write the failing tests**
  - `HealthyNewVersion_Upgraded_PrunesToThree`
  - `NewVersionNeverHealthy_RolledBackToPrevious`
  - `StartThrows_RolledBack`
  - `RollbackAlsoUnhealthy_RollbackFailed`
  - `Prune_NeverDeletesCurrentOrPrevious`
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement the orchestrator, the Windows adapters and the scripts.
- [ ] **Step 4:** Run the tests. Expected: 5 passed.
- [ ] **Step 5: End-to-end on this machine.** Run each step from an elevated PowerShell.
  1. `scripts\install.ps1`. Expected: `proctrack sessions` lists the live desktop sessions.
  2. `scripts\deploy.ps1 -SimulateUnhealthy`. This publishes with `-p:DefineConstants=SIMULATE_UNHEALTHY`, which makes `/api/health` report version `unhealthy`; the constant exists for this test only. Expected: `RolledBack`, and the old version is serving.
  3. `scripts\deploy.ps1` with a normal build. Expected: `Upgraded`; `proctrack orphans` still shows the orphans and first-seen times from before the upgrade, and the history query across the upgrade time shows no more than a 5 s gap.
- [ ] **Step 6:** Commit `feat(deploy): updater with health-checked rollback and install scripts`.
