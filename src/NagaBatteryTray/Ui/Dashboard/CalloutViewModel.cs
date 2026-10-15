using NagaBatteryTray.Hid;

namespace NagaBatteryTray.Ui.Dashboard;

/// <summary>What one write attempt actually did, as the chip and any bulk run need to tell them
/// apart (spec 2026-08-19 §4.10).
/// <list type="bullet">
/// <item>Applied — verified on the mouse (or already identical).</item>
/// <item>Failed — the write was issued and did not verify. The user's retry case, and the only one
/// that may show as a failure.</item>
/// <item>MouseChanged — NOTHING was written, because the mouse underneath is not the one the caller
/// was seeded from (or it hopped onto the OTHER model mid-write). Not a failure: the mouse is fine,
/// the dashboard is already reseeding onto the one that IS there, and a bulk run must STOP rather
/// than write its remaining buttons into it. A SAME-model reconnect is deliberately not this: it is
/// the same mouse, so the write is merely unconfirmed — an ordinary Failed.</item>
/// <item>SlotMissing — NOTHING was written, because the mouse's own slot inventory does not list
/// the active slot. Also not a failure and also not a mouse change, so it gets its own line:
/// retrying or wiggling cannot make a slot appear.</item>
/// </list></summary>
public enum WriteOutcome { Applied, Failed, MouseChanged, SlotMissing }

/// <summary>One thumb-grid button chip: instant-apply state machine
/// (Idle → Capturing → Writing → Confirmed | Failed) with a one-shot undo window after each
/// verified write. The write delegates are AppHost's active-slot pipeline; Kind=Default means
/// "write the factory action". Undo is a RAW restore (spec §13.2): the chip snapshots the
/// button's best-known on-mouse action before every overwrite, so ↶ puts back exactly what was
/// there — including Synapse macros/mouse functions the app can't otherwise model.</summary>
public sealed class CalloutViewModel : ObservableObject
{
    public delegate Task<WriteOutcome> WriteBinding(int position, ButtonActionKind kind, byte modifiers, byte usage);
    public delegate Task<WriteOutcome> WriteRaw(int position, RawButtonAction raw);

    private readonly WriteBinding _write;
    private readonly WriteRaw _writeRaw;
    private readonly Func<Task> _undoWindow; // one-shot delay; injectable for tests (default 5 s)

    private const string PendingText = "…";
    private const string UnsetText = "Not set";
    private const string ReadFailedText = "—";
    /// <summary>A real failure: the write went out and did not verify, so wiggling is the fix.</summary>
    private const string WriteFailedText = "Not applied — wiggle the mouse and retry";
    /// <summary>A refusal: nothing was written and nothing is wrong with the mouse, so it must not
    /// borrow the failure copy — telling the user to wiggle a working mouse is the false-failure this
    /// pass exists to remove. AppHost puts the same words on the profile card, which is the one status
    /// line the compact chip row does not have.</summary>
    internal const string MouseChangedText = "Mouse changed — reloading";
    /// <summary>The other refusal: the mouse has no such slot, so nothing was written. Same rule as
    /// above — nothing failed, so it must not go red or send the user off to wiggle a working
    /// mouse — but it is NOT a mouse change and must not borrow those words either.</summary>
    internal const string SlotMissingText = "Not written — that slot isn't on this mouse";

    private ButtonActionKind _kind = ButtonActionKind.Default;
    private byte _mods, _usage;
    private string? _deviceText; // non-null overrides BindingText: sweep pending "…", failed read "—", or a foreign-category label
    private RawButtonAction? _currentRaw; // best-known on-mouse action for the DISPLAYED slot (null = unknown)
    private RawButtonAction? _prevRaw;    // snapshot taken before the last verified write — what ↶ restores
    private int _snapshotGen; // bumped when the displayed slot may have changed (sweep start) — a stale snapshot must never arm undo
    private int _undoVersion;
    private bool _isCapturing, _isBusy, _canUndo, _isHighlighted, _failed;
    private string _status = "";

