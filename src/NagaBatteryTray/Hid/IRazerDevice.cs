using System.Threading;
using System.Threading.Tasks;

namespace NagaBatteryTray.Hid;

public interface IRazerDevice : IDisposable
{
    Task<BatteryReading> ReadAsync(CancellationToken ct);
    Task<DpiSetting?> GetDpiAsync(CancellationToken ct);
    Task<bool> SetDpiAsync(int dpiX, int dpiY, CancellationToken ct);

    /// <summary>Drop any cached connection so the next call re-selects the active interface. Used after a
    /// device-change (e.g. USB-C plug flips wireless&lt;-&gt;wired) where the cached handle would read stale.</summary>
    void Reset();
}
