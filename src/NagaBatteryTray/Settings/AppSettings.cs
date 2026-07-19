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
}
