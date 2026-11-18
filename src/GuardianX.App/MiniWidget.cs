using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using GuardianX.Core;
using GuardianX.Core.Battery;
using GuardianX.Core.Insights;
using GuardianX.Core.Power;

namespace GuardianX.App;

/// <summary>A small always-on-top live battery readout. Drag to move, double-click to open Guardian X.</summary>
public sealed class MiniWidget : Window
{
    private static readonly Brush Ink = Frozen(0xE8, 0xED, 0xF2), Muted = Frozen(0x8A, 0x97, 0xA6),
        Mint = Frozen(0x3D, 0xFF, 0xB0), Cyan = Frozen(0x3D, 0xE0, 0xFF), Amber = Frozen(0xFF, 0xB5, 0x47), Red = Frozen(0xFF, 0x5D, 0x6C);

    private readonly GuardianMonitor _monitor;
    private readonly TextBlock _percent = new() { FontSize = 30, FontWeight = FontWeights.SemiBold, Foreground = Ink };
    private readonly TextBlock _state = new() { FontSize = 12, Foreground = Muted };
    private readonly TextBlock _detail = new() { FontSize = 11.5, Foreground = Muted, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
    private readonly Rectangle _fill = new() { RadiusX = 3, RadiusY = 3, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _track;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };

    public MiniWidget(GuardianMonitor monitor)
    {
        _monitor = monitor;
        Title = "Guardian X widget";
        Width = 240; SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize; ShowActivated = false;

        _track = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), Child = _fill, Margin = new Thickness(0, 8, 0, 6) };
        var top = new DockPanel();
        var mark = new TextBlock { Text = "GUARDIAN X", FontSize = 9.5, Foreground = Muted, FontFamily = new FontFamily("Cascadia Mono, Consolas"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0) };
        DockPanel.SetDock(mark, Dock.Right);
        top.Children.Add(mark);
        top.Children.Add(new StackPanel { Children = { _percent, _state } });

        Content = new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = new LinearGradientBrush(Color.FromArgb(242, 10, 15, 22), Color.FromArgb(242, 6, 10, 15), 90),
            BorderBrush = new SolidColorBrush(Color.FromArgb(70, 61, 255, 176)), BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 10, 16, 10), Margin = new Thickness(8),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 0, Opacity = 0.55, Color = Colors.Black },
            Child = new StackPanel { Children = { top, _track, _detail } },
        };

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { ((App)Application.Current).ShowWindow(); return; }
            try { DragMove(); } catch { }
        };
        LocationChanged += (_, _) => { _monitor.Settings.WidgetLeft = Left; _monitor.Settings.WidgetTop = Top; };
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "Open Guardian X" };
        open.Click += (_, _) => ((App)Application.Current).ShowWindow();
        var close = new MenuItem { Header = "Hide widget" };
        close.Click += (_, _) => ((App)Application.Current).SetWidgetFromUser(false);
        menu.Items.Add(open); menu.Items.Add(close);
        ContextMenu = menu;

        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Place(); Refresh(); _timer.Start(); };
        Closed += (_, _) => { _timer.Stop(); try { _monitor.Settings.Save(); } catch { } };
    }

    private void Place()
    {
        var area = SystemParameters.WorkArea;
        double l = _monitor.Settings.WidgetLeft ?? double.NaN, t = _monitor.Settings.WidgetTop ?? double.NaN;
        bool valid = !double.IsNaN(l) && !double.IsNaN(t) && l >= SystemParameters.VirtualScreenLeft - 50 &&
                     l < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 60 &&
                     t >= SystemParameters.VirtualScreenTop - 20 && t < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40;
        Left = valid ? l : area.Right - Width - 12;
        Top = valid ? t : area.Bottom - ActualHeight - 12;
    }

    private void Refresh()
    {
        var s = BatteryReader.Read() ?? _monitor.Latest;
        if (s is null) { _percent.Text = "—"; _state.Text = "No battery"; return; }
        _percent.Text = $"{s.Percent:0}%";
        var mode = Modes.Get(_monitor.Settings.Mode);
        string state = s.OnAc ? (s.Charging ? "Charging" : "On charger") : "On battery";
        // Same forecast as the main window: charging slows above 80%.
        var f = BatteryInsights.Forecast(new WearReport(0, 0, s.Cycles, null, null, 0), s, 100, DateTimeOffset.Now);
        double? hours = s.RateMw > 0 ? f.HoursToFull : f.HoursToEmpty;
        _state.Text = hours is double h && h < 48 ? $"{state} · {BatteryInsights.FormatHours(h)} {(s.RateMw > 0 ? "to full" : "left")}" : state;
        _detail.Text = $"{(s.RateMw is int r ? $"{r / 1000.0:+0.0;-0.0} W" : "— W")}  ·  {mode.Name} {mode.ResumeAt}–{mode.ChargeLimit}%";
        Brush color = !s.OnAc && s.Percent <= _monitor.Settings.LowPercent ? Red : !s.OnAc ? Amber : s.Percent >= mode.ChargeLimit ? Cyan : Mint;
        _fill.Fill = color;
        _percent.Foreground = !s.OnAc && s.Percent <= _monitor.Settings.LowPercent ? Red : Ink;
        _fill.Width = Math.Max(4, (Width - 16 - 32 - 2) * Math.Clamp(s.Percent / 100.0, 0, 1));
    }

    private static Brush Frozen(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
}
