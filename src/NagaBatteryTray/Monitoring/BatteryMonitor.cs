using System.Threading;
using System.Threading.Tasks;
using NagaBatteryTray.Hid;
using NagaBatteryTray.Settings;

namespace NagaBatteryTray.Monitoring;

public sealed class BatteryMonitor : IDisposable
{
    private readonly IRazerDevice _device;
    private readonly ISettingsStore _settings;
    private readonly Action<Action> _dispatch;
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private System.Threading.Timer? _timer;
    private bool _armed; // false until we first observe percent > threshold while discharging
    private int _consecutiveMisses;

    public DeviceState State { get; private set; } = DeviceState.Unknown;
    public event EventHandler<DeviceState>? StateChanged;
    public event EventHandler<int>? LowBatteryCrossed;

    public BatteryMonitor(IRazerDevice device, ISettingsStore settings, Action<Action> dispatch)
    {
        _device = device;
        _settings = settings;
        _dispatch = dispatch;
    }

    public void Start()
    {
        _timer = new System.Threading.Timer(_ => _ = PollAsync(), null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    public Task RefreshNowAsync() => PollAsync();

    private async Task PollAsync()
    {
        if (!await _readLock.WaitAsync(0)) return; // a read is already in flight; skip
        try
        {
            var reading = await _device.ReadAsync(_cts.Token);
            ProcessReading(reading);
            ScheduleNext(reading);
        }
        catch (OperationCanceledException) { }
        finally { _readLock.Release(); }
    }

    private void ScheduleNext(BatteryReading reading)
    {
        int seconds = reading is { IsPresent: true, IsCharging: true }
            ? _settings.Settings.PollIntervalChargingSeconds
            : _settings.Settings.PollIntervalSeconds;
        _timer?.Change(TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);
    }

    internal void ProcessReading(BatteryReading r)
    {
        int threshold = _settings.Settings.LowBatteryThreshold;

        if (!r.IsPresent)
        {
            _consecutiveMisses++;
            if (_consecutiveMisses > 3) SetState(DeviceState.Unknown);
            return;
        }
        _consecutiveMisses = 0;

        if (!r.IsCharging)
        {
            if (r.Percent > threshold)
            {
                _armed = true;
            }
            else if (_armed && r.Percent <= threshold)
            {
                _armed = false;
                if (_settings.Settings.LowBatteryNotify)
                    _dispatch(() => LowBatteryCrossed?.Invoke(this, r.Percent));
            }
        }

        SetState(DeviceState.Online(r.Percent, r.IsCharging));
    }

    private void SetState(DeviceState next)
    {
        if (next == State) return;
        State = next;
        _dispatch(() => StateChanged?.Invoke(this, next));
    }

    public void Dispose()
    {
        _cts.Cancel();
        _timer?.Dispose();
        _readLock.Wait(1000); // let any in-flight read finish before the device is disposed elsewhere
        _readLock.Release();
        _cts.Dispose();
    }
}
