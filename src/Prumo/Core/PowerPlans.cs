using System.Runtime.InteropServices;

namespace Prumo.Core;

public sealed record PowerPlan(Guid Id, string Name);

public static unsafe class PowerPlans
{
    const uint ACCESS_SCHEME = 16;

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

    public static Guid? Find(string name) =>
        List().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Id;

    public static bool SetActive(Guid id) => Native.PowerSetActiveScheme(0, ref id) == 0;
}
