using NagaBatteryTray.Hid;
using NagaBatteryTray.Ui.Dashboard;
using Xunit;

public class CalloutViewModelTests
{
    private sealed class Recorder
    {
        public readonly List<(int Pos, ButtonActionKind Kind, byte Mods, byte Usage)> Writes = new();
        public bool Result = true;
        public Task<bool> Write(int p, ButtonActionKind k, byte m, byte u)
        { Writes.Add((p, k, m, u)); return Task.FromResult(Result); }
    }

    private static (CalloutViewModel vm, Recorder rec, TaskCompletionSource undo) NewVm(int pos = 1)
    {
        var rec = new Recorder();
        var tcs = new TaskCompletionSource();
        return (new CalloutViewModel(pos, rec.Write, () => tcs.Task), rec, tcs);
    }

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
    public async Task Undo_rewrites_previous_binding_and_expires()
    {
        var (vm, rec, undo) = NewVm(2);
        vm.SetApplied(ButtonActionKind.Key, 0x00, 0x3a);       // seeded F1
        vm.BeginCapture();
        await vm.CaptureAsync(0x01, 0x06);                     // now Ctrl+C
        Assert.True(vm.CanUndo);
        await vm.UndoAsync();                                  // back to F1
        Assert.Equal("F1", vm.BindingText);
        Assert.False(vm.CanUndo);                              // undo is one-shot
        Assert.Equal(ButtonActionKind.Key, rec.Writes[^1].Kind);
        Assert.Equal((byte)0x3a, rec.Writes[^1].Usage);
    }

    [Fact]
    public async Task Undo_window_expiry_clears_CanUndo()
    {
        var rec = new Recorder();
        var tcs = new TaskCompletionSource();
        var vm = new CalloutViewModel(1, rec.Write, () => tcs.Task);
        vm.BeginCapture();
        await vm.CaptureAsync(0x00, 0x04); // A
        Assert.True(vm.CanUndo);
        tcs.SetResult();                   // the 5 s window elapses
        await Task.Yield();
        Assert.False(vm.CanUndo);
    }

    [Fact]
    public async Task Undo_of_a_previously_default_button_restores_factory()
    {
        var (vm, rec, _) = NewVm(4);       // untouched → applied state is Default
        vm.BeginCapture();
        await vm.CaptureAsync(0x00, 0x04); // A
        await vm.UndoAsync();
        Assert.Equal(ButtonActionKind.Default, rec.Writes[^1].Kind); // AppHost maps Default → factory write + table remove
        Assert.Equal("4", vm.BindingText);
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
        var vm = new CalloutViewModel(1, (_, _, _, _) => tcs.Task, () => undo.Task);

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
        var vm = new CalloutViewModel(1, (_, _, _, _) => tcs.Task);

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
        var vm = new CalloutViewModel(1, (_, _, _, _) =>
        { var tcs = new TaskCompletionSource<bool>(); writes.Add(tcs); return tcs.Task; },
        () => new TaskCompletionSource().Task);

        var applyA = vm.CaptureAsync(0x00, 0x04); // A
        writes[0].SetResult(true);
        await applyA;
        Assert.True(vm.CanUndo);

        var applyB = vm.DisableAsync();           // in flight — IsBusy
        await vm.UndoAsync();                     // must be a no-op, not consume the undo
        Assert.True(vm.CanUndo);
        Assert.Equal(2, writes.Count);            // A + B only, no undo write

        writes[1].SetResult(true);
        await applyB;
    }

    [Fact]
    public async Task Busy_chip_reports_false_so_reset_all_counts_it_failed()
    {
        var writes = new List<TaskCompletionSource<bool>>();
        var vm = new CalloutViewModel(1, (_, _, _, _) =>
        { var tcs = new TaskCompletionSource<bool>(); writes.Add(tcs); return tcs.Task; });

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
}
