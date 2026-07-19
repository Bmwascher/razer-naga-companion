using NagaBatteryTray.Hid;
using NagaBatteryTray.Ui.Dashboard;
using Xunit;

public class CalloutViewModelTests
{
    private sealed class Recorder
    {
        public readonly List<(int Pos, ButtonActionKind Kind, byte Mods, byte Usage)> Writes = new();
        public readonly List<(int Pos, RawButtonAction Raw)> RawWrites = new();
        public bool Result = true;
        public bool RawResult = true;
        public Task<bool> Write(int p, ButtonActionKind k, byte m, byte u)
        { Writes.Add((p, k, m, u)); return Task.FromResult(Result); }
        public Task<bool> WriteRaw(int p, RawButtonAction raw)
        { RawWrites.Add((p, raw)); return Task.FromResult(RawResult); }
    }

    private static (CalloutViewModel vm, Recorder rec, TaskCompletionSource undo) NewVm(int pos = 1)
    {
        var rec = new Recorder();
        var tcs = new TaskCompletionSource();
        return (new CalloutViewModel(pos, rec.Write, rec.WriteRaw, () => tcs.Task), rec, tcs);
    }

    private static RawButtonAction Key(byte mods, byte usage) =>
        new(RazerProtocol.FnKeyboard, new[] { mods, usage });

    [Fact]
    public void Untouched_shows_factory_key_name()
    {
        var (vm, _, _) = NewVm(3);
        Assert.Equal("3", vm.BindingText); // factory digit for position 3
    }

    [Fact]
    public async Task Capture_writes_and_confirms_and_offers_undo()
    {
        var (vm, rec, _) = NewVm();
        vm.SetFromDevice(Key(0x00, 0x1e));  // sweep landed: on-mouse action known
        vm.BeginCapture();
        Assert.True(vm.IsCapturing);
        await vm.CaptureAsync(0x01, 0x06); // Ctrl+C
        Assert.False(vm.IsCapturing);
        Assert.Equal(("Ctrl+C"), vm.BindingText);
        Assert.True(vm.CanUndo);
        Assert.Equal((1, ButtonActionKind.Key, (byte)0x01, (byte)0x06), rec.Writes.Single());
    }

    [Fact]
    public async Task Failed_write_keeps_prior_binding_and_no_undo()
    {
        var (vm, rec, _) = NewVm(1);
        rec.Result = false;
        vm.BeginCapture();
        await vm.CaptureAsync(0x00, 0x3a); // F1
        Assert.Equal("1", vm.BindingText);            // still factory
        Assert.False(vm.CanUndo);
        Assert.Contains("Not applied", vm.Status);
    }

    [Fact]
    public async Task Undo_restores_the_snapshotted_raw_and_expires()
    {
        var (vm, rec, _) = NewVm(2);
        vm.SetFromDevice(Key(0x00, 0x3a));                     // on-mouse: F1
        vm.BeginCapture();
        await vm.CaptureAsync(0x01, 0x06);                     // now Ctrl+C
        Assert.True(vm.CanUndo);
        await vm.UndoAsync();                                  // back to F1, raw path
        Assert.Equal("F1", vm.BindingText);
        Assert.False(vm.CanUndo);                              // undo is one-shot
        var (pos, raw) = rec.RawWrites.Single();
        Assert.Equal(2, pos);
        Assert.Equal(RazerProtocol.FnKeyboard, raw.Category);
        Assert.Equal(new byte[] { 0x00, 0x3a }, raw.Data);
    }

    [Fact]
    public async Task Undo_window_expiry_clears_CanUndo()
    {
        var (vm, _, tcs) = NewVm();
        vm.SetFromDevice(Key(0x00, 0x1e));
        vm.BeginCapture();
        await vm.CaptureAsync(0x00, 0x04); // A
        Assert.True(vm.CanUndo);
        tcs.SetResult();                   // the 5 s window elapses
        await Task.Yield();
        Assert.False(vm.CanUndo);
    }

    [Fact]
    public async Task Disable_and_default_write_through()
    {
        var (vm, rec, _) = NewVm(5);
        await vm.DisableAsync();
        Assert.Equal("Disabled", vm.BindingText);
        await vm.DefaultAsync();
        Assert.Equal("5", vm.BindingText);
        Assert.Equal(ButtonActionKind.Disabled, rec.Writes[0].Kind);
        Assert.Equal(ButtonActionKind.Default, rec.Writes[1].Kind);
    }

