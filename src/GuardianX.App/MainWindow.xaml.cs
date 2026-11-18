using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using GuardianX.Core;
using Microsoft.Web.WebView2.Core;

namespace GuardianX.App;

/// <summary>Hosts the web interface (wwwroot) in WebView2 and connects it to the engine through <see cref="Bridge"/>.</summary>
public partial class MainWindow : Window
{
    public const string Host = "app.guardianx";
    private readonly GuardianMonitor _monitor;
    private Bridge? _bridge;
    private readonly TaskCompletionSource _ready = new();
    public Task Ready => _ready.Task;
    public string StartPage { get; init; } = "overview";

    public MainWindow(GuardianMonitor monitor)
    {
        _monitor = monitor;
        InitializeComponent();
        SourceInitialized += (_, _) => UseDarkTitleBar();
        Loaded += async (_, _) => await InitAsync();
        Closed += (_, _) =>
        {
            _monitor.Updated -= OnUpdated;
            _monitor.Alerted -= OnAlerted;
        };
    }

    private async Task InitAsync()
    {
        try
        {
            var dataFolder = Path.Combine(Core.Data.Store.DefaultFolder, "webview");
            var env = await CoreWebView2Environment.CreateAsync(null, dataFolder);
            await Web.EnsureCoreWebView2Async(env);
            var core = Web.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping(Host, Path.Combine(AppContext.BaseDirectory, "wwwroot"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.Settings.AreDevToolsEnabled = Environment.GetEnvironmentVariable("GUARDIANX_DEVTOOLS") == "1";
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            // Only our own pages; any external link opens in the user's browser.
            core.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith($"https://{Host}/", StringComparison.OrdinalIgnoreCase)) return;
                e.Cancel = true;
                OpenExternal(e.Uri);
            };
            core.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };

            _bridge = new Bridge(_monitor, json => Dispatcher.BeginInvoke(() => Web.CoreWebView2?.PostWebMessageAsJson(json)), PickCsvPath);
            core.WebMessageReceived += (_, e) =>
            {
                string raw;
                try { raw = e.TryGetWebMessageAsString(); } catch { raw = e.WebMessageAsJson; }
                _ = Task.Run(() => _bridge.HandleAsync(raw));
            };
            core.NavigationCompleted += (_, _) => _ready.TrySetResult();
            _monitor.Updated += OnUpdated;
            _monitor.Alerted += OnAlerted;
            core.Navigate($"https://{Host}/index.html?page={StartPage}");
        }
        catch (Exception e)
        {
            App.Log("WebView2: " + e);
            Fallback.Visibility = Visibility.Visible;
            Fallback.Text = "Guardian X needs the Microsoft Edge WebView2 Runtime to show its interface. It's built into Windows 11; " +
                            "on older systems install it from microsoft.com/edge/webview2, then reopen Guardian X. Monitoring keeps running in the tray.\n\n" + e.Message;
            _ready.TrySetResult();
        }
    }

    private void OnUpdated(Core.Battery.BatterySnapshot s) => _bridge?.Emit("update", new { percent = s.Percent });
    private void OnAlerted(Alert a) => _bridge?.Emit("alert", new { title = a.Title, body = a.Body });

    public void Navigate(string page) => _bridge?.Emit("navigate", page);

    private string? PickCsvPath()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"guardian-x-{DateTime.Now:yyyy-MM-dd}.csv", Filter = "CSV|*.csv" };
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    }

    private static void OpenExternal(string uri)
    {
        if (!uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    /// <summary>Captures the rendered interface to a PNG (used by --snapshot).</summary>
    public async Task CaptureAsync(string path)
    {
        await using var f = File.Create(path);
        await Web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, f);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void UseDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int on = 1;
            DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
            int color = 0x000A0604; // COLORREF 0x00BBGGRR → #04060A
            DwmSetWindowAttribute(hwnd, 35, ref color, sizeof(int));
        }
        catch { }
    }
}
