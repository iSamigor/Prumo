namespace Prumo.Core;

/// <summary>One reading of whole-system load, taken about once a second.</summary>
public sealed record SystemSample(
    DateTime Time,
    /// <summary>Total CPU utility in percent (0-100), as Task Manager shows it ("% Processor Utility").</summary>
    double CpuTotal,
    /// <summary>Per logical processor utility in percent, index = logical CPU number.</summary>
    double[] CpuPerCore,
    /// <summary>Physical memory in use, percent (0-100).</summary>
    double MemoryLoad,
    ulong MemoryUsedBytes,
    ulong MemoryTotalBytes,
    /// <summary>Busiest GPU engine utilization in percent (0-100), or -1 when unavailable.</summary>
    double GpuTotal);

/// <summary>Reads system-wide CPU / memory / GPU load for the graphs.</summary>
public interface ISystemSampler : IDisposable
{
    /// <summary>Takes a new reading. The first call may return zeros (counters need two samples).</summary>
    SystemSample Sample();
}
