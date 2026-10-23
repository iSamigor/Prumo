using System.Diagnostics;

namespace Prumo;

/// <summary>
/// Starts Prumo at logon through a Task Scheduler task with "highest privileges", which runs it
/// elevated without a UAC prompt (a Run key cannot do that for a requireAdministrator exe).
/// </summary>
public static class Autostart
{
    const string TaskName = "Prumo";

    public static bool IsEnabled() => Run("schtasks.exe", $"/Query /TN \"{TaskName}\"").code == 0;

    public static string? Enable()
    {
        var exe = Environment.ProcessPath!;
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        // XML instead of /SC ONLOGON flags: lets us drop the 3-day time limit, the battery checks and the delay.
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Prumo: gerenciamento de processos</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{Escape(user)}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
                <AllowHardTerminate>true</AllowHardTerminate>
              </Settings>
              <Actions Context="Author"><Exec><Command>{Escape(exe)}</Command><Arguments>--tray</Arguments></Exec></Actions>
            </Task>
            """;
        var file = Path.Combine(Path.GetTempPath(), "prumo-task.xml");
        File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
        try
        {
            var (code, output) = Run("schtasks.exe", $"/Create /F /TN \"{TaskName}\" /XML \"{file}\"");
            return code == 0 ? null : "Não consegui criar a tarefa de inicialização:\n" + output;
        }
        finally { File.Delete(file); }
    }

    public static string? Disable()
    {
        var (code, output) = Run("schtasks.exe", $"/Delete /F /TN \"{TaskName}\"");
        return code == 0 ? null : "Não consegui remover a tarefa de inicialização:\n" + output;
    }

    static string Escape(string s) => System.Security.SecurityElement.Escape(s) ?? s;

    static (int code, string output) Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(10_000);
        return (p.ExitCode, output.Trim());
    }
}