    public CalloutViewModel(int position, WriteBinding write, WriteRaw writeRaw, Func<Task>? undoWindow = null)
    {
        Position = position;
        _write = write;
        _writeRaw = writeRaw;
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

    /// <summary>Adopt a verified modeled binding as the display + edit state. Clears any
    /// device-display override — a concrete binding always supersedes it.</summary>
    public void SetApplied(ButtonActionKind kind, byte modifiers, byte usage)
    {
        _kind = kind; _mods = modifiers; _usage = usage;
        _deviceText = null;
        NotifyBinding();
    }

    /// <summary>Grid-sweep start (spec §13.1): show the pending marker until this button's read
    /// lands. The raw snapshot AND any open undo window are invalidated first — after a slot
    /// switch the old slot's raw must not masquerade as this slot's undo target, and ↶ armed
    /// from it would write one slot's action into another (review find). Undo expiry applies
    /// even to a busy/capturing chip (its in-flight write checks the generation on completion);
    /// only the DISPLAY update is skipped mid-edit — the sweep must never clobber a live edit.</summary>
    public void SetPending()
    {
        ExpireUndo();
        // the raw is voided even mid-edit: a live capture completing AFTER a slot switch would
        // otherwise snapshot the OLD slot's raw under the NEW generation and arm a cross-slot
        // undo (review find). Only the DISPLAY update below is skipped for a busy/capturing chip.
        _currentRaw = null;
        if (IsBusy || IsCapturing) return;
        _deviceText = PendingText;
        NotifyBinding();
    }

    private void ExpireUndo()
    {
        _snapshotGen++;
        _undoVersion++;
        _prevRaw = null;
        CanUndo = false;
    }

    /// <summary>A grid-sweep read landed: display hardware truth for this button and remember the
    /// raw as the undo snapshot source (spec §13.2). Keyboard/disabled decode into the edit state
    /// (a fresh slot's EMPTY reads as category 0x00 too); a category the app doesn't model shows
    /// as a Synapse-action override, its raw preserved for byte-for-byte restore; null = the read
    /// failed (snapshot unknown). Busy/capturing chips are skipped, same as SetPending.</summary>
    public void SetFromDevice(RawButtonAction? raw)
    {
        if (IsBusy || IsCapturing) return;
        if (raw is not { } r) { _currentRaw = null; _deviceText = ReadFailedText; NotifyBinding(); return; }
        _currentRaw = r;
        DisplayRaw(r);
    }

    private void DisplayRaw(RawButtonAction r)
    {
        if (r.Category == RazerProtocol.FnKeyboard && r.Data.Length == 2)
        { SetApplied(ButtonActionKind.Key, r.Data[0], r.Data[1]); return; }
        if (r.Category == RazerProtocol.FnDisabled)
        { SetApplied(ButtonActionKind.Disabled, 0, 0); return; }
        // the firmware's SHORT keyboard form: a button with nothing assigned, and every button of a
        // profile slot the mouse has not created. Not a failure and not a Synapse macro — decoding it
        // as one painted whole grids red on the V3 (spec 2026-08-19 §1.1, §4.8).
        // Strictly shorter than 2: a LONGER 0x02 reply is a shape we have never measured, and calling
        // an unmodelled binding "Not set" tells the user an assigned button is empty and invites an
        // overwrite. Unmodelled shapes keep the existing Synapse label.
        if (r.Category == RazerProtocol.FnKeyboard && r.Data.Length < 2)
        { _deviceText = UnsetText; NotifyBinding(); return; }
        _deviceText = $"Synapse action (0x{r.Category:x2})";
        NotifyBinding();
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

    public Task<WriteOutcome> DisableAsync() => ApplyAsync(ButtonActionKind.Disabled, 0, 0, offerUndo: true);
    public Task<WriteOutcome> DefaultAsync() => ApplyAsync(ButtonActionKind.Default, 0, 0, offerUndo: true);

    /// <summary>Raw restore (spec §13.2): write the pre-overwrite snapshot back verbatim —
    /// works for actions the app can't model. One-shot; busy guard BEFORE consuming CanUndo:
    /// clicking ↶ while another write is in flight must not burn the undo (review find).</summary>
    public async Task UndoAsync()
    {
        if (!CanUndo || IsBusy) return;
        CanUndo = false;
        if (_prevRaw is not { } prev) return; // the window only opens with a snapshot; defensive
        int gen = _snapshotGen; // if a sweep/slot change lands mid-restore, this undo is superseded
        IsBusy = true;
        Failed = false;
        Status = "Writing…";
        var outcome = await _writeRaw(Position, prev);
        IsBusy = false;
        if (outcome != WriteOutcome.Applied)
        {
            bool moved = outcome == WriteOutcome.MouseChanged;
            Status = StatusFor(outcome);
            // ONLY a real failure is red: both refusals wrote nothing and neither means the mouse
            // is misbehaving
            Failed = outcome == WriteOutcome.Failed;
            // reopen rather than burn: the snapshot may be the only copy of an unmodelable
            // Synapse action, and it's still valid — the mouse still holds the new binding
            // (review find). One transient failure must not lose the restore forever. A
            // SUPERSEDED window (slot changed mid-restore) must NOT reopen, though — its
            // snapshot no longer belongs to the displayed slot (review find). Nor may a MOUSE
            // CHANGE reopen it: the snapshot describes the mouse that left, so ↶ would write one
            // mouse's raw bytes into another — exactly what hard rule 2 exists to stop.
            if (!moved && gen == _snapshotGen) _ = OpenUndoWindowAsync();
            return;
        }
        if (gen == _snapshotGen)
        {
            _currentRaw = prev;
            DisplayRaw(prev);
        }
        // superseded: the restore itself landed where it was aimed (its slot resolved before the
        // switch queued behind it on the read lock), but the OLD slot's raw must not repaint or
        // repopulate the chip that now displays another slot (review find)
        Status = "Applied";
    }

    /// <summary>Applied = the binding is verified on the mouse (written, or already identical).
    /// Failed = busy-skipped or the write failed. MouseChanged = it was refused because the mouse
    /// moved; SlotMissing = it was refused because the mouse has no such slot. Callers counting
    /// outcomes (reset-all) use this, never the display Status string.</summary>
    private async Task<WriteOutcome> ApplyAsync(ButtonActionKind kind, byte modifiers, byte usage, bool offerUndo)
    {
        if (IsBusy) return WriteOutcome.Failed;
        // no-op suppression (ported from the staged model): re-applying the exact current
        // binding skips the HID round-trip — no false "Not applied" when the mouse naps.
        // Default stays exempt: it's the always-available repair path. Requires a clean display
        // (_deviceText null): under an override, _kind describes a RECORD while the chip shows
        // something else, so "identical" would be a lie and the user's explicit action must
        // reach the mouse (review find).
        if (kind != ButtonActionKind.Default && _deviceText is null
            && kind == _kind && modifiers == _mods && usage == _usage)
        { Status = "Applied"; return WriteOutcome.Applied; }
        var snapshot = _currentRaw; // what's on the mouse right now; null = not yet known
        int gen = _snapshotGen;     // if a new sweep starts mid-write, this snapshot is void
        IsBusy = true;
        Failed = false;
        Status = "Writing…";
        var outcome = await _write(Position, kind, modifiers, usage);
        IsBusy = false;
        if (outcome != WriteOutcome.Applied)
        {
            Status = StatusFor(outcome);
            // ONLY a real failure is red: both refusals wrote nothing and neither means the mouse
            // is misbehaving
            Failed = outcome == WriteOutcome.Failed;
            return outcome;
        }

        if (gen == _snapshotGen)
        {
            _prevRaw = snapshot;
            _currentRaw = WireOf(kind, modifiers, usage);
        }
        else
        {
            _currentRaw = null; // the write's slot may not be the one now displayed/swept
        }
        SetApplied(kind, modifiers, usage);
        Status = "Applied";
        // no snapshot (write landed before this chip's first sweep read) or a superseded one
        // (sweep/slot change mid-write) → no undo window: ↶ must never write a guess (§13.2)
        if (offerUndo && snapshot is not null && gen == _snapshotGen) _ = OpenUndoWindowAsync();
        return WriteOutcome.Applied;
    }

    /// <summary>The one place a non-Applied outcome becomes words, so the two write paths cannot
    /// drift apart on what a refusal says.</summary>
    private static string StatusFor(WriteOutcome outcome) => outcome switch
    {
        WriteOutcome.MouseChanged => MouseChangedText,
        WriteOutcome.SlotMissing => SlotMissingText,
        _ => WriteFailedText,
    };

    /// <summary>The wire form of a modeled action — what the mouse holds after a verified write
    /// of it (Default = the baked-in factory action for this position).</summary>
    private RawButtonAction WireOf(ButtonActionKind kind, byte modifiers, byte usage)
    {
        var binding = kind == ButtonActionKind.Default
            ? NagaV2ProButtons.FactoryBindingForPosition(Position)
            : new ButtonBinding(NagaV2ProButtons.IdForPosition(Position), kind, modifiers, usage);
        var (category, data) = binding.ToWire();
        return new RawButtonAction(category, data);
    }

    private async Task OpenUndoWindowAsync()
    {
        int version = ++_undoVersion;
        CanUndo = true;
        await _undoWindow();
        if (version == _undoVersion) CanUndo = false;
    }
}
