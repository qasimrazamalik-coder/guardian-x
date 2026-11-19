using System.Management;

namespace GuardianX.Core.Power;

/// <summary>The laptop maker and whether it has a built-in charge limit Guardian X can use. NativeLimit is 'asus', 'lenovo' or null.</summary>
public sealed record VendorInfo(string Brand, string? NativeLimit, bool? LimitActive, string Note);

/// <summary>
/// Built-in charge limits from the laptop maker. ASUS exposes one through its ATK WMI interface; Lenovo through
/// Lenovo Vantage only; HP consumer laptops (OMEN, Victus, Pavilion) have none Windows software can set.
/// </summary>
public static class VendorLimit
{
    public const uint AsusChargeLimitDevice = 0x00120057;
    private static string? _brand;
    private static bool? _asusActive; // last value set this session; the firmware can't be asked cheaply

    /// <summary>SystemManufacturer from the BIOS registry key, e.g. "HP", "ASUSTeK COMPUTER INC.", "LENOVO".</summary>
    public static string Manufacturer()
    {
        if (_brand is not null) return _brand;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            return _brand = (key?.GetValue("SystemManufacturer") as string)?.Trim() ?? "";
        }
        catch { return _brand = ""; }
    }

    public static VendorInfo Current() => Describe(Manufacturer(), _asusActive);

    /// <summary>What applies to a maker (from the BIOS manufacturer string).</summary>
    public static VendorInfo Describe(string manufacturer, bool? asusActive = null)
    {
        var m = (manufacturer ?? "").Trim();
        var u = m.ToUpperInvariant();
        if (u.Contains("ASUS"))
            return new VendorInfo("ASUS", "asus", asusActive,
                "ASUS laptops have a built-in charge limit. Guardian X can switch it between 80% and 100% (not tested on real hardware).");
        if (u.Contains("LENOVO"))
            return new VendorInfo("Lenovo", "lenovo", null,
                "Lenovo laptops have Conservation mode, which stops charging at about 60–80%. Turn it on in Lenovo Vantage > Power; Guardian X can't switch it.");
        if (u.Contains("HP") || u.Contains("HEWLETT"))
            return new VendorInfo("HP", null, null,
                "HP consumer laptops have no software charge limit, so Guardian X uses the smart plug.");
        return new VendorInfo(m.Length == 0 ? "Unknown" : m, null, null,
            "This laptop's maker offers no charge limit Guardian X can control, so Guardian X uses the smart plug.");
    }

    /// <summary>Turns the maker's charge limit on (80%) or off (100%). Only ASUS can be switched.</summary>
    public static (bool Ok, string Message) Set(bool on)
    {
        var info = Current();
        if (info.NativeLimit == "lenovo") return (false, info.Note);
        if (info.NativeLimit != "asus") return (false, info.Note);
        try
        {
            using var cls = new ManagementClass(@"root\wmi", "AsusAtkWmi_WMNB", null);
            foreach (ManagementObject inst in cls.GetInstances())
            {
                using (inst)
                using (var args = inst.GetMethodParameters("DEVS"))
                {
                    args["Device_ID"] = AsusChargeLimitDevice;
                    args["Control_status"] = on ? 80u : 100u;
                    inst.InvokeMethod("DEVS", args, null);
                }
                _asusActive = on;
                return (true, on ? "ASUS charge limit set to 80% (not tested on real hardware)." : "ASUS charge limit off: charges to 100%.");
            }
            return (false, "The ASUS WMI interface isn't available. Install ASUS System Control Interface (MyASUS) and try again.");
        }
        catch (Exception e)
        {
            return (false, "Couldn't set the ASUS charge limit: " + e.Message);
        }
    }
}
