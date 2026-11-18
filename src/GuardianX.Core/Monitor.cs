using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GuardianX.Core.Apps;
using GuardianX.Core.Battery;
using GuardianX.Core.Data;
using GuardianX.Core.Gpu;
using GuardianX.Core.Insights;
using GuardianX.Core.Power;

namespace GuardianX.Core;

public sealed class Settings
{
    public bool StartWithWindows { get; set; } = true;
    public bool NotifyFullHold { get; set; } = true;
    public int FullHoldMinutes { get; set; } = 120;
    public bool NotifyLow { get; set; } = true;
    public int LowPercent { get; set; } = 20;
    public bool NotifyHigh { get; set; } = true;
    public int HighPercent { get; set; } = 80;
    public bool NotifySleepDrain { get; set; } = true;
    public bool NotifyGpu { get; set; } = true;
    public bool SoundsOn { get; set; } = true;
    public string SoundPack { get; set; } = "nebula";
    public bool PlugSounds { get; set; } = true;
    public string Mode { get; set; } = "desk";
    public bool ApplyPowerMode { get; set; } = true;
    public bool ChargeLimitOn { get; set; } = true;
    public PlugConfig Plug { get; set; } = new();
    public string ChatModel { get; set; } = "qwen2.5:7b";
    public string EmbedModel { get; set; } = "nomic-embed-text";

    public static string FilePath => Path.Combine(Store.DefaultFolder, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
                s.Plug.Password = Unprotect(s.Plug.Password);
                if (s.LowPercent == 15) s.LowPercent = 20; // v1 default
                return s;
            }
        }
        catch (JsonException) { }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Store.DefaultFolder);
        var copy = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this))!;
        copy.Plug.Password = Protect(Plug.Password);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, FilePath, overwrite: true);
    }

    // The plug password is encrypted with Windows DPAPI: only this Windows account can read it back.
    private static string Protect(string plain) => string.IsNullOrEmpty(plain) ? "" :
        "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));

    private static string Unprotect(string stored)
    {
        if (!stored.StartsWith("dpapi:")) return stored;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[6..]), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }
}

public sealed record Alert(string Title, string Body, Cue Sound = Cue.Alert);

public sealed record PlugState(bool Configured, string Name, bool? On, string Status, DateTimeOffset? LastAction);

/// <summary>Samples the battery once a minute, stores it, runs the charge limiter and raises alerts. UI-free.</summary>
public sealed class GuardianMonitor : IDisposable
{
    public Store Store { get; }
    public Settings Settings { get; set; }
    public DgpuWatcher Gpu { get; }
    public AppMonitor Apps { get; } = new();
    public BatterySnapshot? Latest { get; private set; }
    public IReadOnlyList<GpuHolder> GpuHolders { get; private set; } = Array.Empty<GpuHolder>();
    public string? LastError { get; private set; }
    public PlugState Plug { get; private set; } = new(false, "", null, "No smart plug set up.", null);
    public bool ChargeToFull { get; set; }

    public event Action<BatterySnapshot>? Updated;
    public event Action<Alert>? Alerted;
    public event Action? ReportImported;

    private readonly Timer _timer;
    private int _ticking;
    private bool _fullHoldWarned, _lowWarned, _highWarned, _plugMismatchWarned;
    private readonly HashSet<string> _gpuWarned = new(StringComparer.OrdinalIgnoreCase);
    private bool? _lastOnAc;
    private DateTime _lastReport = DateTime.MinValue;
    private ISmartPlug? _plug;
    private string _plugKey = "";

