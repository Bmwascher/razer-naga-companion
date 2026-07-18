using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Application = System.Windows.Application;

namespace NagaBatteryTray;

internal static class Program
{
    private const string MutexName = @"Local\NagaBatteryTray-b3f1c2d4-5a6e-4f80-9c1a-2e7d8b4f6a90";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--probe")
        {
            AllocConsoleIfNeeded();
            return Diagnostics.ProbeCommand.Run();
        }

        if (args.Length > 0 && args[0] == "--probe-dpi")
        {
            AllocConsoleIfNeeded();
            return Diagnostics.ProbeCommand.RunDpi();
        }

        if (args.Length > 0 && args[0] == "--probe-dock")
        {
            AllocConsoleIfNeeded();
            return Diagnostics.ProbeCommand.RunDock();
        }

        if (args.Length > 0 && args[0] == "--probe-buttons")
        {
            AllocConsoleIfNeeded();
            return args.Length > 1 && args[1] == "--reset" ? Diagnostics.ProbeCommand.RunButtonsReset()
                : args.Length > 1 && args[1] == "--slot-test" ? Diagnostics.ProbeCommand.RunButtonsSlotTest()
                : Diagnostics.ProbeCommand.RunButtons();
        }

        if (args.Length > 0 && args[0] == "--probe-profile")
        {
            AllocConsoleIfNeeded();
            return args.Length > 1 && args[1] == "--set-test" ? Diagnostics.ProfileProbeCommand.RunProfileSetTest()
                : Diagnostics.ProfileProbeCommand.Run();
        }

        // Headless run-at-login registration (used by install.ps1). Registers a
        // delayed logon scheduled task pointing at this exe, then exits.
        if (args.Length > 0 && args[0] == "--enable-startup")
        {
            try { new Startup.StartupRegistration().Enable(); return 0; }
            catch { return 1; }
        }

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew) return 0; // already running

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var host = new AppHost(app);
        host.Start();
        return app.Run();
    }

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    private static void AllocConsoleIfNeeded()
    {
        try { if (Console.OpenStandardOutput() == Stream.Null) AllocConsole(); } catch { AllocConsole(); }
    }
}
