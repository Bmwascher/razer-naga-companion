using NagaBatteryTray.Hid;

public sealed class FakeRazerDevice : IRazerDevice
{
    private readonly Queue<BatteryReading> _queue = new();
    public void Enqueue(BatteryReading r) => _queue.Enqueue(r);
    public Task<BatteryReading> ReadAsync(CancellationToken ct) =>
        Task.FromResult(_queue.Count > 0 ? _queue.Dequeue() : BatteryReading.Absent(DateTimeOffset.Now));

    public DpiSetting? Dpi { get; set; }
    public bool SetDpiResult { get; set; } = true;
    public int LastSetX { get; private set; }
    public int LastSetY { get; private set; }
    public Task<DpiSetting?> GetDpiAsync(CancellationToken ct) => Task.FromResult(Dpi);
    public Task<bool> SetDpiAsync(int dpiX, int dpiY, CancellationToken ct)
    {
        LastSetX = dpiX; LastSetY = dpiY;
        return Task.FromResult(SetDpiResult);
    }

    public int ResetCount { get; private set; }
    public void Reset() => ResetCount++;

    public void Dispose() { }
}
