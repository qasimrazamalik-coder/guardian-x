using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GuardianX.Core.Apps;

public sealed record TempSummary(double SizeGb, int Files, double RecycleBinGb, long RecycleBinItems);
public sealed record CleanResult(double FreedGb, int Deleted, int Skipped);
public sealed record BigFile(string Path, string Name, double SizeGb, DateTime Modified);
public sealed record StartupApp(string Id, string Name, string Command, string Location, bool Enabled, bool CanChange);

/// <summary>Disk cleanup that only touches what's safe to remove, and start-up apps the way Task Manager manages them.</summary>
public static class Cleanup
{
    /// <summary>Files in the user's temp folder older than a day. Newer ones may still be in use by an installer or app.</summary>
    public static TempSummary ScanTemp()
    {
        long bytes = 0;
        int files = 0;
        foreach (var f in TempFiles()) { bytes += f.Length; files++; }
        var (binBytes, binItems) = RecycleBin();
        return new TempSummary(Math.Round(bytes / 1073741824.0, 2), files, Math.Round(binBytes / 1073741824.0, 2), binItems);
    }

    public static CleanResult CleanTemp()
    {
        long freed = 0;
        int deleted = 0, skipped = 0;
        foreach (var f in TempFiles().ToList())
        {
            try { long len = f.Length; f.Delete(); freed += len; deleted++; }
            catch { skipped++; } // locked by a running app
        }
        foreach (var dir in SafeDirs(Path.GetTempPath()).OrderByDescending(d => d.Length))
            try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
        return new CleanResult(Math.Round(freed / 1073741824.0, 2), deleted, skipped);
    }

    private static IEnumerable<FileInfo> TempFiles()
    {
        var cutoff = DateTime.Now.AddDays(-1);
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
        IEnumerable<FileInfo> all;
        try { all = new DirectoryInfo(Path.GetTempPath()).EnumerateFiles("*", opts); }
        catch { yield break; }
        foreach (var f in all)
        {
            bool old;
            try { old = f.LastWriteTime < cutoff && f.LastAccessTime < cutoff; } catch { continue; }
            if (old) yield return f;
        }
    }

