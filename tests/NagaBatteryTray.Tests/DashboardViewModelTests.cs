using NagaBatteryTray.Hid;
using NagaBatteryTray.Monitoring;
using NagaBatteryTray.Settings;
using NagaBatteryTray.Ui.Dashboard;
using Xunit;

public class DashboardViewModelTests
{
    private static Task<bool> NoWrite(int p, ButtonActionKind k, byte m, byte u) => Task.FromResult(true);
    private static Task<bool> NoRawWrite(int p, RawButtonAction r) => Task.FromResult(true);

    private static DashboardViewModel NewVm(AppSettings? source = null) =>
        new(source ?? new AppSettings(), runAtStartup: false, NoWrite, NoRawWrite);

    private static DashboardViewModel NewVm(CalloutViewModel.WriteBinding write) =>
        new(new AppSettings(), runAtStartup: false, write, NoRawWrite);

    [Fact]
    public void Callouts_start_on_the_factory_display()
    {
        var vm = NewVm();
        Assert.Equal(12, vm.Callouts.Count);
        Assert.Equal("1", vm.Callout(1).BindingText); // factory digits until the first sweep lands
        Assert.Equal("2", vm.Callout(2).BindingText);
    }

    [Fact]
    public void AnyCapturing_tracks_callout_capture_state()
    {
        var vm = NewVm();
        Assert.False(vm.AnyCapturing);

        vm.Callout(3).BeginCapture();
        Assert.True(vm.AnyCapturing);

        vm.Callout(5).BeginCapture();
        vm.Callout(3).CancelCapture();
        Assert.True(vm.AnyCapturing); // one still armed

        vm.Callout(5).CancelCapture();
        Assert.False(vm.AnyCapturing);
    }

    [Fact]
    public void Preset_checkmark_follows_current_dpi()
    {
        var vm = NewVm();
        vm.SetCurrentDpi(new DpiSetting(1600, 1600));
        Assert.True(vm.Presets.Single(p => p.Value == 1600).IsActive);
        Assert.False(vm.Presets.Single(p => p.Value == 800).IsActive);
        vm.Dpi = 800; // slider move re-evaluates
        Assert.True(vm.Presets.Single(p => p.Value == 800).IsActive);
    }

    [Fact]
    public void CanSavePreset_tracks_current_dpi_against_the_preset_list()
    {
        var vm = NewVm();
        Assert.False(vm.CanSavePreset);            // no device yet

        vm.SetCurrentDpi(new DpiSetting(1600, 1600));
        Assert.False(vm.CanSavePreset);            // 1600 is already a preset

        vm.Dpi = 1000;
        Assert.True(vm.CanSavePreset);

        vm.AddPreset(1000);
        Assert.False(vm.CanSavePreset);

        vm.RemovePreset(vm.Presets.Single(p => p.Value == 1000));
        Assert.True(vm.CanSavePreset);
    }

    [Fact]
    public void AddPreset_sorts_dedupes_and_RemovePreset_removes()
    {
        var vm = NewVm();
        vm.AddPreset(1200);
        vm.AddPreset(1200); // dupe ignored
        Assert.Equal(new[] { 800, 1200, 1600, 3200 }, vm.Presets.Select(p => p.Value));
        vm.RemovePreset(vm.Presets.Single(p => p.Value == 3200));
        Assert.Equal(new[] { 800, 1200, 1600 }, vm.Presets.Select(p => p.Value));
    }

    // ---- profile card: dropdown inventory + selection (spec §13.1/13.2) ----

    [Fact]
    public void SetProfileInventory_builds_the_inventory_ascending_and_selects_the_active_slot()
    {
        var vm = NewVm();
        vm.SetProfileInventory(new byte[] { 3, 1, 2 }, active: 2);

        Assert.Equal(new byte[] { 1, 2, 3 }, vm.ProfileSlots.Select(p => p.Number));
        Assert.True(vm.ProfileSlots.Single(p => p.Number == 2).IsActive);
        Assert.False(vm.ProfileSlots.Single(p => p.Number == 1).IsActive);
        Assert.Equal((byte)2, vm.SelectedProfileSlot?.Number);
        Assert.Equal("", vm.ProfileDetail); // steady state: no status text (v2.3)
    }

    [Fact]
    public void ProfileSlotItem_name_defaults_to_slot_number_and_colour_names_the_led()
    {
        Assert.Equal("Slot 2", new ProfileSlotItem(2).Name);
        Assert.Equal("red", new ProfileSlotItem(2).Colour);
        Assert.Equal("green", new ProfileSlotItem(3).Colour);
    }

