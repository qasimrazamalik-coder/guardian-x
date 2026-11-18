using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GuardianX.Core.Apps;

/// <summary>One app (all its processes together) right now.</summary>
public sealed record AppUsage(
    string Key,              // process name, e.g. "chrome"
    string Name,             // friendly name, e.g. "Google Chrome"
    int Processes,
    double MemoryMb,
    double CpuPercent,       // share of all CPU cores, 0-100
    double DiskMbPerSec,
    double? EstimatedWatts,  // only on battery: its share of the measured discharge
    string Kind);            // browser, game, system, app

public sealed record InstalledApp(string Name, double SizeGb, string Publisher);

public sealed record DriveUsage(string Name, string Label, double TotalGb, double FreeGb);

public sealed record StorageReport(IReadOnlyList<DriveUsage> Drives, IReadOnlyList<InstalledApp> LargestApps, double TempGb);

public sealed record AppTip(string App, string Text);

/// <summary>
/// Per-app RAM, CPU and disk from Windows' process counters, and an estimate of each app's battery use:
/// while on battery, the measured discharge is split by each app's CPU share. Windows does not expose a
/// true per-app energy meter without admin rights, so the split is labelled as an estimate.
/// </summary>
public sealed class AppMonitor
{
    private sealed record Last(TimeSpan Cpu, ulong Io);

    private Dictionary<int, Last> _last = new();
    private DateTime _lastAt = DateTime.MinValue;
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
        { "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "arc" };
    private static readonly HashSet<string> SystemNames = new(StringComparer.OrdinalIgnoreCase)
        { "System", "Idle", "Registry", "smss", "csrss", "wininit", "services", "lsass", "svchost", "dwm", "fontdrvhost",
          "Memory Compression", "MsMpEng", "SearchIndexer", "WmiPrvSE", "spoolsv", "audiodg", "sihost", "ctfmon",
          "RuntimeBroker", "conhost", "dllhost", "taskhostw", "explorer", "StartMenuExperienceHost", "SearchHost",
          "ShellExperienceHost", "TextInputHost", "winlogon", "LsaIso", "NisSrv", "SecurityHealthService" };

    /// <summary>
    /// Samples every process. CPU and disk need two samples; the first call returns zeros for those.
    /// <paramref name="dischargeWatts"/> is the battery discharge right now (null on the charger).
    /// </summary>
    public IReadOnlyList<AppUsage> Sample(double? dischargeWatts)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            double seconds = _lastAt == DateTime.MinValue ? 0 : (now - _lastAt).TotalSeconds;
            var current = new Dictionary<int, Last>();
            var groups = new Dictionary<string, (int procs, double mem, double cpuSec, double ioBytes, string path)>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == 0) continue;
                    var name = p.ProcessName;
                    double mem = p.WorkingSet64;
                    TimeSpan cpu = TimeSpan.Zero;
                    ulong io = 0;
                    string path = "";
                    try { cpu = p.TotalProcessorTime; } catch { }
                    try { if (GetProcessIoCounters(p.Handle, out var c)) io = c.ReadTransferCount + c.WriteTransferCount; } catch { }
                    if (!_names.ContainsKey(name) && groups.GetValueOrDefault(name).path is null or "")
                        path = ImagePath(p.Id);
                    current[p.Id] = new Last(cpu, io);

