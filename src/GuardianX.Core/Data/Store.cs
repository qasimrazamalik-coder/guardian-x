using GuardianX.Core.Battery;
using Microsoft.Data.Sqlite;

namespace GuardianX.Core.Data;

/// <summary>A stored battery sample (one a minute).</summary>
public sealed record Sample(
    DateTimeOffset Time, double Percent, int RemainingMWh, int FullMWh, int DesignMWh, int Cycles,
    int? RateMw, int? VoltageMv, double? TemperatureC, bool OnAc, bool Charging, double? Heat = null);

/// <summary>Something that happened: sleep, wake, charger in or out.</summary>
public sealed record PowerEvent(DateTimeOffset Time, string Kind, double Percent);

/// <summary>Local SQLite history. Nothing leaves this PC.</summary>
public sealed class Store : IDisposable
{
    /// <summary>Where history, settings and logs live. GUARDIANX_DATA points developer runs at a separate folder.</summary>
    public static string DefaultFolder =>
        Environment.GetEnvironmentVariable("GUARDIANX_DATA") is { Length: > 0 } custom
            ? custom
            : LiveFolder;

    public static string LiveFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GuardianXData");

    /// <summary>
    /// Copies the live history into <paramref name="folder"/> with plain file reads, never opening it with SQLite.
    /// Processes started from a packaged app (Claude Desktop from the Store, for example) don't share SQLite's
    /// memory-mapped WAL index with the installed app, so opening the live database from them can corrupt it.
    /// A copy of the database plus its WAL is always safe: at worst SQLite ignores a WAL that no longer matches
    /// and the copy is a few minutes older.
    /// </summary>
    public static string CopyLiveTo(string folder, string? from = null)
    {
        from ??= LiveFolder;
        Directory.CreateDirectory(folder);
        foreach (var name in new[] { "guardian.db", "guardian.db-wal", "settings.json" })
        {
            var src = Path.Combine(from, name);
            var dst = Path.Combine(folder, name);
            if (File.Exists(dst)) File.Delete(dst);
            if (!File.Exists(src)) continue;
            using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var output = File.Create(dst);
            input.CopyTo(output);
        }
        return Path.Combine(folder, "guardian.db");
    }

    public const string SnapshotName = "snapshot.db";

    /// <summary>
    /// A private copy of the history for read-only users such as the MCP server. While Guardian X runs it reads the
    /// consistent snapshot the app writes every few minutes (a copy of a database that's being written can tear);
    /// when the app isn't running nothing writes, so the database and its log can be copied as they are.
    /// </summary>
    public static Store OpenCopyOfLive(string? from = null, bool? appRunning = null)
    {
        from ??= LiveFolder;
        // One folder per open, so concurrent readers never delete each other's copy. Removed on Dispose.
        var folder = Path.Combine(Path.GetTempPath(), "GuardianX-read", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        bool running = appRunning ?? System.Diagnostics.Process.GetProcessesByName("GuardianX").Length > 0;
        var snapshot = Path.Combine(from, SnapshotName);
        string path;
        if (running && File.Exists(snapshot))
        {
            path = Path.Combine(folder, "guardian.db");
            using (var input = new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var output = File.Create(path)) input.CopyTo(output);
            var settings = Path.Combine(from, "settings.json");
            if (File.Exists(settings)) File.Copy(settings, Path.Combine(folder, "settings.json"), true);
        }
        else path = CopyLiveTo(folder, from);
        // If the copy still came out torn (the app had only just started), recovery salvages what it can.
        return new Store(path) { _ownedFolder = folder };
    }

    /// <summary>Writes a consistent copy of the whole database (SQLite VACUUM INTO), replacing the old one atomically.</summary>
    public void WriteSnapshot(string? path = null)
    {
        path ??= Path.Combine(Path.GetDirectoryName(DbPath)!, SnapshotName);
        var tmp = path + ".tmp";
        lock (_lock)
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "VACUUM INTO $p";
            cmd.Parameters.AddWithValue("$p", tmp);
            cmd.ExecuteNonQuery();
        }
        File.Move(tmp, path, overwrite: true);
    }