    [Fact]
    public async Task Apply_shows_Writing_status_during_the_device_round_trip()
    {
        var tcs = new TaskCompletionSource<bool>();
        var undo = new TaskCompletionSource();
        var vm = new CalloutViewModel(1, (_, _, _, _) => tcs.Task, (_, _) => Task.FromResult(true), () => undo.Task);

        var apply = vm.CaptureAsync(0x00, 0x04); // A
        Assert.Equal("Writing…", vm.Status);

        tcs.SetResult(true);
        await apply;
        Assert.Equal("Applied", vm.Status);
    }

    [Fact]
    public async Task Apply_shows_Writing_status_then_failure_message()
    {
        var tcs = new TaskCompletionSource<bool>();
        var vm = new CalloutViewModel(1, (_, _, _, _) => tcs.Task, (_, _) => Task.FromResult(true));

        var apply = vm.CaptureAsync(0x00, 0x04); // A
        Assert.Equal("Writing…", vm.Status);

        tcs.SetResult(false);
        await apply;
        Assert.Contains("Not applied", vm.Status);
    }

    [Fact]
    public async Task Failed_flag_sets_on_write_failure_and_clears_on_the_next_attempt()
    {
        var (vm, rec, _) = NewVm();
        rec.Result = false;
        await vm.DisableAsync();
        Assert.True(vm.Failed);

        rec.Result = true;
        await vm.DisableAsync();
        Assert.False(vm.Failed);
    }

    [Fact]
    public async Task BeginCapture_clears_a_stale_Failed_flag()
    {
        var (vm, rec, _) = NewVm();
        rec.Result = false;
        await vm.DisableAsync();
        Assert.True(vm.Failed);

        vm.BeginCapture();
        Assert.False(vm.Failed);
    }

    [Fact]
    public async Task IsEngaged_spans_capture_through_the_undo_window()
    {
        var (vm, _, undo) = NewVm();
        vm.SetFromDevice(Key(0x00, 0x1e));    // snapshot available → undo window will open
        Assert.False(vm.IsEngaged);

        vm.BeginCapture();
        Assert.True(vm.IsEngaged);            // capturing

        await vm.CaptureAsync(0x00, 0x04);    // capture ends, undo window opens
        Assert.True(vm.IsEngaged);            // undo window

        undo.SetResult();                     // the 5 s window elapses
        await Task.Yield();
        Assert.False(vm.IsEngaged);
    }

    [Fact]
    public async Task Reapplying_the_identical_binding_skips_the_write()
    {
        var (vm, rec, _) = NewVm(5);
        await vm.DisableAsync();                 // Default -> Disabled: real write
        Assert.True(await vm.DisableAsync());    // Disabled -> Disabled: verified no-op
        Assert.Single(rec.Writes);
        Assert.Equal("Applied", vm.Status);
    }

    [Fact]
    public async Task Default_is_exempt_from_noop_suppression_as_the_repair_path()
    {
        var (vm, rec, _) = NewVm(4);             // untouched chip is already "Default"
        Assert.True(await vm.DefaultAsync());
        Assert.Single(rec.Writes);               // still writes the factory action
    }

    [Fact]
    public async Task Undo_clicked_while_another_write_is_in_flight_is_not_consumed()
    {
        var writes = new List<TaskCompletionSource<bool>>();
        var raws = new List<(int, RawButtonAction)>();
        var vm = new CalloutViewModel(1, (_, _, _, _) =>
        { var tcs = new TaskCompletionSource<bool>(); writes.Add(tcs); return tcs.Task; },
        (p, r) => { raws.Add((p, r)); return Task.FromResult(true); },
        () => new TaskCompletionSource().Task);
        vm.SetFromDevice(Key(0x00, 0x1e));        // snapshot so the undo window opens

        var applyA = vm.CaptureAsync(0x00, 0x04); // A
        writes[0].SetResult(true);
        await applyA;
        Assert.True(vm.CanUndo);

        var applyB = vm.DisableAsync();           // in flight — IsBusy
        await vm.UndoAsync();                     // must be a no-op, not consume the undo
        Assert.True(vm.CanUndo);
        Assert.Equal(2, writes.Count);            // A + B only
        Assert.Empty(raws);                       // no undo write happened

        writes[1].SetResult(true);
        await applyB;
    }

