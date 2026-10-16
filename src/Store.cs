using Microsoft.Data.Sqlite;

namespace ResourceLens;

public sealed class SysSample
{
    public long Ts, Dt, Boot;
    public double? Cpu, Gpu;
    public long? MemUsed, MemTotal, CommitUsed, CommitLimit;
    public long? DiskR, DiskW, NetRx, NetTx;
    public int Procs;
    public long? FgIid, FgTitle;
    public double? CpuTemp, GpuTemp, CpuMhz, GpuMhz, PkgPower, GpuPower;
    public long? GpuDed, GpuShared;
    public int? FanRpm;
}

public sealed class ProcSample
{
    public long Nid;
    public int Count;
    public double? Cpu, Gpu;
    public long Mem, Ws, Commit;
    public long? IoR, IoW, NetRx, NetTx;
}

/// <summary>One process instance (a single PID) inside an application.</summary>
public sealed class InstSample
{
    public long Iid;
    public double? Cpu, Gpu;
    public long Mem, Ws, Commit;
    public long? IoR, IoW, NetRx, NetTx;
}

/// <summary>
/// SQLite storage.
///  sys   – one row per snapshot (system totals + foreground window)
///  proc  – per application (all its processes summed), top consumers each snapshot
///  inst  – every individual process seen (PID + what it is: tab renderer, service, script…)
///  iproc – per individual process, top consumers each snapshot
/// Full-resolution samples are kept for the configured number of days, then rolled up to
/// 1-minute buckets, and to 5-minute buckets two weeks later, so months of history stay small.
/// </summary>
public sealed partial class Store : IDisposable
{
    public static string DbPath => Path.Combine(AppSettings.DataDir, "history.db");
    readonly string _cs;
    readonly SqliteConnection _writer;
    readonly object _writeLock = new();

