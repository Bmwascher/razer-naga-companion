using NagaBatteryTray.Hid;
using NagaBatteryTray.Ui;
using Xunit;

public class ButtonRowViewModelTests
{
    [Fact]
    public void Untouched_row_produces_no_op_and_shows_default()
    {
        var row = new ButtonRowViewModel(3);
        Assert.Equal("Button 3", row.Label);
        Assert.Equal("Default", row.CurrentText);
        Assert.Null(row.ToOp());
    }

    [Fact]
    public void Staged_key_produces_apply_op_with_wire_bytes()
    {
        var row = new ButtonRowViewModel(1);
        row.StageKey(0x01, 0x06); // Ctrl+C
        var op = row.ToOp();
        Assert.NotNull(op);
        Assert.Equal(new ButtonOp(1, ButtonOpKind.Apply, ButtonActionKind.Key, 0x01, 0x06), op!.Value);
        Assert.Contains("Ctrl+C", row.CurrentText);
        Assert.Contains("pending", row.CurrentText);
    }

    [Fact]
    public void Staged_disabled_produces_apply_op()
    {
        var row = new ButtonRowViewModel(2);
        row.StageDisabled();
        Assert.Equal(new ButtonOp(2, ButtonOpKind.Apply, ButtonActionKind.Disabled, 0, 0), row.ToOp());
    }

    [Fact]
    public void Default_on_a_remapped_row_produces_restore_op()
    {
        var row = new ButtonRowViewModel(4);
        row.SetApplied(ButtonActionKind.Key, 0x01, 0x06);
        row.StageDefault();
        var op = row.ToOp();
        Assert.NotNull(op);
        Assert.Equal(ButtonOpKind.RestoreDefault, op!.Value.OpKind);
    }

    [Fact]
    public void Default_is_stageable_even_on_an_untouched_row()
    {
        // onboard model: the table can't prove what the slot holds (e.g. after a failed restore),
        // so an explicit Default click always produces a factory rewrite — it's the repair path
        var row = new ButtonRowViewModel(4);
        row.StageDefault();
        var op = row.ToOp();
        Assert.NotNull(op);
        Assert.Equal(ButtonOpKind.RestoreDefault, op!.Value.OpKind);
        Assert.Contains("pending", row.CurrentText);
    }

    [Fact]
    public void Staging_back_to_the_applied_binding_produces_no_op()
    {
        var row = new ButtonRowViewModel(5);
        row.SetApplied(ButtonActionKind.Key, 0x01, 0x06);
        row.StageKey(0x01, 0x06); // same as applied
        Assert.Null(row.ToOp());
    }

    [Fact]
    public void MarkApplied_promotes_pending_to_applied()
    {
        var row = new ButtonRowViewModel(6);
        row.StageKey(0x00, 0x3a); // F1
        row.MarkApplied();
        Assert.Null(row.ToOp());
        Assert.Equal("F1", row.CurrentText);
        Assert.Equal("Applied", row.Status);
    }

    [Fact]
    public void MarkFailed_keeps_pending_and_sets_status()
    {
        var row = new ButtonRowViewModel(7);
        row.StageDisabled();
        row.MarkFailed("Not applied");
        Assert.NotNull(row.ToOp()); // still pending — user can retry
        Assert.Equal("Not applied", row.Status);
    }
}
