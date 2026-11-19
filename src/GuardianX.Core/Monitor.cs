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
    public string SoundPack { get; set; } = "aurora";
    public bool PlugSounds { get; set; } = true;
    public string Mode { get; set; } = "desk";
    public bool ApplyPowerMode { get; set; } = true;
    public bool ChargeLimitOn { get; set; } = true;
    public PlugConfig Plug { get; set; } = new();

    // Quiet hours: alerts still show, without sound.
    public bool QuietHoursOn { get; set; }
    public int QuietFrom { get; set; } = 23;
    public int QuietTo { get; set; } = 7;

    // Auto-actions
    public bool AutoModeOnPower { get; set; }
    public string AcMode { get; set; } = "desk";
    public string BatteryMode { get; set; } = "saver";
    public bool AutoGamingMode { get; set; }
    public bool AutoEfficiencyOnBattery { get; set; }
    public List<string> EfficiencyApps { get; set; } = new();

    public bool WeeklyReportOn { get; set; } = true;
    public string LastWeeklyReport { get; set; } = "";
    public bool WidgetOn { get; set; }
    // Null until the widget has been placed (NaN can't be written as JSON).
    public double? WidgetLeft { get; set; }
    public double? WidgetTop { get; set; }

    /// <summary>"system", "light" or "dark". Read by the web interface and the mini widget.</summary>
    public string Theme { get; set; } = "system";
    /// <summary>Warn when, on battery, the charge won't last until the usual plug-in time.</summary>
    public bool SmartLowAlert { get; set; } = true;
    /// <summary>Windows toast notifications with buttons; off means the classic tray balloons.</summary>
    public bool ToastsOn { get; set; } = true;

    public bool IsQuiet(DateTime now) =>
        QuietHoursOn && QuietFrom != QuietTo &&
        (QuietFrom < QuietTo ? now.Hour >= QuietFrom && now.Hour < QuietTo : now.Hour >= QuietFrom || now.Hour < QuietTo);

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
    public event Action<string>? WeeklyReportReady;

    private readonly Timer _timer;
    private readonly Timer _fast;
    private int _ticking, _fastBusy;
    private bool _fullHoldWarned, _lowWarned, _highWarned, _plugMismatchWarned;
    private readonly HashSet<string> _gpuWarned = new(StringComparer.OrdinalIgnoreCase);
    private bool? _lastOnAc;
    private string? _modeBeforeGame;
    private int _ticks;
    public bool GamingDetected { get; private set; }
    public string? LastAutoAction { get; private set; }
    private DateTime _lastReport = DateTime.MinValue;
    private ISmartPlug? _plug;
    private string _plugKey = "";

    /// <summary>Fast 5-second readings for an accurate, smoothed power figure.</summary>
    public PowerSampler Power { get; } = new();
    public HeatSensor HeatSensor { get; } = new();
    public HeatReading? Heat { get; private set; }
    public HealthPrecision? HealthPrecise { get; private set; }
    public DateTimeOffset? LastCalibrated { get; private set; }
    public HabitReport? Habit { get; private set; }
    /// <summary>Full-screen game, or the foreground app is busy on the NVIDIA GPU.</summary>
    public bool GamingNow { get; private set; }
    private IReadOnlyList<GpuHolder> _holdersNow = Array.Empty<GpuHolder>();
    private IReadOnlyList<PowerEvent> _events21 = Array.Empty<PowerEvent>();
    private DateTime _lastInsights = DateTime.MinValue;
    private bool _smartLowWarned;

    public GuardianMonitor(Store store, Settings settings, DgpuWatcher gpu)
    {
        Store = store;
        Settings = settings;
        Gpu = gpu;
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        _fast = new Timer(_ => FastSample(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>A lightweight reading every 5 seconds, kept only in memory for power smoothing.</summary>
    private void FastSample()
    {
        if (Interlocked.Exchange(ref _fastBusy, 1) == 1) return;
        try
        {
            if (BatteryReader.Read() is { } s) Power.Add(new PowerPoint(s.Time, s.RemainingMWh, s.RateMw, s.OnAc));
        }
        catch { }
        finally { Interlocked.Exchange(ref _fastBusy, 0); }
    }

    /// <summary>The smoothed power right now: 'energy' (slope of remaining Wh), 'driver' (smoothed driver rate) or 'none'.</summary>
    public SmoothedRate Rate => Power.Current(DateTimeOffset.Now);

    /// <summary>The reading with its rate replaced by the smoothed one, for hours left and time to full.</summary>
    public BatterySnapshot? WithSmoothedRate(BatterySnapshot? s)
    {
        if (s is null) return null;
        if (Rate.RateMw is not double mw) return s;
        int rate = Math.Abs(mw) < 300 ? 0 : (int)Math.Round(mw); // under 0.3 W is noise (idle on the charger)
        return s with { RateMw = rate };
    }

    /// <summary>The plug-in routine and what it means for the current charge.</summary>
    public UsagePattern PatternNow(BatterySnapshot? s)
    {
        var sm = WithSmoothedRate(s);
        int limit = Settings.ChargeLimitOn ? Modes.Get(Settings.Mode).ChargeLimit : 100;
        return SmartInsights.Pattern(_events21, sm, sm?.RateMw, limit, DateTimeOffset.Now);
    }

    /// <summary>Precise health, habit score and the event history behind the routine; refreshed every few minutes.</summary>
    private void RefreshInsights(BatterySnapshot s)
    {
        if (DateTime.Now - _lastInsights < TimeSpan.FromMinutes(5)) return;
        _lastInsights = DateTime.Now;
        var now = DateTimeOffset.Now;
        var samples = Store.Samples(now.AddDays(-14), now.AddMinutes(1));
        HealthPrecise = SmartInsights.PreciseHealth(samples, s.FullMWh, s.DesignMWh, now);
        Habit = SmartInsights.Habit(samples, now);
        LastCalibrated = Store.LastCalibrated();
        _events21 = Store.Events(now.AddDays(-21));
    }

    public void Start()
    {
        if (Store.Recovery is string recovered)
            Task.Delay(4000).ContinueWith(_ => Raise(new Alert("Guardian X repaired its history", recovered), sound: false));
        Store.Prune();
        Task.Run(() => { try { Sounds.Generate(); } catch { } });
        _timer.Change(TimeSpan.Zero, TimeSpan.FromMinutes(1));
        _fast.Change(PowerSampler.Interval, PowerSampler.Interval);
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
            Power.Add(new PowerPoint(s.Time, s.RemainingMWh, s.RateMw, s.OnAc));
            try
            {
                _holdersNow = Gpu.Available ? Gpu.Holders() : Array.Empty<GpuHolder>();
                GamingNow = GameDetector.FullScreenGame() || GameDetector.ForegroundOnGpu(_holdersNow);
                var sm = WithSmoothedRate(s)!;
                Heat = HeatSensor.Read(s.TemperatureC, _holdersNow.Count > 0, sm.RateMw is int cr && cr > 0 ? cr / 1000.0 : (double?)null);
            }
            catch { }
            Store.Add(s, Heat?.Score);
            Latest = s;

            if (_lastOnAc is bool was && was != s.OnAc)
            {
                Store.AddEvent(s.OnAc ? "ac_on" : "ac_off", s.Percent);
                if (s.OnAc) { _lowWarned = false; _gpuWarned.Clear(); }
                else { _fullHoldWarned = false; _highWarned = false; ChargeToFull = false; }
                if (Settings.PlugSounds && Settings.SoundsOn && !Settings.IsQuiet(DateTime.Now)) Sounds.Play(s.OnAc ? Cue.PluggedIn : Cue.Unplugged, Settings.SoundPack);
                OnPowerSourceChanged(s.OnAc);
                _lastInsights = DateTime.MinValue; // pick up the new charger event for the routine
            }
            _lastOnAc = s.OnAc;
            RunAutoActions(s);

            ImportReportIfDue();
            try { RefreshInsights(s); } catch { }
            RunChargeLimiter(s);
            CheckThresholds(s);
            CheckSmartLow(s);
            CheckFullHold(s);
            TrackApps(s);
            CheckWeeklyReport(s);
            if (_ticks++ % 5 == 0) try { Store.WriteSnapshot(); } catch { } // consistent copy for the MCP server
            Updated?.Invoke(s);
        }
        catch (Exception e)
        {
            LastError = e.Message;
        }
        finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    private void Raise(Alert a, bool sound = true)
    {
        if (sound && Settings.SoundsOn && !Settings.IsQuiet(DateTime.Now)) Sounds.Play(a.Sound, Settings.SoundPack);
        Alerted?.Invoke(a);
    }

    // ---------- auto-actions ----------

    private void OnPowerSourceChanged(bool onAc)
    {
        if (Settings.AutoModeOnPower && !GamingDetected)
        {
            var target = Modes.Get(onAc ? Settings.AcMode : Settings.BatteryMode);
            if (target.Id != Settings.Mode)
            {
                SwitchMode(target.Id);
                Note($"Switched to {target.Name} mode ({(onAc ? "charger connected" : "on battery")}).");
            }
        }
        if (Settings.AutoEfficiencyOnBattery && onAc)
            foreach (var app in Settings.EfficiencyApps) EfficiencyMode.Set(app, false);
    }

    private void RunAutoActions(BatterySnapshot s)
    {
        if (Settings.AutoGamingMode)
        {
            bool game = GamingNow;
            if (game && !GamingDetected)
            {
                GamingDetected = true;
                if (Settings.Mode != "gaming")
                {
                    _modeBeforeGame = Settings.Mode;
                    SwitchMode("gaming");
                    Note("Game detected: switched to Gaming mode.");
                }
            }
            else if (!game && GamingDetected)
            {
                GamingDetected = false;
                var back = _modeBeforeGame ?? (Settings.AutoModeOnPower ? (s.OnAc ? Settings.AcMode : Settings.BatteryMode) : Settings.Mode);
                _modeBeforeGame = null;
                if (back != Settings.Mode) { SwitchMode(back); Note($"Game closed: back to {Modes.Get(back).Name} mode."); }
            }
        }
        else GamingDetected = false;

        // Re-applied every minute on battery so newly started processes of those apps are covered too.
        if (Settings.AutoEfficiencyOnBattery && !s.OnAc)
            foreach (var app in Settings.EfficiencyApps.Where(EfficiencyMode.Allowed)) EfficiencyMode.Set(app, true);
    }

    /// <summary>Once a week (from Monday morning) writes the report for the past 7 days and says so.</summary>
    private void CheckWeeklyReport(BatterySnapshot s)
    {
        if (!Settings.WeeklyReportOn) return;
        var now = DateTime.Now;
        var monday = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
        if (now.Hour < 9 || Settings.LastWeeklyReport == monday.ToString("yyyy-MM-dd")) return;
        if (Store.SampleCount() < 60) return; // not enough to report on yet
        var ctx = Assistant.AssistantContext.Build(Store, WithSmoothedRate(s), Apps.LastSample, Plug, Settings,
            heat: Heat, habit: Habit, pattern: PatternNow(s));
        var path = Reports.WeeklyReport.Write(Store, ctx, now);
        Settings.LastWeeklyReport = monday.ToString("yyyy-MM-dd");
        Settings.Save();
        WeeklyReportReady?.Invoke(path);
        Raise(new Alert("Your weekly battery report is ready", "Click to open it. It's also in Settings > Reports.", Cue.Alert), sound: false);
    }

    private void Note(string text)
    {
        LastAutoAction = $"{DateTime.Now:HH:mm} {text}";
        Store.AddEvent("auto", Latest?.Percent ?? 0);
        Alerted?.Invoke(new Alert("Guardian X", text, Cue.Alert));
    }

    private void SwitchMode(string id)
    {
        var mode = Modes.Get(id);
        Settings.Mode = mode.Id;
        if (Settings.ApplyPowerMode) Modes.Apply(mode.Overlay);
        Settings.Save();
    }

    /// <summary>Turns Windows Efficiency mode on or off for an app now, and remembers it for auto-actions.</summary>
    public int SetEfficiency(string processName, bool on)
    {
        if (!EfficiencyMode.Allowed(processName)) throw new InvalidOperationException($"{processName} is part of Windows or Guardian X and is left alone.");
        Settings.EfficiencyApps.RemoveAll(a => string.Equals(a, processName, StringComparison.OrdinalIgnoreCase));
        if (on) Settings.EfficiencyApps.Add(processName);
        Settings.Save();
        return EfficiencyMode.Set(processName, on);
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
            var sm = WithSmoothedRate(s)!;
            var hours = BatteryInsights.HoursLeft(sm.RemainingMWh, sm.FullMWh, sm.RateMw);
            Raise(new Alert($"Battery at {s.Percent:0}%",
                (hours is double h ? $"About {Math.Max(1, (int)(h * 60))} minutes left. " : "") +
                "Plug in soon. Running it close to empty also wears the battery.", Cue.Low));
        }
    }

    /// <summary>One alert per discharge when the charge, at the smoothed rate, runs out 30+ minutes before the usual plug-in.</summary>
    private void CheckSmartLow(BatterySnapshot s)
    {
        if (s.OnAc) { _smartLowWarned = false; return; }
        if (!Settings.SmartLowAlert || _smartLowWarned) return;
        var rate = Rate;
        if (rate.Method != "energy" || rate.RateMw is not double mw || mw >= 0) return; // only on a settled measurement
        if (SmartInsights.Routine(_events21).PlugIn is not TimeSpan plugIn) return;
        var now = DateTimeOffset.Now;
        double hours = s.RemainingMWh / -mw;
        var at = SmartInsights.Next(plugIn, now);
        if (!SmartInsights.ShouldWarnSmartLow(false, hours, now, at)) return;
        _smartLowWarned = true;
        Raise(new Alert("This charge won't last until you usually plug in",
            $"At {-mw / 1000:0.0} W it runs out around {now.AddHours(hours).ToLocalTime():HH:mm}, and you usually plug in at {plugIn.Hours:00}:{plugIn.Minutes:00}. " +
            "Switch to Saver mode or plug in sooner.", Cue.Low));
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
        GpuHolders = _holdersNow;
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
        _modeBeforeGame = null; // a manual choice wins over the game auto-switch
        SwitchMode(id);
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

    public void Dispose()
    {
        _timer.Dispose();
        _fast.Dispose();
        HeatSensor.Dispose();
    }
}
