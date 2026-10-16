using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ResourceLens;

/// <summary>One process as the add-on modules see it at a sample (Cpu in % of the whole machine, Net* in bytes this interval).</summary>
public sealed record FeatureProc(uint Pid, long Created, string Name, double Cpu, long Mem, int Priority, uint ParentPid, long NetRx, long NetTx);

/// <summary>What the collector hands every add-on module after each stored sample.</summary>
public sealed record FeatureSample(long Ts, long Dt, double? Cpu, long MemUsed, long MemTotal, uint ForegroundPid,
    IReadOnlyList<FeatureProc> Procs, bool PerProcessNetwork);

/// <summary>
/// An add-on module (network guard, process rules, performance counters). Registered from a [ModuleInitializer] in its
/// own file, so modules never reference each other. OnSample runs on the collector thread after every stored sample and
/// must stay cheap (the whole app aims at ~0.03% CPU in the background).
/// </summary>
internal interface IFeature : IDisposable
{
    /// <summary>Short id, also the name of its config file (&lt;DataDir&gt;\&lt;Id&gt;.json).</summary>
    string Id { get; }
    void Start(FeatureHost host);
    void OnSample(FeatureSample s);
    /// <summary>Hourly: drop rows older than <paramref name="cutMs"/> (retention).</summary>
    void OnMaintain(long cutMs);
    /// <summary>Dashboard / API calls. Return false for methods this module does not own.</summary>
    bool TryQuery(string method, JsonElement p, out object? result);
}

/// <summary>Services the host app offers to modules.</summary>
internal sealed class FeatureHost(Store store, Func<AppSettings> settings, Action<string, string> notify)
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    /// <summary>The history database (read it with <see cref="Store.OpenReader"/>; names(id, name, path, descr) maps apps).</summary>
    public Store Store => store;
    public AppSettings Settings => settings();
    public bool Arabic => settings().Language == "ar";

    /// <summary>Tray notification (balloon), safe from any thread.</summary>
    public void Notify(string title, string body) => notify(title, body);

    /// <summary>The modules' own small database (features.db), created on first use.</summary>
    public FeatureDb Db => FeatureDb.Instance;

    public T LoadConfig<T>(string id) where T : new()
    {
        try
        {
            string path = Path.Combine(AppSettings.DataDir, id + ".json");
            if (File.Exists(path)) return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? new T();
        }
        catch (Exception ex) { Log.Write($"feature {id}: config", ex); }
        return new T();
    }

    public void SaveConfig<T>(string id, T config)
    {
        string path = Path.Combine(AppSettings.DataDir, id + ".json");
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, Json));
        File.Move(tmp, path, true);
    }
}

/// <summary>
/// features.db: small tables owned by the add-on modules, kept apart from history.db so the recorder's schema, repair and
/// rollups stay untouched. One writer connection behind a lock; readers open their own connection. Nothing in it is
/// essential: a damaged file is set aside at startup and recreated empty, and damage found while running only disables
/// writes until the next start (it never reaches the recorder).
/// </summary>
internal sealed class FeatureDb : IDisposable
{
    static readonly Lazy<FeatureDb> _instance = new(() => new FeatureDb());
    public static FeatureDb Instance => _instance.Value;
    public static bool Created => _instance.IsValueCreated;

    public static string DbPath => Path.Combine(AppSettings.DataDir, "features.db");
    readonly string _cs;
    readonly SqliteConnection _writer;
    readonly object _lock = new();
    bool _disposed, _damaged;

    FeatureDb()
    {
        _cs = new SqliteConnectionStringBuilder { DataSource = DbPath, Cache = SqliteCacheMode.Private, Pooling = true }.ToString();
        SetAsideIfDamaged();
        _writer = new SqliteConnection(_cs);
        _writer.Open();
        using var c = _writer.CreateCommand();
        // Same durability as history.db: a system crash cannot tear the file.
        c.CommandText = "PRAGMA auto_vacuum=INCREMENTAL; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-512;";
        c.ExecuteNonQuery();
    }

