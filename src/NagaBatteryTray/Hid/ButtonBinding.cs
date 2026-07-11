namespace NagaBatteryTray.Hid;

public enum ButtonActionKind { Default, Disabled, Key }

/// <summary>A grid button's raw onboard action as read from the device (category + data bytes).
/// Round-trips categories the app doesn't model (mouse, DPI-stage, …) — Default-restore needs that.</summary>
public readonly record struct RawButtonAction(byte Category, byte[] Data);

/// <summary>The mouse's onboard profile inventory (0x05/0x81): slot capacity + the occupied slot
/// numbers. Used to adopt an app-owned slot without ever touching a user's existing slots.</summary>
public readonly record struct ProfileList(byte Capacity, byte[] Slots);

/// <summary>One thumb-grid button binding. Kind=Default is a marker (absent from the remap table);
/// it is never written to the device.</summary>
public readonly record struct ButtonBinding(byte ButtonId, ButtonActionKind Kind, byte Modifiers, byte HidUsage)
{
    /// <summary>Wire form for the 0x02/0x0c SET (spec §5.1). Throws on Default — an untouched/default
    /// button must never be written (§3.1 discipline).</summary>
    public (byte Category, byte[] Data) ToWire() => Kind switch
    {
        ButtonActionKind.Disabled => (RazerProtocol.FnDisabled, Array.Empty<byte>()),
        ButtonActionKind.Key => (RazerProtocol.FnKeyboard, new[] { Modifiers, HidUsage }),
        _ => throw new InvalidOperationException("Default bindings are never written to the device."),
    };
}

/// <summary>Naga V2 Pro thumb grid: 12 buttons, firmware ids 0x40..0x4b contiguous in physical order
/// (hardware-verified 2026-07-11; spec §6).</summary>
public static class NagaV2ProButtons
{
    public const int Count = 12;

    public static byte IdForPosition(int position) =>
        position is >= 1 and <= Count
            ? (byte)(0x3f + position)
            : throw new ArgumentOutOfRangeException(nameof(position));

    // The grid's factory emissions: the keyboard digits row 1..9, 0, -, = (HUT 1.5 usages).
    private static readonly byte[] FactoryUsages =
        { 0x1e, 0x1f, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x2d, 0x2e };

    /// <summary>The factory action for a grid position — what "Default" writes. A freshly created
    /// onboard slot reads back EMPTY (hardware-observed 2026-07-11), so restoring a snapshot taken
    /// from it restores nothingness; the factory map is the only true default.</summary>
    public static ButtonBinding FactoryBindingForPosition(int position) =>
        new(IdForPosition(position), ButtonActionKind.Key, 0, FactoryUsages[position - 1]);
}
