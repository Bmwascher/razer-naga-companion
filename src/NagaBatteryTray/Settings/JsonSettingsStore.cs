using System.IO;
using System.Text.Json;

namespace NagaBatteryTray.Settings;

public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public AppSettings Settings { get; }

    public JsonSettingsStore(string path)
    {
        _path = path;
        if (File.Exists(_path))
        {
            try
            {
                Settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings();
            }
            catch
            {
                Settings = new AppSettings(); // corrupt file -> defaults
            }
            MigrateProfileNames();
        }
        else
        {
            Settings = new AppSettings();
            Save();
        }
    }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NagaBatteryTray", "settings.json");

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(Settings, Options));
    }

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

    /// <summary>One-time move of slot-keyed profile names under the only mouse that could have
    /// written them. Per-mouse names ship with the 2026-08-19 selector change, so a file carrying
    /// legacy names predates dual support and belongs to the V2 Pro (spec §4.9).</summary>
    private void MigrateProfileNames()
    {
        if (Settings.ProfileNames.Count == 0 || Settings.ProfileNamesByMouse.Count > 0) return;
        Settings.ProfileNamesByMouse["Naga V2 Pro"] = new Dictionary<byte, string>(Settings.ProfileNames);
        Settings.ProfileNames.Clear();
        Save();
    }
}
