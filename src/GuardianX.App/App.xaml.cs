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
        _tray.ContextMenuStrip.Items.Add("Mute alerts for 1 hour", null, (_, _) => _mutedUntil = DateTimeOffset.Now.AddHours(1));
        _tray.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
        _tray.ContextMenuStrip.Items.Add("Quit", null, (_, _) => Quit());
        _tray.MouseClick += (_, a) => { if (a.Button == Forms.MouseButtons.Left) ShowWindow(); };
        _tray.BalloonTipClicked += (_, _) => ShowWindow();
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
            _window.Closed += (_, _) => _window = null;
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void Quit()
    {
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
