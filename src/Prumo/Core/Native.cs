using System.Runtime.InteropServices;

namespace Prumo.Core;

internal static unsafe class Native
{
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_SET_INFORMATION = 0x0200;
    public const uint PROCESS_QUERY_INFORMATION = 0x0400;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(nint h);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetPriorityClass(nint h);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetPriorityClass(nint h, uint cls);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessAffinityMask(nint h, out nuint processMask, out nuint systemMask);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetProcessAffinityMask(nint h, nuint mask);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageNameW(nint h, int flags, char* buf, ref int size);

    // ---- Efficiency mode (EcoQoS) ----
    public const int ProcessPowerThrottling = 4;
    public const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetProcessInformation(nint h, int cls, ref PROCESS_POWER_THROTTLING_STATE info, int size);

    // ---- I/O priority, process snapshot ----
    public const int ProcessIoPriority = 33;

    [DllImport("ntdll.dll")]
    public static extern int NtSetInformationProcess(nint h, int cls, ref int value, int size);

    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(nint h, int cls, out int value, int size, out int ret);

    public const int SystemProcessInformation = 5;

    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(int cls, void* buf, int len, out int ret);

    // SYSTEM_PROCESS_INFORMATION (x64), only the fields we read.
    [StructLayout(LayoutKind.Explicit)]
    public struct SYSTEM_PROCESS_INFORMATION
    {
        [FieldOffset(0x00)] public uint NextEntryOffset;
        [FieldOffset(0x04)] public uint NumberOfThreads;
        [FieldOffset(0x20)] public long CreateTime;
        [FieldOffset(0x28)] public long UserTime;
        [FieldOffset(0x30)] public long KernelTime;
        [FieldOffset(0x38)] public ushort NameLength;      // UNICODE_STRING.Length (bytes)
        [FieldOffset(0x40)] public char* NameBuffer;
        [FieldOffset(0x48)] public int BasePriority;
        [FieldOffset(0x50)] public nint UniqueProcessId;
        [FieldOffset(0x58)] public nint InheritedFromUniqueProcessId;
        [FieldOffset(0x64)] public uint SessionId;
        [FieldOffset(0x90)] public nuint WorkingSetSize;
    }

    // ---- GPU scheduling priority (gdi32, used by DirectX) ----
    [DllImport("gdi32.dll")]
    public static extern int D3DKMTSetProcessSchedulingPriorityClass(nint h, int cls);

    [DllImport("gdi32.dll")]
    public static extern int D3DKMTGetProcessSchedulingPriorityClass(nint h, out int cls);

    // ---- Foreground window ----
    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hwnd, out int pid);

    // ---- Power plans ----
    [DllImport("powrprof.dll")]
    public static extern uint PowerGetActiveScheme(nint root, out nint guid);

    [DllImport("powrprof.dll")]
    public static extern uint PowerSetActiveScheme(nint root, ref Guid guid);

    [DllImport("powrprof.dll")]
    public static extern uint PowerEnumerate(nint root, nint schemeGuid, nint subGroup, uint accessFlags, uint index, byte* buffer, ref uint size);

    [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
    public static extern uint PowerReadFriendlyName(nint root, ref Guid scheme, nint subGroup, nint setting, byte* buffer, ref uint size);

    [DllImport("kernel32.dll")]
    public static extern nint LocalFree(nint p);

    // ---- Privileges ----
    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(nint process, uint access, out nint token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct TOKEN_PRIVILEGES { public uint Count; public long Luid; public uint Attributes; }

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool AdjustTokenPrivileges(nint token, bool disableAll, ref TOKEN_PRIVILEGES state, int len, nint prev, nint retLen);

    [DllImport("kernel32.dll")]
    public static extern nint GetCurrentProcess();

    public static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out var token)) return;
        try
        {
            if (!LookupPrivilegeValueW(null, name, out var luid)) return;
            var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = 0x2 };
            AdjustTokenPrivileges(token, false, ref tp, 0, 0, 0);
        }
        finally { CloseHandle(token); }
    }

    public static string? ImagePath(nint h)
    {
        char* buf = stackalloc char[1024];
        int size = 1024;
        return QueryFullProcessImageNameW(h, 0, buf, ref size) ? new string(buf, 0, size) : null;
    }
}
