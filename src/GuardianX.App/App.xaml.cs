using System.Windows;
using GuardianX.Core;
using GuardianX.Core.Data;
using GuardianX.Core.Gpu;
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
        _tray.ShowBalloonTip(8000, a.Title, a.Body, Forms.ToolTipIcon.None);
    }

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
