using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;

namespace NagaBatteryTray.Startup;

/// <summary>
/// Run-at-login via a per-user <b>Scheduled Task</b> (not the HKCU Run key).
/// The task fires on logon with a short delay so Windows' Smart App Control /
/// SmartScreen cloud-reputation (ISG) lookup is ready before our unsigned exe is
/// loaded. The Run key fires ~52 s into boot — before the network is up — so SAC
/// fails the ISG lookup closed and vetoes the load (CodeIntegrity events 3033/3077).
/// The delayed task launches once the machine is online, when the (already
/// reputable) hash is allowed. Battery-agnostic and with no execution-time-limit so
/// a long-running tray app on a laptop isn't refused on battery or killed after 3 days.
/// Created without admin (InteractiveToken, LeastPrivilege).
/// </summary>
public sealed class StartupRegistration
{
    private readonly string _taskName;
    private readonly string _delay; // ISO-8601 duration, e.g. "PT1M"

    public StartupRegistration(string taskName = "NagaBatteryTray", string delay = "PT1M")
    {
        _taskName = taskName;
        _delay = delay;
    }

    public bool IsEnabled() => RunSchtasks($"/Query /TN \"{_taskName}\"") == 0;

    public void Enable()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"naga-task-{Guid.NewGuid():N}.xml");
        // schtasks /XML requires a UTF-16 file matching the prolog's encoding declaration.
        File.WriteAllText(tmp, BuildTaskXml(), Encoding.Unicode);
        try
        {
            int exit = RunSchtasks($"/Create /F /TN \"{_taskName}\" /XML \"{tmp}\"");
            if (exit != 0)
                throw new InvalidOperationException($"schtasks /Create failed (exit {exit}).");
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best-effort temp cleanup */ }
        }
    }

    public void Disable()
    {
        if (IsEnabled()) RunSchtasks($"/Delete /F /TN \"{_taskName}\"");
    }

    private string BuildTaskXml()
    {
        var exe = SecurityElement.Escape(Environment.ProcessPath);
        var user = SecurityElement.Escape($"{Environment.UserDomainName}\\{Environment.UserName}");

        // Element order within <Settings> is schema-significant; keep only the
        // overrides that matter (the rest default sensibly).
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts Razer Naga Companion at logon (delayed so SAC cloud-reputation is ready).</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>{_delay}</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{exe}</Command>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static int RunSchtasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }
}
