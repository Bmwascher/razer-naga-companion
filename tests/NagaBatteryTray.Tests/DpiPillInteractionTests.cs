using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using NagaBatteryTray.Hid;
using NagaBatteryTray.Monitoring;
using NagaBatteryTray.Settings;
using NagaBatteryTray.Ui.Dashboard;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;
using Xunit;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;

/// <summary>Regression for the preset-✕ crash (2026-07-20): the ✕ sits INSIDE the pill Button,
/// so its Click bubbles on into the pill's own Click (= apply). Removal has already discarded
/// the pill's container by then, its DataContext is WPF's {DisconnectedItem} sentinel, and the
/// hard cast in OnApplyPreset killed the process (InvalidCastException, WER APPCRASH). Drives
/// the real routed event through a real DashboardWindow — same recipe as the screenshot probe,
/// and same xunit collection so the two never share a WPF Application concurrently.</summary>
[Collection("wpf-ui")]
public class DpiPillInteractionTests
{
    [Fact]
    public void Removing_a_preset_via_its_x_removes_it_without_applying_or_crashing()
    {
        Exception? failure = null;
        var t = new Thread(() => { try { Run(); } catch (Exception ex) { failure = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        Assert.True(failure is null, failure?.ToString());
    }

    private static void Run()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Dark });
        app.Resources.MergedDictionaries.Add(new ControlsDictionary());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/NagaBatteryTray;component/Ui/Themes/DesignSystem.xaml") });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/NagaBatteryTray;component/Ui/Themes/Porcelain.xaml") });

        var vm = new DashboardViewModel(new AppSettings(), runAtStartup: false,
            (p, k, m, u) => Task.FromResult(true), (p, r) => Task.FromResult(true));
        vm.ApplyState(DeviceState.Online(48, charging: false, wired: false));
        vm.SetCurrentDpi(new DpiSetting(1600, 1600));
        vm.AddPreset(1100); // the user's scenario: a freshly saved preset, then its ✕

        var win = new DashboardWindow(vm)
        { Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            win.Show();
            win.UpdateLayout();
            DoEvents(); // realize the preset ItemsControl's containers

            int applied = 0;
            foreach (var stage in Descendants<MouseStageView>(win)) stage.ApplyDpiRequested += _ => applied++;

            var x = Descendants<Button>(win).Single(b =>
                b.Name == "RemovePill" && b.DataContext is DpiPresetItem { Value: 1100 });
            x.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); // the real bubbling route
            DoEvents();

            Assert.DoesNotContain(vm.Presets, p => p.Value == 1100); // removed...
            Assert.Equal(0, applied);                                // ...without doubling as an apply
            Assert.Equal(1600, vm.Dpi);
        }
        finally
        {
            win.Close();
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) yield return hit;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    private static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
