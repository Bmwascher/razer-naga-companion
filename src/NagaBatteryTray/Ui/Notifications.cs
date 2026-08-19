using CommunityToolkit.WinUI.Notifications;

namespace NagaBatteryTray.Ui;

public static class Notifications
{
    public static void LowBattery(int percent, string? model)
    {
        new ToastContentBuilder()
            .AddText(model ?? "Razer Naga")
            .AddText($"Battery at {percent}% - time to charge.")
            .Show();
    }
}