    private static IEnumerable<string> SafeDirs(string root)
    {
        try { return Directory.EnumerateDirectories(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    // ---------- recycle bin ----------

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ShQueryRbInfo { public int cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? rootPath, ref ShQueryRbInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);

    private static (long Bytes, long Items) RecycleBin()
    {
        var info = new ShQueryRbInfo { cbSize = Marshal.SizeOf<ShQueryRbInfo>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
    }

    /// <summary>Empties the Recycle Bin on every drive. Permanent: the caller must have asked the user first.</summary>
    public static double EmptyRecycleBin()
    {
        var (bytes, _) = RecycleBin();
        const uint NoConfirmation = 0x1, NoProgressUi = 0x2, NoSound = 0x4;
        SHEmptyRecycleBin(IntPtr.Zero, null, NoConfirmation | NoProgressUi | NoSound);
        return Math.Round(bytes / 1073741824.0, 2);
    }

    // ---------- big files ----------

    /// <summary>
    /// The largest files in the user's folders and on the other fixed drives (skipping Windows, program folders and
    /// app data), for the user to review. Never deletes anything. Stops after <paramref name="budget"/>.
    /// </summary>
    public static List<BigFile> FindBigFiles(int count = 25, double minGb = 0.2, TimeSpan? budget = null)
    {
        var deadline = DateTime.UtcNow + (budget ?? TimeSpan.FromSeconds(20));
        long min = (long)(minGb * 1073741824);
        var found = new List<BigFile>();
        // Windows, installed programs and game libraries hold files that break things if removed: never suggest them.
        var skip = new[] { "\\Windows", "\\Program Files", "\\Program Files (x86)", "\\ProgramData", "\\$Recycle.Bin", "\\System Volume Information",
            "\\AppData", "\\Recovery", "\\WindowsApps", "\\XboxGames", "\\Riot Games", "\\SteamLibrary", "\\steamapps", "\\Steam",
            "\\Epic Games", "\\EA Games", "\\Ubisoft", "\\GOG Games", "\\Battle.net", "\\node_modules", "\\.git", "\\$WinREAgent" };
        var skipExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".dll", ".sys", ".pak", ".ucas", ".utoc", ".sig", ".bundle", ".vpk", ".assets", ".resource", ".wim", ".esd" };
        var roots = new List<string> { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        roots.AddRange(DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed &&
            !string.Equals(d.RootDirectory.FullName, systemRoot, StringComparison.OrdinalIgnoreCase)).Select(d => d.RootDirectory.FullName));

        var stack = new Stack<string>(roots);
        while (stack.Count > 0 && DateTime.UtcNow < deadline)
        {
            var dir = stack.Pop();
            if (skip.Any(s => dir.EndsWith(s, StringComparison.OrdinalIgnoreCase) || dir.Contains(s + "\\", StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                var di = new DirectoryInfo(dir);
                foreach (var f in di.EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System }))
                    if (f.Length >= min && !skipExt.Contains(f.Extension)) found.Add(new BigFile(f.FullName, f.Name, Math.Round(f.Length / 1073741824.0, 2), f.LastWriteTime));
                foreach (var d in di.EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System }))
                    stack.Push(d.FullName);
            }
            catch { }
        }
        return found.OrderByDescending(f => f.SizeGb).Take(count).ToList();
    }

    public static void ShowInFolder(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("That file has moved or been deleted.", path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    // ---------- start-up apps ----------

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Key = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovedRun32 = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    private const string ApprovedFolder = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    /// <summary>
    /// Apps that start with Windows. Per-user entries can be switched on and off here, using the same
    /// StartupApproved flags as Task Manager. All-users entries need administrator rights, so they're read-only.
    /// </summary>
    public static List<StartupApp> StartupApps()
    {
        var list = new List<StartupApp>();
        void FromKey(RegistryKey hive, string key, string approved, string location, bool canChange)
        {
            using var k = hive.OpenSubKey(key);
            if (k is null) return;
            foreach (var name in k.GetValueNames())
            {
                if (string.IsNullOrEmpty(name)) continue;
                var cmd = k.GetValue(name)?.ToString() ?? "";
                list.Add(new StartupApp($"{location}|{name}", name, cmd, location, IsEnabled(hive, approved, name), canChange));
            }
        }
        FromKey(Registry.CurrentUser, RunKey, ApprovedRun, "user", true);
        FromKey(Registry.LocalMachine, RunKey, ApprovedRun, "all users", false);
        FromKey(Registry.LocalMachine, Run32Key, ApprovedRun32, "all users (32-bit)", false);

        void FromFolder(string folder, RegistryKey hive, string location, bool canChange)
        {
            if (!Directory.Exists(folder)) return;
            foreach (var f in Directory.EnumerateFiles(folder).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)))
            {
                var file = Path.GetFileName(f);
                list.Add(new StartupApp($"{location}|{file}", Path.GetFileNameWithoutExtension(f), f, location, IsEnabled(hive, ApprovedFolder, file), canChange));
            }
        }
        FromFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), Registry.CurrentUser, "user startup folder", true);
        FromFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Registry.LocalMachine, "all users startup folder", false);
        return list.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsEnabled(RegistryKey hive, string approvedKey, string name)
    {
        using var k = hive.OpenSubKey(approvedKey);
        // Missing = enabled. First byte even (02, 06) = enabled, odd (03, 07) = disabled.
        return k?.GetValue(name) is not byte[] b || b.Length == 0 || (b[0] & 1) == 0;
    }

    public static void SetStartup(string id, bool enabled)
    {
        var app = StartupApps().FirstOrDefault(a => a.Id == id) ?? throw new InvalidOperationException("That start-up app is no longer there.");
        if (!app.CanChange) throw new InvalidOperationException($"{app.Name} starts for all users. Change it in Task Manager > Startup apps (needs administrator).");
        var key = app.Location == "user" ? ApprovedRun : ApprovedFolder;
        var name = app.Location == "user" ? app.Name : Path.GetFileName(app.Command);
        using var k = Registry.CurrentUser.CreateSubKey(key);
        var value = new byte[12];
        value[0] = enabled ? (byte)2 : (byte)3;
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(value, 4);
        k.SetValue(name, value, RegistryValueKind.Binary);
    }
}
