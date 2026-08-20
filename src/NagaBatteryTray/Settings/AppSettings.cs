namespace NagaBatteryTray.Settings;

public sealed class AppSettings
{
    public int PollIntervalSeconds { get; set; } = 60;
    public int PollIntervalChargingSeconds { get; set; } = 15;
    public int LowBatteryThreshold { get; set; } = 15;
    public bool LowBatteryNotify { get; set; } = true;
    public string? CachedTransactionId { get; set; } = null; // e.g. "0x1f"; null = unprobed
    public int SetReadDelayMs { get; set; } = 400;

    /// <summary>Retired (v2.3): kept only so older settings.json files round-trip — no longer read
    /// or written. The firmware holds every binding; the grid reads hardware truth.</summary>
    public Dictionary<int, ButtonBindingSetting> ButtonBindings { get; set; } = new();

    /// <summary>Retired (v2.3): the app-owned-slot model is gone — kept only for JSON back-compat,
    /// no longer read or written.</summary>
    public int? OnboardSlot { get; set; } = null;

    /// <summary>Active theme preset name (Ui/Themes). Unknown value → Porcelain at apply time.</summary>
    public string Theme { get; set; } = "Porcelain";

    /// <summary>Tray icon style: "Gauge" (coin + level ring) or "Text" (classic full-height level-colored
    /// digits). Unknown value → Gauge at the consumer.</summary>
    public string TrayIconStyle { get; set; } = "Gauge";

    /// <summary>App-side one-click DPI presets shown in the dashboard's DPI card.</summary>
    public List<int> DpiPresets { get; set; } = new() { 800, 1600, 3200 };

    /// <summary>Retired (2026-08-19): profile names are per-mouse now (ProfileNamesByMouse) — a slot
    /// number means a different slot on a different model. Kept only so older settings.json files
    /// round-trip; migrated once at load, then never read or written.</summary>
    public Dictionary<byte, string> ProfileNames { get; set; } = new();

    /// <summary>App-side labels for onboard profile slots, keyed by mouse display name
    /// (RazerProtocol.SupportedMice) then slot number (1..5). The firmware stores no names — a slot
    /// is a number + LED colour — so these exist only in the dashboard's Profile card.</summary>
    public Dictionary<string, Dictionary<byte, string>> ProfileNamesByMouse { get; set; } = new();

    /// <summary>Which mouse the app should talk to, as a RazerProtocol.SupportedMice display name.
    /// Null (the default) means "use scan order", which already prefers the newest model. An unknown
    /// name behaves as null (spec 2026-08-19 §4.1).</summary>
    public string? PreferredMouse { get; set; } = null;
}
