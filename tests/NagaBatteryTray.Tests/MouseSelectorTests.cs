using NagaBatteryTray.Hid;
using NagaBatteryTray.Monitoring;
using NagaBatteryTray.Settings;
using NagaBatteryTray.Ui.Dashboard;
using Xunit;

public sealed class MouseSelectorTests
{
    private static DashboardViewModel Build(AppSettings? settings = null) =>
        new(settings ?? new AppSettings(), false,
            (_, _, _, _) => Task.FromResult(true),
            (_, _) => Task.FromResult(true));

    [Fact]
    public void SetMiceFillsTheListAndSelectsTheConnectedMouse()
    {
        var vm = Build();
        vm.SetMice(new[] { "Naga V3 Pro", "Naga V2 Pro" }, "Naga V2 Pro");

        Assert.Equal(new[] { "Naga V3 Pro", "Naga V2 Pro" }, vm.Mice);
        Assert.Equal("Naga V2 Pro", vm.SelectedMouse);
        Assert.True(vm.MiceLoaded);
    }

    [Fact]
    public void TheHeaderShowsTheConnectedMouseBeforeTheProbeLands()
    {
        var vm = Build();
        vm.ApplyState(DeviceState.Online(80, false, false, "Naga V3 Pro"));

        Assert.Equal(new[] { "Naga V3 Pro" }, vm.Mice);
        Assert.Equal("Naga V3 Pro", vm.SelectedMouse);
        Assert.False(vm.MiceLoaded);   // still disabled: nothing has been probed yet
    }

    [Fact]
    public void TheHeaderFollowsAnUnannouncedHopToAnotherMouse()
    {
        var vm = Build();
        vm.SetMice(new[] { "Naga V3 Pro", "Naga V2 Pro" }, "Naga V3 Pro");
        int picks = 0;
        vm.MouseSelectRequested += _ => picks++;

        vm.ApplyState(DeviceState.Online(60, false, false, "Naga V2 Pro"));

        Assert.Equal("Naga V2 Pro", vm.SelectedMouse);   // never claims a mouse it is not on
        Assert.Equal(0, picks);                          // and does not read as a user pick
    }

    [Fact]
    public void AProbeThatFindsNothingStillShowsTheConnectedMouse()
    {
        var vm = Build();
        vm.ApplyState(DeviceState.Online(60, false, false, "Naga V3 Pro"));

        vm.SetMice(Array.Empty<string>(), "Naga V3 Pro");

        Assert.Equal(new[] { "Naga V3 Pro" }, vm.Mice);
        Assert.False(vm.MiceLoaded);
    }

    [Fact]
    public void ProgrammaticSelectionDoesNotRaiseARequest()
    {
        var vm = Build();
        string? asked = null;
        vm.MouseSelectRequested += m => asked = m;

        vm.SetMice(new[] { "Naga V3 Pro" }, "Naga V3 Pro");

        Assert.Null(asked);
    }

    [Fact]
    public void AUserPickRaisesARequest()
    {
        var vm = Build();
        vm.SetMice(new[] { "Naga V3 Pro", "Naga V2 Pro" }, "Naga V3 Pro");
        string? asked = null;
        vm.MouseSelectRequested += m => asked = m;

        vm.SelectedMouse = "Naga V2 Pro";

        Assert.Equal("Naga V2 Pro", asked);
    }

    [Fact]
    public void RepickingTheConnectedMouseIsANoOp()
    {
        var vm = Build();
        vm.SetMice(new[] { "Naga V3 Pro", "Naga V2 Pro" }, "Naga V3 Pro");
        int calls = 0;
        vm.MouseSelectRequested += _ => calls++;

        vm.SelectedMouse = "Naga V3 Pro";

        Assert.Equal(0, calls);
    }

    [Fact]
    public void ResetForNewMouseDropsProfilesAndChipSnapshots()
    {
        var vm = Build();
        vm.SetProfileInventory(new byte[] { 1, 2, 3 }, 3);
        vm.Callout(1).SetFromDevice(new RawButtonAction(RazerProtocol.FnKeyboard, new byte[] { 0x00, 0x3a }));

        vm.ResetForNewMouse();

        Assert.Empty(vm.ProfileSlots);
        Assert.Equal("…", vm.Callout(1).BindingText);
        Assert.False(vm.DevicePresent);   // the old mouse's DPI is gone, not left on screen
    }

    [Fact]
    public void ProfileNamesAreScopedToTheConnectedMouse()
    {
        var settings = new AppSettings();
        settings.ProfileNamesByMouse["Naga V2 Pro"] = new Dictionary<byte, string> { [1] = "World of Warcraft" };
        var vm = Build(settings);

        vm.ApplyState(DeviceState.Online(80, false, false, "Naga V3 Pro"));
        vm.SetProfileInventory(new byte[] { 1 }, 1);

        Assert.Equal("Slot 1", vm.ProfileSlots[0].Name);
    }

    [Fact]
    public void ProfileNamesComeBackForTheMouseThatOwnsThem()
    {
        var settings = new AppSettings();
        settings.ProfileNamesByMouse["Naga V2 Pro"] = new Dictionary<byte, string> { [1] = "World of Warcraft" };
        var vm = Build(settings);

        vm.ApplyState(DeviceState.Online(80, false, false, "Naga V2 Pro"));
        vm.SetProfileInventory(new byte[] { 1 }, 1);

        Assert.Equal("World of Warcraft", vm.ProfileSlots[0].Name);
    }

    [Fact]
    public void ApplyToWritesNamesUnderTheConnectedMouse()
    {
        var vm = Build();
        vm.ApplyState(DeviceState.Online(80, false, false, "Naga V3 Pro"));
        vm.SetProfileInventory(new byte[] { 1 }, 1);
        vm.BeginRename();
        vm.ProfileNameDraft = "Raid";
        vm.CommitRename();

        var target = new AppSettings();
        vm.ApplyTo(target);

        Assert.Equal("Raid", target.ProfileNamesByMouse["Naga V3 Pro"][1]);
    }
}
