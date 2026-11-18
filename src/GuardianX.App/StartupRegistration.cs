using Microsoft.Win32;

namespace GuardianX.App;

/// <summary>Start with Windows through the per-user Run key (no admin needed).</summary>
public static class StartupRegistration
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Guardian X";

    /// <summary>
    /// The installed app lives in ...\GuardianX\current\GuardianX.exe; Velopack also puts a stable
    /// GuardianX.exe one level up that survives updates, so point Windows at that one when it exists.
    /// </summary>
    public static string ExePath
    {
        get
        {
            var exe = Environment.ProcessPath!;
            var dir = Path.GetDirectoryName(exe)!;
            if (string.Equals(Path.GetFileName(dir), "current", StringComparison.OrdinalIgnoreCase))
            {
                var stub = Path.Combine(Path.GetDirectoryName(dir)!, Path.GetFileName(exe));
                if (File.Exists(stub)) return stub;
            }
            return exe;
        }
    }

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{ExePath}\" --background");
        else key.DeleteValue(ValueName, false);
    }

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string v && v.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
    }
}
