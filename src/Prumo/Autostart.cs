using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Prumo;

/// <summary>
/// Starts Prumo at logon through a Task Scheduler task with "highest privileges", which runs it
/// elevated without a UAC prompt (a Run key cannot do that for a requireAdministrator exe).
/// </summary>
public static class Autostart
{
    const string TaskName = "Prumo";

    // Task Scheduler COM: TASK_CREATE_OR_UPDATE and TASK_LOGON_INTERACTIVE_TOKEN (taskschd.h).
    const int TaskCreateOrUpdate = 6;
    const int TaskLogonInteractiveToken = 3;

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
        try
        {
            // Register from memory: a temp XML file would leave a window where another process running
            // as this user could swap it before the elevated Task Scheduler reads it.
            Register(xml, user);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // Only an elevated process may register a task that runs with highest privileges.
            return "Não consegui criar a tarefa de inicialização: é preciso executar o Prumo como administrador.";
        }
        catch (Exception ex)
        {
            return "Não consegui criar a tarefa de inicialização:\n" + ex.Message;
        }
    }

    /// <summary>Registers the logon task through the Task Scheduler COM API, with no temp file involved.</summary>
    static void Register(string xml, string user)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service")
                   ?? throw new InvalidOperationException("O Agendador de Tarefas não está disponível.");
        object? service = null, folder = null, task = null;
        try
        {
            service = Activator.CreateInstance(type)
                      ?? throw new InvalidOperationException("Não consegui abrir o Agendador de Tarefas.");
            dynamic svc = service;
            svc.Connect();
            folder = svc.GetFolder("\\");
            // RegisterTask takes the XML itself; the task service parses it as we hand it over.
            task = ((ITaskFolder)folder).RegisterTask(
                TaskName, xml, TaskCreateOrUpdate, user, null, TaskLogonInteractiveToken, null);
        }
        finally
        {
            Release(task);
            Release(folder);
            Release(service);
        }
    }

    static void Release(object? comObject)
    {
        try { if (comObject is not null) Marshal.FinalReleaseComObject(comObject); }
        catch { /* already gone */ }
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

/// <summary>
/// The slice of ITaskFolder we call. Late binding cannot marshal RegisterTask's VARIANT
/// parameters ("Could not convert argument"), so the folder call goes through this typed
/// IDispatch interface; the rest of the COM surface stays dynamic.
/// </summary>
[ComImport, Guid("8cfac062-a080-4c15-9a88-aa7c2af80dfc"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface ITaskFolder
{
    [DispId(10)]
    object RegisterTask(
        [MarshalAs(UnmanagedType.BStr)] string path,
        [MarshalAs(UnmanagedType.BStr)] string xmlText,
        int flags,
        [MarshalAs(UnmanagedType.Struct)] object? userId,
        [MarshalAs(UnmanagedType.Struct)] object? password,
        int logonType,
        [MarshalAs(UnmanagedType.Struct)] object? sddl);
}
