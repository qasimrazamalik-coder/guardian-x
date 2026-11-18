using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GuardianX.App.Views;
using GuardianX.Core;
using GuardianX.Core.Battery;
using GuardianX.Core.Data;
using GuardianX.Core.Insights;
using Velopack;
using Velopack.Sources;

namespace GuardianX.App;

public partial class MainWindow : Window
{
    public const string RepoUrl = "https://github.com/qasimrazamalik-coder/guardian-x";
    private readonly GuardianMonitor _monitor;
    private readonly DispatcherTimer _refresh;
    private bool _loadingSettings;

    private static readonly Brush GoodBrush = HealthRing.Frozen(Color.FromRgb(0x3D, 0xDC, 0x97));
    private static readonly Brush WarnBrush = HealthRing.Frozen(Color.FromRgb(0xFF, 0xB5, 0x47));
    private static readonly Brush BadBrush = HealthRing.Frozen(Color.FromRgb(0xFF, 0x5D, 0x5D));
    private static readonly Brush InfoBrush = HealthRing.Frozen(Color.FromRgb(0x6C, 0xB6, 0xFF));
    private static readonly Brush InkBrush = HealthRing.Frozen(Color.FromRgb(0xE8, 0xED, 0xF2));

    public MainWindow(GuardianMonitor monitor)
    {
        _monitor = monitor;
        InitializeComponent();
        SourceInitialized += (_, _) => UseDarkTitleBar();
        _monitor.Updated += _ => Dispatcher.BeginInvoke(Render);
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _refresh.Tick += (_, _) => Render();
        Loaded += (_, _) => { LoadSettings(); Render(); _refresh.Start(); };
        Closed += (_, _) => _refresh.Stop();
        VersionText.Text = $"Guardian X {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)} · data stays on this PC";
    }

    private void OnTab(object sender, RoutedEventArgs e)
    {
        if (OverviewPage is null) return;
        OverviewPage.Visibility = TabOverview.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = TabHistory.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = TabSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Render();
    }

