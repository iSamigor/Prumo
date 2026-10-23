using System.Runtime.InteropServices;

namespace Prumo.Core;

/// <summary>
/// Task Manager style readings: CPU via PDH "% Processor Utility", memory via
/// GlobalMemoryStatusEx, GPU via the busiest PDH GPU Engine engine type.
/// </summary>
public sealed class SystemSampler : ISystemSampler
{
    private const string CpuCounterPath = @"\Processor Information(*)\% Processor Utility";
    private const string GpuCounterPath = @"\GPU Engine(*)\Utilization Percentage";

    private const uint PdhFmtDouble = 0x0000_0200;
    private const uint PdhFmtNoCap100 = 0x0000_8000;
    private const uint PdhStatusValidData = 0x0000_0000;
    private const uint PdhStatusNewData = 0x0000_0001;
    private const uint PdhMoreData = 0x8000_07D2;
    private const uint PdhNoData = 0x8000_07D5;
    private const uint PdhCStatusNoInstance = 0x8000_07D3;

    private readonly object _gate = new();
    private IntPtr _query;
    private IntPtr _cpuCounter;
    private IntPtr _gpuCounter;
    private bool _disposed;

    public SystemSampler()
    {
        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out IntPtr query) != 0) return;
            _query = query;

            if (PdhAddEnglishCounterW(_query, CpuCounterPath, IntPtr.Zero, out IntPtr cpu) == 0)
                _cpuCounter = cpu;

            // Missing GPU counters (VMs, some drivers) just mean "unknown" -> -1.
            if (PdhAddEnglishCounterW(_query, GpuCounterPath, IntPtr.Zero, out IntPtr gpu) == 0)
                _gpuCounter = gpu;

            PdhCollectQueryData(_query); // rate counters need one prior sample
        }
        catch
        {
            _query = _cpuCounter = _gpuCounter = IntPtr.Zero;
        }
    }

    public SystemSample Sample()
    {
        double cpuTotal = 0;
        double[] perCore = Array.Empty<double>();
        double memLoad = 0;
        ulong memUsed = 0;
        ulong memTotal = 0;
        double gpuTotal = -1;

        lock (_gate)
        {
            if (_disposed)
                return new SystemSample(DateTime.Now, 0, Array.Empty<double>(), 0, 0, 0, -1);

            if (_query != IntPtr.Zero)
            {
                try { PdhCollectQueryData(_query); } catch { /* stale data is fine */ }
            }

            try
            {
                if (_cpuCounter != IntPtr.Zero && TryReadCounterArray(_cpuCounter, out var cpuItems))
                    perCore = BuildPerCore(cpuItems, out cpuTotal);
            }
            catch { cpuTotal = 0; perCore = Array.Empty<double>(); }

            try
            {
                if (_gpuCounter != IntPtr.Zero && TryReadCounterArray(_gpuCounter, out var gpuItems))
                    gpuTotal = BusiestGpuEngine(gpuItems);
            }
            catch { gpuTotal = -1; }

            try
            {
                var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                if (GlobalMemoryStatusEx(ref status) && status.ullTotalPhys > 0)
                {
                    memTotal = status.ullTotalPhys;
                    memUsed = memTotal - status.ullAvailPhys;
                    memLoad = Math.Clamp(memUsed * 100.0 / memTotal, 0, 100);
                }
            }
            catch { }
        }

        return new SystemSample(DateTime.Now, cpuTotal, perCore, memLoad, memUsed, memTotal, gpuTotal);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_query != IntPtr.Zero)
            {
                PdhCloseQuery(_query);
                _query = _cpuCounter = _gpuCounter = IntPtr.Zero;
            }
        }
    }

    // Instance names look like "0,7" (group,index); "_Total" and "0,_Total" must be skipped.
    private static bool TryParseProcessorInstance(string name, out int cpu)
    {
        cpu = -1;
        int comma = name.IndexOf(',');
        string groupPart = comma < 0 ? "0" : name[..comma];
        string indexPart = comma < 0 ? name : name[(comma + 1)..];

        if (indexPart.StartsWith('_') || groupPart == "_Total") return false;
        if (!int.TryParse(groupPart, out int group) || !int.TryParse(indexPart, out int index)) return false;

        cpu = group * 64 + index; // processor numbers continue across groups of 64
        return true;
    }

    private static double[] BuildPerCore(List<(string Name, double Value)> items, out double total)
    {
        total = 0;
        var cores = new List<(int Cpu, double Value)>(items.Count);
        foreach (var (name, value) in items)
        {
            if (TryParseProcessorInstance(name, out int cpu))
                cores.Add((cpu, Math.Clamp(value, 0, 100)));
        }

        if (cores.Count == 0) return Array.Empty<double>();

        int length = 0;
        foreach (var (cpu, _) in cores)
            if (cpu + 1 > length) length = cpu + 1;

        var perCore = new double[length];
        double sum = 0;
        foreach (var (cpu, value) in cores)
        {
            perCore[cpu] = value;
            sum += value;
        }

        total = Math.Clamp(sum / cores.Count, 0, 100);
        return perCore;
    }

    // "\GPU Engine" gives one row per process and engine, e.g.
    // pid_1234_luid_0x00000000_0x0000C4BB_phys_0_eng_0_engtype_3D.
    private static string GpuEngineKey(string instanceName)
    {
        string[] parts = instanceName.Split('_');
        string luidHigh = "", luidLow = "", engineType = "";

        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] == "luid" && i + 2 < parts.Length)
            {
                luidHigh = parts[i + 1];
                luidLow = parts[i + 2];
            }
            else if (parts[i] == "engtype" && i + 1 < parts.Length)
            {
                engineType = parts[i + 1];
            }
        }

        return luidHigh + "_" + luidLow + "_" + engineType;
    }

    private static double BusiestGpuEngine(List<(string Name, double Value)> items)
    {
        var usage = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (name, value) in items)
        {
            if (name.Length == 0 || value <= 0) continue;
            string key = GpuEngineKey(name);
            usage[key] = usage.TryGetValue(key, out double sum) ? sum + value : value;
        }

        double best = 0;
        foreach (double util in usage.Values)
            if (util > best) best = util;

        return Math.Clamp(best, 0, 100);
    }

    // The GPU instance set changes as processes start/stop, so the array may grow between
    // the size probe and the read; retry with the newly reported size.
    private static bool TryReadCounterArray(IntPtr counter, out List<(string Name, double Value)> items)
    {
        items = new List<(string, double)>();
        uint format = PdhFmtDouble | PdhFmtNoCap100;
        uint size = 0, count = 0;

        uint status = PdhGetFormattedCounterArrayW(counter, format, ref size, ref count, IntPtr.Zero);
        if (status == PdhNoData || status == PdhCStatusNoInstance) return true;
        if (status != PdhMoreData) return false;

        int stride = Marshal.SizeOf<PdhFmtCounterValueItem>();
        for (int attempt = 0; attempt < 4 && size > 0; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal((nint)size);
            try
            {
                uint needed = size;
                uint n = count;
                status = PdhGetFormattedCounterArrayW(counter, format, ref needed, ref n, buffer);

                if (status == PdhMoreData)
                {
                    size = needed;
                    count = n;
                    continue;
                }
                if (status == PdhNoData || status == PdhCStatusNoInstance) return true;
                if (status != 0) return false;

                for (uint i = 0; i < n; i++)
                {
                    IntPtr at = new(buffer.ToInt64() + (long)i * stride);
                    var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(at);
                    if (item.FmtValue.CStatus != PdhStatusValidData &&
                        item.FmtValue.CStatus != PdhStatusNewData) continue;

                    string name = Marshal.PtrToStringUni(item.szName) ?? "";
                    items.Add((name, item.FmtValue.Value));
                }
                return true;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    // CStatus followed by the union; on x64 the double lands at offset 8.
    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValue
    {
        public uint CStatus;
        public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItem
    {
        public IntPtr szName;
        public PdhFmtCounterValue FmtValue;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
