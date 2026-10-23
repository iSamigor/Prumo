using System.Runtime.InteropServices;

namespace Prumo.Core;

public sealed record PowerPlan(Guid Id, string Name);

public static unsafe class PowerPlans
{
    const uint ACCESS_SCHEME = 16;

    // Built-in plans, matched by GUID: the friendly names are localized, so never match on them.
    static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    static readonly Guid Ultimate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    public static List<PowerPlan> List()
    {
        var plans = new List<PowerPlan>();
        var buf = stackalloc byte[16];
        for (uint i = 0; ; i++)
        {
            uint size = 16;
            if (Native.PowerEnumerate(0, 0, 0, ACCESS_SCHEME, i, buf, ref size) != 0) break;
            var id = *(Guid*)buf;
            plans.Add(new PowerPlan(id, FriendlyName(id) ?? id.ToString()));
        }
        return plans;
    }

    static string? FriendlyName(Guid id)
    {
        uint size = 0;
        Native.PowerReadFriendlyName(0, ref id, 0, 0, null, ref size);
        if (size == 0) return null;
        var buf = new byte[size];
        fixed (byte* p = buf)
            if (Native.PowerReadFriendlyName(0, ref id, 0, 0, p, ref size) != 0) return null;
        return System.Text.Encoding.Unicode.GetString(buf).TrimEnd('\0');
    }

    public static Guid? Active()
    {
        if (Native.PowerGetActiveScheme(0, out var p) != 0) return null;
        var g = Marshal.PtrToStructure<Guid>(p);
        Native.LocalFree(p);
        return g;
    }

    public static string? ActiveName()
    {
        var a = Active();
        return a is null ? null : List().FirstOrDefault(p => p.Id == a)?.Name;
    }

    /// <summary>
    /// A performance plan name for settings that need one: the active plan when it is neither
    /// Balanced nor Power saver (so a tuned plan such as "Prumo Alto Desempenho" wins), otherwise
    /// Ultimate Performance, otherwise High performance; null when none of them exists.
    /// </summary>
    public static string? DefaultPerformancePlanName()
    {
        var plans = List();
        string? NameOf(Guid id) => plans.FirstOrDefault(p => p.Id == id)?.Name;

        if (Active() is { } active && active != Balanced && active != PowerSaver)
            return NameOf(active);

        return NameOf(Ultimate) ?? NameOf(HighPerformance);
    }

    public static Guid? Find(string name) =>
        List().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Id;

    public static bool SetActive(Guid id) => Native.PowerSetActiveScheme(0, ref id) == 0;
}
