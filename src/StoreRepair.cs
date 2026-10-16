using Microsoft.Data.Sqlite;

namespace ResourceLens;

/// <summary>
/// Damage handling: recording must outlive a damaged database file. The file is checked when the app starts and
/// every hour; a damaged one is set aside (kept as history.damaged-*.db) and every row that can still be read is
/// copied into a fresh database. Damage seen while running restarts the app so this happens right away.
/// </summary>
public sealed partial class Store
{
    /// <summary>Raised once per run when SQLite reports the file as damaged.</summary>
    public static event Action? Damaged;
    static int _damageRaised;

    /// <summary>After a startup repair: rows recovered and the kept damaged copy.</summary>
    public static (long Rows, string Backup)? LastRepair { get; private set; }

    // SQLITE_CORRUPT / SQLITE_NOTADB
    public static bool IsDamage(Exception ex) => ex is SqliteException { SqliteErrorCode: 11 or 26 };

    /// <summary>Call from any catch around database work.</summary>
    public static void Note(Exception ex)
    {
        if (IsDamage(ex)) RaiseDamaged(ex.Message);
    }

    static void RaiseDamaged(string why)
    {
        if (Interlocked.Exchange(ref _damageRaised, 1) != 0) return;
        Log.Write($"database: damage detected ({why}); restarting to repair");
        Damaged?.Invoke();
    }

    /// <summary>Full read of every page (hourly, on a reader so recording is not held up).</summary>
    public void CheckHealth()
    {
        string? result;
        try
        {
            using var c = OpenReader();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check";
            result = cmd.ExecuteScalar() as string;
        }
        catch (SqliteException ex) when (IsDamage(ex)) { result = ex.Message; }
        if (result != "ok") RaiseDamaged(result ?? "quick_check");
    }

    /// <summary>Moves a damaged database (and its WAL) aside; returns the kept copy's path, or null when healthy.</summary>
    static string? SetAsideIfDamaged(string cs)
    {
        if (!File.Exists(DbPath)) return null;
        string? problem;
        try
        {
            using var c = new SqliteConnection(cs);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check";
            problem = cmd.ExecuteScalar() is "ok" ? null : "quick_check failed";
        }
        catch (SqliteException ex) when (IsDamage(ex)) { problem = ex.Message; }
        finally { SqliteConnection.ClearAllPools(); }
        if (problem is null) return null;

        foreach (var old in Directory.GetFiles(AppSettings.DataDir, "history.damaged-*"))
            try { File.Delete(old); } catch { } // keep only the newest damaged copy
        string backup = Path.Combine(AppSettings.DataDir, $"history.damaged-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(DbPath + suffix)) File.Move(DbPath + suffix, backup + suffix);
        Log.Write($"database: damaged ({problem}); kept as {Path.GetFileName(backup)}, rebuilding");
        return backup;
    }

    // Most valuable first, each with the key ranges are cut on (rowid / id, or the timestamp leading the key),
    // the chunk read at a time, and the smallest range worth splitting further around a damaged page.
    static readonly (string Table, string Key, long Chunk, long MinSpan)[] SalvageOrder =
    {
        ("sys", "ts", 3_600_000, 30_000), ("names", "id", 2000, 8), ("proc", "ts", 3_600_000, 30_000),
        ("iproc", "ts", 3_600_000, 30_000), ("inst", "id", 2000, 8), ("strings", "id", 2000, 8), ("events", "id", 2000, 8),
    };

    int _salvageBudget;

    /// <summary>
    /// Copies every row that can still be read from the damaged copy into the fresh database. The damaged file is
    /// read on its own read-only connection, so its errors never touch the new file's transactions.
    /// </summary>
    void Salvage(string backup)
    {
        long rows = 0;
        try
        {
            using var old = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = backup, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            old.Open();
            foreach (var (table, key, chunk, minSpan) in SalvageOrder)
            {
                var cols = SharedColumns(old, table);
                if (cols.Count == 0) continue;
                var (lo, hi) = KeyRange(old, table, key);
                _salvageBudget = 20_000; // read attempts per table: a badly damaged table cannot starve the others
                lock (_writeLock)
                {
                    using var tx = _writer.BeginTransaction();
                    using var ins = _writer.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = $"INSERT OR IGNORE INTO {table}({string.Join(",", cols)}) VALUES({string.Join(",", cols.Select((_, i) => "$p" + i))})";
                    for (int i = 0; i < cols.Count; i++) ins.Parameters.Add(new SqliteParameter("$p" + i, null));
                    for (long a = lo; a < hi; a += chunk)
                        rows += CopyReadable(old, ins, table, key, cols, a, Math.Min(hi, a + chunk), minSpan);
                    tx.Commit();
                }
            }
        }
        catch (Exception ex) { Log.Write("database: salvage", ex); }
        LastRepair = (rows, backup);
        Log.Write($"database: rebuilt; {rows} rows recovered");
    }

    List<string> SharedColumns(SqliteConnection old, string table)
    {
        static List<string> Cols(SqliteConnection c, string table)
        {
            var list = new List<string>();
            try
            {
                using var q = c.CreateCommand();
                q.CommandText = $"PRAGMA table_info({table})";
                using var r = q.ExecuteReader();
                while (r.Read()) list.Add(r.GetString(1));
            }
            catch (SqliteException) { }
            return list;
        }
        var have = Cols(old, table);
        return Cols(_writer, table).Where(c => have.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    static (long Lo, long Hi) KeyRange(SqliteConnection old, string table, string key)
    {
        try
        {
            using var q = old.CreateCommand();
            q.CommandText = $"SELECT MIN({key}), MAX({key}) FROM {table} NOT INDEXED";
            using var r = q.ExecuteReader();
            return r.Read() && !r.IsDBNull(0) ? (r.GetInt64(0), r.GetInt64(1) + 1) : (0, 0);
        }
        catch (SqliteException)
        {
            // Either end may sit on a damaged page: search the whole plausible range instead.
            return key == "ts"
                ? (DateTimeOffset.UtcNow.AddDays(-400).ToUnixTimeMilliseconds(), DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds())
                : (0, 1L << 22);
        }
    }

    /// <summary>Copies the rows in [lo, hi); when a damaged page is in the way, halves the range until it is isolated.</summary>
    long CopyReadable(SqliteConnection old, SqliteCommand ins, string table, string key, List<string> cols, long lo, long hi, long minSpan)
    {
        if (hi <= lo || _salvageBudget-- <= 0) return 0;
        var batch = new List<object[]>();
        try
        {
            using var q = old.CreateCommand();
            q.CommandText = $"SELECT {string.Join(",", cols)} FROM {table} NOT INDEXED WHERE {key} >= $lo AND {key} < $hi";
            q.Parameters.AddWithValue("$lo", lo);
            q.Parameters.AddWithValue("$hi", hi);
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                var v = new object[cols.Count];
                r.GetValues(v);
                batch.Add(v);
            }
        }
        catch (SqliteException ex) when (IsDamage(ex))
        {
            if (hi - lo <= minSpan) return 0;
            long mid = lo + (hi - lo) / 2;
            return CopyReadable(old, ins, table, key, cols, lo, mid, minSpan) + CopyReadable(old, ins, table, key, cols, mid, hi, minSpan);
        }
        foreach (var v in batch)
        {
            for (int i = 0; i < v.Length; i++) ins.Parameters[i].Value = v[i];
            ins.ExecuteNonQuery();
        }
        return batch.Count;
    }
}
