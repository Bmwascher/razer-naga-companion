using Microsoft.Win32;

namespace NagaBatteryTray.Startup;

public sealed class StartupRegistration
{
    private readonly string _valueName;
    private readonly string _subKey;

    public StartupRegistration(
        string valueName = "NagaBatteryTray",
        string subKey = @"Software\Microsoft\Windows\CurrentVersion\Run")
    {
        _valueName = valueName;
        _subKey = subKey;
    }

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_subKey);
        return key?.GetValue(_valueName) is not null;
    }

    public void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(_subKey);
        key.SetValue(_valueName, $"\"{Environment.ProcessPath}\"");
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_subKey, writable: true);
        key?.DeleteValue(_valueName, throwOnMissingValue: false);
    }
}