    [Fact]
    public void Slot_names_seed_from_settings_and_default_when_absent()
    {
        var src = new AppSettings();
        src.ProfileNames[2] = "Work";
        var vm = NewVm(src);
        vm.SetProfileInventory(new byte[] { 1, 2, 3 }, active: 1);

        Assert.Equal("Slot 1", vm.ProfileSlots.Single(p => p.Number == 1).Name);
        Assert.Equal("Work", vm.ProfileSlots.Single(p => p.Number == 2).Name);
        Assert.Equal("Slot 3", vm.ProfileSlots.Single(p => p.Number == 3).Name);
    }

    [Fact]
    public void SetProfileInventory_unknown_active_keeps_items_and_shows_unknown_detail()
    {
        var vm = NewVm();
        vm.SetProfileInventory(new byte[] { 1, 2, 3 }, active: 2); // establish items first
        vm.SetProfileInventory(null, active: null);                // mouse went unreachable

        Assert.Equal(3, vm.ProfileSlots.Count);                    // last-known items kept, not cleared
        Assert.DoesNotContain(vm.ProfileSlots, p => p.IsActive);    // none active — state is unknown
        Assert.Null(vm.SelectedProfileSlot);                        // the dropdown doesn't guess either
        Assert.Contains("unknown", vm.ProfileDetail);
    }

    [Fact]
    public void SetProfileInventory_list_read_fails_but_active_known_keeps_items_and_marks_active()
    {
        var vm = NewVm();
        vm.SetProfileInventory(new byte[] { 1, 2, 3 }, active: 2); // establish items first
        vm.SetProfileInventory(null, active: 3);                  // list read failed, active read succeeded

        Assert.Equal(3, vm.ProfileSlots.Count);                            // prior items kept
        Assert.True(vm.ProfileSlots.Single(p => p.Number == 3).IsActive);
        Assert.False(vm.ProfileSlots.Single(p => p.Number == 2).IsActive);
        Assert.Equal((byte)3, vm.SelectedProfileSlot?.Number);             // selection follows the mouse
        Assert.Equal("", vm.ProfileDetail);                                // normal state, not "unknown"
    }

    [Fact]
    public void Inventory_read_syncs_the_dropdown_selection_without_raising_SwitchRequested()
    {
        var vm = NewVm();
        byte? requested = null;
        vm.SwitchRequested += s => requested = s;

        vm.SetProfileInventory(new byte[] { 1, 2, 3 }, active: 2);

        Assert.Equal((byte)2, vm.SelectedProfileSlot?.Number);
        Assert.Null(requested);                                   // programmatic sync, not a user pick
    }

    [Fact]
    public void User_pick_of_another_slot_raises_SwitchRequested_once()
    {
        var vm = NewVm();
        var requests = new List<byte>();
        vm.SwitchRequested += requests.Add;
        vm.SetProfileInventory(new byte[] { 1, 2, 3 }, active: 2);

        vm.SelectedProfileSlot = vm.ProfileSlots.Single(p => p.Number == 3); // user picks in the ComboBox

        Assert.Equal(new byte[] { 3 }, requests);
    }

    [Fact]
    public void Picking_the_already_active_slot_is_a_noop()
    {
        var vm = NewVm();
        var requests = new List<byte>();
        vm.SwitchRequested += requests.Add;
        vm.SetProfileInventory(new byte[] { 1, 2, 3 }, active: 2);

        vm.SelectedProfileSlot = null;                                       // e.g. transient ComboBox clear
        vm.SelectedProfileSlot = vm.ProfileSlots.Single(p => p.Number == 2); // back to the active slot

        Assert.Empty(requests);
    }

    [Fact]
    public void ResyncSelection_snaps_the_dropdown_back_after_a_failed_switch()
    {
        var vm = NewVm();
        var requests = new List<byte>();
        vm.SwitchRequested += requests.Add;
        vm.SetProfileInventory(new byte[] { 1, 2, 3 }, active: 2);
        vm.SelectedProfileSlot = vm.ProfileSlots.Single(p => p.Number == 3); // pick fails downstream

        vm.ResyncSelection();

        Assert.Equal((byte)2, vm.SelectedProfileSlot?.Number);    // dropdown never lies about the mouse
        Assert.Equal(new byte[] { 3 }, requests);                 // the resync raised nothing new
    }

    [Fact]
    public void SetProfileNote_overwrites_the_detail_line()
    {
        var vm = NewVm();
        vm.SetProfileInventory(new byte[] { 1, 2, 3 }, active: 3);
        vm.SetProfileNote("Couldn't switch — wiggle the mouse and retry");
        Assert.Contains("wiggle", vm.ProfileDetail);
        Assert.Equal((byte)3, vm.SelectedProfileSlot?.Number); // selection untouched by a note
    }