    [Fact]
    public async Task Busy_chip_reports_false_so_reset_all_counts_it_failed()
    {
        var writes = new List<TaskCompletionSource<bool>>();
        var vm = new CalloutViewModel(1, (_, _, _, _) =>
        { var tcs = new TaskCompletionSource<bool>(); writes.Add(tcs); return tcs.Task; },
        (_, _) => Task.FromResult(true));

        var inFlight = vm.DisableAsync();
        Assert.False(await vm.DefaultAsync());    // busy-skip = not reset

        writes[0].SetResult(true);
        await inFlight;
    }

    [Fact]
    public async Task RejectKey_surfaces_visibly_and_clears_on_next_capture()
    {
        var (vm, _, _) = NewVm();
        vm.BeginCapture();
        vm.CancelCapture();
        vm.RejectKey("PrintScreen can't be bound");
        Assert.True(vm.Failed);                   // Failed drives the visible red border + tooltip
        Assert.Contains("can't be bound", vm.Status);

        vm.BeginCapture();
        Assert.False(vm.Failed);
    }

    [Fact]
    public void CancelCapture_returns_to_idle()
    {
        var (vm, rec, _) = NewVm();
        vm.BeginCapture();
        vm.CancelCapture();
        Assert.False(vm.IsCapturing);
        Assert.Empty(rec.Writes);
    }

    // ---- grid sweep display (spec §13.1/13.2: the grid shows hardware truth for the active profile) ----

    [Fact]
    public void SetPending_shows_the_pending_marker_until_a_read_lands()
    {
        var (vm, _, _) = NewVm(3);
        vm.SetPending();
        Assert.Equal("…", vm.BindingText);
    }

    [Fact]
    public void SetFromDevice_keyboard_decodes_into_the_edit_state()
    {
        var (vm, _, _) = NewVm(3);
        vm.SetPending();
        vm.SetFromDevice(Key(0x01, 0x06)); // Ctrl+C
        Assert.Equal("Ctrl+C", vm.BindingText);
    }

    [Fact]
    public void SetFromDevice_disabled_and_empty_both_show_Disabled()
    {
        var (vm, _, _) = NewVm(3);
        vm.SetFromDevice(new RawButtonAction(0x00, Array.Empty<byte>())); // fresh-slot EMPTY reads like this too
        Assert.Equal("Disabled", vm.BindingText);
    }

    [Fact]
    public void SetFromDevice_foreign_category_shows_synapse_label()
    {
        var (vm, _, _) = NewVm(3);
        vm.SetFromDevice(new RawButtonAction(0x01, new byte[] { 0x01 })); // mouse-function category
        Assert.Equal("Synapse action (0x01)", vm.BindingText);
        Assert.Equal("Synapse action (0x01)", vm.BindingTip);
    }

    [Fact]
    public void SetFromDevice_keyboard_with_wrong_length_falls_back_to_synapse_label()
    {
        var (vm, _, _) = NewVm(3);
        vm.SetFromDevice(new RawButtonAction(0x02, new byte[] { 0x01 })); // truncated keyboard frame
        Assert.Equal("Synapse action (0x02)", vm.BindingText);
    }

    [Fact]
    public void SetFromDevice_null_shows_read_failed_with_explanatory_tooltip()
    {
        var (vm, _, _) = NewVm(3);
        vm.SetFromDevice(null);
        Assert.Equal("—", vm.BindingText);
        Assert.Contains("refresh", vm.BindingTip);
    }

