using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NagaBatteryTray.Hid;
using NagaBatteryTray.Monitoring;
using NagaBatteryTray.Settings;
using NagaBatteryTray.Ui.Dashboard;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;
using Xunit;
using Application = System.Windows.Application;

/// <summary>Visual diagnostic, NOT a unit test: renders the real DashboardWindow off-screen with
/// seeded data and writes a PNG, so dashboard layout can be inspected (by a human or an agent)
/// without installing the app and clicking the tray. Gated behind NAGA_UI_PROBE=1 so ordinary
/// `dotnet test` runs never touch it — the WPF-windows-verified-on-the-installed-build convention
/// stands; this is a magnifying glass for iterating on layout, not coverage.
/// Optional: NAGA_UI_PROBE_OUT (PNG path), NAGA_UI_PROBE_THEME (preset name, default Ultraviolet),
/// NAGA_UI_PROBE_STATE (steady | renaming | switching | offline, default steady).</summary>
public class DashboardScreenshotProbe
{
    [Fact]
    public void Render_dashboard_png()
    {
        if (Environment.GetEnvironmentVariable("NAGA_UI_PROBE") != "1") return;
        string outPath = Environment.GetEnvironmentVariable("NAGA_UI_PROBE_OUT")
            ?? Path.Combine(Path.GetTempPath(), "naga-dashboard-probe.png");
        string theme = Environment.GetEnvironmentVariable("NAGA_UI_PROBE_THEME") ?? "Ultraviolet";
        string state = Environment.GetEnvironmentVariable("NAGA_UI_PROBE_STATE") ?? "steady";

        Exception? failure = null;
        var t = new Thread(() => { try { Run(outPath, theme, state); } catch (Exception ex) { failure = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        Assert.True(failure is null, failure?.ToString());
    }

    private static void Run(string outPath, string theme, string state)
    {
        // mirror AppHost.Start's resource setup on a fresh Application (none exists in a test host)
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Dark });
        app.Resources.MergedDictionaries.Add(new ControlsDictionary());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/NagaBatteryTray;component/Ui/Themes/DesignSystem.xaml") });
        ApplicationThemeManager.Apply(ApplicationTheme.Dark);
        // not ThemeManager.Apply: its relative pack URIs resolve against the ENTRY assembly, which
        // here is the test host. Assembly-qualified URI instead; the skipped WPF-UI accent push only
        // tints Fluent chrome, which doesn't matter for layout diagnosis.
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri($"pack://application:,,,/NagaBatteryTray;component/Ui/Themes/{theme}.xaml") });

        var vm = new DashboardViewModel(new AppSettings(), runAtStartup: false,
            (p, k, m, u) => Task.FromResult(true), (p, r) => Task.FromResult(true));

        // seeded online state ≈ the user's real dashboard: 48% wireless, 1600 DPI, slot 1 of 3,
        // grid = F1..F12 (keyboard category 0x02, data [modifiers, usage], F1 = usage 0x3A)
        vm.ApplyState(DeviceState.Online(48, charging: false, wired: false));
        vm.SetCurrentDpi(new DpiSetting(1600, 1600));
        vm.SetProfileInventory(new byte[] { 1, 2, 3 }, active: 1);
        for (int pos = 1; pos <= 12; pos++)
            vm.Callout(pos).SetFromDevice(new RawButtonAction(0x02, new byte[] { 0x00, (byte)(0x39 + pos) }));

        switch (state)
        {
            case "renaming": vm.BeginRename(); vm.ProfileNameDraft = "Work"; break;
            case "switching": vm.SetProfileNote("Switching…"); break;
            case "offline": vm.ApplyState(DeviceState.Unknown); vm.SetProfileInventory(null, null); break;
        }

        var win = new DashboardWindow(vm)
        { Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
        win.Show();
        win.UpdateLayout();
        DoEvents(); // let bindings, triggers, and the render tick settle
        // the chip stagger-in runs ~600 ms of opacity animation — pump frames until it's done,
        // or the capture is a frame-zero snapshot with every chip still at opacity 0
        long until = Environment.TickCount64 + 1200;
        while (Environment.TickCount64 < until) { Thread.Sleep(50); DoEvents(); }

        var rtb = new RenderTargetBitmap((int)win.ActualWidth, (int)win.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(win);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(outPath)) enc.Save(fs);

        win.Close();
        Dispatcher.CurrentDispatcher.InvokeShutdown();
    }

    private static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
