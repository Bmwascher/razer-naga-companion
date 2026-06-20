using CommunityToolkit.WinUI.Notifications;

namespace NagaBatteryTray.Ui;

public static class Notifications
{
    public static void LowBattery(int percent)
    {
        new ToastContentBuilder()
            .AddText("Naga V2 Pro")
            .AddText($"Battery at {percent}% - time to charge.")
            .Show();
    }
}
