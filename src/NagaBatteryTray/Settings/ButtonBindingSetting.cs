using System.Text.Json.Serialization;
using NagaBatteryTray.Hid;

namespace NagaBatteryTray.Settings;

/// <summary>Retired (v2.3): the shape of one entry in the legacy <see cref="AppSettings.ButtonBindings"/>
/// table — kept only so older settings.json files round-trip; no longer read or written.</summary>
public sealed class ButtonBindingSetting
{
    [JsonConverter(typeof(JsonStringEnumConverter<ButtonActionKind>))]
    public ButtonActionKind Kind { get; set; } = ButtonActionKind.Key; // Key | Disabled (never Default)
    public byte Modifiers { get; set; }
    public byte HidUsage { get; set; }
}
