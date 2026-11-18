using System.Runtime.InteropServices;

namespace GuardianX.Core.Power;

public enum PowerOverlay { Efficiency, Balanced, Performance }

/// <summary>A Guardian X mode: Windows power mode plus the charge window the smart plug keeps.</summary>
public sealed record GuardianMode(string Id, string Name, string Description, PowerOverlay Overlay, int ChargeLimit, int ResumeAt, bool GpuAlerts);

public static class Modes
{
    public static readonly IReadOnlyList<GuardianMode> All = new[]
    {
        new GuardianMode("desk", "Desk", "Plugged in most of the day. Keeps the battery between 60% and 80%.",
            PowerOverlay.Balanced, 80, 60, true),
        new GuardianMode("gaming", "Gaming", "Best performance. Still keeps the battery at 80% or below, because games make it hot.",
            PowerOverlay.Performance, 80, 60, false),
        new GuardianMode("travel", "Travel", "Charges to 100% before you leave, so you get the full runtime.",
            PowerOverlay.Balanced, 100, 95, true),
        new GuardianMode("saver", "Saver", "Best power efficiency for the longest time on battery.",
            PowerOverlay.Efficiency, 80, 50, true),
    };

    public static GuardianMode Get(string id) => All.FirstOrDefault(m => m.Id == id) ?? All[0];

    // Windows 11 power mode overlays (Settings > System > Power > Power mode).
    private static readonly Guid Efficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid Balanced = Guid.Empty;
    private static readonly Guid Performance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    public static bool Apply(PowerOverlay overlay)
    {
        var g = overlay switch { PowerOverlay.Efficiency => Efficiency, PowerOverlay.Performance => Performance, _ => Balanced };
        try { return PowerSetActiveOverlayScheme(g) == 0; } catch { return false; }
    }

    public static PowerOverlay? Current()
    {
        try
        {
            if (PowerGetEffectiveOverlayScheme(out var g) != 0) return null;
            return g == Efficiency ? PowerOverlay.Efficiency : g == Performance ? PowerOverlay.Performance : PowerOverlay.Balanced;
        }
        catch { return null; }
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveOverlayScheme(Guid overlay);
    [DllImport("powrprof.dll")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);
}
