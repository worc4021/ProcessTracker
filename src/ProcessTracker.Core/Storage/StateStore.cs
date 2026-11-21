using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ProcessTracker.Core.Activity;
using ProcessTracker.Core.Model;
using ProcessTracker.Core.Sessions;
using Attr = ProcessTracker.Core.Model.Attribution;

namespace ProcessTracker.Core.Storage;

/// <summary>Everything the tracker needs to resume after a restart.</summary>
public sealed record PersistedState(
    DateTime? LastTickUtc,
    IReadOnlyList<ProcessRecord> Processes,
    IReadOnlyDictionary<ProcessKey, (ActivityState Activity, RawProcessCounters Counters)> Live,
    IReadOnlyList<SessionInfo> Sessions);

/// <summary>Audit row for a kill performed by the CLI and reported to the service.</summary>
public sealed record KillAudit(
    DateTime TsUtc, ProcessKey Key, string ImageName, string? UnitId, string CallerImage, bool Tree, string Result);

/// <summary>
/// SQLite persistence for process records, sessions, live counters and kill audit.
/// Schema changes are additive only (CREATE TABLE / ALTER TABLE ADD COLUMN) so an older binary keeps working;
/// every statement uses explicit column lists so unknown extra columns are harmless.
/// All times are stored as UTC ticks (INTEGER); nullable times as NULL.
/// </summary>
public sealed class StateStore : IDisposable
{
    // Ordered, append-only. Index i migrates schema_version i -> i+1. Only CREATE TABLE / ALTER TABLE ADD COLUMN.
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE processes(
          pid INTEGER NOT NULL, create_ticks INTEGER NOT NULL,
          parent_pid INTEGER, parent_create_ticks INTEGER,
          image_name TEXT NOT NULL, image_path TEXT, command_line TEXT,
          kind INTEGER NOT NULL, unit_id TEXT, claude_session_id TEXT, is_anchor INTEGER NOT NULL,
          first_seen_ticks INTEGER NOT NULL, exited_ticks INTEGER,
          PRIMARY KEY(pid, create_ticks));
        CREATE TABLE live_counters(
          pid INTEGER NOT NULL, create_ticks INTEGER NOT NULL,
          parent_pid INTEGER NOT NULL, image_name TEXT NOT NULL,
          kernel_time INTEGER NOT NULL, user_time INTEGER NOT NULL,
          private_bytes INTEGER NOT NULL, working_set INTEGER NOT NULL,
          read_ops INTEGER NOT NULL, write_ops INTEGER NOT NULL, other_ops INTEGER NOT NULL,
          read_bytes INTEGER NOT NULL, write_bytes INTEGER NOT NULL, other_bytes INTEGER NOT NULL,
          context_switches INTEGER NOT NULL, handle_count INTEGER NOT NULL, thread_count INTEGER NOT NULL,
          last_cpu_ticks INTEGER, last_io_ticks INTEGER, last_write_ticks INTEGER, last_wake_ticks INTEGER,
          PRIMARY KEY(pid, create_ticks));
        CREATE TABLE sessions(
          unit_id TEXT NOT NULL PRIMARY KEY, host_session_id TEXT,
          anchor_pid INTEGER, anchor_create_ticks INTEGER,
          first_seen_ticks INTEGER NOT NULL, ended_ticks INTEGER,
          cwd TEXT, title TEXT, last_transcript_ticks INTEGER);
        CREATE TABLE session_ids(
          unit_id TEXT NOT NULL, claude_session_id TEXT NOT NULL,
          PRIMARY KEY(unit_id, claude_session_id));
        CREATE TABLE kills(
          id INTEGER PRIMARY KEY AUTOINCREMENT, ts_ticks INTEGER NOT NULL,
          pid INTEGER NOT NULL, create_ticks INTEGER NOT NULL, image_name TEXT NOT NULL,
          unit_id TEXT, caller_image TEXT NOT NULL, tree INTEGER NOT NULL, result TEXT NOT NULL);
        """,
        // The desktop app's session metadata (SessionInfo.Desktop); all NULL when there is none.
        """
        ALTER TABLE sessions ADD COLUMN desktop_title TEXT;
        ALTER TABLE sessions ADD COLUMN desktop_branch TEXT;
        ALTER TABLE sessions ADD COLUMN desktop_worktree TEXT;
        ALTER TABLE sessions ADD COLUMN desktop_cwd TEXT;
        ALTER TABLE sessions ADD COLUMN desktop_origin_cwd TEXT;
        ALTER TABLE sessions ADD COLUMN desktop_summary TEXT;
        ALTER TABLE sessions ADD COLUMN desktop_focused_ticks INTEGER;
        """,
    ];

    private readonly object _lock = new();
    private readonly SqliteConnection _db;
    private readonly ILogger? _log;
    private SqliteCommand? _upsertProc, _insertLive, _upsertSession, _deleteSessionIds, _insertSessionId, _setMeta;

    public StateStore(string dbPath, ILogger? log = null)
    {
        _log = log;
        _db = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        _db.Open();
        Exec("PRAGMA journal_mode=WAL");
        Exec("PRAGMA synchronous=NORMAL");
        Migrate();
    }

    private void Exec(string sql)
    {
        using var c = _db.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    private void Migrate()
    {
        Exec("CREATE TABLE IF NOT EXISTS meta(key TEXT NOT NULL PRIMARY KEY, value TEXT)");
        int current;
        using (var c = _db.CreateCommand())
        {
            c.CommandText = "SELECT value FROM meta WHERE key='schema_version'";
            current = c.ExecuteScalar() is string v && int.TryParse(v, out var n) ? n : 0;
        }

        if (current > Migrations.Length)
        {
            _log?.LogWarning(
                "State database schema_version {Found} is newer than supported {Supported}; continuing without migration.",
                current, Migrations.Length);
            return;
        }

        for (var i = current; i < Migrations.Length; i++)
        {
            using var tx = _db.BeginTransaction();
            using var c = _db.CreateCommand();
            c.Transaction = tx;
            c.CommandText = Migrations[i];
            c.ExecuteNonQuery();
            c.CommandText =
                "INSERT INTO meta(key,value) VALUES('schema_version',@v) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            c.Parameters.AddWithValue("@v", (i + 1).ToString());
            c.ExecuteNonQuery();
            tx.Commit();
        }
    }

    private SqliteCommand Prepare(ref SqliteCommand? slot, string sql, params string[] names)
    {
        if (slot is null)
        {
            var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            foreach (var n in names) cmd.Parameters.Add(new SqliteParameter(n, DBNull.Value));
            cmd.Prepare();
            slot = cmd;
        }
        return slot;
    }

    private static object Db(object? v) => v ?? DBNull.Value;
    private static object Ticks(DateTime? d) => d is null ? DBNull.Value : d.Value.ToUniversalTime().Ticks;
    private static DateTime? ReadTicks(SqliteDataReader r, int i) =>
        r.IsDBNull(i) ? null : new DateTime(r.GetInt64(i), DateTimeKind.Utc);
    private static string? ReadString(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    /// <summary>The seven desktop columns starting at <paramref name="i"/>; null when all are NULL.</summary>
    private static DesktopSessionInfo? ReadDesktop(SqliteDataReader r, int i)
    {
        var d = new DesktopSessionInfo(ReadString(r, i), ReadString(r, i + 1), ReadString(r, i + 2), ReadString(r, i + 3),
            ReadString(r, i + 4), ReadString(r, i + 5), ReadTicks(r, i + 6));
        return d == Empty ? null : d;
    }

    private static readonly DesktopSessionInfo Empty = new(null, null, null, null, null, null, null);

    /// <summary>
    /// Upserts process records and sessions in one transaction. When <paramref name="live"/> is given, the live
    /// counters are replaced by it and <c>last_tick_ticks</c> is set to <paramref name="ts"/> in the same transaction,
    /// so a restore always pairs counters with the time they were taken; when null, both are left as they are.
    /// </summary>
    public void SaveTick(
        DateTime ts,
        IReadOnlyList<ProcessRecord> upserts,
        IEnumerable<(ProcessKey, ActivityState, RawProcessCounters)>? live,
        IReadOnlyList<SessionInfo> sessions)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();

            var proc = Prepare(ref _upsertProc,
                """
                INSERT INTO processes(pid,create_ticks,parent_pid,parent_create_ticks,image_name,image_path,command_line,
                                      kind,unit_id,claude_session_id,is_anchor,first_seen_ticks,exited_ticks)
                VALUES(@pid,@ct,@ppid,@pct,@img,@path,@cmd,@kind,@unit,@csid,@anchor,@first,@exit)
                ON CONFLICT(pid,create_ticks) DO UPDATE SET
                  parent_pid=excluded.parent_pid, parent_create_ticks=excluded.parent_create_ticks,
                  image_name=excluded.image_name, image_path=excluded.image_path, command_line=excluded.command_line,
                  kind=excluded.kind, unit_id=excluded.unit_id, claude_session_id=excluded.claude_session_id,
                  is_anchor=excluded.is_anchor, first_seen_ticks=excluded.first_seen_ticks, exited_ticks=excluded.exited_ticks
                """,
                "@pid", "@ct", "@ppid", "@pct", "@img", "@path", "@cmd", "@kind", "@unit", "@csid", "@anchor", "@first", "@exit");
            proc.Transaction = tx;
            foreach (var p in upserts)
            {
                var v = proc.Parameters;
                v["@pid"].Value = p.Key.Pid;
                v["@ct"].Value = p.Key.CreateTimeTicks;
                v["@ppid"].Value = p.Parent is { } par ? par.Pid : DBNull.Value;
                v["@pct"].Value = p.Parent is { } par2 ? par2.CreateTimeTicks : DBNull.Value;
                v["@img"].Value = p.ImageName;
                v["@path"].Value = Db(p.ImagePath);
                v["@cmd"].Value = Db(p.CommandLine);
                v["@kind"].Value = (int)p.Attribution.Kind;
                v["@unit"].Value = Db(p.Attribution.UnitId);
                v["@csid"].Value = Db(p.Attribution.ClaudeSessionId);
                v["@anchor"].Value = p.Attribution.IsAnchor ? 1 : 0;
                v["@first"].Value = Ticks(p.FirstSeenUtc);
                v["@exit"].Value = Ticks(p.ExitedUtc);
                proc.ExecuteNonQuery();
            }

            if (live is not null) SaveLive(tx, ts, live);

            var ses = Prepare(ref _upsertSession,
                """
                INSERT INTO sessions(unit_id,host_session_id,anchor_pid,anchor_create_ticks,first_seen_ticks,ended_ticks,cwd,title,last_transcript_ticks,
                  desktop_title,desktop_branch,desktop_worktree,desktop_cwd,desktop_origin_cwd,desktop_summary,desktop_focused_ticks)
                VALUES(@unit,@host,@apid,@act,@first,@ended,@cwd,@title,@lt,@dt,@db,@dw,@dc,@do,@ds,@df)
                ON CONFLICT(unit_id) DO UPDATE SET
                  host_session_id=excluded.host_session_id, anchor_pid=excluded.anchor_pid,
                  anchor_create_ticks=excluded.anchor_create_ticks, first_seen_ticks=excluded.first_seen_ticks,
                  ended_ticks=excluded.ended_ticks, cwd=excluded.cwd, title=excluded.title,
                  last_transcript_ticks=excluded.last_transcript_ticks,
                  desktop_title=excluded.desktop_title, desktop_branch=excluded.desktop_branch,
                  desktop_worktree=excluded.desktop_worktree, desktop_cwd=excluded.desktop_cwd,
                  desktop_origin_cwd=excluded.desktop_origin_cwd, desktop_summary=excluded.desktop_summary,
                  desktop_focused_ticks=excluded.desktop_focused_ticks
                """,
                "@unit", "@host", "@apid", "@act", "@first", "@ended", "@cwd", "@title", "@lt",
                "@dt", "@db", "@dw", "@dc", "@do", "@ds", "@df");
            ses.Transaction = tx;
            var delIds = Prepare(ref _deleteSessionIds, "DELETE FROM session_ids WHERE unit_id=@unit", "@unit");
            delIds.Transaction = tx;
            var insId = Prepare(ref _insertSessionId,
                "INSERT OR IGNORE INTO session_ids(unit_id,claude_session_id) VALUES(@unit,@sid)", "@unit", "@sid");
            insId.Transaction = tx;
            foreach (var s in sessions)
            {
                var v = ses.Parameters;
                v["@unit"].Value = s.UnitId;
                v["@host"].Value = Db(s.HostSessionId);
                v["@apid"].Value = s.Anchor is { } a ? a.Pid : DBNull.Value;
                v["@act"].Value = s.Anchor is { } a2 ? a2.CreateTimeTicks : DBNull.Value;
                v["@first"].Value = Ticks(s.FirstSeenUtc);
                v["@ended"].Value = Ticks(s.EndedUtc);
                v["@cwd"].Value = Db(s.Cwd);
                v["@title"].Value = Db(s.Title);
                v["@lt"].Value = Ticks(s.LastTranscriptWriteUtc);
                var d = s.Desktop;
                v["@dt"].Value = Db(d?.Title);
                v["@db"].Value = Db(d?.Branch);
                v["@dw"].Value = Db(d?.WorktreeName);
                v["@dc"].Value = Db(d?.Cwd);
                v["@do"].Value = Db(d?.OriginCwd);
                v["@ds"].Value = Db(d?.Summary);
                v["@df"].Value = Ticks(d?.LastFocusedUtc);
                ses.ExecuteNonQuery();

                delIds.Parameters["@unit"].Value = s.UnitId;
                delIds.ExecuteNonQuery();
                foreach (var id in s.ClaudeSessionIds)
                {
                    insId.Parameters["@unit"].Value = s.UnitId;
                    insId.Parameters["@sid"].Value = id;
                    insId.ExecuteNonQuery();
                }
            }

            tx.Commit();
        }
    }

    /// <summary>Replaces every live-counter row and sets <c>last_tick_ticks</c>, inside the caller's transaction.</summary>
    private void SaveLive(SqliteTransaction tx, DateTime ts, IEnumerable<(ProcessKey, ActivityState, RawProcessCounters)> live)
    {
        using (var del = _db.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM live_counters";
            del.ExecuteNonQuery();
        }

        var lc = Prepare(ref _insertLive,
            """
            INSERT INTO live_counters(pid,create_ticks,parent_pid,image_name,kernel_time,user_time,private_bytes,working_set,
                                      read_ops,write_ops,other_ops,read_bytes,write_bytes,other_bytes,
                                      context_switches,handle_count,thread_count,
                                      last_cpu_ticks,last_io_ticks,last_write_ticks,last_wake_ticks)
            VALUES(@pid,@ct,@ppid,@img,@kt,@ut,@pb,@ws,@ro,@wo,@oo,@rb,@wb,@ob,@cs,@hc,@tc,@lcpu,@lio,@lwr,@lwk)
            ON CONFLICT(pid,create_ticks) DO UPDATE SET
              parent_pid=excluded.parent_pid, image_name=excluded.image_name, kernel_time=excluded.kernel_time,
              user_time=excluded.user_time, private_bytes=excluded.private_bytes, working_set=excluded.working_set,
              read_ops=excluded.read_ops, write_ops=excluded.write_ops, other_ops=excluded.other_ops,
              read_bytes=excluded.read_bytes, write_bytes=excluded.write_bytes, other_bytes=excluded.other_bytes,
              context_switches=excluded.context_switches, handle_count=excluded.handle_count, thread_count=excluded.thread_count,
              last_cpu_ticks=excluded.last_cpu_ticks, last_io_ticks=excluded.last_io_ticks,
              last_write_ticks=excluded.last_write_ticks, last_wake_ticks=excluded.last_wake_ticks
            """,
            "@pid", "@ct", "@ppid", "@img", "@kt", "@ut", "@pb", "@ws", "@ro", "@wo", "@oo", "@rb", "@wb", "@ob",
            "@cs", "@hc", "@tc", "@lcpu", "@lio", "@lwr", "@lwk");
        lc.Transaction = tx;
        foreach (var (key, act, raw) in live)
        {
            var v = lc.Parameters;
            v["@pid"].Value = key.Pid;
            v["@ct"].Value = key.CreateTimeTicks;
            v["@ppid"].Value = raw.ParentPid;
            v["@img"].Value = raw.ImageName;
            v["@kt"].Value = raw.KernelTime;
            v["@ut"].Value = raw.UserTime;
            v["@pb"].Value = raw.PrivateBytes;
            v["@ws"].Value = raw.WorkingSet;
            v["@ro"].Value = raw.ReadOps;
            v["@wo"].Value = raw.WriteOps;
            v["@oo"].Value = raw.OtherOps;
            v["@rb"].Value = raw.ReadBytes;
            v["@wb"].Value = raw.WriteBytes;
            v["@ob"].Value = raw.OtherBytes;
            v["@cs"].Value = raw.ContextSwitches;
            v["@hc"].Value = raw.HandleCount;
            v["@tc"].Value = raw.ThreadCount;
            v["@lcpu"].Value = Ticks(act.LastCpuAt);
            v["@lio"].Value = Ticks(act.LastIoAt);
            v["@lwr"].Value = Ticks(act.LastWriteAt);
            v["@lwk"].Value = Ticks(act.LastWakeAt);
            lc.ExecuteNonQuery();
        }

        var meta = Prepare(ref _setMeta,
            "INSERT INTO meta(key,value) VALUES('last_tick_ticks',@v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", "@v");
        meta.Transaction = tx;
        meta.Parameters["@v"].Value = ts.ToUniversalTime().Ticks.ToString();
        meta.ExecuteNonQuery();
    }

    public PersistedState Load()
    {
        lock (_lock)
        {
            DateTime? lastTick = null;
            using (var c = _db.CreateCommand())
            {
                c.CommandText = "SELECT value FROM meta WHERE key='last_tick_ticks'";
                if (c.ExecuteScalar() is string s && long.TryParse(s, out var t))
                    lastTick = new DateTime(t, DateTimeKind.Utc);
            }

            var procs = new List<ProcessRecord>();
            using (var c = _db.CreateCommand())
            {
                c.CommandText =
                    "SELECT pid,create_ticks,parent_pid,parent_create_ticks,image_name,image_path,command_line," +
                    "kind,unit_id,claude_session_id,is_anchor,first_seen_ticks,exited_ticks FROM processes";
                using var r = c.ExecuteReader();
                while (r.Read())
                {
                    ProcessKey? parent = r.IsDBNull(2) ? null : new ProcessKey(r.GetInt32(2), r.GetInt64(3));
                    var attr = new Attr((AttributionKind)r.GetInt32(7), ReadString(r, 8), ReadString(r, 9), r.GetInt32(10) != 0);
                    procs.Add(new ProcessRecord(new ProcessKey(r.GetInt32(0), r.GetInt64(1)), parent, r.GetString(4),
                        ReadString(r, 5), ReadString(r, 6), attr, ReadTicks(r, 11)!.Value, ReadTicks(r, 12)));
                }
            }

            var liveMap = new Dictionary<ProcessKey, (ActivityState Activity, RawProcessCounters Counters)>();
            using (var c = _db.CreateCommand())
            {
                c.CommandText =
                    "SELECT pid,create_ticks,parent_pid,image_name,kernel_time,user_time,private_bytes,working_set," +
                    "read_ops,write_ops,other_ops,read_bytes,write_bytes,other_bytes,context_switches,handle_count,thread_count," +
                    "last_cpu_ticks,last_io_ticks,last_write_ticks,last_wake_ticks FROM live_counters";
                using var r = c.ExecuteReader();
                while (r.Read())
                {
                    var key = new ProcessKey(r.GetInt32(0), r.GetInt64(1));
                    var raw = new RawProcessCounters(key, r.GetInt32(2), r.GetString(3),
                        r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7),
                        r.GetInt64(8), r.GetInt64(9), r.GetInt64(10), r.GetInt64(11), r.GetInt64(12), r.GetInt64(13),
                        r.GetInt64(14), r.GetInt32(15), r.GetInt32(16));
                    liveMap[key] = (new ActivityState(ReadTicks(r, 17), ReadTicks(r, 18), ReadTicks(r, 19), ReadTicks(r, 20)), raw);
                }
            }

            var ids = new Dictionary<string, List<string>>();
            using (var c = _db.CreateCommand())
            {
                c.CommandText = "SELECT unit_id,claude_session_id FROM session_ids";
                using var r = c.ExecuteReader();
                while (r.Read())
                {
                    var unit = r.GetString(0);
                    if (!ids.TryGetValue(unit, out var l)) ids[unit] = l = [];
                    l.Add(r.GetString(1));
                }
            }

            var sessions = new List<SessionInfo>();
            using (var c = _db.CreateCommand())
            {
                c.CommandText =
                    "SELECT unit_id,host_session_id,anchor_pid,anchor_create_ticks,first_seen_ticks,ended_ticks,cwd,title,last_transcript_ticks," +
                    "desktop_title,desktop_branch,desktop_worktree,desktop_cwd,desktop_origin_cwd,desktop_summary,desktop_focused_ticks FROM sessions";
                using var r = c.ExecuteReader();
                while (r.Read())
                {
                    var unit = r.GetString(0);
                    ProcessKey? anchor = r.IsDBNull(2) ? null : new ProcessKey(r.GetInt32(2), r.GetInt64(3));
                    sessions.Add(new SessionInfo(unit, ReadString(r, 1),
                        ids.TryGetValue(unit, out var l) ? l : [], anchor, ReadTicks(r, 4)!.Value, ReadTicks(r, 5),
                        ReadString(r, 6), ReadString(r, 7), ReadTicks(r, 8), ReadDesktop(r, 9)));
                }
            }

            return new PersistedState(lastTick, procs, liveMap, sessions);
        }
    }

    public void RecordKill(KillAudit a)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText =
                "INSERT INTO kills(ts_ticks,pid,create_ticks,image_name,unit_id,caller_image,tree,result) " +
                "VALUES(@ts,@pid,@ct,@img,@unit,@caller,@tree,@res)";
            c.Parameters.AddWithValue("@ts", a.TsUtc.ToUniversalTime().Ticks);
            c.Parameters.AddWithValue("@pid", a.Key.Pid);
            c.Parameters.AddWithValue("@ct", a.Key.CreateTimeTicks);
            c.Parameters.AddWithValue("@img", a.ImageName);
            c.Parameters.AddWithValue("@unit", Db(a.UnitId));
            c.Parameters.AddWithValue("@caller", a.CallerImage);
            c.Parameters.AddWithValue("@tree", a.Tree ? 1 : 0);
            c.Parameters.AddWithValue("@res", a.Result);
            c.ExecuteNonQuery();
        }
    }

    public void PruneExitedBefore(DateTime utc)
    {
        lock (_lock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "DELETE FROM processes WHERE exited_ticks IS NOT NULL AND exited_ticks < @t";
            c.Parameters.AddWithValue("@t", utc.ToUniversalTime().Ticks);
            c.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Marks every record that is still open (no exit time) and not in <paramref name="running"/> as exited at
    /// <paramref name="exitedUtc"/>. Used when the persisted state could not be loaded, so stale records still close.
    /// </summary>
    public void CloseOpenRecordsExcept(ICollection<ProcessKey> running, DateTime exitedUtc)
    {
        lock (_lock)
        {
            var stale = new List<ProcessKey>();
            using (var c = _db.CreateCommand())
            {
                c.CommandText = "SELECT pid,create_ticks FROM processes WHERE exited_ticks IS NULL";
                using var r = c.ExecuteReader();
                while (r.Read())
                {
                    var key = new ProcessKey(r.GetInt32(0), r.GetInt64(1));
                    if (!running.Contains(key)) stale.Add(key);
                }
            }
            if (stale.Count == 0) return;

            using var tx = _db.BeginTransaction();
            using var u = _db.CreateCommand();
            u.Transaction = tx;
            u.CommandText = "UPDATE processes SET exited_ticks=@t WHERE pid=@pid AND create_ticks=@ct AND exited_ticks IS NULL";
            var pid = u.Parameters.Add(new SqliteParameter("@pid", DBNull.Value));
            var ct = u.Parameters.Add(new SqliteParameter("@ct", DBNull.Value));
            u.Parameters.AddWithValue("@t", exitedUtc.ToUniversalTime().Ticks);
            foreach (var k in stale)
            {
                pid.Value = k.Pid;
                ct.Value = k.CreateTimeTicks;
                u.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _upsertProc?.Dispose();
            _insertLive?.Dispose();
            _upsertSession?.Dispose();
            _deleteSessionIds?.Dispose();
            _insertSessionId?.Dispose();
            _setMeta?.Dispose();
            _db.Dispose();
        }
    }
}
