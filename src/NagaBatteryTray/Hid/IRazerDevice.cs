using System.Threading;
using System.Threading.Tasks;

namespace NagaBatteryTray.Hid;

public interface IRazerDevice : IDisposable
{
    Task<BatteryReading> ReadAsync(CancellationToken ct);
}
