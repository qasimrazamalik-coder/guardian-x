using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GuardianX.Core.Battery;

/// <summary>One reading of the battery, straight from the Windows battery driver.</summary>
public sealed record BatterySnapshot(
    DateTimeOffset Time,
    double Percent,
    int RemainingMWh,
    int FullMWh,
    int DesignMWh,
    int Cycles,
    int? RateMw,          // positive while charging, negative while discharging, null if unknown
    int? VoltageMv,
    double? TemperatureC, // null when the battery does not report it
    bool OnAc,
    bool Charging,
    string Chemistry,
    string Manufacturer,
    string DeviceName)
{
    public double HealthPercent => DesignMWh > 0 ? FullMWh * 100.0 / DesignMWh : 0;
}

/// <summary>
/// Reads the battery through the battery class driver (IOCTL_BATTERY_*), which works without admin rights
/// and gives design capacity, cycle count and temperature that WMI hides from normal users.
/// </summary>
public static class BatteryReader
{
    private static readonly Guid BatteryClass = new("72631e54-78a4-11d0-bcf7-00aa00b7b32a");
    private const uint IoctlQueryTag = 0x294040;
    private const uint IoctlQueryInformation = 0x294044;
    private const uint IoctlQueryStatus = 0x29404C;

    private const int InfoBattery = 0, InfoTemperature = 2, InfoDeviceName = 4, InfoManufacturer = 6;
    private const uint PowerOnline = 0x1, PowerCharging = 0x4;
    private const uint Unknown = 0xFFFFFFFF;

    /// <summary>The first battery, or null if this PC has none.</summary>
    public static BatterySnapshot? Read()
    {
        var path = FirstBatteryPath();
        if (path is null) return null;
        using var handle = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the battery");

        uint wait = 0, tag = 0;
        if (!DeviceIoControl(handle, IoctlQueryTag, ref wait, 4, out tag, 4, out _, IntPtr.Zero) || tag == 0)
            return null;

        var info = new BatteryInformation();
        var q = new BatteryQueryInformation { BatteryTag = tag, InformationLevel = InfoBattery };
        if (!DeviceIoControl(handle, IoctlQueryInformation, ref q, Marshal.SizeOf<BatteryQueryInformation>(),
                ref info, Marshal.SizeOf<BatteryInformation>(), out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read battery information");

        var waitStatus = new BatteryWaitStatus { BatteryTag = tag };
        var status = new BatteryStatus();
        if (!DeviceIoControl(handle, IoctlQueryStatus, ref waitStatus, Marshal.SizeOf<BatteryWaitStatus>(),
                ref status, Marshal.SizeOf<BatteryStatus>(), out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read battery status");

        double? tempC = null;
        q.InformationLevel = InfoTemperature;
        uint tenthsKelvin = 0;
        if (DeviceIoControl(handle, IoctlQueryInformation, ref q, Marshal.SizeOf<BatteryQueryInformation>(),
                out tenthsKelvin, 4, out _, IntPtr.Zero) && tenthsKelvin > 2000 && tenthsKelvin < 4000)
            tempC = Math.Round(tenthsKelvin / 10.0 - 273.15, 1);

        int full = (int)info.FullChargedCapacity;
        int remaining = status.Capacity == Unknown ? 0 : (int)status.Capacity;
        double pct = full > 0 ? Math.Clamp(remaining * 100.0 / full, 0, 100) : 0;
        int? rate = status.Rate == unchecked((int)0x80000000) ? null : status.Rate;
        int? voltage = status.Voltage == Unknown ? null : (int)status.Voltage;

        return new BatterySnapshot(
            DateTimeOffset.Now,
            Math.Round(pct, 1),
            remaining,
            full,
            (int)info.DesignedCapacity,
            info.CycleCount == Unknown ? 0 : (int)info.CycleCount,
            rate,
            voltage,
            tempC,
            (status.PowerState & PowerOnline) != 0,
            (status.PowerState & PowerCharging) != 0,
            info.ChemistryText(),
            ReadString(handle, tag, InfoManufacturer),
            ReadString(handle, tag, InfoDeviceName));
    }

    private static string ReadString(SafeFileHandle handle, uint tag, int level)
    {
        var q = new BatteryQueryInformation { BatteryTag = tag, InformationLevel = level };
        var buffer = new byte[512];
        if (!DeviceIoControl(handle, IoctlQueryInformation, ref q, Marshal.SizeOf<BatteryQueryInformation>(),
                buffer, buffer.Length, out var got, IntPtr.Zero) || got == 0)
            return "";
        return Encoding.Unicode.GetString(buffer, 0, (int)got).TrimEnd('\0', ' ');
    }

    private static string? FirstBatteryPath()
    {
        var guid = BatteryClass;
        var devs = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x2 | 0x10);
        if (devs == new IntPtr(-1)) return null;
        try
        {
            var iface = new SpDeviceInterfaceData { CbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
            if (!SetupDiEnumDeviceInterfaces(devs, IntPtr.Zero, ref guid, 0, ref iface)) return null;
            SetupDiGetDeviceInterfaceDetail(devs, ref iface, IntPtr.Zero, 0, out var size, IntPtr.Zero);
            var detail = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetail(devs, ref iface, detail, size, out _, IntPtr.Zero)) return null;
                return Marshal.PtrToStringUni(detail + 4);
            }
            finally { Marshal.FreeHGlobal(detail); }
        }
        finally { SetupDiDestroyDeviceInfoList(devs); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData { public int CbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryQueryInformation { public uint BatteryTag; public int InformationLevel; public int AtRate; }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct BatteryInformation
    {
        public uint Capabilities;
        public byte Technology;
        public fixed byte Reserved[3];
        public fixed byte Chemistry[4];
        public uint DesignedCapacity, FullChargedCapacity, DefaultAlert1, DefaultAlert2, CriticalBias, CycleCount;

        public string ChemistryText()
        {
            var bytes = new byte[4];
            for (int i = 0; i < 4; i++) bytes[i] = Chemistry[i];
            return Encoding.ASCII.GetString(bytes).TrimEnd('\0', ' ');
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryWaitStatus { public uint BatteryTag, Timeout, PowerState, LowCapacity, HighCapacity; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryStatus { public uint PowerState, Capacity, Voltage; public int Rate; }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, int flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr devs, IntPtr devInfo, ref Guid guid, int index, ref SpDeviceInterfaceData data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devs, ref SpDeviceInterfaceData data, IntPtr detail, int size, out int required, IntPtr devInfo);
    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devs);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref uint input, int inSize, out uint output, int outSize, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref BatteryQueryInformation input, int inSize, ref BatteryInformation output, int outSize, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref BatteryQueryInformation input, int inSize, out uint output, int outSize, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref BatteryQueryInformation input, int inSize, byte[] output, int outSize, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref BatteryWaitStatus input, int inSize, ref BatteryStatus output, int outSize, out uint returned, IntPtr overlapped);
}
