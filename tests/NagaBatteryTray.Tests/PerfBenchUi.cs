using System.Diagnostics;
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

/// <summary>The window half of the perf harness (see PerfBenchIo for the device half), gated the
/// same way (NAGA_PERF=1). It answers "how long until the screen is actually on screen" by timing
/// construction, layout and a forced render pass, and "how does it scroll" by stepping the settings
/// scroller and rendering each step.
/// <para>A forced RenderTargetBitmap pass is not the compositor, so these are not frame times as a
/// profiler would report them. What they are is a faithful measure of the work WPF does walking and
/// rasterizing this visual tree — which is the part the app controls, and the part a change to the
/// tree moves. Treat them as comparable to each other, not as absolute frame budgets.</para></summary>
[Collection("wpf-ui")] // shares the WPF Application slot with the screenshot probe — never in parallel
public sealed class PerfBenchUi
{
    [Fact]
    public void Ui_open_and_scroll()
    {
        if (Environment.GetEnvironmentVariable("NAGA_PERF") != "1") return;

        Exception? failure = null;
        var t = new Thread(() => { try { Run(); } catch (Exception ex) { failure = ex; } }, 16 * 1024 * 1024);
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        Assert.True(failure is null, failure?.ToString());
    }

    private static void Run()
    {
        using var r = PerfReport.Open("ui");
        SetUpResources();

        // Cold vs warm is the distinction that matters here: the first open of a window type pays
        // BAML load and template instantiation for the whole tree, every later open does not. The
        // user feels the cold number once per app run and the warm number every time after.
        r.Write("dashboard_open_cold", OpenDashboard());
        var warm = new List<double>();
        for (int i = 0; i < 3; i++) warm.Add(OpenDashboard());
        r.Write("dashboard_open_warm", Median(warm));

        r.Write("popup_open_cold", OpenPopup());
        warm.Clear();
        for (int i = 0; i < 3; i++) warm.Add(OpenPopup());
        r.Write("popup_open_warm", Median(warm));

        MeasureSettings(r);

        Dispatcher.CurrentDispatcher.InvokeShutdown();
    }

    /// <summary>Mirrors AppHost.Start's resource setup, assembly-qualified for a test host exactly as
    /// DashboardScreenshotProbe does it.</summary>
    private static void SetUpResources()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Dark });
        app.Resources.MergedDictionaries.Add(new ControlsDictionary());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/NagaBatteryTray;component/Ui/Themes/DesignSystem.xaml") });
        ApplicationThemeManager.Apply(ApplicationTheme.Dark);
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/NagaBatteryTray;component/Ui/Themes/Porcelain.xaml") });
    }

    /// <summary>A dashboard seeded the way the user's really looks, timed from "nothing" to "pixels":
    /// view model, window, Show, layout, one render pass. The chip stagger animation is deliberately
    /// NOT waited out — this measures time to first paint, not time to settle.</summary>
    private static double OpenDashboard()
    {
        var sw = Stopwatch.StartNew();
        var vm = SeededVm();
        var win = new DashboardWindow(vm)
        { Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
        win.Show();
        win.UpdateLayout();
        Render(win);
        sw.Stop();
        win.Close();
        DoEvents();
        return sw.Elapsed.TotalMilliseconds;
    }

    private static double OpenPopup()
    {
        var sw = Stopwatch.StartNew();
        var pop = new NagaBatteryTray.Ui.PopupWindow { ShowInTaskbar = false, ShowActivated = false };
        pop.ApplyState(DeviceState.Online(71, charging: false, wired: false, model: "Naga V3 Pro"));
        pop.Show();
        pop.UpdateLayout();
        Render(pop);
        sw.Stop();
        pop.Close();
        DoEvents();
        return sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>The settings drawer: how long it takes to appear, and what a scroll step costs. The
    /// no-mask variant is a diagnostic, not a proposal — the drawer's edge fade is an OpacityMask on
    /// the scrolling viewport, which forces an intermediate surface, and the only way to know whether
    /// that matters here is to price it.</summary>
    private static void MeasureSettings(PerfReport r)
    {
        var vm = SeededVm();
        var win = new DashboardWindow(vm)
        { Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
        win.Show();
        win.UpdateLayout();
        Render(win);

        var opens = new List<double>();
        for (int i = 0; i < 3; i++)
        {
            var sw = Stopwatch.StartNew();
            var view = new SettingsView { DataContext = vm };
            view.SetTrayIconStyle("Gauge");
            win.ShowOverlay(view);
            win.UpdateLayout();
            Render(win);
            sw.Stop();
            opens.Add(sw.Elapsed.TotalMilliseconds);
            if (i < 2) { win.HideOverlay(); DoEvents(); }
        }
        r.Write("settings_overlay_open", Median(opens));

        // the overlay from the last iteration is still up; scroll the live one
        var live = new SettingsView { DataContext = vm };
        live.SetTrayIconStyle("Gauge");
        win.ShowOverlay(live);
        win.UpdateLayout();
        Render(win);

        r.Write("settings_scroll_frame", ScrollFrames(win, live));
        live.Scroller.OpacityMask = null;
        live.Scroller.ScrollToVerticalOffset(0);
        win.UpdateLayout();
        Render(win);
        r.Write("settings_scroll_frame_nomask", ScrollFrames(win, live));

        win.Close();
        DoEvents();
    }

    /// <summary>Average cost of one scroll step: move the viewport a few pixels, lay out, render.
    /// Scrolls down then back up so the measurement is not dominated by one direction's realization.</summary>
    private static double ScrollFrames(Window win, SettingsView view)
    {
        const int Steps = 24;
        double extent = Math.Max(1, view.Scroller.ScrollableHeight);
        double step = extent / (Steps / 2.0);

        view.Scroller.ScrollToVerticalOffset(0);
        win.UpdateLayout();
        Render(win);   // first frame pays realization; not counted below

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < Steps; i++)
        {
            double offset = i < Steps / 2 ? step * i : step * (Steps - i);
            view.Scroller.ScrollToVerticalOffset(offset);
            win.UpdateLayout();
            Render(win);
        }
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / Steps;
    }

    private static DashboardViewModel SeededVm()
    {
        var vm = new DashboardViewModel(new AppSettings(), runAtStartup: false,
            (p, k, m, u) => Task.FromResult(WriteOutcome.Applied), (p, r) => Task.FromResult(WriteOutcome.Applied));
        vm.ApplyState(DeviceState.Online(71, charging: false, wired: false, model: "Naga V3 Pro"));
        vm.SetCurrentDpi(new DpiSetting(1600, 1600));
        vm.SetProfileInventory(new byte[] { 1, 2, 3 }, active: 1);
        vm.SetMice(new[] { "Naga V3 Pro", "Naga V2 Pro" }, "Naga V3 Pro");
        for (int pos = 1; pos <= NagaV2ProButtons.Count; pos++)
            vm.Callout(pos).SetFromDevice(new RawButtonAction(0x02, new byte[] { 0x00, (byte)(0x39 + pos) }));
        return vm;
    }

    private static void Render(Window win)
    {
        int w = (int)Math.Max(1, win.ActualWidth), h = (int)Math.Max(1, win.ActualHeight);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(win);
    }

    private static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static double Median(List<double> xs)
    {
        var s = new List<double>(xs);
        s.Sort();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }
}
