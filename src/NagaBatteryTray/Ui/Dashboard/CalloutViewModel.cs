using NagaBatteryTray.Hid;

namespace NagaBatteryTray.Ui.Dashboard;

/// <summary>One thumb-grid button chip: instant-apply state machine
/// (Idle → Capturing → Writing → Confirmed | Failed) with a one-shot undo window after each
/// verified write. The write delegate is AppHost's slot pipeline; Kind=Default means
/// "write the factory action and drop the table entry".</summary>
public sealed class CalloutViewModel : ObservableObject
{
    public delegate Task<bool> WriteBinding(int position, ButtonActionKind kind, byte modifiers, byte usage);

    private readonly WriteBinding _write;
    private readonly Func<Task> _undoWindow; // one-shot delay; injectable for tests (default 5 s)

    private const string PendingText = "…";
    private const string ReadFailedText = "—";

    private ButtonActionKind _kind = ButtonActionKind.Default;
    private byte _mods, _usage;
    private string? _deviceText; // non-null overrides BindingText: sweep pending "…", failed read "—", or a foreign-category label
    private (ButtonActionKind Kind, byte Mods, byte Usage) _prev;
    private int _undoVersion;
    private bool _isCapturing, _isBusy, _canUndo, _isHighlighted, _failed;
    private string _status = "";

    public CalloutViewModel(int position, WriteBinding write, Func<Task>? undoWindow = null)
    {
        Position = position;
        _write = write;
        _undoWindow = undoWindow ?? (() => Task.Delay(TimeSpan.FromSeconds(5)));
    }

    public int Position { get; }
    public string Label => Position.ToString();

    public string BindingText => _deviceText ?? _kind switch
    {
        ButtonActionKind.Disabled => "Disabled",
        ButtonActionKind.Key => KeyToHidUsage.Describe(_mods, _usage),
        _ => KeyToHidUsage.Describe(0, NagaV2ProButtons.FactoryBindingForPosition(Position).HidUsage),
    };

    /// <summary>Key-box tooltip: normally the binding text itself (full value for a trimmed box);
    /// a failed sweep read gets an explanation instead of a bare dash.</summary>
    public string BindingTip => _deviceText == ReadFailedText
        ? "couldn't read this button — refresh to retry"
        : BindingText;

    public bool IsCapturing
    {
        get => _isCapturing;
        private set { if (Set(ref _isCapturing, value)) { NotifyBinding(); Notify(nameof(IsEngaged)); } }
    }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    /// <summary>Last write failed — the compact row has no status line, so this drives the
    /// key-box's red border (with Status as its tooltip) until the next attempt.</summary>
    public bool Failed { get => _failed; private set => Set(ref _failed, value); }
    public bool CanUndo
    {
        get => _canUndo;
        private set { if (Set(ref _canUndo, value)) Notify(nameof(IsEngaged)); }
    }
    /// <summary>The row the user is working with — capturing, or inside the undo window. The
    /// view's ONE expansion signal (padding + font), so the engaged look can't drift across
    /// per-state trigger copies.</summary>
    public bool IsEngaged => IsCapturing || CanUndo;
    public bool IsHighlighted { get => _isHighlighted; set => Set(ref _isHighlighted, value); }
    public string Status { get => _status; set => Set(ref _status, value); }

    /// <summary>Seed from the persisted table (dashboard open) or a verified write/sweep decode.
    /// Clears any device-display override — a concrete binding always supersedes it.</summary>
    public void SetApplied(ButtonActionKind kind, byte modifiers, byte usage)
    {
        _kind = kind; _mods = modifiers; _usage = usage;
        _deviceText = null;
        NotifyBinding();
    }

    /// <summary>Grid-sweep start (spec §13.1): show the pending marker until this button's read
    /// lands. Skipped while the chip is mid-edit — the sweep must never clobber a live edit.</summary>
    public void SetPending()
    {
        if (IsBusy || IsCapturing) return;
        _deviceText = PendingText;
        NotifyBinding();
    }