    public Store()
    {
        Directory.CreateDirectory(AppSettings.DataDir);
        _cs = new SqliteConnectionStringBuilder { DataSource = DbPath, Cache = SqliteCacheMode.Private, Pooling = true }.ToString();
        string? damaged = SetAsideIfDamaged(_cs);
        _writer = new SqliteConnection(_cs);
        _writer.Open();
        // FULL: every commit reaches the disk, so a system crash (e.g. a GPU driver reset) cannot tear the file.
        // auto_vacuum only takes effect on a new, empty file; older files are switched once by CompactOnce.
        Exec(_writer, """
            PRAGMA auto_vacuum=INCREMENTAL;
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            PRAGMA temp_store=MEMORY;
            PRAGMA cache_size=-2000;
            CREATE TABLE IF NOT EXISTS sys(
                ts INTEGER PRIMARY KEY, dt INTEGER NOT NULL, boot INTEGER NOT NULL,
                cpu REAL, cpu_max REAL, gpu REAL, gpu_max REAL,
                mem_used INTEGER, mem_max INTEGER, mem_total INTEGER,
                commit_used INTEGER, commit_limit INTEGER,
                disk_r INTEGER, disk_w INTEGER, net_rx INTEGER, net_tx INTEGER,
                procs INTEGER);
            CREATE TABLE IF NOT EXISTS names(
                id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                path TEXT, descr TEXT, icon BLOB, resolved INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS proc(
                ts INTEGER NOT NULL, nid INTEGER NOT NULL, pdt INTEGER NOT NULL, cnt INTEGER,
                cpu REAL, cpu_max REAL, gpu REAL,
                mem INTEGER, mem_max INTEGER, ws INTEGER, commit_b INTEGER,
                io_r INTEGER, io_w INTEGER, net_rx INTEGER, net_tx INTEGER,
                PRIMARY KEY(ts, nid)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS inst(
                id INTEGER PRIMARY KEY, nid INTEGER NOT NULL, pid INTEGER NOT NULL, created INTEGER NOT NULL,
                role TEXT, detail TEXT, cmd TEXT, title TEXT, first_ts INTEGER, last_ts INTEGER,
                UNIQUE(pid, created));
            CREATE TABLE IF NOT EXISTS iproc(
                ts INTEGER NOT NULL, iid INTEGER NOT NULL, pdt INTEGER NOT NULL,
                cpu REAL, cpu_max REAL, gpu REAL,
                mem INTEGER, mem_max INTEGER, ws INTEGER, commit_b INTEGER,
                io_r INTEGER, io_w INTEGER, net_rx INTEGER, net_tx INTEGER,
                PRIMARY KEY(ts, iid)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS strings(id INTEGER PRIMARY KEY, text TEXT NOT NULL UNIQUE);
            CREATE TABLE IF NOT EXISTS events(
                id INTEGER PRIMARY KEY, ts INTEGER NOT NULL, kind TEXT NOT NULL, value REAL, detail TEXT);
            CREATE INDEX IF NOT EXISTS proc_nid_ts ON proc(nid, ts);
            CREATE INDEX IF NOT EXISTS inst_nid ON inst(nid);
            CREATE INDEX IF NOT EXISTS sys_boot ON sys(boot);
            CREATE INDEX IF NOT EXISTS events_ts ON events(ts);
            """);
        // v1 -> v2 migration: foreground window columns
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var q = _writer.CreateCommand())
        {
            q.CommandText = "PRAGMA table_info(sys)";
            using var r = q.ExecuteReader();
            while (r.Read()) cols.Add(r.GetString(1));
        }
        if (!cols.Contains("fg_iid")) Exec(_writer, "ALTER TABLE sys ADD COLUMN fg_iid INTEGER; ALTER TABLE sys ADD COLUMN fg_title INTEGER;");
        // v2 -> v3: sensors (temperatures, clocks, power, GPU memory, fan)
        if (!cols.Contains("cpu_temp"))
            Exec(_writer, """
                ALTER TABLE sys ADD COLUMN cpu_temp REAL; ALTER TABLE sys ADD COLUMN cpu_temp_max REAL;
                ALTER TABLE sys ADD COLUMN gpu_temp REAL; ALTER TABLE sys ADD COLUMN gpu_temp_max REAL;
                ALTER TABLE sys ADD COLUMN cpu_mhz REAL; ALTER TABLE sys ADD COLUMN gpu_mhz REAL;
                ALTER TABLE sys ADD COLUMN pkg_power REAL; ALTER TABLE sys ADD COLUMN gpu_power REAL;
                ALTER TABLE sys ADD COLUMN gpu_ded INTEGER; ALTER TABLE sys ADD COLUMN gpu_shared INTEGER;
                ALTER TABLE sys ADD COLUMN fan_rpm INTEGER;
                """);
        // v3 -> v4: the per-process index is gone. It cost ~13 bytes per row and, being keyed by process, ~40 scattered
        // page writes per sample; per-process reads scan the time range of the primary key instead. (proc_nid_ts stays:
        // grouping a long range by application without it means sorting every row.)
        Exec(_writer, "DROP INDEX IF EXISTS iproc_iid_ts;");
        CompactOnce();
        if (damaged is not null) Salvage(damaged);
    }

    /// <summary>
    /// One-time switch of an older file to auto_vacuum=INCREMENTAL (needs a full VACUUM), so space freed by retention
    /// and rollups is handed back to the disk. Skipped, and retried next start, when the disk is too full for the copy.
    /// </summary>
    void CompactOnce()
    {
        try
        {
            using (var q = _writer.CreateCommand())
            {
                q.CommandText = "PRAGMA auto_vacuum";
                if (Convert.ToInt64(q.ExecuteScalar()) == 2) return;
            }
            long size = FileSize();
            long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(DbPath))!).AvailableFreeSpace;
            if (free < 2 * size + 512L * 1048576) { Log.Write($"database: compaction postponed ({free / 1048576} MB free)"); return; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Exec(_writer, "PRAGMA auto_vacuum=INCREMENTAL; VACUUM; PRAGMA wal_checkpoint(TRUNCATE);");
            Log.Write($"database: compacted {size / 1048576} MB -> {FileSize() / 1048576} MB in {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex) { Log.Write("database: compaction", ex); }
    }

    public SqliteConnection OpenReader()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        Exec(c, "PRAGMA query_only=1; PRAGMA cache_size=-4000;");
        return c;
    }

    static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static object Db(object? v) => v ?? DBNull.Value;

    // ---------------- Names (applications) ----------------

    readonly Dictionary<string, long> _nameIds = new(StringComparer.OrdinalIgnoreCase);

    public long GetNameId(string name, out bool isNew)
    {
        isNew = false;
        lock (_writeLock)
        {
            if (_nameIds.TryGetValue(name, out var id)) return id;
            using (var q = _writer.CreateCommand())
            {
                q.CommandText = "SELECT id, resolved FROM names WHERE name=$n";
                q.Parameters.AddWithValue("$n", name);
                using var r = q.ExecuteReader();
                if (r.Read())
                {
                    id = r.GetInt64(0);
                    isNew = r.GetInt64(1) == 0;
                    _nameIds[name] = id;
                    return id;
                }
            }
            using var ins = _writer.CreateCommand();
            ins.CommandText = "INSERT INTO names(name, descr) VALUES($n, $d); SELECT last_insert_rowid();";
            ins.Parameters.AddWithValue("$n", name);
            ins.Parameters.AddWithValue("$d", Db(ProcessDetails.KnownSystem.GetValueOrDefault(name)));
            id = (long)ins.ExecuteScalar()!;
            _nameIds[name] = id;
            isNew = true;
            return id;
        }
    }

    public void SetNameInfo(long id, string? path, string? descr, byte[]? icon)
    {
        lock (_writeLock)
        {
            using var c = _writer.CreateCommand();
            c.CommandText = "UPDATE names SET path=$p, descr=COALESCE($d, descr), icon=$i, resolved=1 WHERE id=$id";
            c.Parameters.AddWithValue("$p", Db(path));
            c.Parameters.AddWithValue("$d", Db(descr));
            c.Parameters.AddWithValue("$i", Db(icon));
            c.Parameters.AddWithValue("$id", id);
            c.ExecuteNonQuery();
        }
    }

    public byte[]? GetIcon(long id)
    {
        using var c = OpenReader();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT icon FROM names WHERE id=$id";
        q.Parameters.AddWithValue("$id", id);
        return q.ExecuteScalar() as byte[];
    }

    // ---------------- Instances (individual processes) ----------------

    internal long GetInstanceId(long nid, uint pid, long created, ProcessDetails.Info info, long ts)
    {
        lock (_writeLock)
        {
            using var c = _writer.CreateCommand();
            c.CommandText = """
                INSERT INTO inst(nid, pid, created, role, detail, cmd, first_ts, last_ts) VALUES($n,$p,$c,$r,$d,$cmd,$ts,$ts)
                ON CONFLICT(pid, created) DO UPDATE SET last_ts=$ts
                RETURNING id;
                """;
            c.Parameters.AddWithValue("$n", nid);
            c.Parameters.AddWithValue("$p", (long)pid);
            c.Parameters.AddWithValue("$c", created);
            c.Parameters.AddWithValue("$r", Db(info.Role));
            c.Parameters.AddWithValue("$d", Db(info.Detail));
            c.Parameters.AddWithValue("$cmd", Db(info.CommandLine));
            c.Parameters.AddWithValue("$ts", ts);
            return (long)c.ExecuteScalar()!;
        }
    }

    public void SetInstanceTitle(long iid, string title)
    {
        lock (_writeLock)
        {
            using var c = _writer.CreateCommand();
            c.CommandText = "UPDATE inst SET title=$t WHERE id=$id";
            c.Parameters.AddWithValue("$t", title);
            c.Parameters.AddWithValue("$id", iid);
            c.ExecuteNonQuery();
        }
    }

    readonly Dictionary<string, long> _strings = new(StringComparer.Ordinal);

    public long GetStringId(string text)
    {
        lock (_writeLock)
        {
            if (_strings.TryGetValue(text, out var id)) return id;
            if (_strings.Count > 4000) _strings.Clear();
            using var c = _writer.CreateCommand();
            c.CommandText = "INSERT INTO strings(text) VALUES($t) ON CONFLICT(text) DO UPDATE SET text=excluded.text RETURNING id;";
            c.Parameters.AddWithValue("$t", text);
            id = (long)c.ExecuteScalar()!;
            _strings[text] = id;
            return id;
        }
    }

    public void InsertEvent(long ts, string kind, double value, string detailJson)
    {
        lock (_writeLock)
        {
            using var c = _writer.CreateCommand();
            c.CommandText = "INSERT INTO events(ts, kind, value, detail) VALUES($ts,$k,$v,$d)";
            c.Parameters.AddWithValue("$ts", ts);
            c.Parameters.AddWithValue("$k", kind);
            c.Parameters.AddWithValue("$v", value);
            c.Parameters.AddWithValue("$d", detailJson);
            c.ExecuteNonQuery();
        }
    }

    // ---------------- Writes ----------------

    SqliteCommand? _insSys, _insProc, _insInst;

    public void Insert(SysSample s, IReadOnlyList<ProcSample> procs, IReadOnlyList<InstSample> insts)
    {
        lock (_writeLock)
        {
            if (_disposed) return;
            using var tx = _writer.BeginTransaction();
            _insSys ??= Prepare("""
                INSERT OR REPLACE INTO sys(ts,dt,boot,cpu,cpu_max,gpu,gpu_max,mem_used,mem_max,mem_total,commit_used,commit_limit,disk_r,disk_w,net_rx,net_tx,procs,fg_iid,fg_title,
                    cpu_temp,cpu_temp_max,gpu_temp,gpu_temp_max,cpu_mhz,gpu_mhz,pkg_power,gpu_power,gpu_ded,gpu_shared,fan_rpm)
                VALUES($ts,$dt,$boot,$cpu,$cpu,$gpu,$gpu,$mu,$mu,$mt,$cu,$cl,$dr,$dw,$nr,$nt,$pr,$fi,$ft,
                    $ct,$ct,$gt,$gt,$cm,$gm,$pp,$gp,$gd,$gs,$fan)
                """, "$ts", "$dt", "$boot", "$cpu", "$gpu", "$mu", "$mt", "$cu", "$cl", "$dr", "$dw", "$nr", "$nt", "$pr", "$fi", "$ft",
                "$ct", "$gt", "$cm", "$gm", "$pp", "$gp", "$gd", "$gs", "$fan");
            _insSys.Transaction = tx;
            Bind(_insSys, s.Ts, s.Dt, s.Boot, s.Cpu, s.Gpu, s.MemUsed, s.MemTotal, s.CommitUsed, s.CommitLimit,
                s.DiskR, s.DiskW, s.NetRx, s.NetTx, s.Procs, s.FgIid, s.FgTitle,
                s.CpuTemp, s.GpuTemp, s.CpuMhz, s.GpuMhz, s.PkgPower, s.GpuPower, s.GpuDed, s.GpuShared, s.FanRpm);
            _insSys.ExecuteNonQuery();

            _insProc ??= Prepare("""
                INSERT OR REPLACE INTO proc(ts,nid,pdt,cnt,cpu,cpu_max,gpu,mem,mem_max,ws,commit_b,io_r,io_w,net_rx,net_tx)
                VALUES($ts,$id,$pdt,$cnt,$cpu,NULL,$gpu,$mem,NULL,$ws,$cm,$ir,$iw,$nr,$nt)
                """, "$ts", "$id", "$pdt", "$cnt", "$cpu", "$gpu", "$mem", "$ws", "$cm", "$ir", "$iw", "$nr", "$nt");
            _insProc.Transaction = tx;
            foreach (var p in procs)
            {
                Bind(_insProc, s.Ts, p.Nid, s.Dt, p.Count, p.Cpu, p.Gpu, p.Mem, p.Ws, p.Commit, p.IoR, p.IoW, p.NetRx, p.NetTx);
                _insProc.ExecuteNonQuery();
            }

            _insInst ??= Prepare("""
                INSERT OR REPLACE INTO iproc(ts,iid,pdt,cpu,cpu_max,gpu,mem,mem_max,ws,commit_b,io_r,io_w,net_rx,net_tx)
                VALUES($ts,$id,$pdt,$cpu,NULL,$gpu,$mem,NULL,$ws,$cm,$ir,$iw,$nr,$nt)
                """, "$ts", "$id", "$pdt", "$cpu", "$gpu", "$mem", "$ws", "$cm", "$ir", "$iw", "$nr", "$nt");
            _insInst.Transaction = tx;
            foreach (var p in insts)
            {
                Bind(_insInst, s.Ts, p.Iid, s.Dt, p.Cpu, p.Gpu, p.Mem, p.Ws, p.Commit, p.IoR, p.IoW, p.NetRx, p.NetTx);
                _insInst.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    SqliteCommand Prepare(string sql, params string[] names)
    {
        var c = _writer.CreateCommand();
        c.CommandText = sql;
        foreach (var n in names) c.Parameters.Add(new SqliteParameter(n, null));
        return c;
    }

    static void Bind(SqliteCommand c, params object?[] values)
    {
        for (int i = 0; i < values.Length; i++) c.Parameters[i].Value = values[i] ?? DBNull.Value;
    }

    // ---------------- Maintenance ----------------

    public void Maintain(int retentionDays, int fullResolutionDays)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long cut = now - retentionDays * 86_400_000L;
        lock (_writeLock)
        {
            using var c = _writer.CreateCommand();
            // A running process's last_ts is refreshed hourly, so rows of an ended one lie before its last_ts + 1 day:
            // only that oldest stretch of iproc is read (there is no per-process index to look it up by).
            c.CommandText = """
                DELETE FROM sys WHERE ts < $cut; DELETE FROM proc WHERE ts < $cut; DELETE FROM iproc WHERE ts < $cut;
                DELETE FROM events WHERE ts < $cut;
                DELETE FROM inst WHERE last_ts < $cut AND id NOT IN (SELECT DISTINCT iid FROM iproc
                    WHERE ts <= (SELECT MAX(last_ts) FROM inst WHERE last_ts < $cut) + 86400000);
                DELETE FROM strings WHERE id NOT IN (SELECT fg_title FROM sys WHERE fg_title IS NOT NULL);
                """;
            c.Parameters.AddWithValue("$cut", cut);
            c.ExecuteNonQuery();
            _strings.Clear();
        }
        long full = Math.Max(1, fullResolutionDays) * 86_400_000L;
        Rollup(now - full, 60_000);                       // after the full-resolution period -> 1 minute
        Rollup(now - full - 14 * 86_400_000L, 300_000);   // two weeks later -> 5 minutes
        lock (_writeLock)
        {
            // Give freed pages back to the disk, keeping ~8 MB (a few hours of new samples) to refill without regrowing.
            using (var q = _writer.CreateCommand())
            {
                q.CommandText = "PRAGMA freelist_count";
                long free = Convert.ToInt64(q.ExecuteScalar());
                if (free > 4096) Exec(_writer, $"PRAGMA incremental_vacuum({free - 2048});");
            }
            Exec(_writer, "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA optimize;");
        }
    }

    void Rollup(long olderThan, long bucket)
    {
        long cut = olderThan / bucket * bucket;
        lock (_writeLock)
        {
            using var tx = _writer.BeginTransaction();
            using var c = _writer.CreateCommand();
            c.Transaction = tx;
            c.CommandTimeout = 600;
            c.Parameters.AddWithValue("$cut", cut);
            c.Parameters.AddWithValue("$b", bucket);
            // Only buckets that still contain finer-grained rows.
            c.CommandText = """
                DROP TABLE IF EXISTS temp.bk;
                CREATE TEMP TABLE bk(b INTEGER PRIMARY KEY);
                INSERT OR IGNORE INTO bk SELECT ts/$b*$b FROM sys WHERE ts < $cut AND dt < $b*0.9;

                DROP TABLE IF EXISTS temp.sa;
                CREATE TEMP TABLE sa AS
                SELECT ts/$b*$b AS ts, SUM(dt) AS dt, MAX(boot) AS boot,
                    SUM(cpu*dt)/SUM(CASE WHEN cpu IS NULL THEN NULL ELSE dt END) AS cpu, MAX(cpu_max) AS cpu_max,
                    SUM(gpu*dt)/SUM(CASE WHEN gpu IS NULL THEN NULL ELSE dt END) AS gpu, MAX(gpu_max) AS gpu_max,
                    CAST(SUM(mem_used*dt)/SUM(CASE WHEN mem_used IS NULL THEN NULL ELSE dt END) AS INTEGER) AS mem_used,
                    MAX(mem_max) AS mem_max, MAX(mem_total) AS mem_total,
                    CAST(SUM(commit_used*dt)/SUM(CASE WHEN commit_used IS NULL THEN NULL ELSE dt END) AS INTEGER) AS commit_used,
                    MAX(commit_limit) AS commit_limit,
                    SUM(disk_r) AS disk_r, SUM(disk_w) AS disk_w, SUM(net_rx) AS net_rx, SUM(net_tx) AS net_tx,
                    MAX(procs) AS procs, MAX(fg_iid) AS fg_iid, MAX(fg_title) AS fg_title,
                    SUM(cpu_temp*dt)/SUM(CASE WHEN cpu_temp IS NULL THEN NULL ELSE dt END) AS cpu_temp, MAX(cpu_temp_max) AS cpu_temp_max,
                    SUM(gpu_temp*dt)/SUM(CASE WHEN gpu_temp IS NULL THEN NULL ELSE dt END) AS gpu_temp, MAX(gpu_temp_max) AS gpu_temp_max,
                    SUM(cpu_mhz*dt)/SUM(CASE WHEN cpu_mhz IS NULL THEN NULL ELSE dt END) AS cpu_mhz,
                    SUM(gpu_mhz*dt)/SUM(CASE WHEN gpu_mhz IS NULL THEN NULL ELSE dt END) AS gpu_mhz,
                    SUM(pkg_power*dt)/SUM(CASE WHEN pkg_power IS NULL THEN NULL ELSE dt END) AS pkg_power,
                    SUM(gpu_power*dt)/SUM(CASE WHEN gpu_power IS NULL THEN NULL ELSE dt END) AS gpu_power,
                    CAST(SUM(gpu_ded*dt)/SUM(CASE WHEN gpu_ded IS NULL THEN NULL ELSE dt END) AS INTEGER) AS gpu_ded,
                    CAST(SUM(gpu_shared*dt)/SUM(CASE WHEN gpu_shared IS NULL THEN NULL ELSE dt END) AS INTEGER) AS gpu_shared,
                    MAX(fan_rpm) AS fan_rpm
                FROM sys WHERE ts/$b*$b IN (SELECT b FROM bk) GROUP BY ts/$b;

                DROP TABLE IF EXISTS temp.pa;
                CREATE TEMP TABLE pa AS
                SELECT ts/$b*$b AS ts, nid, SUM(pdt) AS pdt, MAX(cnt) AS cnt,
                    SUM(cpu*pdt)/SUM(CASE WHEN cpu IS NULL THEN NULL ELSE pdt END) AS cpu, MAX(COALESCE(cpu_max, cpu)) AS cpu_max,
                    SUM(gpu*pdt)/SUM(CASE WHEN gpu IS NULL THEN NULL ELSE pdt END) AS gpu,
                    CAST(SUM(mem*pdt)/SUM(pdt) AS INTEGER) AS mem, MAX(COALESCE(mem_max, mem)) AS mem_max,
                    CAST(SUM(ws*pdt)/SUM(pdt) AS INTEGER) AS ws, CAST(SUM(commit_b*pdt)/SUM(pdt) AS INTEGER) AS commit_b,
                    SUM(io_r) AS io_r, SUM(io_w) AS io_w, SUM(net_rx) AS net_rx, SUM(net_tx) AS net_tx
                FROM proc WHERE ts/$b*$b IN (SELECT b FROM bk) GROUP BY ts/$b, nid;

                DROP TABLE IF EXISTS temp.ia;
                CREATE TEMP TABLE ia AS
                SELECT ts/$b*$b AS ts, iid, SUM(pdt) AS pdt,
                    SUM(cpu*pdt)/SUM(CASE WHEN cpu IS NULL THEN NULL ELSE pdt END) AS cpu, MAX(COALESCE(cpu_max, cpu)) AS cpu_max,
                    SUM(gpu*pdt)/SUM(CASE WHEN gpu IS NULL THEN NULL ELSE pdt END) AS gpu,
                    CAST(SUM(mem*pdt)/SUM(pdt) AS INTEGER) AS mem, MAX(COALESCE(mem_max, mem)) AS mem_max,
                    CAST(SUM(ws*pdt)/SUM(pdt) AS INTEGER) AS ws, CAST(SUM(commit_b*pdt)/SUM(pdt) AS INTEGER) AS commit_b,
                    SUM(io_r) AS io_r, SUM(io_w) AS io_w, SUM(net_rx) AS net_rx, SUM(net_tx) AS net_tx
                FROM iproc WHERE ts/$b*$b IN (SELECT b FROM bk) GROUP BY ts/$b, iid;

                DELETE FROM sys WHERE ts/$b*$b IN (SELECT b FROM bk);
                DELETE FROM proc WHERE ts/$b*$b IN (SELECT b FROM bk);
                DELETE FROM iproc WHERE ts/$b*$b IN (SELECT b FROM bk);
                INSERT INTO sys(ts,dt,boot,cpu,cpu_max,gpu,gpu_max,mem_used,mem_max,mem_total,commit_used,commit_limit,disk_r,disk_w,net_rx,net_tx,procs,fg_iid,fg_title,
                        cpu_temp,cpu_temp_max,gpu_temp,gpu_temp_max,cpu_mhz,gpu_mhz,pkg_power,gpu_power,gpu_ded,gpu_shared,fan_rpm)
                    SELECT ts,dt,boot,cpu,cpu_max,gpu,gpu_max,mem_used,mem_max,mem_total,commit_used,commit_limit,disk_r,disk_w,net_rx,net_tx,procs,fg_iid,fg_title,
                        cpu_temp,cpu_temp_max,gpu_temp,gpu_temp_max,cpu_mhz,gpu_mhz,pkg_power,gpu_power,gpu_ded,gpu_shared,fan_rpm FROM sa;
                INSERT INTO proc(ts,nid,pdt,cnt,cpu,cpu_max,gpu,mem,mem_max,ws,commit_b,io_r,io_w,net_rx,net_tx)
                    SELECT ts,nid,pdt,cnt,cpu,cpu_max,gpu,mem,mem_max,ws,commit_b,io_r,io_w,net_rx,net_tx FROM pa;
                INSERT INTO iproc(ts,iid,pdt,cpu,cpu_max,gpu,mem,mem_max,ws,commit_b,io_r,io_w,net_rx,net_tx)
                    SELECT ts,iid,pdt,cpu,cpu_max,gpu,mem,mem_max,ws,commit_b,io_r,io_w,net_rx,net_tx FROM ia;
                DROP TABLE temp.bk; DROP TABLE temp.sa; DROP TABLE temp.pa; DROP TABLE temp.ia;
                """;
            c.ExecuteNonQuery();
            tx.Commit();
        }
    }

    /// <summary>Raised after ClearAll so cached ids (instances, strings) are dropped by the collector.</summary>
    public event Action? Cleared;

    public void ClearAll()
    {
        lock (_writeLock)
        {
            Exec(_writer, "DELETE FROM sys; DELETE FROM proc; DELETE FROM iproc; DELETE FROM inst; DELETE FROM strings; DELETE FROM events;");
            Exec(_writer, "VACUUM;");
            _strings.Clear();
        }
        Cleared?.Invoke();
    }

    /// <summary>Marks still-running processes as alive so retention never removes them.</summary>
    public void TouchInstances(IEnumerable<long> iids, long ts)
    {
        lock (_writeLock)
        {
            using var tx = _writer.BeginTransaction();
            using var c = _writer.CreateCommand();
            c.Transaction = tx;
            c.CommandText = "UPDATE inst SET last_ts=$ts WHERE id=$id";
            var pId = c.Parameters.Add("$id", SqliteType.Integer);
            c.Parameters.AddWithValue("$ts", ts);
            foreach (var id in iids) { pId.Value = id; c.ExecuteNonQuery(); }
            tx.Commit();
        }
    }

    public long FileSize()
    {
        long total = 0;
        foreach (var f in new[] { DbPath, DbPath + "-wal" })
            if (File.Exists(f)) total += new FileInfo(f).Length;
        return total;
    }

    bool _disposed;

    /// <summary>
    /// Closes the file completely (writer and pooled readers, WAL folded back in) so the next instance
    /// — e.g. the elevated one after "restart as administrator" — starts on a clean, self-contained file.
    /// </summary>
    public void Dispose()
    {
        lock (_writeLock)
        {
            if (_disposed) return;
            _disposed = true;
            _insSys?.Dispose(); _insProc?.Dispose(); _insInst?.Dispose();
            try { Exec(_writer, "PRAGMA wal_checkpoint(TRUNCATE);"); } catch (Exception ex) { Log.Write("close checkpoint", ex); }
            _writer.Dispose();
        }
        SqliteConnection.ClearAllPools();
    }
}