    private string? _ownedFolder;

    public string DbPath { get; }
    private readonly SqliteConnection _db;
    private readonly object _lock = new();

    /// <summary>Set when the database was damaged and rebuilt on open: what was recovered and where the damaged copy is.</summary>
    public string? Recovery { get; }

    public Store(string? dbPath = null)
    {
        DbPath = dbPath ?? Path.Combine(DefaultFolder, "guardian.db");
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        Recovery = RecoverIfDamaged(DbPath);
        _db = new SqliteConnection($"Data Source={DbPath}");
        _db.Open();
        Exec("PRAGMA journal_mode=WAL;");
        CreateSchema();
    }

    private static readonly string[] Tables = { "samples", "events", "gpu", "report", "app_energy" };

    /// <summary>
    /// Checks the database before use. If SQLite reports damage, the damaged files are moved (never deleted) into a
    /// "damaged-…" folder next to it, every row that can still be read is copied into a fresh database, and a
    /// description of what was recovered is returned.
    /// </summary>
    internal static string? RecoverIfDamaged(string dbPath)
    {
        if (!File.Exists(dbPath)) return null;
        string? problem = null;
        try
        {
            using var check = new SqliteConnection($"Data Source={dbPath};Pooling=False");
            check.Open();
            using var cmd = check.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check";
            var result = Convert.ToString(cmd.ExecuteScalar());
            if (result != "ok") problem = result;
            else
            {
                // quick_check doesn't read every table's rows; touch each one so a broken page is found now, not later.
                foreach (var t in Tables)
                {
                    cmd.CommandText = $"SELECT name FROM sqlite_master WHERE type='table' AND name='{t}'";
                    if (cmd.ExecuteScalar() is null) continue;
                    cmd.CommandText = $"SELECT COUNT(*) FROM {t}";
                    cmd.ExecuteScalar();
                }
            }
        }
        // Only real damage (SQLITE_CORRUPT, SQLITE_NOTADB). A locked or busy file is not a reason to touch anything.
        catch (SqliteException e) when (e.SqliteErrorCode is 11 or 26) { problem = e.Message; }
        if (problem is null) return null;

        var dir = Path.GetDirectoryName(dbPath)!;
        var backup = Path.Combine(dir, $"damaged-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(backup);
        var name = Path.GetFileName(dbPath);
        try
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(dbPath + suffix)) File.Move(dbPath + suffix, Path.Combine(backup, name + suffix));
        }
        catch (IOException)
        {
            // Another process has it open. Put back anything already moved and carry on with the database as it is.
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(Path.Combine(backup, name + suffix)) && !File.Exists(dbPath + suffix)) File.Move(Path.Combine(backup, name + suffix), dbPath + suffix);
            try { Directory.Delete(backup); } catch { }
            return null;
        }

        // Read from a scratch copy so the backup stays exactly as it was found.
        var work = Path.Combine(Path.GetTempPath(), "GuardianX-recover", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        foreach (var suffix in new[] { "", "-wal" })
            if (File.Exists(Path.Combine(backup, name + suffix))) File.Copy(Path.Combine(backup, name + suffix), Path.Combine(work, name + suffix));

        var counts = new Dictionary<string, int>();
        using (var fresh = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            fresh.Open();
            using (var c = fresh.CreateCommand()) { c.CommandText = "PRAGMA journal_mode=WAL;" + Schema; c.ExecuteNonQuery(); }
            AddColumns(fresh);
            try
            {
                using var old = new SqliteConnection($"Data Source={Path.Combine(work, name)};Pooling=False");
                old.Open();
                foreach (var t in Tables) counts[t] = Salvage(old, fresh, t);
            }
            catch (SqliteException) { } // nothing readable at all: start fresh
        }
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(work, true); } catch { }
        int samples = counts.GetValueOrDefault("samples"), periods = counts.GetValueOrDefault("report");
        try { File.WriteAllText(Path.Combine(backup, "what-happened.txt"), $"{DateTime.Now:s} SQLite reported: {problem}\nRecovered {samples} readings and {periods} Windows history periods into a fresh database.\n"); } catch { }
        return $"Its history file was damaged, so it rebuilt it: {samples} readings and {periods} Windows history periods recovered. " +
               $"The damaged copy is kept in GuardianXData\\{Path.GetFileName(backup)}.";
    }

    /// <summary>Copies every readable row of a table, stepping past unreadable stretches by rowid.</summary>
    private static int Salvage(SqliteConnection old, SqliteConnection fresh, string table)
    {
        int copied = 0, failures = 0;
        long after = long.MinValue;
        long[] skips = { 1, 100, 10_000, 1_000_000 };
        using var tx = fresh.BeginTransaction();
        while (true)
        {
            try
            {
                using var read = old.CreateCommand();
                read.CommandText = $"SELECT rowid, * FROM {table} WHERE rowid > $a ORDER BY rowid";
                read.Parameters.AddWithValue("$a", after);
                using var r = read.ExecuteReader();
                while (r.Read())
                {
                    after = r.GetInt64(0);
                    using var ins = fresh.CreateCommand();
                    ins.Transaction = tx;
                    var names = Enumerable.Range(1, r.FieldCount - 1).Select(i => "$p" + i).ToArray();
                    // Named columns: an older file may have fewer columns (no heat) than the fresh schema.
                    var cols = Enumerable.Range(1, r.FieldCount - 1).Select(i => "\"" + r.GetName(i) + "\"");
                    ins.CommandText = $"INSERT OR IGNORE INTO {table}({string.Join(",", cols)}) VALUES({string.Join(",", names)})";
                    for (int i = 1; i < r.FieldCount; i++) ins.Parameters.AddWithValue(names[i - 1], r.GetValue(i));
                    ins.ExecuteNonQuery();
                    copied++;
                }
                break; // read to the end
            }
            catch (SqliteException)
            {
                if (failures >= skips.Length) break;
                after = (after == long.MinValue ? 0 : after) + skips[failures++];
            }
        }
        tx.Commit();
        return copied;
    }

    private const string Schema = """
            CREATE TABLE IF NOT EXISTS samples(
              ts INTEGER PRIMARY KEY, pct REAL, remaining INTEGER, full INTEGER, design INTEGER, cycles INTEGER,
              rate INTEGER, voltage INTEGER, temp REAL, on_ac INTEGER, charging INTEGER);
            CREATE TABLE IF NOT EXISTS events(ts INTEGER, kind TEXT, pct REAL);
            CREATE INDEX IF NOT EXISTS events_ts ON events(ts);
            CREATE TABLE IF NOT EXISTS gpu(ts INTEGER, app TEXT, bytes INTEGER);
            CREATE TABLE IF NOT EXISTS report(
              start TEXT, end TEXT PRIMARY KEY, full INTEGER, design INTEGER, cycles INTEGER,
              ac_active INTEGER, dc_active INTEGER, ac_standby INTEGER, dc_standby INTEGER);
            CREATE TABLE IF NOT EXISTS app_energy(day TEXT, app TEXT, name TEXT, mwh REAL, cpu REAL, PRIMARY KEY(day, app));
            CREATE INDEX IF NOT EXISTS gpu_ts ON gpu(ts);
            """;

    /// <summary>Columns added after the first release. ADD COLUMN fails with "duplicate column" once it's there.</summary>
    private static readonly string[] Migrations = { "ALTER TABLE samples ADD COLUMN heat REAL" };

    private static void AddColumns(SqliteConnection db)
    {
        foreach (var sql in Migrations)
        {
            try
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException e) when (e.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) { }
        }
    }

    private void CreateSchema()
    {
        Exec(Schema);
        AddColumns(_db);
        Exec("DROP TABLE IF EXISTS kb;"); // search index of the old Ollama assistant, no longer used
    }

    /// <param name="heat">Heat score 0..100 at the time of the reading, if known.</param>
    public void Add(BatterySnapshot s, double? heat = null)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO samples(ts,pct,remaining,full,design,cycles,rate,voltage,temp,on_ac,charging,heat)
                VALUES($ts,$pct,$rem,$full,$design,$cycles,$rate,$volt,$temp,$ac,$chg,$heat)
                """;
            cmd.Parameters.AddWithValue("$ts", s.Time.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$pct", s.Percent);
            cmd.Parameters.AddWithValue("$rem", s.RemainingMWh);
            cmd.Parameters.AddWithValue("$full", s.FullMWh);
            cmd.Parameters.AddWithValue("$design", s.DesignMWh);
            cmd.Parameters.AddWithValue("$cycles", s.Cycles);
            cmd.Parameters.AddWithValue("$rate", (object?)s.RateMw ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$volt", (object?)s.VoltageMv ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$temp", (object?)s.TemperatureC ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ac", s.OnAc ? 1 : 0);
            cmd.Parameters.AddWithValue("$chg", s.Charging ? 1 : 0);
            cmd.Parameters.AddWithValue("$heat", heat is double h ? (object)Math.Round(h, 1) : DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public void AddEvent(string kind, double percent, DateTimeOffset? at = null)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO events VALUES($ts,$kind,$pct)";
            cmd.Parameters.AddWithValue("$ts", (at ?? DateTimeOffset.Now).ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$kind", kind);
            cmd.Parameters.AddWithValue("$pct", percent);
            cmd.ExecuteNonQuery();
        }
    }

    public void AddGpu(string app, long bytes)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO gpu VALUES($ts,$app,$b)";
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.Now.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$app", app);
            cmd.Parameters.AddWithValue("$b", bytes);
            cmd.ExecuteNonQuery();
        }
    }

    public List<Sample> Samples(DateTimeOffset from, DateTimeOffset to)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT ts,pct,remaining,full,design,cycles,rate,voltage,temp,on_ac,charging,heat FROM samples WHERE ts >= $a AND ts < $b ORDER BY ts";
            cmd.Parameters.AddWithValue("$a", from.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$b", to.ToUnixTimeSeconds());
            using var r = cmd.ExecuteReader();
            var list = new List<Sample>();
            while (r.Read())
                list.Add(new Sample(
                    DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)).ToLocalTime(), r.GetDouble(1), r.GetInt32(2),
                    r.GetInt32(3), r.GetInt32(4), r.GetInt32(5),
                    r.IsDBNull(6) ? null : r.GetInt32(6), r.IsDBNull(7) ? null : r.GetInt32(7),
                    r.IsDBNull(8) ? null : r.GetDouble(8), r.GetInt32(9) == 1, r.GetInt32(10) == 1,
                    r.IsDBNull(11) ? null : r.GetDouble(11)));
            return list;
        }
    }

    public List<PowerEvent> Events(DateTimeOffset from)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT ts, kind, pct FROM events WHERE ts >= $a ORDER BY ts";
            cmd.Parameters.AddWithValue("$a", from.ToUnixTimeSeconds());
            using var r = cmd.ExecuteReader();
            var list = new List<PowerEvent>();
            while (r.Read())
                list.Add(new PowerEvent(DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)).ToLocalTime(), r.GetString(1), r.GetDouble(2)));
            return list;
        }
    }

    /// <summary>
    /// Capacity per day: Guardian X's own readings where it has them, and Windows' battery history
    /// (weekly, then daily) for everything before that.
    /// </summary>
    public List<(DateOnly Day, int FullMWh, int DesignMWh, int Cycles)> DailyCapacity(int days)
    {
        var from = DateOnly.FromDateTime(DateTime.Now.AddDays(-days));
        var map = new SortedDictionary<DateOnly, (int, int, int)>();
        foreach (var p in ReportPeriods())
        {
            var day = DateOnly.FromDateTime(p.End);
            if (day >= from) map[day] = (p.FullMWh, p.DesignMWh, p.Cycles);
        }
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT date(ts,'unixepoch','localtime') d, MAX(full), MAX(design), MAX(cycles)
                FROM samples WHERE ts >= $a GROUP BY d ORDER BY d
                """;
            cmd.Parameters.AddWithValue("$a", DateTimeOffset.Now.AddDays(-days).ToUnixTimeSeconds());
            using var r = cmd.ExecuteReader();
            while (r.Read())
                map[DateOnly.Parse(r.GetString(0))] = (r.GetInt32(1), r.GetInt32(2), r.GetInt32(3));
        }
        return map.Select(kv => (kv.Key, kv.Value.Item1, kv.Value.Item2, kv.Value.Item3)).ToList();
    }

    /// <summary>Replaces the stored copy of Windows' battery history.</summary>
    public void SaveReport(IReadOnlyList<GuardianX.Core.Battery.ReportPeriod> periods)
    {
        if (periods.Count == 0) return;
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            using (var del = _db.CreateCommand()) { del.Transaction = tx; del.CommandText = "DELETE FROM report"; del.ExecuteNonQuery(); }
            foreach (var p in periods)
            {
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR REPLACE INTO report VALUES($s,$e,$f,$d,$c,$aa,$da,$as,$ds)";
                cmd.Parameters.AddWithValue("$s", p.Start.ToString("s"));
                cmd.Parameters.AddWithValue("$e", p.End.ToString("s"));
                cmd.Parameters.AddWithValue("$f", p.FullMWh);
                cmd.Parameters.AddWithValue("$d", p.DesignMWh);
                cmd.Parameters.AddWithValue("$c", p.Cycles);
                cmd.Parameters.AddWithValue("$aa", (long)p.ActiveOnCharger.TotalSeconds);
                cmd.Parameters.AddWithValue("$da", (long)p.ActiveOnBattery.TotalSeconds);
                cmd.Parameters.AddWithValue("$as", (long)p.StandbyOnCharger.TotalSeconds);
                cmd.Parameters.AddWithValue("$ds", (long)p.StandbyOnBattery.TotalSeconds);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public List<GuardianX.Core.Battery.ReportPeriod> ReportPeriods()
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT * FROM report ORDER BY end";
            using var r = cmd.ExecuteReader();
            var list = new List<GuardianX.Core.Battery.ReportPeriod>();
            while (r.Read())
                list.Add(new GuardianX.Core.Battery.ReportPeriod(
                    DateTime.Parse(r.GetString(0)), DateTime.Parse(r.GetString(1)), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4),
                    TimeSpan.FromSeconds(r.GetInt64(5)), TimeSpan.FromSeconds(r.GetInt64(6)),
                    TimeSpan.FromSeconds(r.GetInt64(7)), TimeSpan.FromSeconds(r.GetInt64(8))));
            return list;
        }
    }

    public List<(string App, int Times)> GpuHolders(DateTimeOffset from)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT app, COUNT(*) c FROM gpu WHERE ts >= $a GROUP BY app ORDER BY c DESC";
            cmd.Parameters.AddWithValue("$a", from.ToUnixTimeSeconds());
            using var r = cmd.ExecuteReader();
            var list = new List<(string, int)>();
            while (r.Read()) list.Add((r.GetString(0), r.GetInt32(1)));
            return list;
        }
    }

    /// <summary>Adds an app's estimated battery use for today.</summary>
    public void AddAppEnergy(string app, string name, double mwh, double cpuSeconds)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO app_energy VALUES($d,$a,$n,$m,$c)
                ON CONFLICT(day, app) DO UPDATE SET mwh = mwh + $m, cpu = cpu + $c, name = $n
                """;
            cmd.Parameters.AddWithValue("$d", DateTime.Now.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("$a", app);
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$m", mwh);
            cmd.Parameters.AddWithValue("$c", cpuSeconds);
            cmd.ExecuteNonQuery();
        }
    }

    public List<(string App, string Name, double MWh)> AppEnergy(int days)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT app, MAX(name), SUM(mwh) s FROM app_energy WHERE day >= $d GROUP BY app ORDER BY s DESC LIMIT 12";
            cmd.Parameters.AddWithValue("$d", DateTime.Now.AddDays(-days + 1).ToString("yyyy-MM-dd"));
            using var r = cmd.ExecuteReader();
            var list = new List<(string, string, double)>();
            while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.GetDouble(2)));
            return list;
        }
    }

    /// <summary>Minutes spent in each 10% charge band (0-9, 10-19 ... 90-100) over the last days.</summary>
    public double[] ChargeHistogram(int days)
    {
        var bands = new double[10];
        var s = Samples(DateTimeOffset.Now.AddDays(-days), DateTimeOffset.Now.AddMinutes(1));
        for (int i = 1; i < s.Count; i++)
        {
            double m = (s[i].Time - s[i - 1].Time).TotalMinutes;
            if (m <= 0 || m > 5) continue;
            bands[Math.Min(9, (int)(s[i - 1].Percent / 10))] += m;
        }
        return bands;
    }

    /// <summary>Minutes awake per weekday (0 = Monday) and hour, split into on charger / on battery.</summary>
    public (double[,] Charger, double[,] Battery) UsageGrid(int days)
    {
        var ac = new double[7, 24];
        var dc = new double[7, 24];
        var s = Samples(DateTimeOffset.Now.AddDays(-days), DateTimeOffset.Now.AddMinutes(1));
        for (int i = 1; i < s.Count; i++)
        {
            double m = (s[i].Time - s[i - 1].Time).TotalMinutes;
            if (m <= 0 || m > 5) continue;
            var t = s[i - 1].Time;
            int wd = ((int)t.DayOfWeek + 6) % 7;
            if (s[i - 1].OnAc) ac[wd, t.Hour] += m; else dc[wd, t.Hour] += m;
        }
        return (ac, dc);
    }

    /// <summary>
    /// The last time the charge went from under 15% to 99% or more (a full calibration cycle): the first full reading
    /// after the latest low reading that was later followed by a full one. Null if that never happened.
    /// </summary>
    public DateTimeOffset? LastCalibrated()
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT MIN(ts) FROM samples WHERE pct >= 99 AND ts > (
                  SELECT MAX(ts) FROM samples WHERE pct < 15 AND ts < (SELECT MAX(ts) FROM samples WHERE pct >= 99))
                """;
            var v = cmd.ExecuteScalar();
            return v is null or DBNull ? null : DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(v)).ToLocalTime();
        }
    }

    public int SampleCount()
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM samples";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    /// <summary>"ok" when the database is healthy, otherwise SQLite's description of the damage.</summary>
    public string IntegrityCheck()
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check";
            return Convert.ToString(cmd.ExecuteScalar()) ?? "unknown";
        }
    }

    public void ExportCsv(string path)
    {
        var rows = Samples(DateTimeOffset.FromUnixTimeSeconds(0), DateTimeOffset.Now.AddMinutes(1));
        using var w = new StreamWriter(path);
        w.WriteLine("time,percent,remaining_mwh,full_mwh,design_mwh,cycles,rate_mw,voltage_mv,temperature_c,on_ac,charging");
        foreach (var s in rows)
            w.WriteLine(string.Join(',', s.Time.ToString("yyyy-MM-dd HH:mm:ss"), s.Percent, s.RemainingMWh, s.FullMWh,
                s.DesignMWh, s.Cycles, s.RateMw, s.VoltageMv, s.TemperatureC, s.OnAc ? 1 : 0, s.Charging ? 1 : 0));
    }

    public void DeleteAll()
    {
        lock (_lock) Exec("DELETE FROM samples; DELETE FROM events; DELETE FROM gpu; DELETE FROM report; DELETE FROM app_energy; VACUUM;");
    }

    /// <summary>Drops samples older than two years so the file stays small.</summary>
    public void Prune()
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM samples WHERE ts < $a; DELETE FROM gpu WHERE ts < $a; DELETE FROM events WHERE ts < $a;";
            cmd.Parameters.AddWithValue("$a", DateTimeOffset.Now.AddYears(-2).ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
        }
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _db.Dispose();
        if (_ownedFolder is null) { SqliteConnection.ClearAllPools(); return; }
        SqliteConnection.ClearPool(_db);
        try { Directory.Delete(_ownedFolder, true); } catch { }
    }
}
