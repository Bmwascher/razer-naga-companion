namespace NagaBatteryTray.Settings;

public sealed class AppSettings
{
    public int PollIntervalSeconds { get; set; } = 60;
    public int PollIntervalChargingSeconds { get; set; } = 15;
    public int LowBatteryThreshold { get; set; } = 15;
    public bool LowBatteryNotify { get; set; } = true;
    public string? CachedTransactionId { get; set; } = null; // e.g. "0x1f"; null = unprobed
    public int SetReadDelayMs { get; set; } = 400;

    /// <summary>Thumb-grid remaps keyed by grid position (1..12); sparse — only non-Default buttons.</summary>
    public Dictionary<int, ButtonBindingSetting> ButtonBindings { get; set; } = new();

    /// <summary>The onboard profile slot this app created and owns (bindings are written there and
    /// persist in the mouse's own memory). Null = not adopted yet. Never a user's pre-existing slot.</summary>
    public int? OnboardSlot { get; set; } = null;
}