    private void Render()
    {
        try
        {
            var s = _monitor.Latest ?? BatteryReader.Read();
            ErrorText.Visibility = _monitor.LastError is null ? Visibility.Collapsed : Visibility.Visible;
            ErrorText.Text = _monitor.LastError ?? "";
            if (s is null) { PercentText.Text = "No battery"; return; }

            if (OverviewPage.Visibility == Visibility.Visible) RenderOverview(s);
            if (HistoryPage.Visibility == Visibility.Visible) RenderHistory(s);
            if (SettingsPage.Visibility == Visibility.Visible) RenderData();
        }
        catch (Exception e)
        {
            App.Log("Render: " + e);
            ErrorText.Text = "Something went wrong reading the battery: " + e.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private void RenderOverview(BatterySnapshot s)
    {
        var store = _monitor.Store;
        var now = DateTimeOffset.Now;
        DeviceText.Text = string.Join(" · ", new[] { Model, $"{Clean(s.Manufacturer)} {s.Chemistry} battery".Trim(), $"{s.DesignMWh / 1000.0:0.0} Wh when new" }
            .Where(x => !string.IsNullOrWhiteSpace(x)));

        // Header chip
        StateText.Text = s.OnAc ? (s.Charging ? "CHARGING" : "ON CHARGER") : "ON BATTERY";
        StateText.Foreground = s.OnAc ? GoodBrush : WarnBrush;
        StateChip.Background = HealthRing.Frozen(s.OnAc ? Color.FromRgb(0x14, 0x30, 0x2A) : Color.FromRgb(0x33, 0x29, 0x14));

        // Health
        var daily = store.DailyCapacity(400);
        var wear = BatteryInsights.Wear(s.FullMWh, s.DesignMWh, s.Cycles, daily);
        Ring.Health = wear.HealthPercent;
        CapacityText.Text = $"Holds {s.FullMWh / 1000.0:0.0} Wh of the {s.DesignMWh / 1000.0:0.0} Wh it had when new";
        CyclesText.Text = $"{s.Cycles} charge cycles · {wear.WearPercent:0.0}% worn";
        if (wear.WearPer100Cycles is double w)
        {
            WearSpeedText.Text = $"{w:0.0}% per 100 cycles";
            double scale = Math.Max(w, BatteryInsights.TypicalWearPer100Cycles) * 1.15;
            double barWidth = WearTrack.ActualWidth > 0 ? WearTrack.ActualWidth : 300;
            WearTypicalMark.Margin = new Thickness(barWidth * BatteryInsights.TypicalWearPer100Cycles / scale - 1.5, 0, 0, 0);
            WearYoursBar.Width = barWidth * w / scale;
            WearYoursBar.Background = w > BatteryInsights.TypicalWearPer100Cycles * 2 ? BadBrush : w > BatteryInsights.TypicalWearPer100Cycles ? WarnBrush : GoodBrush;
            WearCompareText.Text = $"The white mark is typical, about {BatteryInsights.TypicalWearPer100Cycles:0}%. " +
                (w > BatteryInsights.TypicalWearPer100Cycles * 2 ? "Yours is ageing faster than its use explains." : "You're in a healthy range.") +
                (wear.WearPerMonth is double m ? $" Measured trend: {m:0.0}% a month." : "");
        }
        else
        {
            WearSpeedText.Text = "Needs 10+ cycles";
            WearCompareText.Text = "Guardian X compares wear with cycles once the battery has a little history.";
        }

        // Right now
        PercentText.Text = $"{s.Percent:0}%";
        var hours = BatteryInsights.HoursLeft(s.RemainingMWh, s.FullMWh, s.RateMw);
        FlowText.Text = s switch
        {
            { OnAc: true, Charging: true } => $"Charging{(hours is double h ? $" · full in {Duration(h)}" : "")}",
            { OnAc: true } => s.Percent >= 99 ? "On the charger, full" : "On the charger, not charging",
            _ => $"On battery{(hours is double h2 ? $" · about {Duration(h2)} left at this rate" : "")}"
        };
        PowerText.Text = s.RateMw is int r && r != 0 ? $"{(r > 0 ? "+" : "−")}{Math.Abs(r) / 1000.0:0.0} W" : "0 W";
        VoltText.Text = s.VoltageMv is int v ? $"{v / 1000.0:0.00} V" : "—";
        TempText.Text = s.TemperatureC is double t ? $"{t:0.0} °C" : "not reported";
        TempText.Foreground = s.TemperatureC is null ? HealthRing.Frozen(Color.FromRgb(0x8A, 0x96, 0xA3)) : InkBrush;

        var last12 = store.Samples(now.AddHours(-12), now.AddMinutes(1));
        int hold = BatteryInsights.FullHoldStreakMinutes(last12);
        HoldText.Text = hold > 0 ? BatteryInsights.FormatMinutes(hold) : "—";
        HoldText.Foreground = hold >= 120 ? WarnBrush : InkBrush;

        // 24 h chart
        var day = store.Samples(now.AddHours(-24), now.AddMinutes(1));
        DayChart.From = now.AddHours(-24);
        DayChart.To = now;
        DayChart.BandFrom = BatteryInsights.FullThreshold;
        DayChart.BandLabel = "STRESS ZONE";
        DayChart.Points = day.Select(x => new LineChart.Point2(x.Time, x.Percent, !x.OnAc)).ToList();
        DayChart.Refresh();

        // Stress
        var today = store.Samples(new DateTimeOffset(now.Date, now.Offset), now.AddMinutes(1));
        var stress = BatteryInsights.Stress(today);
        StressScore.Text = $"{stress.Score}";
        StressLevel.Text = stress.Level.ToUpperInvariant() + " / 100";
        StressLevel.Foreground = stress.Score >= 60 ? BadBrush : stress.Score >= 30 ? WarnBrush : GoodBrush;
        StressDetail.Text = $"{stress.HoursAtFull:0.0} h at 100% on the charger" +
            (stress.TemperatureKnown ? $", {stress.HoursHot:0.0} h above {BatteryInsights.HotThresholdC:0} °C" : "") +
            $", {stress.HoursLow:0.0} h under {BatteryInsights.LowThreshold:0}%." +
            (stress.TemperatureKnown ? "" : " Your battery doesn't report its temperature, so heat isn't counted.");

        // Sleep
        var sleeps = BatteryInsights.Sleeps(store.Events(now.AddDays(-60)));
        var lastSleep = sleeps.Count > 0 ? sleeps[^1] : null;
        if (lastSleep is null)
        {
            SleepBig.Text = "—";
            SleepDetail.Text = "No overnight sleep recorded yet. After the next sleep of 30+ minutes on battery, you'll see how much it drained.";
        }
        else
        {
            SleepBig.Text = $"−{lastSleep.PercentLost:0.#}%";
            SleepBig.Foreground = lastSleep.Unusual ? BadBrush : InkBrush;
            SleepDetail.Text = $"{lastSleep.Start:ddd HH:mm} to {lastSleep.End:HH:mm}, {lastSleep.PercentPerHour:0.0}% an hour. " +
                (lastSleep.Unusual ? "More than usual: run 'powercfg /sleepstudy' to see what kept it awake." : "Normal for this laptop.");
        }

        // GPU
        GpuTitle.Text = _monitor.Gpu.AdapterName is string name ? name.Replace("NVIDIA ", "").ToUpperInvariant() : "DISCRETE GPU";
        if (!_monitor.Gpu.Available)
        {
            GpuBig.Text = "—";
            GpuDetail.Text = "No discrete GPU found.";
        }
        else if (s.OnAc)
        {
            GpuBig.Text = "On charger";
            GpuBig.Foreground = InkBrush;
            GpuDetail.Text = "Guardian X watches for apps that keep the NVIDIA GPU awake while you're on battery.";
        }
        else if (_monitor.GpuHolders.Count == 0)
        {
            GpuBig.Text = "Asleep";
            GpuBig.Foreground = GoodBrush;
            GpuDetail.Text = "No app is keeping it awake. Good for battery life.";
        }
        else
        {
            GpuBig.Text = "Awake";
            GpuBig.Foreground = WarnBrush;
            GpuDetail.Text = "Kept awake by " + string.Join(", ", _monitor.GpuHolders.Take(3).Select(h => h.ProcessName)) +
                ". Close them, or set them to Power saving in Settings > System > Display > Graphics.";
        }

        // Coach
        var gpuWeek = store.GpuHolders(now.AddDays(-7));
        var chargerHours = BatteryInsights.ChargerHoursPerDay(store.ReportPeriods());
        TipsList.ItemsSource = BatteryInsights.Coach(wear, stress, hold, lastSleep, gpuWeek, chargerHours)
            .Select(t => new
            {
                t.Title,
                t.Body,
                Accent = t.Level switch { TipLevel.Alert => BadBrush, TipLevel.Warn => WarnBrush, TipLevel.Info => InfoBrush, _ => GoodBrush }
            }).ToList();
    }

    private void RenderHistory(BatterySnapshot s)
    {
        var store = _monitor.Store;
        var now = DateTimeOffset.Now;
        var daily = store.DailyCapacity(400);
        var wear = BatteryInsights.Wear(s.FullMWh, s.DesignMWh, s.Cycles, daily);
        var report = store.ReportPeriods();
        var since = daily.Count > 0 ? daily[0].Day.ToString("d MMM yyyy") : "";
        var chargerHours = BatteryInsights.ChargerHoursPerDay(report);
        TrendText.Text = (wear.WearPerMonth is double m
            ? $"Losing about {m:0.0}% of capacity a month, measured since {since}."
            : $"The trend needs two weeks of history ({Math.Max(0, 14 - wear.TrendDays)} more days). Today: {wear.HealthPercent:0.0}% health.") +
            (chargerHours is double ch ? $" On the charger about {ch:0.#} h a day over the last two weeks." : "") +
            (report.Count > 0 ? " Older points come from Windows' own battery records." : "");

        var pts = daily.Select(d => new LineChart.Point2(
            new DateTimeOffset(d.Day.ToDateTime(new TimeOnly(12, 0))), d.DesignMWh > 0 ? d.FullMWh * 100.0 / d.DesignMWh : 0, false)).ToList();
        HealthChart.From = pts.Count > 0 ? pts[0].Time.AddDays(-1) : now.AddDays(-14);
        HealthChart.To = now.AddDays(1);
        if (HealthChart.To - HealthChart.From < TimeSpan.FromDays(14)) HealthChart.From = HealthChart.To.AddDays(-14);
        HealthChart.Min = Math.Floor(Math.Min(80, pts.Count > 0 ? pts.Min(p => p.Value) - 2 : 80));
        HealthChart.Max = 100;
        HealthChart.MaxGap = TimeSpan.FromDays(3);
        HealthChart.DailyTicks = true;
        HealthChart.LineColor = Color.FromRgb(0x3D, 0xDC, 0x97);
        HealthChart.EmptyText = "One point is added each day. Come back tomorrow to see the line start.";
        HealthChart.Points = pts;
        HealthChart.Refresh();

        var rows = new List<object>();
        for (int i = 0; i < 14; i++)
        {
            var dayStart = new DateTimeOffset(now.Date.AddDays(-i), now.Offset);
            var samples = store.Samples(dayStart, dayStart.AddDays(1));
            if (samples.Count == 0) continue;
            var st = BatteryInsights.Stress(samples);
            var last = samples[^1];
            rows.Add(new
            {
                Day = dayStart.ToString("ddd d MMM"),
                Health = $"{(last.DesignMWh > 0 ? last.FullMWh * 100.0 / last.DesignMWh : 0):0.0}%",
                Cycles = last.Cycles.ToString(),
                Stress = $"{st.Score} {st.Level.ToLowerInvariant()}",
                StressBrush = st.Score >= 60 ? BadBrush : st.Score >= 30 ? WarnBrush : GoodBrush,
                Full = $"{st.HoursAtFull:0.0} h",
                Lowest = $"{samples.Min(x => x.Percent):0}%"
            });
        }
        DaysList.ItemsSource = rows;
        DaysEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var gpu = store.GpuHolders(now.AddDays(-7));
        GpuHistoryText.Text = gpu.Count == 0
            ? "None so far."
            : string.Join(Environment.NewLine, gpu.Select(g => $"{g.App} · seen awake {g.Times} min"));
    }

    // ---------- Settings ----------

    private void LoadSettings()
    {
        _loadingSettings = true;
        var st = _monitor.Settings;
        OptFull.IsChecked = st.NotifyFullHold;
        OptLow.IsChecked = st.NotifyLow;
        OptSleep.IsChecked = st.NotifySleepDrain;
        OptGpu.IsChecked = st.NotifyGpu;
        OptStartup.IsChecked = st.StartWithWindows;
        (st.FullHoldMinutes switch { <= 60 => Hold60, <= 120 => Hold120, _ => Hold240 }).IsChecked = true;
        _loadingSettings = false;
    }

    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings || Hold240 is null || OptGpu is null) return;
        var st = _monitor.Settings;
        st.NotifyFullHold = OptFull.IsChecked == true;
        st.NotifyLow = OptLow.IsChecked == true;
        st.NotifySleepDrain = OptSleep.IsChecked == true;
        st.NotifyGpu = OptGpu.IsChecked == true;
        st.FullHoldMinutes = Hold60.IsChecked == true ? 60 : Hold240.IsChecked == true ? 240 : 120;
        if (st.StartWithWindows != (OptStartup.IsChecked == true))
        {
            st.StartWithWindows = OptStartup.IsChecked == true;
            try { StartupRegistration.Apply(st.StartWithWindows); }
            catch (Exception ex) { MaintText.Text = "Couldn't change start-up: " + ex.Message; }
        }
        st.Save();
    }

