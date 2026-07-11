using System.Text.Json.Serialization;
using NagaBatteryTray.Hid;

namespace NagaBatteryTray.Settings;

/// <summary>One remapped grid button as persisted in settings.json (keyed by grid position 1..12 in
/// <see cref="AppSettings.ButtonBindings"/>; a button absent from the table is Default and is never
/// written). Stock* holds the action read from the direct profile before this button's first-ever
/// write, so "Default" restores instantly; HasStock=false means that read failed (Default then takes
/// effect at the next reconnect instead).</summary>
public sealed class ButtonBindingSetting
{
    [JsonConverter(typeof(JsonStringEnumConverter<ButtonActionKind>))]
    public ButtonActionKind Kind { get; set; } = ButtonActionKind.Key; // Key | Disabled (never Default)
    public byte Modifiers { get; set; }
    public byte HidUsage { get; set; }
    public byte StockCategory { get; set; }
    public byte[] StockData { get; set; } = Array.Empty<byte>();
    public bool HasStock { get; set; }
}
