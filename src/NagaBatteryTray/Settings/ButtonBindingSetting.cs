using System.Text.Json.Serialization;
using NagaBatteryTray.Hid;

namespace NagaBatteryTray.Settings;

/// <summary>One remapped grid button as persisted in settings.json (keyed by grid position 1..12 in
/// <see cref="AppSettings.ButtonBindings"/>; a button absent from the table holds its factory action
/// — "Default" rewrites it from the baked-in factory map, no snapshot needed).</summary>
public sealed class ButtonBindingSetting
{
    [JsonConverter(typeof(JsonStringEnumConverter<ButtonActionKind>))]
    public ButtonActionKind Kind { get; set; } = ButtonActionKind.Key; // Key | Disabled (never Default)
    public byte Modifiers { get; set; }
    public byte HidUsage { get; set; }
}
