using GuardianX.Core.Battery;
using Microsoft.Data.Sqlite;

namespace GuardianX.Core.Data;

/// <summary>A stored battery sample (one a minute).</summary>
public sealed record Sample(
    DateTimeOffset Time, double Percent, int RemainingMWh, int FullMWh, int DesignMWh, int Cycles,
    int? RateMw, int? VoltageMv, double? TemperatureC, bool OnAc, bool Charging);

/// <summary>Something that happened: sleep, wake, charger in or out.</summary>
public sealed record PowerEvent(DateTimeOffset Time, string Kind, double Percent);

/// <summary>Local SQLite history. Nothing leaves this PC.</summary>
public sealed class Store : IDisposable
{
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GuardianXData");

    public string DbPath { get; }
    private readonly SqliteConnection _db;
    private readonly object _lock = new();

    public Store(string? dbPath = null)
    {
        DbPath = dbPath ?? Path.Combine(DefaultFolder, "guardian.db");
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        _db = new SqliteConnection($"Data Source={DbPath}");
        _db.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS samples(
              ts INTEGER PRIMARY KEY, pct REAL, remaining INTEGER, full INTEGER, design INTEGER, cycles INTEGER,
              rate INTEGER, voltage INTEGER, temp REAL, on_ac INTEGER, charging INTEGER);
            CREATE TABLE IF NOT EXISTS events(ts INTEGER, kind TEXT, pct REAL);
            CREATE INDEX IF NOT EXISTS events_ts ON events(ts);
            CREATE TABLE IF NOT EXISTS gpu(ts INTEGER, app TEXT, bytes INTEGER);
            CREATE TABLE IF NOT EXISTS report(
              start TEXT, end TEXT PRIMARY KEY, full INTEGER, design INTEGER, cycles INTEGER,
              ac_active INTEGER, dc_active INTEGER, ac_standby INTEGER, dc_standby INTEGER);
            CREATE INDEX IF NOT EXISTS gpu_ts ON gpu(ts);
            """);
    }

    public void Add(BatterySnapshot s)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO samples VALUES($ts,$pct,$rem,$full,$design,$cycles,$rate,$volt,$temp,$ac,$chg)
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
            cmd.CommandText = "SELECT * FROM samples WHERE ts >= $a AND ts < $b ORDER BY ts";
            cmd.Parameters.AddWithValue("$a", from.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$b", to.ToUnixTimeSeconds());
            using var r = cmd.ExecuteReader();
            var list = new List<Sample>();
            while (r.Read())
                list.Add(new Sample(
                    DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)).ToLocalTime(), r.GetDouble(1), r.GetInt32(2),
                    r.GetInt32(3), r.GetInt32(4), r.GetInt32(5),
                    r.IsDBNull(6) ? null : r.GetInt32(6), r.IsDBNull(7) ? null : r.GetInt32(7),
                    r.IsDBNull(8) ? null : r.GetDouble(8), r.GetInt32(9) == 1, r.GetInt32(10) == 1));
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
        lock (_lock) Exec("DELETE FROM samples; DELETE FROM events; DELETE FROM gpu; DELETE FROM report; VACUUM;");
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
        SqliteConnection.ClearAllPools();
    }
}
