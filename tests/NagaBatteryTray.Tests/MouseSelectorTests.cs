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

        vm.ApplyState(DeviceState.Online(60, false, false, "Naga V2 Pro"));

        Assert.Equal("Naga V2 Pro", vm.SelectedMouse);   // never claims a mouse it is not on
        // no "did not raise a pick" assertion here: ApplyState assigns ModelName BEFORE it syncs the
        // selection, so the setter's own on-this-mouse rule suppresses the event whether or not the
        // _syncingMouse guard exists — the assertion could not go red. ProgrammaticSelectionDoesNot-
        // RaiseARequest is where that guard is actually the deciding factor.
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
        // ApplyState is what makes "Naga V3 Pro" the mouse the app is ON (ModelName + online);
        // without it the setter's rule has nothing to compare against
        vm.ApplyState(DeviceState.Online(80, false, false, "Naga V3 Pro"));
        vm.SetMice(new[] { "Naga V3 Pro", "Naga V2 Pro" }, "Naga V3 Pro");
        int calls = 0;
        vm.MouseSelectRequested += _ => calls++;

        vm.SelectedMouse = "Naga V2 Pro";   // away — a real pick, and it moves the selection
        vm.SelectedMouse = "Naga V3 Pro";   // back onto the connected mouse: the value DOES change,
                                            // so only the on-this-mouse rule can suppress this one

        Assert.Equal(1, calls);
    }

    [Fact]
    public void AnOfflineRepickOfTheDisplayedMouseIsHonoured()
    {
        var vm = Build();
        vm.ApplyState(DeviceState.Online(80, false, false, "Naga V3 Pro"));
        vm.SetMice(new[] { "Naga V3 Pro", "Naga V2 Pro" }, "Naga V3 Pro");
        vm.ApplyState(DeviceState.Unknown);   // link dropped: the box still names the V3
        string? asked = null;
        vm.MouseSelectRequested += m => asked = m;

        vm.SelectedMouse = "Naga V2 Pro";
        vm.SelectedMouse = "Naga V3 Pro";   // the user retrying the mouse the header shows them

        Assert.Equal("Naga V3 Pro", asked); // offline, that is a retry — never a silent no-op
    }

    [Fact]
    public void AProbeWithNoConnectedMouseSelectsNothing()
    {
        var vm = Build();

        vm.SetMice(new[] { "Naga V3 Pro", "Naga V2 Pro" }, null);

        Assert.Null(vm.SelectedMouse);   // on nothing — the box must not claim a mouse
    }

    [Fact]
    public async Task ResetForNewMouseDropsProfilesAndChipSnapshots()
    {
        var vm = Build();
        vm.SetProfileInventory(new byte[] { 1, 2, 3 }, 3);
        var chip = vm.Callout(1);
        // arm a REAL undo window on the old mouse: the sweep read gives the chip its raw snapshot,
        // and the verified write that follows is what sets _prevRaw and opens the window
        chip.SetFromDevice(new RawButtonAction(RazerProtocol.FnKeyboard, new byte[] { 0x00, 0x3a }));
        await chip.CaptureAsync(0, 0x3b);
        Assert.True(chip.CanUndo);        // the arrangement itself must hold, or the test proves nothing

        vm.ResetForNewMouse();

        Assert.Empty(vm.ProfileSlots);
        Assert.Equal("…", chip.BindingText);
        Assert.False(vm.DevicePresent);   // the old mouse's DPI is gone, not left on screen
        // hard rule 2's other half: the old mouse's raw bytes must never be restorable onto the new
        // one, so the window is closed AND ↶ has nothing left to write
        Assert.False(chip.CanUndo);
        await chip.UndoAsync();
        Assert.Equal("…", chip.BindingText);
    }

    [Fact]
    public void ResetForNewMouseCancelsAnInFlightRename()
    {
        var vm = Build();
        vm.SetProfileInventory(new byte[] { 1 }, 1);
        vm.BeginRename();
        vm.ProfileNameDraft = "Raid";

        vm.ResetForNewMouse();

        // the draft named the OLD mouse's slot; it must not survive to land on the new mouse's
        Assert.False(vm.IsRenamingProfile);
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
