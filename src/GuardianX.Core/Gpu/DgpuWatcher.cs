using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GuardianX.Core.Gpu;

/// <summary>An app holding memory on the NVIDIA GPU, which keeps the GPU powered on battery.</summary>
public sealed record GpuHolder(int Pid, string ProcessName, long DedicatedBytes);

/// <summary>
/// Finds apps that keep the discrete GPU awake. It reads Windows' "GPU Process Memory" counters, which
/// does not wake the GPU itself (running nvidia-smi would).
/// </summary>
public sealed class DgpuWatcher
{
    // Windows parts that sit on the dGPU when a display is attached to it; not something the user can close.
    private static readonly HashSet<string> SystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwm", "csrss", "System", "Idle", "explorer", "nvcontainer", "NVDisplay.Container", "audiodg"
    };

    private readonly string? _luidToken;
    public string? AdapterName { get; }
    public bool Available => _luidToken is not null;

    public DgpuWatcher()
    {
        var dgpu = DxgiAdapters.List().FirstOrDefault(a => a.VendorId == 0x10DE)       // NVIDIA
                   ?? DxgiAdapters.List().FirstOrDefault(a => a.VendorId == 0x1002 && a.DedicatedVideoMemory > 1L << 31); // AMD dGPU
        if (dgpu is null) return;
        AdapterName = dgpu.Description;
        _luidToken = $"luid_0x{dgpu.LuidHigh:X8}_0x{dgpu.LuidLow:X8}";
    }

    /// <summary>User apps holding memory on the discrete GPU right now, biggest first.</summary>
    public IReadOnlyList<GpuHolder> Holders()
    {
        if (_luidToken is null) return Array.Empty<GpuHolder>();
        var byPid = new Dictionary<int, long>();
        try
        {
            var category = new PerformanceCounterCategory("GPU Process Memory");
            var data = category.ReadCategory();
            var usage = data["Dedicated Usage"];
            if (usage is null) return Array.Empty<GpuHolder>();
            foreach (InstanceData inst in usage.Values)
            {
                var name = inst.InstanceName;
                if (!name.Contains(_luidToken, StringComparison.OrdinalIgnoreCase)) continue;
                if (inst.RawValue <= 0) continue;
                var pid = ParsePid(name);
                if (pid <= 0) continue;
                byPid[pid] = byPid.GetValueOrDefault(pid) + inst.RawValue;
            }
        }
        catch (Exception) { return Array.Empty<GpuHolder>(); }

        var result = new List<GpuHolder>();
        foreach (var (pid, bytes) in byPid)
        {
            string proc;
            try { proc = Process.GetProcessById(pid).ProcessName; } catch { continue; }
            if (SystemProcesses.Contains(proc)) continue;
            result.Add(new GpuHolder(pid, proc, bytes));
        }
        return result.OrderByDescending(h => h.DedicatedBytes).ToList();
    }

    internal static int ParsePid(string instance)
    {
        // "pid_1234_luid_0x00000000_0x0001802E_phys_0"
        if (!instance.StartsWith("pid_", StringComparison.OrdinalIgnoreCase)) return -1;
        var end = instance.IndexOf('_', 4);
        return end > 4 && int.TryParse(instance.AsSpan(4, end - 4), out var pid) ? pid : -1;
    }
}

internal sealed record DxgiAdapter(string Description, uint VendorId, long DedicatedVideoMemory, uint LuidLow, int LuidHigh);

/// <summary>Lists graphics adapters with their LUIDs through DXGI, read once at start-up.</summary>
internal static unsafe class DxgiAdapters
{
    private static List<DxgiAdapter>? _cache;

    public static List<DxgiAdapter> List()
    {
        if (_cache is not null) return _cache;
        var list = new List<DxgiAdapter>();
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        if (CreateDXGIFactory1(ref iid, out var factory) < 0 || factory == IntPtr.Zero) return _cache = list;
        try
        {
            var vtbl = *(IntPtr**)factory;
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, out IntPtr, int>)vtbl[12];
            for (uint i = 0; enumAdapters1(factory, i, out var adapter) >= 0; i++)
            {
                try
                {
                    var avtbl = *(IntPtr**)adapter;
                    var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, AdapterDesc1*, int>)avtbl[10];
                    AdapterDesc1 desc;
                    if (getDesc1(adapter, &desc) >= 0 && (desc.Flags & 2) == 0) // skip software adapters
                    {
                        var name = new string(desc.Description);
                        list.Add(new DxgiAdapter(name, desc.VendorId, (long)desc.DedicatedVideoMemory, desc.LuidLow, desc.LuidHigh));
                    }
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
        return _cache = list;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);
}