    void SetAsideIfDamaged()
    {
        if (!File.Exists(DbPath)) return;
        string? problem;
        try
        {
            using var c = new SqliteConnection(_cs);
            c.Open();
            using var q = c.CreateCommand();
            q.CommandText = "PRAGMA quick_check";
            problem = q.ExecuteScalar() is "ok" ? null : "quick_check failed";
        }
        catch (SqliteException ex) when (Store.IsDamage(ex)) { problem = ex.Message; }
        finally { SqliteConnection.ClearAllPools(); }
        if (problem is null) return;

        foreach (var old in Directory.GetFiles(AppSettings.DataDir, "features.damaged-*"))
            try { File.Delete(old); } catch { } // keep only the newest damaged copy
        string backup = Path.Combine(AppSettings.DataDir, $"features.damaged-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(DbPath + suffix)) File.Move(DbPath + suffix, backup + suffix);
        Log.Write($"features.db: damaged ({problem}); kept as {Path.GetFileName(backup)}, starting empty");
    }

    /// <summary>Runs <paramref name="work"/> on the writer connection inside one transaction.</summary>
    public void Write(Action<SqliteConnection, SqliteTransaction> work)
    {
        lock (_lock)
        {
            if (_disposed || _damaged) return;
            try
            {
                using var tx = _writer.BeginTransaction();
                work(_writer, tx);
                tx.Commit();
            }
            catch (SqliteException ex) when (Store.IsDamage(ex))
            {
                _damaged = true; // set aside and recreated at the next start
                Log.Write($"features.db: damage detected ({ex.Message}); module data paused until restart");
            }
        }
    }

    public SqliteConnection OpenReader()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        using var q = c.CreateCommand();
        q.CommandText = "PRAGMA query_only=1; PRAGMA cache_size=-1000;";
        q.ExecuteNonQuery();
        return c;
    }

    /// <summary>Hourly: release free pages beyond ~1 MB.</summary>
    public void Compact()
    {
        lock (_lock)
        {
            if (_disposed || _damaged) return;
            using var q = _writer.CreateCommand();
            q.CommandText = "PRAGMA freelist_count";
            long free = Convert.ToInt64(q.ExecuteScalar());
            q.CommandText = free > 512 ? $"PRAGMA incremental_vacuum({free - 256}); PRAGMA wal_checkpoint(TRUNCATE);" : "PRAGMA wal_checkpoint(TRUNCATE);";
            q.ExecuteNonQuery();
        }
    }

    public long FileSize()
    {
        long total = 0;
        foreach (var f in new[] { DbPath, DbPath + "-wal" })
            if (File.Exists(f)) total += new FileInfo(f).Length;
        return total;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                using var q = _writer.CreateCommand();
                q.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                q.ExecuteNonQuery();
            }
            catch (Exception ex) { Log.Write("features.db close", ex); }
            _writer.Dispose();
        }
    }
}

/// <summary>Registry of add-on modules; every call is isolated so one failing module never stops recording.</summary>
internal static class Features
{
    static readonly List<IFeature> _all = new();
    static FeatureHost? _host;

    public static IReadOnlyList<IFeature> All => _all;
    public static bool Any => _all.Count > 0;

    /// <summary>Called from each module's [ModuleInitializer].</summary>
    public static void Register(IFeature f) { lock (_all) _all.Add(f); }

    public static void Start(FeatureHost host)
    {
        _host = host;
        foreach (var f in _all)
            try { f.Start(host); } catch (Exception ex) { Log.Write($"feature {f.Id}: start", ex); }
    }

    public static void OnSample(FeatureSample s)
    {
        foreach (var f in _all)
            try { f.OnSample(s); } catch (Exception ex) { Log.Write($"feature {f.Id}: sample", ex); }
    }

    public static void OnMaintain(long cutMs)
    {
        foreach (var f in _all)
            try { f.OnMaintain(cutMs); } catch (Exception ex) { Log.Write($"feature {f.Id}: maintain", ex); }
        if (FeatureDb.Created)
            try { FeatureDb.Instance.Compact(); } catch (Exception ex) { Log.Write("features.db compact", ex); }
    }

    /// <summary>Dashboard calls not handled by the core API.</summary>
    public static object? Query(string method, JsonElement p)
    {
        foreach (var f in _all)
            if (f.TryQuery(method, p, out var result)) return result;
        throw new InvalidOperationException("Unknown method " + method);
    }

    public static void Dispose()
    {
        foreach (var f in _all)
            try { f.Dispose(); } catch (Exception ex) { Log.Write($"feature {f.Id}: dispose", ex); }
        if (FeatureDb.Created) FeatureDb.Instance.Dispose();
    }
}