    [Fact]
    public async Task Sweep_never_clobbers_a_busy_or_capturing_chip()
    {
        var writes = new List<TaskCompletionSource<bool>>();
        var vm = new CalloutViewModel(1, (_, _, _, _) =>
        { var tcs = new TaskCompletionSource<bool>(); writes.Add(tcs); return tcs.Task; },
        (_, _) => Task.FromResult(true));

        var inFlight = vm.DisableAsync();          // busy
        vm.SetPending();
        vm.SetFromDevice(Key(0x00, 0x04));
        Assert.NotEqual("…", vm.BindingText);      // pending marker skipped
        writes[0].SetResult(true);
        await inFlight;
        Assert.Equal("Disabled", vm.BindingText);  // the edit won, not the stale sweep value

        vm.BeginCapture();                         // capturing
        vm.SetFromDevice(null);
        vm.CancelCapture();
        Assert.Equal("Disabled", vm.BindingText);
    }

    [Fact]
    public async Task A_verified_write_clears_a_device_display_override()
    {
        var (vm, _, _) = NewVm(3);
        vm.SetFromDevice(new RawButtonAction(0x01, new byte[] { 0x01 })); // Synapse display
        await vm.DisableAsync();                                          // user writes over it
        Assert.Equal("Disabled", vm.BindingText);
    }

    [Fact]
    public async Task Suppression_requires_a_clean_display()
    {
        var (vm, rec, _) = NewVm(5);
        await vm.DisableAsync();                    // write 1: edit state = Disabled
        vm.SetFromDevice(new RawButtonAction(0x01, new byte[] { 0x01 })); // Synapse rebound it

        Assert.True(await vm.DisableAsync());       // same as edit state, but the display disagrees
        Assert.Equal(2, rec.Writes.Count);          // → must reach the mouse, not be suppressed
        Assert.Equal("Disabled", vm.BindingText);
    }

    // ---- raw-snapshot undo (spec §13.2) ----

    [Fact]
    public async Task Undo_restores_a_synapse_action_byte_for_byte()
    {
        var (vm, rec, _) = NewVm(3);
        vm.SetFromDevice(new RawButtonAction(0x01, new byte[] { 0x05, 0x00 })); // unmodeled raw
        await vm.DisableAsync();                     // user overwrites the Synapse action
        Assert.True(vm.CanUndo);

        await vm.UndoAsync();

        var (pos, raw) = rec.RawWrites.Single();
        Assert.Equal(3, pos);
        Assert.Equal(0x01, raw.Category);
        Assert.Equal(new byte[] { 0x05, 0x00 }, raw.Data);
        Assert.Equal("Synapse action (0x01)", vm.BindingText); // display restored too
    }

    [Fact]
    public async Task First_write_without_a_known_prior_offers_no_undo()
    {
        var (vm, _, _) = NewVm(1);                   // no sweep read yet — prior unknown
        await vm.DisableAsync();
        Assert.False(vm.CanUndo);                    // ↶ must never write a guess
    }

    [Fact]
    public async Task SetPending_clears_the_snapshot_so_a_mid_sweep_write_has_no_undo()
    {
        var (vm, _, _) = NewVm(1);
        vm.SetFromDevice(Key(0x00, 0x1e));           // old slot's raw known
        vm.SetPending();                             // new sweep started (e.g. slot switch)
        await vm.DisableAsync();                     // write lands before this chip's read
        Assert.False(vm.CanUndo);                    // the OLD slot's raw must not be the undo target
    }

    [Fact]
    public async Task Failed_undo_surfaces_visibly_and_reopens_the_window_for_retry()
    {
        var (vm, rec, _) = NewVm(2);
        vm.SetFromDevice(Key(0x00, 0x3a));
        await vm.CaptureAsync(0x00, 0x04);
        rec.RawResult = false;

        await vm.UndoAsync();

        Assert.True(vm.Failed);
        Assert.Contains("Not applied", vm.Status);
        Assert.True(vm.CanUndo);            // snapshot not burned by one transient failure

        rec.RawResult = true;
        await vm.UndoAsync();               // retry succeeds
        Assert.Equal("F1", vm.BindingText);
        Assert.Equal(2, rec.RawWrites.Count);
    }

    [Fact]
    public async Task SetPending_expires_an_open_undo_window()
    {
        var (vm, rec, _) = NewVm(1);
        vm.SetFromDevice(Key(0x00, 0x1e));
        await vm.CaptureAsync(0x00, 0x04);
        Assert.True(vm.CanUndo);

        vm.SetPending();                    // slot switch: a new sweep begins

        Assert.False(vm.CanUndo);           // slot A's snapshot must never be written into slot B
        await vm.UndoAsync();               // and a late click is inert
        Assert.Empty(rec.RawWrites);
    }