    // ---- profile card: rename (dashboard-polish spec §5.4) ----

    [Fact]
    public void Rename_commit_updates_the_item_and_round_trips_via_ApplyTo()
    {
        var vm = NewVm();
        vm.SetProfileInventory(new byte[] { 1, 2, 3 }, active: 2);

        vm.BeginRename();
        Assert.True(vm.IsRenamingProfile);
        Assert.Equal("Slot 2", vm.ProfileNameDraft); // draft seeds from the current name
        vm.ProfileNameDraft = "Work";
        vm.CommitRename();

        Assert.False(vm.IsRenamingProfile);
        Assert.Equal("Work", vm.ProfileSlots.Single(p => p.Number == 2).Name);
        var target = new AppSettings();
        vm.ApplyTo(target);
        Assert.Equal("Work", target.ProfileNames[2]);
    }

    [Fact]
    public void Rename_trims_and_clamps_to_24_chars()
    {
        var vm = NewVm();
        vm.SetProfileInventory(new byte[] { 1 }, active: 1);
        vm.BeginRename();
        vm.ProfileNameDraft = "  " + new string('x', 40) + "  ";
        vm.CommitRename();
        Assert.Equal(new string('x', 24), vm.ProfileSlots.Single().Name);
    }

    [Fact]
    public void Rename_to_empty_or_the_default_resets_and_drops_the_map_entry()
    {
        var vm = NewVm();
        vm.SetProfileInventory(new byte[] { 1, 2 }, active: 2);
        vm.BeginRename(); vm.ProfileNameDraft = "Work"; vm.CommitRename();

        vm.BeginRename(); vm.ProfileNameDraft = "   "; vm.CommitRename();
        Assert.Equal("Slot 2", vm.ProfileSlots.Single(p => p.Number == 2).Name);
        var target = new AppSettings();
        vm.ApplyTo(target);
        Assert.Empty(target.ProfileNames);

        vm.BeginRename(); vm.ProfileNameDraft = "Work"; vm.CommitRename();
        vm.BeginRename(); vm.ProfileNameDraft = "Slot 2"; vm.CommitRename(); // typing the default = reset
        vm.ApplyTo(target);
        Assert.Empty(target.ProfileNames);
    }

    [Fact]
    public void Rename_cancel_discards_the_draft()
    {
        var vm = NewVm();
        vm.SetProfileInventory(new byte[] { 1 }, active: 1);
        vm.BeginRename();
        vm.ProfileNameDraft = "Nope";
        vm.CancelRename();
        Assert.False(vm.IsRenamingProfile);
        Assert.Equal("Slot 1", vm.ProfileSlots.Single().Name);
    }

    [Fact]
    public void BeginRename_without_a_selection_is_a_noop()
    {
        var vm = NewVm();
        vm.BeginRename(); // no inventory yet — nothing selected
        Assert.False(vm.IsRenamingProfile);
    }

    [Fact]
    public void Inventory_sync_mid_rename_cancels_the_edit()
    {
        var vm = NewVm();
        vm.SetProfileInventory(new byte[] { 1, 2 }, active: 1);
        vm.BeginRename();
        vm.ProfileNameDraft = "Drafted";

        vm.SetProfileInventory(new byte[] { 1, 2 }, active: 2); // mouse moved under the edit

        Assert.False(vm.IsRenamingProfile); // draft can't land on a different slot
        Assert.Equal("Slot 1", vm.ProfileSlots.Single(p => p.Number == 1).Name);
    }

    // ---- profile card: LED caption (dashboard-polish spec §5.5) ----

    [Fact]
    public void ShowLedCaption_true_only_with_a_selection_and_no_note()
    {
        var vm = NewVm();
        Assert.False(vm.ShowLedCaption);                            // nothing selected yet

        vm.SetProfileInventory(new byte[] { 1, 2 }, active: 1);
        Assert.True(vm.ShowLedCaption);                             // steady state

        vm.SetProfileNote("Switching…");
        Assert.False(vm.ShowLedCaption);                            // a note takes the line

        vm.SetProfileInventory(new byte[] { 1, 2 }, active: 2);     // refresh clears the note
        Assert.True(vm.ShowLedCaption);

        vm.SetProfileInventory(null, active: null);                 // unreachable → note + no selection
        Assert.False(vm.ShowLedCaption);
    }

    // ---- DPI status line (dashboard-polish spec §4.2) ----

    [Fact]
    public void DpiStatus_sets_and_clears()
    {
        var vm = NewVm();
        Assert.Equal("", vm.DpiStatus);
        vm.SetDpiStatus("Couldn't confirm — wiggle the mouse and retry");
        Assert.Contains("wiggle", vm.DpiStatus);
        vm.SetDpiStatus("");
        Assert.Equal("", vm.DpiStatus);
    }

