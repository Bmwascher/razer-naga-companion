using NagaBatteryTray.Hid;

public sealed class FakeRazerDevice : IRazerDevice
{
    private readonly Queue<BatteryReading> _queue = new();
    public void Enqueue(BatteryReading r) => _queue.Enqueue(r);
    public Task<BatteryReading> ReadAsync(CancellationToken ct) =>
        Task.FromResult(_queue.Count > 0 ? _queue.Dequeue() : BatteryReading.Absent(DateTimeOffset.Now));
    public void Dispose() { }
}