    private void RenderData()
    {
        var store = _monitor.Store;
        long bytes = File.Exists(store.DbPath) ? new FileInfo(store.DbPath).Length : 0;
        DataText.Text = $"{store.SampleCount():N0} readings stored ({bytes / 1024.0 / 1024.0:0.0} MB) in {Store.DefaultFolder}. " +
                        "Nothing is sent anywhere. Readings older than two years are removed automatically.";
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"guardian-x-{DateTime.Now:yyyy-MM-dd}.csv", Filter = "CSV|*.csv" };
        if (dlg.ShowDialog(this) != true) return;
        _monitor.Store.ExportCsv(dlg.FileName);
        MaintText.Text = "Exported to " + dlg.FileName;
    }

    private void OnOpenData(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", Store.DefaultFolder) { UseShellExecute = true });

    private void OnDeleteHistory(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Delete all stored battery history? This can't be undone.", "Guardian X",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _monitor.Store.DeleteAll();
        MaintText.Text = "History deleted. Recording starts again now.";
        _monitor.Tick();
        Render();
    }

    private void OnRepair(object sender, RoutedEventArgs e)
    {
        var report = new List<string>();
        var check = _monitor.Store.IntegrityCheck();
        report.Add(check == "ok" ? "History database: healthy." : "History database problem: " + check);
        try
        {
            var fresh = Settings.Load();
            fresh.Save();
            report.Add("Settings file: rewritten.");
        }
        catch (Exception ex) { report.Add("Settings file: " + ex.Message); }
        try
        {
            StartupRegistration.Apply(_monitor.Settings.StartWithWindows);
            report.Add(_monitor.Settings.StartWithWindows ? "Start with Windows: re-registered." : "Start with Windows: off.");
        }
        catch (Exception ex) { report.Add("Start-up: " + ex.Message); }
        try
        {
            var b = BatteryReader.Read();
            report.Add(b is null ? "Battery: not found." : $"Battery: read OK ({b.Percent:0}%).");
        }
        catch (Exception ex) { report.Add("Battery: " + ex.Message); }
        report.Add(_monitor.Gpu.Available ? $"GPU watch: {_monitor.Gpu.AdapterName}." : "GPU watch: no discrete GPU found.");
        if (check != "ok")
            report.Add("Your history file is damaged. Export what you can, then use Delete history to start a clean one.");
        MaintText.Text = string.Join(Environment.NewLine, report);
    }

    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        try
        {
            var mgr = new UpdateManager(new GithubSource(RepoUrl, null, false));
            if (!mgr.IsInstalled) { MaintText.Text = "Updates work in the installed app, not in a development build."; return; }
            MaintText.Text = "Checking…";
            var info = await mgr.CheckForUpdatesAsync();
            if (info is null) { MaintText.Text = "You're on the latest version."; return; }
            MaintText.Text = $"Downloading {info.TargetFullRelease.Version}…";
            await mgr.DownloadUpdatesAsync(info);
            MaintText.Text = "Installing and restarting…";
            mgr.ApplyUpdatesAndRestart(info);
        }
        catch (Exception ex)
        {
            MaintText.Text = "Couldn't check for updates: " + ex.Message;
        }
        finally { UpdateButton.IsEnabled = true; }
    }

    private void OnQuit(object sender, RoutedEventArgs e) => ((App)Application.Current).Quit();

    // ---------- helpers ----------

    private static string Duration(double hours)
    {
        int m = (int)Math.Round(hours * 60);
        return m >= 60 ? $"{m / 60} h {m % 60:00} min" : $"{Math.Max(1, m)} min";
    }

    private static string Clean(string s) => string.IsNullOrWhiteSpace(s) ? "" : s.Trim();

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

    private void UseDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int on = 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
        int caption = 0x00130E0B; // COLORREF for #0B0E13
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
