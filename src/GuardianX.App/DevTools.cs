using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GuardianX.Core;
using GuardianX.Core.Battery;
using GuardianX.Core.Data;
using GuardianX.Core.Gpu;

namespace GuardianX.App;

/// <summary>Developer checks: dump raw readings, or render the window to a PNG without showing it.</summary>
internal static class DevTools
{
    public static void SelfTest(string outPath)
    {
        object result;
        try
        {
            var gpu = new DgpuWatcher();
            result = new
            {
                battery = BatteryReader.Read(),
                gpuAdapter = gpu.AdapterName,
                gpuAvailable = gpu.Available,
                gpuHolders = gpu.Holders(),
                startupExe = StartupRegistration.ExePath,
            };
        }
        catch (Exception e) { result = new { error = e.ToString() }; }
        File.WriteAllText(outPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void Snapshot(App app, string path, string tab)
    {
        var monitor = new GuardianMonitor(new Store(), Settings.Load(), new DgpuWatcher());
        monitor.Tick();
        var w = new MainWindow(monitor)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false,
            Height = tab == "overview" ? 1320 : 860
        };
        w.Show();
        if (tab == "history") w.TabHistory.IsChecked = true;
        if (tab == "settings") w.TabSettings.IsChecked = true;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var content = (FrameworkElement)w.Content;
            var dpi = VisualTreeHelper.GetDpi(w);
            var bmp = new RenderTargetBitmap((int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var bg = new DrawingVisual();
            using (var dc = bg.RenderOpen())
                dc.DrawRectangle((Brush)app.Resources["Bg"], null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            bmp.Render(bg);
            bmp.Render(content);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var f = File.Create(path)) enc.Save(f);
            w.Close();
            monitor.Dispose();
            monitor.Store.Dispose();
            app.Shutdown();
        };
        timer.Start();
    }
}
