using System.Windows;
using GuardianX.Core;
using GuardianX.Core.Data;
using Velopack;

namespace GuardianX.App;

public static class Program
{
    public const string ShowSignalName = "GuardianX.Show.7c3e";

    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => UninstallHooks.BeforeUninstall())
            .Run();

        if (args.Length >= 2 && args[0] == "--selftest") { DevTools.SelfTest(args[1]); return; }
        if (args.Length >= 2 && args[0] == "--snapshot")
        {
            var snapApp = new App { StartHidden = true, SnapshotPath = args[1], SnapshotTab = args.Length > 2 ? args[2] : "overview" };
            snapApp.InitializeComponent();
            snapApp.Run();
            return;
        }

        using var mutex = new Mutex(true, "GuardianX.Single.7c3e", out bool first);
        if (!first)
        {
            // Already running in the tray: ask it to show its window, then leave.
            try { EventWaitHandle.OpenExisting(ShowSignalName).Set(); } catch (WaitHandleCannotBeOpenedException) { }
            return;
        }

        var app = new App { StartHidden = args.Contains("--background") };
        app.InitializeComponent();
        app.Run();
    }
}

internal static class UninstallHooks
{
    /// <summary>Asks whether to keep the battery history when Guardian X is uninstalled. Keeping is the default.</summary>
    public static void BeforeUninstall()
    {
        try { Microsoft.Win32.Registry.CurrentUser.OpenSubKey(StartupRegistration.RunKey, true)?.DeleteValue(StartupRegistration.ValueName, false); }
        catch { }
        // Removes the toast registration (start menu entry, registry keys, icon cache) Windows keeps for the app.
        try { CommunityToolkit.WinUI.Notifications.ToastNotificationManagerCompat.Uninstall(); }
        catch { }

        if (!Directory.Exists(Store.DefaultFolder)) return;
        var answer = System.Windows.Forms.MessageBox.Show(
            "Delete your Guardian X battery history too?\n\nChoose No to keep it, so a reinstall picks up where you left off.",
            "Uninstall Guardian X", System.Windows.Forms.MessageBoxButtons.YesNo, System.Windows.Forms.MessageBoxIcon.Question,
            System.Windows.Forms.MessageBoxDefaultButton.Button2);
        if (answer == System.Windows.Forms.DialogResult.Yes)
        {
            try { Directory.Delete(Store.DefaultFolder, true); } catch { }
        }
    }
}
