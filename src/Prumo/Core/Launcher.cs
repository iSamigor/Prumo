using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Prumo.Core;

/// <summary>
/// Starts a program as the signed-in user, not as administrator: Prumo runs elevated, and a program it
/// restarts ("manter rodando", vigia) must not inherit that. Uses the token of the shell (explorer.exe).
/// </summary>
public static unsafe class Launcher
{
    [DllImport("user32.dll")] static extern nint GetShellWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out int pid);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool DuplicateTokenEx(nint token, uint access, nint attrs, int impersonation, int type, out nint dup);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public nint lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public nint hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessWithTokenW(nint token, int logonFlags, string? app, char* cmdLine, uint flags,
        nint env, string? dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string? app, char* cmdLine, nint procAttrs, nint threadAttrs, bool inherit, uint flags,
        nint env, string? dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    const uint TOKEN_ALL_ACCESS = 0xF01FF, CREATE_UNICODE_ENVIRONMENT = 0x400;

    /// <summary>Returns null on success, or a pt-BR error.</summary>
    public static string? StartAsUser(string commandLine, string? workingDir)
    {
        var dir = workingDir is not null && Directory.Exists(workingDir) ? workingDir : null;
        // Not elevated (a test run): a plain CreateProcess already runs as the user.
        if (!Elevated()) return Create(0, commandLine, dir);

        GetWindowThreadProcessId(GetShellWindow(), out var shellPid);
        if (shellPid == 0) return "o Explorer não está rodando";
        var hp = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, shellPid);
        if (hp == 0) return "sem acesso ao Explorer";
        nint token = 0, dup = 0;
        try
        {
            if (!Native.OpenProcessToken(hp, 0x0002 | 0x0008 | 0x0001, out token)) return "sem acesso ao token do usuário";
            // SecurityImpersonation, TokenPrimary
            if (!DuplicateTokenEx(token, TOKEN_ALL_ACCESS, 0, 2, 1, out dup)) return "não consegui duplicar o token do usuário";
            return Create(dup, commandLine, dir);
        }
        finally
        {
            if (dup != 0) Native.CloseHandle(dup);
            if (token != 0) Native.CloseHandle(token);
            Native.CloseHandle(hp);
        }
    }

    /// <summary>CreateProcessWithTokenW with the user's token, or CreateProcessW when token is 0.</summary>
    static string? Create(nint token, string commandLine, string? dir)
    {
        var si = new STARTUPINFO { cb = sizeof(STARTUPINFO) };
        var cmd = (commandLine + "\0").ToCharArray();   // CreateProcess may write to the buffer
        fixed (char* c = cmd)
        {
            PROCESS_INFORMATION pi;
            bool ok = token != 0
                ? CreateProcessWithTokenW(token, 0, null, c, CREATE_UNICODE_ENVIRONMENT, 0, dir, ref si, out pi)
                : CreateProcessW(null, c, 0, 0, false, 0, 0, dir, ref si, out pi);
            if (!ok) return $"falha ao iniciar (erro {Marshal.GetLastWin32Error()})";
            Native.CloseHandle(pi.hThread);
            Native.CloseHandle(pi.hProcess);
        }
        return null;
    }

    static bool Elevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
