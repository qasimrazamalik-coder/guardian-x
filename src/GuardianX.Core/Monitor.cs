using System.Text.Json;
using GuardianX.Core.Battery;
using GuardianX.Core.Data;
using GuardianX.Core.Gpu;
using GuardianX.Core.Insights;

namespace GuardianX.Core;

public sealed class Settings
{
    public bool StartWithWindows { get; set; } = true;
    public bool NotifyFullHold { get; set; } = true;
    public int FullHoldMinutes { get; set; } = 120;
    public bool NotifyLow { get; set; } = true;
    public int LowPercent { get; set; } = 15;
    public bool NotifySleepDrain { get; set; } = true;
    public bool NotifyGpu { get; set; } = true;

    public static string FilePath => Path.Combine(Store.DefaultFolder, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (JsonException) { }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Store.DefaultFolder);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, FilePath, overwrite: true);
    }
}

public sealed record Alert(string Title, string Body);

/// <summary>Samples the battery once a minute, stores it, and raises alerts. UI-free so it can be tested and reused.</summary>
public sealed class GuardianMonitor : IDisposable
{
    public Store Store { get; }
    public Settings Settings { get; set; }
    public DgpuWatcher Gpu { get; }
    public BatterySnapshot? Latest { get; private set; }
    public IReadOnlyList<GpuHolder> GpuHolders { get; private set; } = Array.Empty<GpuHolder>();
    public string? LastError { get; private set; }

    public event Action<BatterySnapshot>? Updated;
    public event Action<Alert>? Alerted;

    private readonly Timer _timer;
    private bool _fullHoldWarned, _lowWarned;
    private readonly HashSet<string> _gpuWarned = new(StringComparer.OrdinalIgnoreCase);
    private bool? _lastOnAc;

    public GuardianMonitor(Store store, Settings settings, DgpuWatcher gpu)
    {
        Store = store;
        Settings = settings;
        Gpu = gpu;
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event Action? ReportImported;
    private DateTime _lastReport = DateTime.MinValue;

    public void Start()
    {
        Store.Prune();
        _timer.Change(TimeSpan.Zero, TimeSpan.FromMinutes(1));
    }

    /// <summary>Copies Windows' battery history into the store, at most once a day. Takes a few seconds.</summary>
    public void ImportReportIfDue()
    {
        if (DateTime.Now - _lastReport < TimeSpan.FromHours(24)) return;
        _lastReport = DateTime.Now;
        var periods = BatteryReport.Load();
        if (periods.Count == 0) return;
        Store.SaveReport(periods);
        ReportImported?.Invoke();
    }

    public void Tick()
    {
        try
        {
            var s = BatteryReader.Read();
            if (s is null) { LastError = "No battery found."; return; }
            LastError = null;
            Store.Add(s);
            Latest = s;

            if (_lastOnAc is bool was && was != s.OnAc)
            {
                Store.AddEvent(s.OnAc ? "ac_on" : "ac_off", s.Percent);
                if (s.OnAc) { _lowWarned = false; _gpuWarned.Clear(); } else _fullHoldWarned = false;
            }
            _lastOnAc = s.OnAc;

            ImportReportIfDue();
            CheckFullHold(s);
            CheckLow(s);
            CheckGpu(s);
            Updated?.Invoke(s);
        }
        catch (Exception e)
        {
            LastError = e.Message;
        }
    }

    private void CheckFullHold(BatterySnapshot s)
    {
        if (!Settings.NotifyFullHold || _fullHoldWarned || !s.OnAc) return;
        var recent = Store.Samples(DateTimeOffset.Now.AddHours(-12), DateTimeOffset.Now.AddMinutes(1));
        int streak = BatteryInsights.FullHoldStreakMinutes(recent);
        if (streak < Settings.FullHoldMinutes) return;
        _fullHoldWarned = true;
        Alerted?.Invoke(new Alert($"At 100% on the charger for {BatteryInsights.FormatMinutes(streak)}",
            "Unplug for a while if you don't need a full battery. Sitting full is what ages it fastest."));
    }

    private void CheckLow(BatterySnapshot s)
    {
        if (!Settings.NotifyLow || _lowWarned || s.OnAc || s.Percent > Settings.LowPercent) return;
        _lowWarned = true;
        var hours = BatteryInsights.HoursLeft(s.RemainingMWh, s.FullMWh, s.RateMw);
        Alerted?.Invoke(new Alert($"Battery at {s.Percent:0}%",
            (hours is double h ? $"About {Math.Max(1, (int)(h * 60))} minutes left. " : "") +
            "Plug in soon. Running it close to empty also wears the battery."));
    }

    private void CheckGpu(BatterySnapshot s)
    {
        if (s.OnAc || !Gpu.Available) { GpuHolders = Array.Empty<GpuHolder>(); return; }
        GpuHolders = Gpu.Holders();
        foreach (var h in GpuHolders)
        {
            Store.AddGpu(h.ProcessName, h.DedicatedBytes);
            if (Settings.NotifyGpu && _gpuWarned.Add(h.ProcessName))
                Alerted?.Invoke(new Alert($"{h.ProcessName} is keeping the NVIDIA GPU awake",
                    "That drains the battery much faster. Close it if you're not using it."));
        }
    }

    public void OnSuspend()
    {
        if (Latest is { } s) Store.AddEvent("suspend", BatteryReader.Read()?.Percent ?? s.Percent);
    }

    public void OnResume()
    {
        var now = BatteryReader.Read();
        if (now is null) return;
        Store.AddEvent("resume", now.Percent);
        if (!Settings.NotifySleepDrain) return;
        var sleeps = BatteryInsights.Sleeps(Store.Events(DateTimeOffset.Now.AddDays(-60)));
        if (sleeps.Count > 0 && sleeps[^1].Unusual && sleeps[^1].End > DateTimeOffset.Now.AddMinutes(-5))
            Alerted?.Invoke(new Alert($"Lost {sleeps[^1].PercentLost:0.#}% while asleep",
                $"{sleeps[^1].PercentPerHour:0.#}% an hour, more than usual. Open Guardian X to see why."));
    }

    public void Dispose()
    {
        _timer.Dispose();
    }
}
