using System.Threading;
using System.Threading.Tasks;

namespace NagaBatteryTray.Hid;

public interface IRazerDevice : IDisposable
{
    Task<BatteryReading> ReadAsync(CancellationToken ct);
    Task<DpiSetting?> GetDpiAsync(CancellationToken ct);
    Task<bool> SetDpiAsync(int dpiX, int dpiY, CancellationToken ct);

    /// <summary>Write one raw button action (category + data) to a profile: 0x00 = volatile direct,
    /// 0x01..0x05 = onboard slot. The app only ever writes its own adopted slot — a user's existing
    /// slots must never be a target. True = the firmware acked the SET (status 0x02).</summary>
    Task<bool> SetButtonAsync(byte profile, byte buttonId, byte category, byte[] data, CancellationToken ct);

    /// <summary>Read a button's current action from a profile (0x00 direct / 0x01..0x05 onboard).
    /// Null = unreachable or invalid reply.</summary>
    Task<RawButtonAction?> GetButtonAsync(byte profile, byte buttonId, CancellationToken ct);

    /// <summary>Onboard profile inventory (0x05/0x81). Null = unreachable or invalid reply.</summary>
    Task<ProfileList?> GetProfileListAsync(CancellationToken ct);

    /// <summary>Create an onboard profile slot (0x05/0x02). True = firmware acked (status 0x02).</summary>
    Task<bool> CreateProfileAsync(byte slot, CancellationToken ct);

    /// <summary>Drop any cached connection so the next call re-selects the active interface. Used after a
    /// device-change (e.g. USB-C plug flips wireless&lt;-&gt;wired) where the cached handle would read stale.</summary>
    void Reset();
}