    public GuardianMonitor(Store store, Settings settings, DgpuWatcher gpu)
    {
        Store = store;
        Settings = settings;
        Gpu = gpu;
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        Store.Prune();
        Task.Run(() => { try { Sounds.Generate(); } catch { } });
        _timer.Change(TimeSpan.Zero, TimeSpan.FromMinutes(1));
    }

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
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return; // one tick at a time
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
                if (s.OnAc) { _lowWarned = false; _gpuWarned.Clear(); }
                else { _fullHoldWarned = false; _highWarned = false; ChargeToFull = false; }
                if (Settings.PlugSounds && Settings.SoundsOn) Sounds.Play(s.OnAc ? Cue.PluggedIn : Cue.Unplugged, Settings.SoundPack);
            }
            _lastOnAc = s.OnAc;

            ImportReportIfDue();
            RunChargeLimiter(s);
            CheckThresholds(s);
            CheckFullHold(s);
            TrackApps(s);
            Updated?.Invoke(s);
        }
        catch (Exception e)
        {
            LastError = e.Message;
        }
        finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    private void Raise(Alert a)
    {
        if (Settings.SoundsOn) Sounds.Play(a.Sound, Settings.SoundPack);
        Alerted?.Invoke(a);
    }

    // ---------- smart plug ----------

    private ISmartPlug? CurrentPlug()
    {
        var key = $"{Settings.Plug.Kind}|{Settings.Plug.Host}|{Settings.Plug.Username}|{Settings.Plug.Password.GetHashCode()}";
        if (key != _plugKey) { _plug = SmartPlugs.Create(Settings.Plug); _plugKey = key; }
        return _plug;
    }

    public async Task<PlugState> RefreshPlugAsync()
    {
        var plug = CurrentPlug();
        if (plug is null) return Plug = new(false, "", null, "No smart plug set up.", Plug.LastAction);
        try
        {
            var on = await plug.IsOnAsync();
            return Plug = new(true, plug.Name, on, on == true ? "On, charging allowed" : "Off, holding the battery", Plug.LastAction);
        }
        catch (Exception e)
        {
            return Plug = new(true, plug.Name, null, "Can't reach the plug: " + e.Message, Plug.LastAction);
        }
    }

    public async Task SetPlugAsync(bool on)
    {
        var plug = CurrentPlug() ?? throw new InvalidOperationException("No smart plug set up.");
        await plug.SetAsync(on);
        Plug = Plug with { On = on, LastAction = DateTimeOffset.Now };
        await RefreshPlugAsync();
    }

    private void RunChargeLimiter(BatterySnapshot s)
    {
        if (!Settings.ChargeLimitOn || CurrentPlug() is null) { _ = RefreshPlugAsync(); return; }
        var state = RefreshPlugAsync().GetAwaiter().GetResult();
        var mode = Modes.Get(Settings.Mode);
        if (ChargeToFull && s.Percent >= 99.5) ChargeToFull = false;
        var action = ChargeController.Decide(s.OnAc, s.Percent, mode.ChargeLimit, mode.ResumeAt, state.On, ChargeToFull);
        switch (action)
        {
            case PlugAction.TurnOff:
                SetPlugAsync(false).GetAwaiter().GetResult();
                Store.AddEvent("plug_off", s.Percent);
                Raise(new Alert($"Holding at {s.Percent:0}%", $"Smart plug switched off. It switches back on at {mode.ResumeAt}%.", Cue.Charged));
                _highWarned = true;
                break;
            case PlugAction.TurnOn:
                SetPlugAsync(true).GetAwaiter().GetResult();
                Store.AddEvent("plug_on", s.Percent);
                Raise(new Alert($"Charging again at {s.Percent:0}%", $"Smart plug switched on. It stops at {mode.ChargeLimit}%.", Cue.PluggedIn));
                break;
            case PlugAction.ChargerNotOnPlug when !_plugMismatchWarned:
                _plugMismatchWarned = true;
                Raise(new Alert("The charger isn't on the smart plug",
                    "The plug is off but the laptop is still charging. Plug the charger into the smart plug for the limit to work."));
                break;
        }
        if (action != PlugAction.ChargerNotOnPlug) _plugMismatchWarned = false;
    }

    // ---------- alerts ----------

    private void CheckThresholds(BatterySnapshot s)
    {
        if (Settings.NotifyHigh && !_highWarned && s.OnAc && s.Percent >= Settings.HighPercent)
        {
            _highWarned = true;
            bool plugHandles = Settings.ChargeLimitOn && CurrentPlug() is not null && Modes.Get(Settings.Mode).ChargeLimit <= Settings.HighPercent;
            if (!plugHandles)
                Raise(new Alert($"Battery at {s.Percent:0}%", "Unplug now to keep the battery healthy, unless you need a full charge.", Cue.Charged));
        }
        if (Settings.NotifyLow && !_lowWarned && !s.OnAc && s.Percent <= Settings.LowPercent)
        {
            _lowWarned = true;
            var hours = BatteryInsights.HoursLeft(s.RemainingMWh, s.FullMWh, s.RateMw);
            Raise(new Alert($"Battery at {s.Percent:0}%",
                (hours is double h ? $"About {Math.Max(1, (int)(h * 60))} minutes left. " : "") +
                "Plug in soon. Running it close to empty also wears the battery.", Cue.Low));
        }
    }

    private void CheckFullHold(BatterySnapshot s)
    {
        if (!Settings.NotifyFullHold || _fullHoldWarned || !s.OnAc) return;
        var recent = Store.Samples(DateTimeOffset.Now.AddHours(-12), DateTimeOffset.Now.AddMinutes(1));
        int streak = BatteryInsights.FullHoldStreakMinutes(recent);
        if (streak < Settings.FullHoldMinutes) return;
        _fullHoldWarned = true;
        Raise(new Alert($"At 100% on the charger for {BatteryInsights.FormatMinutes(streak)}",
            "Unplug for a while if you don't need a full battery. Sitting full is what ages it fastest."));
    }

    private void TrackApps(BatterySnapshot s)
    {
        double? watts = !s.OnAc && s.RateMw is int r && r < 0 ? -r / 1000.0 : null;
        var apps = Apps.Sample(watts);
        if (watts is double w)
        {
            // One minute at w watts = w/60 Wh = w*1000/60 mWh, shared out by the estimate.
            foreach (var a in apps.Where(a => a.EstimatedWatts > 0.05).Take(25))
                Store.AddAppEnergy(a.Key, a.Name, a.EstimatedWatts!.Value * 1000 / 60, a.CpuPercent / 100 * Environment.ProcessorCount * 60);
        }
        CheckGpu(s);
    }

    private void CheckGpu(BatterySnapshot s)
    {
        if (s.OnAc || !Gpu.Available) { GpuHolders = Array.Empty<GpuHolder>(); return; }
        GpuHolders = Gpu.Holders();
        bool alerts = Settings.NotifyGpu && Modes.Get(Settings.Mode).GpuAlerts;
        foreach (var h in GpuHolders)
        {
            Store.AddGpu(h.ProcessName, h.DedicatedBytes);
            if (alerts && _gpuWarned.Add(h.ProcessName))
                Raise(new Alert($"{h.ProcessName} is keeping the NVIDIA GPU awake",
                    "That drains the battery much faster. Close it if you're not using it."));
        }
    }

    public void ApplyMode(string id)
    {
        var mode = Modes.Get(id);
        Settings.Mode = mode.Id;
        if (Settings.ApplyPowerMode) Modes.Apply(mode.Overlay);
        Settings.Save();
        Task.Run(Tick);
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
            Raise(new Alert($"Lost {sleeps[^1].PercentLost:0.#}% while asleep",
                $"{sleeps[^1].PercentPerHour:0.#}% an hour, more than usual. Open Guardian X to see why."));
    }

    public void Dispose() => _timer.Dispose();
}
