using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using GuardianX.Core;
using GuardianX.Core.Apps;
using GuardianX.Core.Assistant;
using GuardianX.Core.Battery;
using GuardianX.Core.Data;
using GuardianX.Core.Insights;
using GuardianX.Core.Power;
using Velopack;
using Velopack.Sources;

namespace GuardianX.App;

/// <summary>
/// Answers the web interface's requests. Each request is {id, method, params}; the reply is {id, result} or {id, error}.
/// Pushed events are {event, data}.
/// </summary>
public sealed class Bridge(GuardianMonitor monitor, Action<string> post, Func<string?> saveDialog)
{
    public const string RepoUrl = "https://github.com/qasimrazamalik-coder/guardian-x";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Ollama _ollama = new();
    private GuardianAssistant? _assistant;
    private GuardianAssistant Assistant => _assistant ??= new GuardianAssistant(monitor.Store, _ollama);
    private CancellationTokenSource? _chatCts;
    private bool _setupRunning;

    public static string Version => typeof(Bridge).Assembly.GetName().Version?.ToString(3) ?? "2.0.0";

    public void Emit(string evt, object? data) => post(JsonSerializer.Serialize(new { @event = evt, data }, Json));

    public async Task HandleAsync(string raw)
    {
        int id = 0;
        try
        {
            var msg = JsonNode.Parse(raw)!;
            id = msg["id"]!.GetValue<int>();
            var method = msg["method"]!.GetValue<string>();
            var p = msg["params"];
            var result = await Dispatch(method, p);
            post(JsonSerializer.Serialize(new { id, result }, Json));
        }
        catch (Exception e)
        {
            App.Log("Bridge: " + e);
            post(JsonSerializer.Serialize(new { id, error = e is TargetInvocationLikeException ? e.InnerException?.Message : e.Message }, Json));
        }
    }

    private sealed class TargetInvocationLikeException : Exception { }

    private Task<object?> Dispatch(string method, JsonNode? p) => method switch
    {
        "dashboard" => Run(Dashboard),
        "day" => Run(Day),
        "history" => Run(History),
        "analytics" => Run(Analytics),
        "apps" => Run(Apps),
        "storage" => Run(() => (object)AppMonitor.Storage()),
        "modes" => Run(() => new { modes = Modes.All, current = monitor.Settings.Mode, applyPowerMode = monitor.Settings.ApplyPowerMode, powerOverlay = Modes.Current()?.ToString() }),
        "setMode" => Run(() => { monitor.ApplyMode(p!["id"]!.GetValue<string>()); return Ok(); }),
        "plugGet" => PlugGet(),
        "plugSave" => PlugSave(p!),
        "plugSet" => PlugSet(p!["on"]!.GetValue<bool>()),
        "chargeToFull" => Run(() => { monitor.ChargeToFull = true; Task.Run(monitor.Tick); return Ok(); }),
        "settingsGet" => Run(() => SettingsView()),
        "settingsSave" => Run(() => { SaveSettings(p!); return Ok(); }),
        "playSound" => Run(() => { Sounds.Play(Enum.Parse<Cue>(p!["cue"]!.GetValue<string>(), true), monitor.Settings.SoundPack); return Ok(); }),
        "assistantStatus" => AssistantStatusAsync(),
        "assistantSetup" => Run(() => { _ = SetupAssistantAsync(); return Ok(); }),
        "assistantReset" => Run(() => { Assistant.History.Clear(); return Ok(); }),
        "ask" => Run(() => { _ = AskAsync(p!["id"]!.GetValue<long>(), p["question"]!.GetValue<string>()); return Ok(); }),
        "mcpInfo" => Run(() => (object)McpSetup.Info()),
        "mcpConnect" => Run(() => { McpSetup.Connect(); return McpSetup.Info(); }),
        "exportCsv" => Run(ExportCsv),
        "openData" => Run(() => { Process.Start(new ProcessStartInfo("explorer.exe", Store.DefaultFolder) { UseShellExecute = true }); return Ok(); }),
        "deleteHistory" => Run(DeleteHistory),
        "repair" => Run(Repair),
        "checkUpdate" => CheckUpdateAsync(),
        "quit" => Run(() => { System.Windows.Application.Current.Dispatcher.BeginInvoke(() => ((App)System.Windows.Application.Current).Quit()); return Ok(); }),
        _ => throw new InvalidOperationException("Unknown method " + method)
    };

    private static Task<object?> Run(Func<object?> f) => Task.Run(f);
    private static object Ok(string? message = null) => new { ok = true, message };

    // ---------- data ----------

