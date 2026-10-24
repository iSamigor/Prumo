using System.Runtime.InteropServices;

namespace Prumo.Core;

/// <summary>What "Limpar memória agora" did: RAM available before and after, and the regions it could not clean.</summary>
public sealed record MemoryCleanResult(ulong FreedBytes, ulong AvailableBefore, ulong AvailableAfter, int Trimmed, List<string> Failed);

/// <summary>Memory housekeeping (standby purge, working-set trim) and quick counters. Never throws.</summary>
public static class MemoryTools
{
    private const int SystemMemoryListInformation = 80;
    private const int SystemCombinePhysicalMemoryInformation = 130;
    private const int SystemRegistryReconciliationInformation = 155;
    private const int MemoryFlushModifiedList = 3;
    private const int MemoryPurgeStandbyList = 4;
    private const int MemoryPurgeLowPriorityStandbyList = 5;

    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x2;
    private const int ErrorNotAllAssigned = 1300;

    /// <summary>Drops the standby page list. Needs SeProfileSingleProcessPrivilege (elevated).</summary>
    public static bool PurgeStandbyList()
    {
        try
        {
            if (!EnablePrivilege("SeProfileSingleProcessPrivilege")) return false;
            int command = MemoryPurgeStandbyList;
            return NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int)) >= 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// "Limpar memória agora": the regions Mem Reduct cleans, in its order (working sets, system file
    /// cache, modified list, standby, combine identical pages, registry cache). Takes up to a few
    /// seconds: call it off the UI thread. Never throws.
    /// </summary>
    public static MemoryCleanResult Clean(MemoryCleanSettings s, IReadOnlyList<(int Pid, long Create)> trimTargets)
    {
        ulong before = AvailableBytes();
        var failed = new List<string>();
        int trimmed = 0;
        try
        {
            bool profile = EnablePrivilege("SeProfileSingleProcessPrivilege");
            bool quota = EnablePrivilege("SeIncreaseQuotaPrivilege");

            if (s.WorkingSets)
                foreach (var (pid, create) in trimTargets)
                    if (TrimWorkingSet(pid, create)) trimmed++;
            if (s.SystemFileCache && !(quota && SetSystemFileCacheSize(-1, -1, 0))) failed.Add("cache de arquivos do sistema");
            // Modified pages go to the page file and land on the standby list, which the next step drops.
            if (s.ModifiedList && !(profile && ListCommand(MemoryFlushModifiedList))) failed.Add("lista modificada");
            if (s.WholeStandby) { if (!(profile && ListCommand(MemoryPurgeStandbyList))) failed.Add("lista de espera"); }
            else if (s.LowPriorityStandby && !(profile && ListCommand(MemoryPurgeLowPriorityStandbyList))) failed.Add("lista de espera (baixa prioridade)");
            if (s.CombinePages && !(profile && CombinePages())) failed.Add("páginas iguais");
            if (s.RegistryCache && NtSetSystemInformation(SystemRegistryReconciliationInformation, 0, 0) < 0) failed.Add("cache do registro");
        }
        catch { failed.Add("erro inesperado"); }

        ulong after = AvailableBytes();
        return new MemoryCleanResult(after > before ? after - before : 0, before, after, trimmed, failed);
    }

    static bool ListCommand(int command) => NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int)) >= 0;

    static unsafe bool CombinePages()
    {
        var info = default(MemoryCombineInformationEx);
        return NtSetSystemInformation(SystemCombinePhysicalMemoryInformation, (nint)(&info), sizeof(MemoryCombineInformationEx)) >= 0;
    }

    /// <summary>Empties the working set of one process without touching its private commit.</summary>
    public static bool TrimWorkingSet(int pid, long createTime = 0)
    {
        nint handle = 0;
        try
        {
            handle = OpenProcess(ProcessSetQuota | ProcessQueryLimitedInformation, false, pid);
            if (handle == 0 || !Native.IsSameProcess(handle, createTime)) return false;   // queued a few ticks ago: PID may be reused
            if (EmptyWorkingSet(handle)) return true;
            // Some builds refuse EmptyWorkingSet with only limited query rights; -1/-1 empties the set.
            return SetProcessWorkingSetSizeEx(handle, -1, -1, 0);
        }
        catch { return false; }
        finally
        {
            if (handle != 0) CloseHandle(handle);
        }
    }

    /// <summary>Bytes on the standby list, or 0 when the query fails.</summary>
    public static ulong StandbyBytes()
    {
        try
        {
            unsafe
            {
                var info = default(MemoryListInformation);
                int status = NtQuerySystemInformation(SystemMemoryListInformation, &info, sizeof(MemoryListInformation), out _);
                if (status < 0) return 0;

                ulong pages = 0;
                for (int i = 0; i < 8; i++) pages += info.PageCountByPriority[i];
                return pages * (ulong)Environment.SystemPageSize;
            }
        }
        catch { return 0; }
    }

    /// <summary>Physical memory available right now, or 0 when the query fails.</summary>
    public static ulong AvailableBytes()
    {
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref status) ? status.AvailPhys : 0;
        }
        catch { return 0; }
    }

    // ---- privilege ----
    private static bool EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out nint token)) return false;
        try
        {
            if (!LookupPrivilegeValueW(null, name, out long luid)) return false;
            var state = new TokenPrivileges { Count = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            if (!AdjustTokenPrivileges(token, false, ref state, 0, 0, 0)) return false;
            return Marshal.GetLastWin32Error() != ErrorNotAllAssigned;
        }
        finally { CloseHandle(token); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges
    {
        public uint Count;
        public long Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    // SYSTEM_MEMORY_LIST_INFORMATION (x64): five counters, then the standby pages per priority.
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct MemoryListInformation
    {
        public nuint ZeroPageCount;
        public nuint FreePageCount;
        public nuint ModifiedPageCount;
        public nuint ModifiedNoWritePageCount;
        public nuint BadPageCount;
        public fixed ulong PageCountByPriority[8];
        public fixed ulong RepurposedPagesByPriority[8];
        public nuint ModifiedPageCountPageFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCombineInformationEx
    {
        public nint Handle;
        public nuint PagesCombined;
        public uint Flags;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int classId, ref int info, int length);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int classId, nint info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetSystemFileCacheSize(nint min, nint max, uint flags);

    [DllImport("ntdll.dll")]
    private static extern unsafe int NtQuerySystemInformation(int classId, void* info, int length, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSizeEx(nint process, nint min, nint max, uint flags);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(nint token, bool disableAll, ref TokenPrivileges state, int length, nint previous, nint returnLength);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();
}
