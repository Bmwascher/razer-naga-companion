using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using System.Reflection;
using NagaBatteryTray.Hid;
using NagaBatteryTray.Settings;
using NagaBatteryTray.Ui.Dashboard;
using Wpf.Ui.Controls;
using Xunit;

[Collection("wpf-ui")]
public sealed class SettingsViewTests
{
    [Fact]
    public void Binding_startup_state_is_silent_while_a_user_click_is_forwarded()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { RunStartupToggleScenario(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.True(failure is null, failure?.ToString());
    }

    private static void RunStartupToggleScenario()
    {
        var vm = new DashboardViewModel(new AppSettings(), runAtStartup: true,
            (p, k, m, u) => Task.FromResult(WriteOutcome.Applied),
            (p, r) => Task.FromResult(WriteOutcome.Applied));
        var view = new SettingsView { DataContext = vm };
        int toggled = 0;
        bool? requested = null;
        view.StartupToggled += value => { toggled++; requested = value; };

        var window = new Window
        {
            Content = view,
            Left = -20000,
            Top = -20000,
            ShowInTaskbar = false,
            ShowActivated = false,
            Width = 360,
            Height = 700,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            DoEvents();

            Assert.Equal(0, toggled);

            var startupToggle = Descendants<ToggleSwitch>(view).Single(control =>
                control.GetBindingExpression(ToggleButton.IsCheckedProperty)?.ParentBinding.Path.Path
                    == nameof(DashboardViewModel.RunAtStartup));
            startupToggle.IsChecked = false; // later programmatic synchronization is also silent
            DoEvents();
            Assert.Equal(0, toggled);

            startupToggle.IsChecked = true;
            DoEvents();
            Assert.Equal(0, toggled);

            typeof(ToggleButton).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(startupToggle, null);
            DoEvents();

            Assert.False(startupToggle.IsChecked);
            Assert.False(vm.RunAtStartup);
            Assert.Equal(1, toggled);
            Assert.False(requested);
        }
        finally
        {
            window.Close();
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