    private object Dashboard()
    {
        var store = monitor.Store;
        var now = DateTimeOffset.Now;
        var s = monitor.Latest ?? BatteryReader.Read();
        var settings = monitor.Settings;
        object? battery = null;
        WearReport? wear = null;
        if (s is not null)
        {
            wear = BatteryInsights.Wear(s.FullMWh, s.DesignMWh, s.Cycles, store.DailyCapacity(400));
            battery = new
            {
                percent = s.Percent, onAc = s.OnAc, charging = s.Charging,
                rateW = s.RateMw is int r ? Math.Round(r / 1000.0, 1) : (double?)null,
                voltage = s.VoltageMv is int v ? v / 1000.0 : (double?)null,
                tempC = s.TemperatureC,
                remainingWh = s.RemainingMWh / 1000.0, fullWh = s.FullMWh / 1000.0, designWh = s.DesignMWh / 1000.0, cycles = s.Cycles,
                health = wear.HealthPercent, wear = wear.WearPercent, wearPer100 = wear.WearPer100Cycles, wearPerMonth = wear.WearPerMonth,
                hoursLeft = BatteryInsights.HoursLeft(s.RemainingMWh, s.FullMWh, s.RateMw),
                model = Model, chemistry = s.Chemistry, manufacturer = s.Manufacturer,
            };
        }
        var today = BatteryInsights.Stress(store.Samples(new DateTimeOffset(now.Date, now.Offset), now.AddMinutes(1)));
        int hold = BatteryInsights.FullHoldStreakMinutes(store.Samples(now.AddHours(-12), now.AddMinutes(1)));
        var sleeps = BatteryInsights.Sleeps(store.Events(now.AddDays(-60)));
        var last = sleeps.Count > 0 ? sleeps[^1] : null;
        var chargerHours = BatteryInsights.ChargerHoursPerDay(store.ReportPeriods());
        var tips = wear is null ? new List<Tip>() : BatteryInsights.Coach(wear, today, hold, last, store.GpuHolders(now.AddDays(-7)), chargerHours);
        var plug = monitor.Plug;
        return new
        {
            battery,
            stress = new { score = today.Score, level = today.Level, hoursFull = today.HoursAtFull, hoursHot = today.HoursHot, hoursLow = today.HoursLow, tempKnown = today.TemperatureKnown },
            holdMinutes = hold,
            chargerHours,
            lastSleep = last is null ? null : new { start = last.Start, end = last.End, lost = last.PercentLost, perHour = last.PercentPerHour, unusual = last.Unusual },
            gpu = new { name = monitor.Gpu.AdapterName, available = monitor.Gpu.Available, holders = monitor.GpuHolders },
            tips = tips.Select(t => new { level = t.Level.ToString(), t.Title, t.Body }),
            mode = Modes.Get(settings.Mode),
            plug,
            chargeLimitOn = settings.ChargeLimitOn,
            chargeToFull = monitor.ChargeToFull,
            error = monitor.LastError,
            version = Version,
        };
    }

    private object Day()
    {
        var now = DateTimeOffset.Now;
        return monitor.Store.Samples(now.AddHours(-24), now.AddMinutes(1))
            .Select(x => new { t = x.Time, p = x.Percent, ac = x.OnAc, w = x.RateMw is int r ? r / 1000.0 : (double?)null });
    }

    private object History()
    {
        var store = monitor.Store;
        var now = DateTimeOffset.Now;
        var daily = store.DailyCapacity(400);
        var lastDay = daily.Count > 0 ? daily[^1] : default;
        var wear = daily.Count > 0 ? BatteryInsights.Wear(lastDay.FullMWh, lastDay.DesignMWh, lastDay.Cycles, daily) : null;
        var drop = BatteryInsights.BiggestDrop(daily);
        var days = new List<object>();
        for (int i = 0; i < 14; i++)
        {
            var start = new DateTimeOffset(now.Date.AddDays(-i), now.Offset);
            var samples = store.Samples(start, start.AddDays(1));
            if (samples.Count == 0) continue;
            var st = BatteryInsights.Stress(samples);
            var l = samples[^1];
            days.Add(new
            {
                day = start.ToString("yyyy-MM-dd"), health = l.DesignMWh > 0 ? Math.Round(l.FullMWh * 100.0 / l.DesignMWh, 1) : 0, cycles = l.Cycles,
                stress = st.Score, level = st.Level, hoursFull = st.HoursAtFull, lowest = samples.Min(x => x.Percent)
            });
        }
        return new
        {
            daily = daily.Select(d => new { day = d.Day.ToString("yyyy-MM-dd"), health = d.DesignMWh > 0 ? Math.Round(d.FullMWh * 100.0 / d.DesignMWh, 2) : 0, fullWh = d.FullMWh / 1000.0, cycles = d.Cycles }),
            trendPerMonth = wear?.WearPerMonth,
            biggestDrop = drop is { } x ? new { from = x.From.ToString("yyyy-MM-dd"), to = x.To.ToString("yyyy-MM-dd"), lost = x.Lost } : null,
            chargerHours = BatteryInsights.ChargerHoursPerDay(store.ReportPeriods()),
            days,
            gpuWeek = store.GpuHolders(now.AddDays(-7)).Select(g => new { app = g.App, times = g.Times }),
        };
    }

