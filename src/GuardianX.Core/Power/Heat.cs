using System.Diagnostics;
using GuardianX.Core.Insights;

namespace GuardianX.Core.Power;

/// <summary>How warm the laptop is, and how that was worked out. Source is 'thermal-zone', 'battery' or 'estimate'.</summary>
public sealed record HeatReading(double? TempC, string Source, string Level, int Score, double CpuPercent, bool GpuBusy, double? ChargingW, string Basis);

/// <summary>
/// Reads a real temperature where Windows offers one without admin rights (the ACPI thermal zones in the
/// "Thermal Zone Information" performance counters, in Kelvin), else the battery's own sensor, else estimates the heat
/// load from CPU use, the NVIDIA GPU and charging power.
/// </summary>
public sealed class HeatSensor : IDisposable
{
    private const string ZoneCategory = "Thermal Zone Information";
    private readonly object _lock = new();
    private PerformanceCounter? _cpu;
    private bool _cpuFailed, _zonesMissing;

    /// <summary>Hottest thermal zone in °C, or null when this PC doesn't publish one.</summary>
    public double? ThermalZoneC()
    {
        if (_zonesMissing) return null;
        try
        {
            if (!PerformanceCounterCategory.Exists(ZoneCategory)) { _zonesMissing = true; return null; }
            var data = new PerformanceCounterCategory(ZoneCategory).ReadCategory();
            var temps = data["Temperature"];
            if (temps is null) { _zonesMissing = true; return null; }
            double? max = null;
            foreach (InstanceData inst in temps.Values)
            {
                long k = inst.RawValue;
                if (k <= 200 || k >= 400) continue; // not a plausible Kelvin reading
                double c = k - 273.15;
                if (max is null || c > max) max = c;
            }
            return max is double m ? Math.Round(m, 1) : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Whole-machine CPU use in percent (0 on the first call, then the average since the last call).</summary>
    public double CpuPercent()
    {
        lock (_lock)
        {
            if (_cpuFailed) return 0;
            try
            {
                _cpu ??= new PerformanceCounter("Processor", "% Processor Time", "_Total");
                return Math.Clamp(_cpu.NextValue(), 0, 100);
            }
            catch (Exception) { _cpuFailed = true; return 0; }
        }
    }

    public HeatReading Read(double? batteryTempC, bool gpuBusy, double? chargingW) =>
        Assess(ThermalZoneC(), batteryTempC, CpuPercent(), gpuBusy, chargingW);

    /// <summary>Works out the heat level from whatever is available, best source first.</summary>
    public static HeatReading Assess(double? zoneC, double? batteryC, double cpuPercent, bool gpuBusy, double? chargingW)
    {
        double cpu = Math.Round(cpuPercent, 1);
        double? chg = chargingW is double w && w > 0 ? Math.Round(w, 1) : null;
        if (zoneC is double z)
        {
            double score = SmartInsights.HeatScoreFromTemp(ZoneAsBatteryC(z));
            return new HeatReading(z, "thermal-zone", ZoneLevel(z), (int)Math.Round(score), cpu, gpuBusy, chg,
                $"Measured {z:0.0} °C by Windows' system thermal sensor (inside the laptop, not the battery itself; normal is under 60 °C).");
        }
        if (batteryC is double b)
        {
            double score = SmartInsights.HeatScoreFromTemp(b);
            return new HeatReading(b, "battery", Level(b), (int)Math.Round(score), cpu, gpuBusy, chg,
                $"Measured {b:0.0} °C by the battery's own sensor.");
        }
        double est = Math.Clamp(cpu * 0.55 + (gpuBusy ? 30 : 0) + Math.Min(chg ?? 0, 100) * 0.25, 0, 100);
        return new HeatReading(null, "estimate", SmartInsights.HeatLevel(est), (int)Math.Round(est), cpu, gpuBusy, chg,
            "No temperature sensor is readable, so this is estimated from CPU use" + (gpuBusy ? ", the busy NVIDIA GPU" : "") +
            (chg is not null ? " and charging power" : "") + ".");
    }

    /// <summary>The system thermal zone sits near the CPU and runs far hotter than the battery: cool under 60 °C, warm under 75 °C, hot from 75 °C.</summary>
    public static string ZoneLevel(double zoneC) => zoneC < 60 ? "cool" : zoneC < 75 ? "warm" : "hot";

    /// <summary>Maps the zone's 60–75 °C band onto the battery's 40–48 °C band so scores and "hot hours" mean the same thing for both sensors.</summary>
    public static double ZoneAsBatteryC(double zoneC) => 40 + (zoneC - 60) * 8.0 / 15.0;

    /// <summary>Battery temperature: cool under 40 °C, warm under 48 °C, hot from 48 °C.</summary>
    public static string Level(double tempC) => tempC < 40 ? "cool" : tempC < 48 ? "warm" : "hot";

    public void Dispose()
    {
        lock (_lock) { _cpu?.Dispose(); _cpu = null; }
    }
}
