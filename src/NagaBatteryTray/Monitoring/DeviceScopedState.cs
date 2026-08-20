namespace NagaBatteryTray.Monitoring;

/// <summary>Everything the app caches about ONE mouse. Feeding it the connected model on every
/// state change makes a model swap clear the cache, which is the fix for the 2026-08-19
/// acceptance defects: the V2's active slot 3 does not exist on a V3, and a button write aimed
/// there reads back the firmware's 1-byte stub, so it can never verify and reports "Not applied"
/// on a write the user watched succeed elsewhere (spec 2026-08-19 §1.1, §4.6).</summary>
public sealed class DeviceScopedState
{
    private string? _model;
    private bool _seen;

    /// <summary>Last active-slot read (0x05/0x84). Null = unknown, resolve on demand.</summary>
    public byte? ActiveSlot { get; set; }

    /// <summary>Slots the connected mouse has actually created (0x05/0x81). Null = not read yet.</summary>
    public byte[]? CreatedSlots { get; set; }

    /// <summary>The connection id the cache was last seeded under, so a caller can tell whether a
    /// hop landed between the seed and now. Null = nothing seeded yet (spec §4.10).</summary>
    public int? SeededConnection { get; set; }

    /// <summary>Feed every observed model name, including null when the mouse goes away. True = the
    /// mouse changed and the cache was dropped, so the caller must supersede in-flight work and
    /// re-seed anything on screen.</summary>
    public bool Observe(string? model)
    {
        if (_seen && string.Equals(_model, model, StringComparison.Ordinal)) return false;
        bool changed = _seen;
        _seen = true;
        _model = model;
        Clear();
        return changed;
    }

    public void Clear()
    {
        ActiveSlot = null;
        CreatedSlots = null;
        SeededConnection = null;
    }

    /// <summary>True when the inventory is KNOWN and the slot is absent from it — the one case where
    /// a write must be refused rather than aimed into nothing. An unknown inventory never blocks:
    /// the dashboard resolves the active slot on demand before its first profile read, and that path
    /// has to keep working (spec §4.7).</summary>
    public bool IsBlocked(byte slot) => CreatedSlots is { } s && Array.IndexOf(s, slot) < 0;
}