    private object Analytics()
    {
        var store = monitor.Store;
        var (ac, dc) = store.UsageGrid(30);
        static double[][] Rows(double[,] g) => Enumerable.Range(0, 7).Select(d => Enumerable.Range(0, 24).Select(h => Math.Round(g[d, h], 1)).ToArray()).ToArray();
        var split = store.ReportPeriods().Where(p => p.End >= DateTime.Now.AddDays(-30)).Select(p => new
        {
            day = p.End.ToString("yyyy-MM-dd"),
            acActive = Math.Round(p.ActiveOnCharger.TotalHours / p.Days, 2),
            dcActive = Math.Round(p.ActiveOnBattery.TotalHours / p.Days, 2),
            acStandby = Math.Round(p.StandbyOnCharger.TotalHours / p.Days, 2),
            dcStandby = Math.Round(p.StandbyOnBattery.TotalHours / p.Days, 2),
        }).TakeLast(21);
        var first = store.Samples(DateTimeOffset.Now.AddDays(-30), DateTimeOffset.Now.AddMinutes(1)).FirstOrDefault();
        return new
        {
            histogram = store.ChargeHistogram(30).Select(m => Math.Round(m, 1)),
            gridAc = Rows(ac),
            gridDc = Rows(dc),
            split,
            appEnergy = store.AppEnergy(7).Select(e => new { name = e.Name, wh = Math.Round(e.MWh / 1000, 2) }),
            sampleDays = first is null ? 0 : Math.Max(1, (int)Math.Ceiling((DateTimeOffset.Now - first.Time).TotalDays)),
        };
    }

    private object Apps()
    {
        var s = monitor.Latest;
        double? watts = s is { OnAc: false, RateMw: < 0 } ? -s.RateMw!.Value / 1000.0 : null;
        var list = monitor.Apps.Sample(watts);
        var mem = new MemoryStatus();
        GlobalMemoryStatusEx(mem);
        double cpu = Math.Min(100, list.Sum(a => a.CpuPercent));
        return new
        {
            apps = list.Take(60),
            onBattery = watts is not null,
            totalRamGb = Math.Round(mem.ullTotalPhys / 1073741824.0, 1),
            usedRamGb = Math.Round((mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0, 1),
            cpuTotal = Math.Round(cpu),
            tips = AppMonitor.Tips(list, watts is not null),
        };
    }

    // ---------- plug ----------

    private async Task<object?> PlugGet()
    {
        var c = monitor.Settings.Plug;
        var state = await monitor.RefreshPlugAsync();
        return new { config = new { c.Kind, c.Host, c.Username, hasPassword = c.Password != "" }, state };
    }

    private async Task<object?> PlugSave(JsonNode p)
    {
        var c = monitor.Settings.Plug;
        c.Kind = p["kind"]?.GetValue<string>() ?? "none";
        c.Host = p["host"]?.GetValue<string>()?.Trim() ?? "";
        c.Username = p["username"]?.GetValue<string>()?.Trim() ?? "";
        var pw = p["password"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(pw)) c.Password = pw;
        if (c.Kind == "none") { c.Host = c.Username = c.Password = ""; }
        monitor.Settings.Save();
        return await monitor.RefreshPlugAsync();
    }

    private async Task<object?> PlugSet(bool on)
    {
        await monitor.SetPlugAsync(on);
        return monitor.Plug;
    }

    // ---------- settings ----------

    private object SettingsView()
    {
        var s = monitor.Settings;
        return new
        {
            s.StartWithWindows, s.NotifyFullHold, s.FullHoldMinutes, s.NotifyLow, s.LowPercent, s.NotifyHigh, s.HighPercent,
            s.NotifySleepDrain, s.NotifyGpu, s.SoundsOn, s.SoundPack, s.PlugSounds, s.Mode, s.ApplyPowerMode, s.ChargeLimitOn, s.ChatModel, s.EmbedModel
        };
    }