                    double cpuDelta = 0, ioDelta = 0;
                    if (seconds > 0 && _last.TryGetValue(p.Id, out var prev))
                    {
                        cpuDelta = Math.Max(0, (cpu - prev.Cpu).TotalSeconds);
                        ioDelta = io >= prev.Io ? io - prev.Io : 0;
                    }
                    var g = groups.GetValueOrDefault(name);
                    groups[name] = (g.procs + 1, g.mem + mem, g.cpuSec + cpuDelta, g.ioBytes + ioDelta, string.IsNullOrEmpty(g.path) ? path : g.path);
                }
                catch { }
                finally { p.Dispose(); }
            }

            _last = current;
            _lastAt = now;
            int cores = Environment.ProcessorCount;
            double totalCpu = groups.Values.Sum(g => g.cpuSec);

            var list = new List<AppUsage>();
            foreach (var (key, g) in groups)
            {
                double cpuPct = seconds > 0 ? g.cpuSec / (seconds * cores) * 100 : 0;
                double? watts = null;
                if (dischargeWatts is double w && totalCpu > 0)
                    watts = Math.Round(EstimateShare(w, g.cpuSec / totalCpu), 2);
                list.Add(new AppUsage(key, FriendlyName(key, g.path), g.procs, Math.Round(g.mem / 1048576.0, 1),
                    Math.Round(cpuPct, 1), seconds > 0 ? Math.Round(g.ioBytes / seconds / 1048576.0, 2) : 0, watts, KindOf(key)));
            }
            return list.OrderByDescending(a => a.CpuPercent).ThenByDescending(a => a.MemoryMb).ToList();
        }
    }

    /// <summary>
    /// The screen, Wi-Fi and chipset use a large part of the power whatever runs, so only the rest is split by CPU share.
    /// </summary>
    public static double EstimateShare(double dischargeWatts, double cpuShare, double baseFraction = 0.45) =>
        dischargeWatts * (1 - baseFraction) * Math.Clamp(cpuShare, 0, 1);

    public static string KindOf(string processName) =>
        Browsers.Contains(processName) ? "browser" : SystemNames.Contains(processName) ? "system" : "app";

    private static readonly Dictionary<string, string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["msedge"] = "Microsoft Edge", ["msedgewebview2"] = "Edge WebView2 (apps)", ["chrome"] = "Google Chrome",
        ["firefox"] = "Firefox", ["brave"] = "Brave", ["opera"] = "Opera", ["claude"] = "Claude", ["node"] = "Node.js",
        ["Code"] = "Visual Studio Code", ["explorer"] = "File Explorer", ["dwm"] = "Desktop Window Manager",
        ["Taskmgr"] = "Task Manager", ["powershell"] = "Windows PowerShell", ["pwsh"] = "PowerShell", ["cmd"] = "Command Prompt",
        ["dotnet"] = ".NET", ["Discord"] = "Discord", ["Teams"] = "Microsoft Teams", ["ms-teams"] = "Microsoft Teams",
        ["GuardianX"] = "Guardian X", ["MsMpEng"] = "Microsoft Defender", ["svchost"] = "Windows services",
    };

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder name, ref int size);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>The exe path with the least access Windows allows (works for many processes MainModule can't open).</summary>
    private static string ImagePath(int pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : "";
        }
        finally { CloseHandle(h); }
    }

    private string FriendlyName(string key, string path)
    {
        if (_names.TryGetValue(key, out var n)) return n;
        if (KnownNames.TryGetValue(key, out var known)) return _names[key] = known;
        string name = key;
        try
        {
            if (path != "")
            {
                var desc = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(desc) && desc.Length <= 48) name = desc.Trim();
            }
        }
        catch { }
        if (path != "") _names[key] = name; // retry next time if no process would give us its path
        return name;
    }

    /// <summary>Practical advice for the heaviest apps.</summary>
    public static List<AppTip> Tips(IReadOnlyList<AppUsage> apps, bool onBattery)
    {
        var tips = new List<AppTip>();
        foreach (var b in apps.Where(a => a.Kind == "browser" && a.MemoryMb > 1500).Take(2))
            tips.Add(new AppTip(b.Name, $"{b.Name} is using {b.MemoryMb / 1024:0.0} GB of RAM across {b.Processes} processes. " +
                "Turn on sleeping tabs / Efficiency mode in its settings, and close tabs you're not reading."));
        foreach (var a in apps.Where(a => a.Kind == "app" && a.CpuPercent >= 15).Take(2))
            tips.Add(new AppTip(a.Name, onBattery
                ? $"{a.Name} is using {a.CpuPercent:0}% of the CPU on battery, about {a.EstimatedWatts:0.#} W. Close it or pause it if you don't need it right now."
                : $"{a.Name} is using {a.CpuPercent:0}% of the CPU. That's heat, and heat ages the battery even on the charger."));
        foreach (var a in apps.Where(a => a.Kind == "app" && a.MemoryMb > 2500).Take(1))
            if (tips.All(t => t.App != a.Name))
                tips.Add(new AppTip(a.Name, $"{a.Name} holds {a.MemoryMb / 1024:0.0} GB of RAM. If it's not in use, closing it frees memory for games."));
        return tips;
    }

    public static StorageReport Storage()
    {
        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
            .Select(d => new DriveUsage(d.Name.TrimEnd('\\'), d.VolumeLabel, Math.Round(d.TotalSize / 1e9, 1), Math.Round(d.AvailableFreeSpace / 1e9, 1)))
            .ToList();

        var apps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        void Scan(RegistryKey root, string path)
        {
            using var key = root.OpenSubKey(path);
            if (key is null) return;
            foreach (var sub in key.GetSubKeyNames())
            {
                using var k = key.OpenSubKey(sub);
                if (k?.GetValue("DisplayName") is not string name || k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                if (k.GetValue("EstimatedSize") is not int kb || kb <= 0) continue;
                double gb = kb / 1048576.0;
                if (!apps.TryGetValue(name, out var existing) || existing.SizeGb < gb)
                    apps[name] = new InstalledApp(name, Math.Round(gb, 2), k.GetValue("Publisher") as string ?? "");
            }
        }
        try
        {
            Scan(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            Scan(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
            Scan(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        }
        catch { }

        double temp = 0;
        try
        {
            temp = new DirectoryInfo(Path.GetTempPath()).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Sum(f => f.Length) / 1e9;
        }
        catch { }
        return new StorageReport(drives, apps.Values.OrderByDescending(a => a.SizeGb).Take(12).ToList(), Math.Round(temp, 2));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
}
