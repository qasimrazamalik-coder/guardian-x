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
            StartPage = tab,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = -20000, Width = 1440, Height = 920, ShowInTaskbar = false, ShowActivated = false,
        };
        w.Show();
        _ = Finish();

        async Task Finish()
        {
            try
            {
                await w.Ready;
                await Task.Delay(TimeSpan.FromSeconds(tab == "apps" ? 6 : 4)); // let data load and animations settle
                await w.CaptureAsync(path);
            }
            catch (Exception e) { App.Log("Snapshot: " + e); }
            w.Close();
            monitor.Dispose();
            monitor.Store.Dispose();
            app.Shutdown();
        }
    }
}