    private void SaveSettings(JsonNode p)
    {
        var s = monitor.Settings;
        foreach (var (key, value) in p.AsObject())
        {
            if (value is null) continue;
            switch (key)
            {
                case "startWithWindows": s.StartWithWindows = value.GetValue<bool>(); StartupRegistration.Apply(s.StartWithWindows); break;
                case "notifyFullHold": s.NotifyFullHold = value.GetValue<bool>(); break;
                case "fullHoldMinutes": s.FullHoldMinutes = Math.Clamp(value.GetValue<int>(), 30, 600); break;
                case "notifyLow": s.NotifyLow = value.GetValue<bool>(); break;
                case "lowPercent": s.LowPercent = Math.Clamp(value.GetValue<int>(), 5, 50); break;
                case "notifyHigh": s.NotifyHigh = value.GetValue<bool>(); break;
                case "highPercent": s.HighPercent = Math.Clamp(value.GetValue<int>(), 50, 100); break;
                case "notifySleepDrain": s.NotifySleepDrain = value.GetValue<bool>(); break;
                case "notifyGpu": s.NotifyGpu = value.GetValue<bool>(); break;
                case "soundsOn": s.SoundsOn = value.GetValue<bool>(); break;
                case "soundPack": s.SoundPack = value.GetValue<string>() == "pulse" ? "pulse" : "nebula"; break;
                case "plugSounds": s.PlugSounds = value.GetValue<bool>(); break;
                case "applyPowerMode": s.ApplyPowerMode = value.GetValue<bool>(); break;
                case "chargeLimitOn": s.ChargeLimitOn = value.GetValue<bool>(); break;
            }
        }
        s.Save();
    }

    // ---------- assistant ----------

    private async Task<object?> AssistantStatusAsync()
    {
        var running = await _ollama.RunningAsync();
        var models = running ? await _ollama.ModelsAsync() : new List<string>();
        var s = monitor.Settings;
        return new AssistantStatus(Ollama.Installed, running, Ollama.Has(models, s.ChatModel), Ollama.Has(models, s.EmbedModel), s.ChatModel, s.EmbedModel, OllamaSetup.ModelsFolder());
    }

    private async Task SetupAssistantAsync()
    {
        if (_setupRunning) return;
        _setupRunning = true;
        try
        {
            void Progress(string stage, double pct) => Emit("setup", new { stage, progress = pct });
            await OllamaSetup.InstallAsync(Progress);
            Progress("Starting the AI engine", -1);
            if (!await _ollama.EnsureRunningAsync()) throw new InvalidOperationException("Ollama is installed but didn't start. Open it from the Start menu once, then try again.");
            var s = monitor.Settings;
            foreach (var model in new[] { s.EmbedModel, s.ChatModel })
            {
                if (Ollama.Has(await _ollama.ModelsAsync(), model)) continue;
                await _ollama.PullAsync(model, (status, pct) => Progress($"Downloading {model}: {status}", pct));
            }
            Progress("Indexing the knowledge base", -1);
            await Assistant.IndexAsync(s.EmbedModel);
            Emit("setup", new { done = true });
        }
        catch (Exception e)
        {
            Emit("setup", new { done = true, error = e.Message });
            App.Log("Assistant setup: " + e);
        }
        finally { _setupRunning = false; }
    }

    private async Task AskAsync(long id, string question)
    {
        _chatCts?.Cancel();
        _chatCts = new CancellationTokenSource();
        try
        {
            var s = monitor.Settings;
            double? watts = monitor.Latest is { OnAc: false, RateMw: < 0 } b ? -b.RateMw!.Value / 1000.0 : null;
            var facts = Facts.Build(monitor.Store, monitor.Latest ?? BatteryReader.Read(), monitor.Apps.Sample(watts), monitor.Plug, s);
            var hits = await Assistant.AskAsync(question, facts, s.ChatModel, s.EmbedModel, d => Emit("chat", new { id, delta = d }), _chatCts.Token);
            Emit("chat", new { id, done = true, sources = hits.Select(h => h.Chunk.Title) });
        }
        catch (Exception e)
        {
            Emit("chat", new { id, error = e.Message });
        }
    }

    // ---------- actions ----------

    private object ExportCsv()
    {
        var path = System.Windows.Application.Current.Dispatcher.Invoke(saveDialog);
        if (path is null) return Ok("Export cancelled.");
        monitor.Store.ExportCsv(path);
        return Ok("Exported to " + path);
    }

