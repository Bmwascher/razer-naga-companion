using NagaBatteryTray.Settings;

/// <summary>An ISettingsStore that keeps everything in memory. JsonSettingsStore WRITES on
/// construction when its file does not exist, and again on SetCachedTransactionId, so the suite's
/// usual `new JsonSettingsStore(Path.GetTempPath() + guid)` leaves a file behind per store. The perf
/// bench builds one per repetition of every scenario - about forty files for a single run - which is
/// what made it worth having a store that touches no disk at all (Fable diff r1, F7).
/// <para>Tests that assert on PERSISTENCE still want the real thing (SettingsStoreTests is about the
/// file format, and its migration cases only mean anything against a real file). This is for the
/// callers that merely need somewhere for a cached transaction id and a wait setting to live.</para>
/// </summary>
internal sealed class FakeSettingsStore : ISettingsStore
{
    public AppSettings Settings { get; } = new();

    /// <summary>Save calls seen. Nothing asserts on it today; it is here so a caller that needs to
    /// prove a write happened does not have to reach for a temp file to do it.</summary>
    public int Saves { get; private set; }

    public void Save() => Saves++;

    public byte? GetCachedTransactionId()
    {
        var s = Settings.CachedTransactionId;
        if (string.IsNullOrWhiteSpace(s)) return null;
        try { return Convert.ToByte(s, 16); } catch { return null; }
    }

    public void SetCachedTransactionId(byte id)
    {
        Settings.CachedTransactionId = $"0x{id:x2}";
        Save();
    }
}
