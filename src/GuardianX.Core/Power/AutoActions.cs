using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GuardianX.Core.Power;

/// <summary>
/// Windows "Efficiency mode" for an app, the same switch Task Manager uses: EcoQoS power throttling plus low
/// priority, so the app runs on efficient cores at lower clocks. Nothing is closed and it's undone the same way.
/// </summary>
public static class EfficiencyMode
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState { public uint Version; public uint ControlMask; public uint StateMask; }

    private const int ProcessPowerThrottling = 4;
    private const uint ExecutionSpeed = 0x1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, uint size);

    /// <summary>Turns efficiency mode on or off for every running process of an app. Returns how many it changed.</summary>
    public static int Set(string processName, bool on)
    {
        int changed = 0;
        foreach (var p in Process.GetProcessesByName(processName))
        {
            try
            {
                var state = new PowerThrottlingState { Version = 1, ControlMask = ExecutionSpeed, StateMask = on ? ExecutionSpeed : 0 };
                if (!SetProcessInformation(p.Handle, ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>())) continue;
                // Leave apps that chose their own priority alone, apart from undoing ours.
                if (on && p.PriorityClass == ProcessPriorityClass.Normal) p.PriorityClass = ProcessPriorityClass.Idle;
                else if (!on && p.PriorityClass == ProcessPriorityClass.Idle) p.PriorityClass = ProcessPriorityClass.Normal;
                changed++;
            }
            catch { } // protected or exited
            finally { p.Dispose(); }
        }
        return changed;
    }

    /// <summary>Apps never throttled: Windows itself, security software and Guardian X.</summary>
    public static bool Allowed(string processName) => !Blocked.Contains(processName);

    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm", "explorer",
        "MsMpEng", "SecurityHealthService", "audiodg", "fontdrvhost", "ctfmon", "sihost", "GuardianX", "GuardianX.Mcp",
    };
}

/// <summary>Knows when a game is running full screen, from the same signal Windows uses for Focus assist.</summary>
public static class GameDetector
{
    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    private const int RunningD3DFullScreen = 3;

    /// <summary>True while a Direct3D app runs exclusive full screen. Borderless-window games aren't detected.</summary>
    public static bool FullScreenGame()
    {
        try { return SHQueryUserNotificationState(out var state) == 0 && state == RunningD3DFullScreen; }
        catch { return false; }
    }
}