    private object DeleteHistory()
    {
        var yes = System.Windows.Application.Current.Dispatcher.Invoke(() => System.Windows.MessageBox.Show(
            "Delete all stored battery history? This can't be undone.", "Guardian X",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.No));
        if (yes != System.Windows.MessageBoxResult.Yes) return Ok("Kept your history.");
        monitor.Store.DeleteAll();
        Task.Run(monitor.Tick);
        return Ok("History deleted. Recording starts again now.");
    }

    private object Repair()
    {
        var report = new List<string>();
        var check = monitor.Store.IntegrityCheck();
        report.Add(check == "ok" ? "History database: healthy." : "History database problem: " + check);
        try { monitor.Settings.Save(); report.Add("Settings: rewritten."); } catch (Exception e) { report.Add("Settings: " + e.Message); }
        try { StartupRegistration.Apply(monitor.Settings.StartWithWindows); report.Add("Start with Windows: " + (monitor.Settings.StartWithWindows ? "re-registered." : "off.")); }
        catch (Exception e) { report.Add("Start-up: " + e.Message); }
        try { Sounds.Generate(); report.Add("Sounds: regenerated."); } catch (Exception e) { report.Add("Sounds: " + e.Message); }
        try { var b = BatteryReader.Read(); report.Add(b is null ? "Battery: not found." : $"Battery: read OK ({b.Percent:0}%)."); } catch (Exception e) { report.Add("Battery: " + e.Message); }
        report.Add(monitor.Gpu.Available ? $"GPU watch: {monitor.Gpu.AdapterName}." : "GPU watch: no discrete GPU found.");
        report.Add(File.Exists(McpSetup.ExePath) ? "MCP server: present." : "MCP server: missing (reinstall to restore).");
        return Ok(string.Join("\n", report));
    }

    private async Task<object?> CheckUpdateAsync()
    {
        var mgr = new UpdateManager(new GithubSource(RepoUrl, null, false));
        if (!mgr.IsInstalled) return Ok("Updates work in the installed app, not in a development build.");
        var info = await mgr.CheckForUpdatesAsync();
        if (info is null) return Ok("You're on the latest version.");
        await mgr.DownloadUpdatesAsync(info);
        _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            mgr.WaitExitThenApplyUpdates(info, silent: false, restart: true);
            ((App)System.Windows.Application.Current).Quit();
        });
        return Ok($"Installing {info.TargetFullRelease.Version} and restarting…");
    }

    // ---------- helpers ----------

    private static readonly string Model = ReadModel();
    private static string ReadModel()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            return (key?.GetValue("SystemProductName") as string)?.Trim() ?? "";
        }
        catch { return ""; }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private sealed class MemoryStatus
    {
        public uint dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryStatus>();
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx([System.Runtime.InteropServices.In, System.Runtime.InteropServices.Out] MemoryStatus buffer);
}

/// <summary>Registers the bundled MCP server with Claude Desktop.</summary>
public static class McpSetup
{
    public static string ExePath => Path.Combine(AppContext.BaseDirectory, "GuardianX.Mcp.exe");

    /// <summary>Claude Desktop's config, including the copy a Store-installed Claude keeps in its package folder.</summary>
    public static List<string> ConfigPaths()
    {
        var list = new List<string>();
        var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json");
        list.Add(roaming);
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        try
        {
            foreach (var dir in Directory.Exists(packages) ? Directory.GetDirectories(packages, "Claude_*") : Array.Empty<string>())
            {
                var p = Path.Combine(dir, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json");
                if (Directory.Exists(Path.GetDirectoryName(p))) list.Add(p);
            }
        }
        catch { }
        return list;
    }

    public static object Info()
    {
        bool connected = ConfigPaths().Any(p =>
        {
            try { return File.Exists(p) && JsonNode.Parse(File.ReadAllText(p))?["mcpServers"]?["guardian-x"] is not null; } catch { return false; }
        });
        return new { exePath = ExePath, connected, configs = ConfigPaths() };
    }

    public static void Connect()
    {
        foreach (var path in ConfigPaths())
        {
            if (!File.Exists(path) && !Directory.Exists(Path.GetDirectoryName(path))) continue;
            JsonObject root;
            try { root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? new JsonObject() : new JsonObject(); }
            catch { continue; } // never overwrite a config we can't read
            if (File.Exists(path)) File.Copy(path, path + ".guardianx-backup", true);
            var servers = root["mcpServers"] as JsonObject ?? new JsonObject();
            servers["guardian-x"] = new JsonObject { ["command"] = ExePath, ["args"] = new JsonArray() };
            root["mcpServers"] = servers;
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
