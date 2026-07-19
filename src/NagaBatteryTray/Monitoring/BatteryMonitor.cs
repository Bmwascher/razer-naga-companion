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

    /// <summary>Explicit refresh (manual button, wake, USB device-change). Drops the cached HID handle first
    /// so the read re-selects the now-active interface — after a USB-C plug flips the mouse from the wireless
    /// receiver to the wired interface, the stale wireless handle would otherwise keep reporting the old
    /// not-charging state. The frequent background poll keeps reusing the handle for efficiency.</summary>
    public Task RefreshNowAsync() => PollAsync(reconnect: true);

    /// <summary>Read the mouse's active DPI. Blocks for the read lock (never skips) so it can't be dropped
    /// mid-poll, and serializes against the battery poll on the single HID handle.</summary>
    public async Task<DpiSetting?> GetDpiAsync()
    {
        try { await _readLock.WaitAsync(_cts.Token); }
        catch (OperationCanceledException) { return null; }
        try { return await _device.GetDpiAsync(_cts.Token); }
        catch (OperationCanceledException) { return null; }
        finally { _readLock.Release(); }
    }

    /// <summary>Set the mouse's active DPI (persisted on the device). Blocks for the read lock.</summary>
    public async Task<bool> SetDpiAsync(int dpiX, int dpiY)
    {
        try { await _readLock.WaitAsync(_cts.Token); }
        catch (OperationCanceledException) { return false; }
        try { return await _device.SetDpiAsync(dpiX, dpiY, _cts.Token); }
        catch (OperationCanceledException) { return false; }
        finally { _readLock.Release(); }
    }

    /// <summary>Write one raw button action to a profile (0x00 direct / 0x01..0x05 onboard). Blocks for
    /// the read lock (serializes against battery poll + DPI on the single HID handle).</summary>
    public async Task<bool> SetButtonAsync(byte profile, byte buttonId, byte category, byte[] data)
    {
        try { await _readLock.WaitAsync(_cts.Token); }
        catch (OperationCanceledException) { return false; }
        try { return await _device.SetButtonAsync(profile, buttonId, category, data, _cts.Token); }
        catch (OperationCanceledException) { return false; }
        finally { _readLock.Release(); }
    }

    /// <summary>Read a button's current action from a profile. Blocks for the read lock.</summary>
    public async Task<RawButtonAction?> GetButtonAsync(byte profile, byte buttonId)
    {
        try { await _readLock.WaitAsync(_cts.Token); }
        catch (OperationCanceledException) { return null; }
        try { return await _device.GetButtonAsync(profile, buttonId, _cts.Token); }
        catch (OperationCanceledException) { return null; }
        finally { _readLock.Release(); }
    }

    /// <summary>Onboard profile inventory. Blocks for the read lock.</summary>
    public async Task<ProfileList?> GetProfileListAsync()
    {
        try { await _readLock.WaitAsync(_cts.Token); }
        catch (OperationCanceledException) { return null; }
        try { return await _device.GetProfileListAsync(_cts.Token); }
        catch (OperationCanceledException) { return null; }
        finally { _readLock.Release(); }
    }

    /// <summary>Create an onboard profile slot. Blocks for the read lock.</summary>
    public async Task<bool> CreateProfileAsync(byte slot)
    {
        try { await _readLock.WaitAsync(_cts.Token); }
        catch (OperationCanceledException) { return false; }
        try { return await _device.CreateProfileAsync(slot, _cts.Token); }
        catch (OperationCanceledException) { return false; }
        finally { _readLock.Release(); }
    }

    /// <summary>Read the active onboard slot. Blocks for the read lock.</summary>
    public async Task<byte?> GetActiveProfileAsync()
    {
        try { await _readLock.WaitAsync(_cts.Token); }
        catch (OperationCanceledException) { return null; }
        try { return await _device.GetActiveProfileAsync(_cts.Token); }
        catch (OperationCanceledException) { return null; }
        finally { _readLock.Release(); }
    }

    /// <summary>Switch the active onboard slot (write-on-action only). Blocks for the read lock.</summary>
    public async Task<bool> SetActiveProfileAsync(byte slot)
    {
        try { await _readLock.WaitAsync(_cts.Token); }
        catch (OperationCanceledException) { return false; }
        try { return await _device.SetActiveProfileAsync(slot, _cts.Token); }
        catch (OperationCanceledException) { return false; }
        finally { _readLock.Release(); }
    }

    private async Task PollAsync(bool reconnect = false)
    {
        if (!await _readLock.WaitAsync(0)) return; // a read is already in flight; skip
        try
        {
            if (reconnect) _device.Reset(); // re-select the active interface for explicit refreshes
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

        SetState(DeviceState.Online(r.Percent, r.IsCharging, r.IsWired));
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
