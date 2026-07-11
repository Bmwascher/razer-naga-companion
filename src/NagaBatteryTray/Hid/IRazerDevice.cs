using System.Threading;
using System.Threading.Tasks;

namespace NagaBatteryTray.Hid;

public interface IRazerDevice : IDisposable
{
    Task<BatteryReading> ReadAsync(CancellationToken ct);
    Task<DpiSetting?> GetDpiAsync(CancellationToken ct);
    Task<bool> SetDpiAsync(int dpiX, int dpiY, CancellationToken ct);

    /// <summary>Write one raw button action (category + data) to the VOLATILE direct profile (0x00).
    /// Never touches onboard profiles. True = the firmware acked the SET (status 0x02).</summary>
    Task<bool> SetButtonAsync(byte buttonId, byte category, byte[] data, CancellationToken ct);

    /// <summary>Read a button's current effective action from the direct profile. Null = unreachable
    /// or invalid reply.</summary>
    Task<RawButtonAction?> GetButtonAsync(byte buttonId, CancellationToken ct);

    /// <summary>Drop any cached connection so the next call re-selects the active interface. Used after a
    /// device-change (e.g. USB-C plug flips wireless&lt;-&gt;wired) where the cached handle would read stale.</summary>
    void Reset();
}
