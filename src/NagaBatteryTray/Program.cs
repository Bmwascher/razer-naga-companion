using System.IO;
using System.Runtime.InteropServices;

namespace NagaBatteryTray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--probe")
        {
            AllocConsoleIfNeeded();
            return Diagnostics.ProbeCommand.Run();
        }
        return 0;
    }

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    private static void AllocConsoleIfNeeded()
    {
        try { if (Console.OpenStandardOutput() == Stream.Null) AllocConsole(); } catch { AllocConsole(); }
    }
}