    [Fact]
    public async Task A_sweep_starting_mid_write_prevents_arming_undo_from_the_old_slot()
    {
        var writes = new List<TaskCompletionSource<bool>>();
        var vm = new CalloutViewModel(1, (_, _, _, _) =>
        { var tcs = new TaskCompletionSource<bool>(); writes.Add(tcs); return tcs.Task; },
        (_, _) => Task.FromResult(true), () => new TaskCompletionSource().Task);
        vm.SetFromDevice(Key(0x00, 0x1e));       // old slot's raw known

        var apply = vm.DisableAsync();           // write in flight
        vm.SetPending();                         // slot switch mid-write (busy → display skip, undo expiry still applies)
        writes[0].SetResult(true);
        await apply;

        Assert.False(vm.CanUndo);                // pre-switch snapshot must not arm undo
    }

    [Fact]
    public async Task Chained_undo_snapshots_the_restored_value()
    {
        var (vm, rec, _) = NewVm(2);
        vm.SetFromDevice(Key(0x00, 0x3a));      // on-mouse: F1
        await vm.CaptureAsync(0x00, 0x04);      // A (snapshot = F1)
        await vm.UndoAsync();                   // back to F1
        await vm.CaptureAsync(0x00, 0x05);      // B (snapshot must be the restored F1)
        await vm.UndoAsync();

        Assert.Equal(2, rec.RawWrites.Count);
        Assert.Equal(new byte[] { 0x00, 0x3a }, rec.RawWrites[^1].Raw.Data);
        Assert.Equal("F1", vm.BindingText);
    }

    [Fact]
    public async Task SetPending_during_a_live_capture_voids_the_old_slots_raw()
    {
        var (vm, rec, _) = NewVm(1);
        vm.SetFromDevice(Key(0x00, 0x1e));   // slot A's raw known
        vm.BeginCapture();
        vm.SetPending();                     // slot switch while the capture is live
        await vm.CaptureAsync(0x00, 0x04);   // capture completes — the write targets slot B

        Assert.False(vm.CanUndo);            // slot A's raw must not arm undo on slot B
        await vm.UndoAsync();
        Assert.Empty(rec.RawWrites);
    }

    [Fact]
    public async Task Raw_undo_superseded_mid_flight_does_not_repaint_or_repopulate()
    {
        var rawWrites = new List<TaskCompletionSource<bool>>();
        var vm = new CalloutViewModel(1, (_, _, _, _) => Task.FromResult(true),
            (_, _) => { var t = new TaskCompletionSource<bool>(); rawWrites.Add(t); return t.Task; },
            () => new TaskCompletionSource().Task);
        vm.SetFromDevice(Key(0x00, 0x3a));       // F1 on slot A
        await vm.CaptureAsync(0x00, 0x04);       // now "A"

        var undo = vm.UndoAsync();               // raw restore in flight
        vm.SetPending();                         // slot switch mid-restore (busy → display skip)
        rawWrites[0].SetResult(true);
        await undo;

        Assert.NotEqual("F1", vm.BindingText);   // no stale repaint of slot A's raw
        await vm.DisableAsync();                 // next write happens on slot B...
        Assert.False(vm.CanUndo);                // ...and finds no snapshot (nothing repopulated)
    }

    [Fact]
    public async Task Failed_undo_superseded_mid_flight_does_not_reopen_the_window()
    {
        var rawWrites = new List<TaskCompletionSource<bool>>();
        var vm = new CalloutViewModel(1, (_, _, _, _) => Task.FromResult(true),
            (_, _) => { var t = new TaskCompletionSource<bool>(); rawWrites.Add(t); return t.Task; },
            () => new TaskCompletionSource().Task);
        vm.SetFromDevice(Key(0x00, 0x3a));
        await vm.CaptureAsync(0x00, 0x04);

        var undo = vm.UndoAsync();
        vm.SetPending();                         // superseded mid-restore
        rawWrites[0].SetResult(false);           // ...and the restore failed
        await undo;

        Assert.False(vm.CanUndo);                // a voided snapshot must not be re-armed
    }
}
