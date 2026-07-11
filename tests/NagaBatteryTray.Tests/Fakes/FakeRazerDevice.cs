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

    public sealed record ButtonWrite(byte ButtonId, byte Category, byte[] Data);
    public List<ButtonWrite> ButtonWrites { get; } = new();
    public bool SetButtonResult { get; set; } = true;
    public Dictionary<byte, RawButtonAction> ButtonActions { get; } = new();

    public Task<bool> SetButtonAsync(byte buttonId, byte category, byte[] data, CancellationToken ct)
    {
        ButtonWrites.Add(new ButtonWrite(buttonId, category, data));
        return Task.FromResult(SetButtonResult);
    }

    public int GetButtonCount { get; private set; }

    public Task<RawButtonAction?> GetButtonAsync(byte buttonId, CancellationToken ct)
    {
        GetButtonCount++;
        return Task.FromResult(ButtonActions.TryGetValue(buttonId, out var a) ? a : (RawButtonAction?)null);
    }

    public int ResetCount { get; private set; }
    public void Reset() => ResetCount++;

    public void Dispose() { }
}
