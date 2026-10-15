using NagaBatteryTray.Hid;
using NagaBatteryTray.Monitoring;
using NagaBatteryTray.Settings;
using NagaBatteryTray.Ui.Dashboard;
using Xunit;

/// <summary>The settings overlay's "Preferred mouse" row (spec 2026-10-15). The header selector
/// cannot clear a preference naming a mouse that is gone — it is disabled whenever one mouse is
/// reachable, which is exactly that case — so the value has to be legible and clearable here.</summary>
public sealed class MousePreferenceTests
{
    private static DashboardViewModel Build(string? preferred = null) =>
        new(new AppSettings { PreferredMouse = preferred }, false,
            (_, _, _, _) => Task.FromResult(WriteOutcome.Applied),
            (_, _) => Task.FromResult(WriteOutcome.Applied));

    [Fact]
    public void NoPreferenceReadsAsAutomaticWithNothingToClear()
    {
        var vm = Build();

        Assert.Null(vm.PreferredMouse);
        Assert.Equal("Automatic", vm.PreferredMouseLabel);
        Assert.False(vm.HasMousePreference);   // the button has nothing to do
        Assert.Equal("", vm.MousePreferenceNote);
    }

    [Fact]
    public void AStoredPreferenceIsVisibleAndClearable()
    {
        var vm = Build("Naga V2 Pro");

        Assert.Equal("Naga V2 Pro", vm.PreferredMouse);
        Assert.Equal("Naga V2 Pro", vm.PreferredMouseLabel);
        Assert.True(vm.HasMousePreference);
    }

    [Fact]
    public void TheNoteNamesTheMouseTheAppIsActuallyUsing()
    {
        var vm = Build("Naga V2 Pro");

        vm.ApplyState(DeviceState.Online(60, false, false, "Naga V3 Pro"));

        Assert.Equal("Not connected — the app is using Naga V3 Pro.", vm.MousePreferenceNote);
    }

    [Fact]
    public void ThePreferredMouseBeingTheConnectedOneSaysNothing()
    {
        var vm = Build("Naga V3 Pro");

        vm.ApplyState(DeviceState.Online(60, false, false, "Naga V3 Pro"));

        Assert.Equal("", vm.MousePreferenceNote);
    }

    /// <summary>Offline, ModelName falls back to the generic "Razer Naga", which names no real
    /// mouse. The note must not report that as the mouse in use.</summary>
    [Fact]
    public void WithNothingConnectedTheNoteNamesNoMouse()
    {
        var vm = Build("Naga V2 Pro");

        vm.ApplyState(DeviceState.Unknown);

        Assert.Equal("Not connected.", vm.MousePreferenceNote);
    }

    [Fact]
    public void NoPreferenceNeverShowsANote()
    {
        var vm = Build();

        vm.ApplyState(DeviceState.Online(60, false, false, "Naga V3 Pro"));

        Assert.Equal("", vm.MousePreferenceNote);
    }

    /// <summary>The note is a statement about the connection, not only about the setting: a hop
    /// changes it with the preference untouched.</summary>
    [Fact]
    public void AHopToAnotherMouseRecomputesTheNote()
    {
        var vm = Build("Naga V2 Pro");
        vm.ApplyState(DeviceState.Online(60, false, false, "Naga V2 Pro"));
        Assert.Equal("", vm.MousePreferenceNote);

        var seen = new List<string>();
        vm.PropertyChanged += (_, e) => seen.Add(e.PropertyName!);
        vm.ApplyState(DeviceState.Online(60, false, false, "Naga V3 Pro"));

        Assert.Equal("Not connected — the app is using Naga V3 Pro.", vm.MousePreferenceNote);
        Assert.Contains(nameof(DashboardViewModel.MousePreferenceNote), seen);
    }

    [Fact]
    public void SettingThePreferenceNotifiesEverythingTheRowBindsTo()
    {
        var vm = Build();
        vm.ApplyState(DeviceState.Online(60, false, false, "Naga V3 Pro"));
        var seen = new List<string>();
        vm.PropertyChanged += (_, e) => seen.Add(e.PropertyName!);

        vm.SetPreferredMouse("Naga V2 Pro");

        Assert.Contains(nameof(DashboardViewModel.PreferredMouseLabel), seen);
        Assert.Contains(nameof(DashboardViewModel.HasMousePreference), seen);
        Assert.Contains(nameof(DashboardViewModel.MousePreferenceNote), seen);
    }

    [Fact]
    public void ClearingReturnsTheRowToAutomatic()
    {
        var vm = Build("Naga V2 Pro");
        vm.ApplyState(DeviceState.Online(60, false, false, "Naga V3 Pro"));

        vm.SetPreferredMouse(null);

        Assert.Equal("Automatic", vm.PreferredMouseLabel);
        Assert.False(vm.HasMousePreference);
        Assert.Equal("", vm.MousePreferenceNote);
    }

    [Fact]
    public void TheButtonAsksTheHostToClear()
    {
        var vm = Build("Naga V2 Pro");
        int asked = 0;
        vm.ClearMousePreferenceRequested += () => asked++;

        vm.RequestClearMousePreference();

        Assert.Equal(1, asked);
    }

    /// <summary>A failed header pick persists the name it could not reach (AppHost.SelectMouseAsync
    /// writes before it tries), which is one way the stuck value is created. The row must show that
    /// name, or the user cannot tell why the app keeps looking for a mouse that is not there.</summary>
    [Fact]
    public void APreferenceSetForAnUnreachableMouseStillShowsInTheRow()
    {
        var vm = Build();
        vm.ApplyState(DeviceState.Online(60, false, false, "Naga V3 Pro"));

        vm.SetPreferredMouse("Naga V2 Pro");   // what the host does on a pick, reachable or not

        Assert.True(vm.HasMousePreference);
        Assert.Equal("Naga V2 Pro", vm.PreferredMouseLabel);
        Assert.Equal("Not connected — the app is using Naga V3 Pro.", vm.MousePreferenceNote);
    }
}