    /// <summary>A grid-sweep read landed: display hardware truth for this button (spec §13.1).
    /// Keyboard/disabled decode into the normal edit state (a fresh slot's EMPTY reads as
    /// category 0x00 too); any other category displays as a Synapse action — the app never
    /// rewrites raw it doesn't model; null = the read failed. Busy/capturing chips are skipped,
    /// same as SetPending.</summary>
    public void SetFromDevice(RawButtonAction? raw)
    {
        if (IsBusy || IsCapturing) return;
        if (raw is not { } r) { _deviceText = ReadFailedText; NotifyBinding(); return; }
        if (r.Category == RazerProtocol.FnKeyboard && r.Data.Length == 2)
            SetApplied(ButtonActionKind.Key, r.Data[0], r.Data[1]);
        else if (r.Category == RazerProtocol.FnDisabled)
            SetApplied(ButtonActionKind.Disabled, 0, 0);
        else { _deviceText = $"Synapse action (0x{r.Category:x2})"; NotifyBinding(); }
    }

    private void NotifyBinding() { Notify(nameof(BindingText)); Notify(nameof(BindingTip)); }

    public void BeginCapture() { Status = ""; Failed = false; IsCapturing = true; }
    public void CancelCapture() => IsCapturing = false;

    /// <summary>Capture ended on a key with no HID usage mapping: surface why nothing happened.
    /// Failed drives the key-box's red border + tooltip — the compact row has no status line,
    /// so writing Status alone renders nowhere (review find).</summary>
    public void RejectKey(string message) { Status = message; Failed = true; }

    public Task CaptureAsync(byte modifiers, byte usage)
    {
        IsCapturing = false;
        return ApplyAsync(ButtonActionKind.Key, modifiers, usage, offerUndo: true);
    }

    public Task<bool> DisableAsync() => ApplyAsync(ButtonActionKind.Disabled, 0, 0, offerUndo: true);
    public Task<bool> DefaultAsync() => ApplyAsync(ButtonActionKind.Default, 0, 0, offerUndo: true);

    public Task UndoAsync()
    {
        // busy guard BEFORE consuming CanUndo: clicking ↶ while another write is in flight
        // used to burn the one-shot undo on ApplyAsync's silent IsBusy return (review find)
        if (!CanUndo || IsBusy) return Task.CompletedTask;
        CanUndo = false;
        var (k, m, u) = _prev;
        return ApplyAsync(k, m, u, offerUndo: false);
    }

    /// <summary>True = the binding is verified on the mouse (written, or already identical).
    /// False = busy-skipped or the write failed — callers counting outcomes (reset-all) use
    /// this, never the display Status string.</summary>
    private async Task<bool> ApplyAsync(ButtonActionKind kind, byte modifiers, byte usage, bool offerUndo)
    {
        if (IsBusy) return false;
        // no-op suppression (ported from the staged model): re-applying the exact current
        // binding skips the HID round-trip — no false "Not applied" when the mouse naps, no
        // slot creation for a no-op. Default stays exempt: it's the always-available repair path.
        if (kind != ButtonActionKind.Default && kind == _kind && modifiers == _mods && usage == _usage)
        { Status = "Applied"; return true; }
        IsBusy = true;
        Failed = false;
        Status = "Writing…";
        bool ok = await _write(Position, kind, modifiers, usage);
        IsBusy = false;
        if (!ok) { Status = "Not applied — wiggle the mouse and retry"; Failed = true; return false; }

        _prev = (_kind, _mods, _usage);
        SetApplied(kind, modifiers, usage);
        Status = "Applied";
        if (offerUndo) _ = OpenUndoWindowAsync();
        return true;
    }

    private async Task OpenUndoWindowAsync()
    {
        int version = ++_undoVersion;
        CanUndo = true;
        await _undoWindow();
        if (version == _undoVersion) CanUndo = false;
    }
}
