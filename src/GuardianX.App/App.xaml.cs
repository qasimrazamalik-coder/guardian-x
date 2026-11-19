using System.Windows;
using GuardianX.Core;
using GuardianX.Core.Data;
using GuardianX.Core.Gpu;
using Toasts = Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace GuardianX.App;

public partial class App : Application
{
    public bool StartHidden { get; init; }
    public string? SnapshotPath { get; init; }
    public string SnapshotTab { get; init; } = "overview";
    public GuardianMonitor Monitor { get; private set; } = null!;
    private Forms.NotifyIcon _tray = null!;
    private MainWindow? _window;
    private EventWaitHandle? _showSignal;
    private DateTimeOffset _mutedUntil = DateTimeOffset.MinValue;
    private bool _toldAboutTray;
    private MiniWidget? _widget;
    private Forms.ToolStripMenuItem? _widgetItem;
    private string? _pendingReport;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            Log("Unhandled: " + ex.Exception);
            ex.Handled = true;
        };

        if (SnapshotPath is not null)
        {
            DevTools.Snapshot(this, SnapshotPath, SnapshotTab);
            return;
        }

        var settings = Settings.Load();
        Monitor = new GuardianMonitor(new Store(), settings, new DgpuWatcher());
        try { Toasts.ToastNotificationManagerCompat.OnActivated += OnToastActivated; }
        catch (Exception ex) { Log("Toasts: " + ex.Message); }
        Monitor.Alerted += a => Dispatcher.BeginInvoke(() => Notify(a));
        Monitor.WeeklyReportReady += path => _pendingReport = path;
        if (settings.StartWithWindows && !StartupRegistration.IsRegistered())
        {
            try { StartupRegistration.Apply(true); } catch (Exception ex) { Log("Startup: " + ex.Message); }
        }

        CreateTray();
        SystemEvents.PowerModeChanged += OnPowerMode;

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowSignalName);
        new Thread(() =>
        {
            while (_showSignal.WaitOne()) Dispatcher.BeginInvoke(ShowWindow);
        }) { IsBackground = true }.Start();

        Monitor.Start();
        if (!StartHidden) ShowWindow();
        if (settings.WidgetOn) ShowWidget(true);
    }

    private void CreateTray()
    {
        var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/guardian.ico"))!.Stream;
        _tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream),
            Text = "Guardian X",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip()
        };
        _tray.ContextMenuStrip.Items.Add("Open Guardian X", null, (_, _) => ShowWindow());
        _widgetItem = new Forms.ToolStripMenuItem("Mini widget") { CheckOnClick = true, Checked = Monitor.Settings.WidgetOn };
        _widgetItem.Click += (_, _) => SetWidgetFromUser(_widgetItem.Checked);
        _tray.ContextMenuStrip.Items.Add(_widgetItem);
        _tray.ContextMenuStrip.Items.Add("Mute alerts for 1 hour", null, (_, _) => _mutedUntil = DateTimeOffset.Now.AddHours(1));
        _tray.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
        _tray.ContextMenuStrip.Items.Add("Quit", null, (_, _) => Quit());
        _tray.MouseClick += (_, a) => { if (a.Button == Forms.MouseButtons.Left) ShowWindow(); };
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_pendingReport is string report && File.Exists(report))
            {
                _pendingReport = null;
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(report) { UseShellExecute = true }); return; } catch { }
            }
            ShowWindow();
        };
        Monitor.Updated += s => Dispatcher.BeginInvoke(() =>
        {
            var text = $"Guardian X · {s.Percent:0}% · health {s.HealthPercent:0.0}%";
            _tray.Text = text.Length > 63 ? text[..63] : text;
        });
    }

    private void Notify(Alert a)
    {
        if (DateTimeOffset.Now < _mutedUntil) return;
        if (Monitor.Settings.ToastsOn)
        {
            try
            {
                // Silent: Guardian X plays its own alert sounds.
                new Toasts.ToastContentBuilder()
                    .AddArgument("action", "open")
                    .AddText(a.Title)
                    .AddText(a.Body)
                    .AddButton(new Toasts.ToastButton().SetContent("Snooze 1 h").AddArgument("action", "snooze").SetBackgroundActivation())
                    .AddButton(new Toasts.ToastButton().SetContent("Switch to Saver").AddArgument("action", "saver").SetBackgroundActivation())
                    .AddButton(new Toasts.ToastButton().SetContent("Open").AddArgument("action", "open"))
                    .AddAudio(new Uri("ms-winsoundevent:Notification.Default"), null, true)
                    .Show();
                return;
            }
            catch (Exception ex) { Log("Toast failed, using a balloon: " + ex.Message); }
        }
        _tray.ShowBalloonTip(8000, a.Title, a.Body, Forms.ToolTipIcon.None);
    }

    /// <summary>A toast or one of its buttons was clicked. Arrives on a background thread.</summary>
    private void OnToastActivated(Toasts.ToastNotificationActivatedEventArgsCompat e)
    {
        string action;
        try { action = Toasts.ToastArguments.Parse(e.Argument).TryGetValue("action", out string value) ? value : "open"; }
        catch { action = "open"; }
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                switch (action)
                {
                    case "snooze":
                        _mutedUntil = DateTimeOffset.Now.AddHours(1);
                        break;
                    case "saver":
                        Monitor.ApplyMode("saver");
                        break;
                    default:
                        if (_pendingReport is string report && File.Exists(report))
                        {
                            _pendingReport = null;
                            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(report) { UseShellExecute = true }); break; } catch { }
                        }
                        ShowWindow();
                        break;
                }
            }
            catch (Exception ex) { Log("Toast action: " + ex.Message); }
        });
    }

    /// <summary>Settings > Theme changed: the widget follows it (the web interface gets the push event).</summary>
    public void ApplyTheme() => _widget?.ApplyTheme();

    private void OnPowerMode(object? sender, PowerModeChangedEventArgs e)
    {
        try
        {
            if (e.Mode == PowerModes.Suspend) Monitor.OnSuspend();
            else if (e.Mode == PowerModes.Resume) { Monitor.OnResume(); Monitor.Tick(); }
            else if (e.Mode == PowerModes.StatusChange) Monitor.Tick();
        }
        catch (Exception ex) { Log("Power: " + ex.Message); }
    }

    public void ShowWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow(Monitor);
            _window.Closed += (_, _) =>
            {
                _window = null;
                if (!_toldAboutTray)
                {
                    _toldAboutTray = true;
                    _tray.ShowBalloonTip(5000, "Guardian X is still watching",
                        "It keeps recording from the tray. Right-click the shield icon to quit.", Forms.ToolTipIcon.None);
                }
            };
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>Shows or hides the mini widget (called on the UI thread).</summary>
    public void ShowWidget(bool on)
    {
        if (on)
        {
            if (_widget is null)
            {
                _widget = new MiniWidget(Monitor);
                _widget.Closed += (_, _) => _widget = null;
            }
            _widget.Show();
        }
        else { _widget?.Close(); _widget = null; }
        if (_widgetItem is not null) _widgetItem.Checked = on;
    }

    /// <summary>Widget switched from the tray or its own menu: remember the choice.</summary>
    public void SetWidgetFromUser(bool on)
    {
        Monitor.Settings.WidgetOn = on;
        Monitor.Settings.Save();
        ShowWidget(on);
    }

    public void Quit()
    {
        _widget?.Close();
        SystemEvents.PowerModeChanged -= OnPowerMode;
        _tray.Visible = false;
        _tray.Dispose();
        Monitor.Dispose();
        Monitor.Store.Dispose();
        Shutdown();
    }

    public static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Store.DefaultFolder);
            File.AppendAllText(Path.Combine(Store.DefaultFolder, "guardian.log"), $"{DateTime.Now:s} {line}{Environment.NewLine}");
        }
        catch { }
    }
}