    // ---- settings / state ----

    [Fact]
    public void ApplyTo_clamps_cadences_and_threshold()
    {
        var vm = NewVm();
        vm.PollSeconds = 3; vm.PollChargingSeconds = 1; vm.LowBatteryThreshold = 0;
        var target = new AppSettings();
        vm.ApplyTo(target);
        Assert.Equal(15, target.PollIntervalSeconds);
        Assert.Equal(15, target.PollIntervalChargingSeconds);
        Assert.Equal(1, target.LowBatteryThreshold);
    }

    [Fact]
    public void ApplyState_maps_online_and_offline()
    {
        var vm = NewVm();
        vm.ApplyState(DeviceState.Online(87, true, false));
        Assert.True(vm.DeviceOnline);
        Assert.Contains("87", vm.BatteryChipText);
        vm.ApplyState(DeviceState.Unknown);
        Assert.False(vm.DeviceOnline);
    }

    [Fact]
    public void Header_subtitle_is_link_state_only_the_profile_card_owns_slot_identity()
    {
        var vm = NewVm();
        vm.ApplyState(DeviceState.Online(87, false, false));
        Assert.Equal("Wireless", vm.HeaderSubtitle);
        vm.ApplyState(DeviceState.Online(87, false, true));
        Assert.Equal("Wired", vm.HeaderSubtitle);
        vm.ApplyState(DeviceState.Unknown);
        Assert.Equal("offline", vm.HeaderSubtitle);
    }

    [Fact]
    public async Task ResetAllAsync_counts_all_12_as_ok_when_every_write_succeeds()
    {
        var vm = NewVm();
        var (ok, failed) = await vm.ResetAllAsync();
        Assert.Equal(12, ok);
        Assert.Equal(0, failed);
    }

    [Fact]
    public async Task ResetAllAsync_counts_failures_for_positions_whose_write_fails()
    {
        Task<bool> FailTwo(int p, ButtonActionKind k, byte m, byte u) => Task.FromResult(p != 3 && p != 7);
        var vm = NewVm(FailTwo);
        var (ok, failed) = await vm.ResetAllAsync();
        Assert.Equal(10, ok);
        Assert.Equal(2, failed);
    }

    [Fact]
    public async Task ResetAllAsync_counts_a_busy_chip_as_failed_not_reset()
    {
        var pending = new TaskCompletionSource<bool>();
        bool first = true;
        Task<bool> Write(int p, ButtonActionKind k, byte m, byte u)
        {
            if (first) { first = false; return pending.Task; }
            return Task.FromResult(true);
        }
        var vm = NewVm(Write);
        var inFlight = vm.Callout(3).DisableAsync(); // occupies chip 3

        var (ok, failed) = await vm.ResetAllAsync();
        Assert.Equal(11, ok);
        Assert.Equal(1, failed);

        pending.SetResult(true);
        await inFlight;
    }

    [Fact]
    public void ApplyTo_preserves_an_untouched_sub15_cadence_but_clamps_a_user_change()
    {
        var src = new AppSettings { PollIntervalSeconds = 10 }; // documented hand-edit bypass
        var vm = NewVm(src);
        var target = new AppSettings();
        vm.ApplyTo(target);
        Assert.Equal(10, target.PollIntervalSeconds);           // untouched → passes through

        vm.PollSeconds = 3;
        vm.ApplyTo(target);
        Assert.Equal(15, target.PollIntervalSeconds);           // user change → floor applies
    }

    [Fact]
    public void DpiSliderPos_endpoints_map_to_dpi_range()
    {
        var vm = NewVm();
        vm.DpiSliderPos = 0.0;
        Assert.Equal(100, vm.Dpi);
        vm.DpiSliderPos = 1.0;
        Assert.Equal(30000, vm.Dpi);
    }

    [Fact]
    public void DpiSliderPos_round_trips_through_dpi()
    {
        var vm = NewVm();
        vm.Dpi = 1600;
        double pos = vm.DpiSliderPos;
        vm.DpiSliderPos = pos;
        Assert.Equal(1600, vm.Dpi);
    }

    [Fact]
    public void DpiSliderPos_midpoint_is_sqrt300_times_100_rounded_to_50()
    {
        var vm = NewVm();
        vm.DpiSliderPos = 0.5;
        Assert.Equal(1750, vm.Dpi);
    }

    [Fact]
    public void DpiSliderPos_setter_always_snaps_dpi_to_a_multiple_of_50()
    {
        var vm = NewVm();
        vm.DpiSliderPos = 0.37;
        Assert.Equal(0, vm.Dpi % 50);
    }
}
