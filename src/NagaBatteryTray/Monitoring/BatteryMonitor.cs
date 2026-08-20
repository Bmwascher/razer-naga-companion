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

    /// <summary>Outcome of one atomic button write. Slot is the slot actually used (null = it could
    /// not be established). ConnectionChanged = the mouse underneath changed between the entry id
    /// (see below) and the id observed just before returning, so the caller must re-seed rather than
    /// trust anything it cached. Connection is the id the operation actually ran under - the final id
    /// observed inside the lock, equal to the entry id whenever nothing was adopted.
    /// <para>ConnectionChanged only covers the interval inside THIS lock acquisition; it cannot see a
    /// hop that landed before the lock was taken. A caller holding an undo snapshot must instead
    /// compare Connection against the id its snapshot was taken under - that catches a pre-lock hop
    /// too, which ConnectionChanged alone would miss.</para>
    /// <para>Model is the connected MODEL the operation ran under, captured at entry inside the lock.
    /// It is the identity a caller compares ACROSS TIME (is this undo snapshot still about the mouse
    /// in front of me), because Connection increases on every adoption including a reconnect to the
    /// same mouse - comparing ids across time reds a landed write over an unrelated USB event
    /// (spec 4.10 point 3).</para></summary>
    public readonly record struct ButtonWriteResult(
        bool Verified, byte? Slot, bool ConnectionChanged, int Connection, string? Model);

    /// <summary>Read the active slot, write, and read back to verify - all inside ONE read-lock
    /// acquisition. Split across three acquisitions (as it was before 2026-08-19) a reselect can land
    /// between the resolve and the write, and one mouse's slot number gets written to another;
    /// clearing a cache cannot undo that, because the slot is already a local (spec 4.10).
    /// <para>The slot is ALWAYS read here rather than taken from the caller's cache. That is what makes
    /// the target belong to the mouse being written to: the read forces any pending reconnect to happen
    /// BEFORE a slot is committed to, at the cost of one extra device read per write. A failed read
    /// fails the write - writing a cached slot to a mouse we could not interrogate is the whole
    /// defect.</para>
    /// <para>createdSlots null = inventory unknown, which never blocks; a known inventory that lacks
    /// the slot refuses before any write.</para>
    /// <para>On the path where the lock was never entered (the WaitAsync failure) Connection is a
    /// best-effort read of whatever mouse is current, tied to nothing this operation did; Verified is
    /// false there, so no caller acts on it.</para></summary>
    public async Task<ButtonWriteResult> WriteButtonVerifiedAsync(
        byte[]? createdSlots, byte buttonId, byte category, byte[] data)
    {
        try { await _readLock.WaitAsync(_cts.Token); }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { return new(false, null, false, _device.ConnectionId, _device.ConnectedModel); }

        // captured BEFORE the slot read, because the slot read is itself the device call that can
        // trigger a reconnect: a dropped handle re-scans on the next call, possibly onto a different
        // mouse, and that can happen even when the read then fails to parse. Every return path below
        // reports ConnectionChanged against THIS id, never a later one, so an adoption that happens
        // during a failed slot read is never reported as "connection unchanged".
        // The model is captured in the same breath, and is what the caller compares across time.
        int entry = _device.ConnectionId;
        string? entryModel = _device.ConnectedModel;
        try
        {
            if (await _device.GetActiveProfileAsync(_cts.Token) is not { } slot)
                return new(false, null, _device.ConnectionId != entry, _device.ConnectionId, entryModel);
            if (createdSlots is not null && Array.IndexOf(createdSlots, slot) < 0)
                return new(false, slot, _device.ConnectionId != entry, _device.ConnectionId, entryModel);

            bool ok = await _device.SetButtonAsync(slot, buttonId, category, data, _cts.Token);
            if (ok)
            {
                var readBack = await _device.GetButtonAsync(slot, buttonId, _cts.Token);
                ok = readBack is { } r && r.Category == category && r.Data.AsSpan().SequenceEqual(data);
            }
            bool changed = _device.ConnectionId != entry;
            return new(ok && !changed, slot, changed, _device.ConnectionId, entryModel);
        }
        catch (OperationCanceledException) { return new(false, null, _device.ConnectionId != entry, _device.ConnectionId, entryModel); }
        finally { _readLock.Release(); }
    }

    /// <summary>Which mouse the app should talk to (spec 2026-08-19 §4.1). Set-only plumbing: the
    /// device acts on it at its next battery read, so this never blocks and never takes the lock.</summary>
    public string? PreferredMouse
    {
        get => _device.PreferredModel;
        set => _device.PreferredModel = value;
    }

    /// <summary>Which mouse the app is talking to right now, as an identity that changes on every
    /// adoption (spec §4.10). Callers capture it before a multi-step operation and discard results
    /// whose id no longer matches.</summary>
    public int ConnectionId => _device.ConnectionId;

    /// <summary>Which mouse the app is talking to, by MODEL — the identity to compare ACROSS TIME
    /// (spec §4.10 point 3). Plain read, no lock, like <see cref="ConnectionId"/>.</summary>
    public string? ConnectedModel => _device.ConnectedModel;

    /// <summary>Which supported mice answer right now, for the dashboard's selector. Blocks for the
    /// read lock like every other pass-through — this is several device probes, so it must not
    /// interleave with the battery poll. Called on dashboard open / refresh only, never polled.
    /// <para>Republishes State before returning: discovery reconnects at the end and can land on a
    /// different mouse than State names, and State only ever updates from a battery read. Without
    /// this the selector and the profile-name key describe the wrong mouse (spec §4.4).</para></summary>
    public async Task<IReadOnlyList<string>> DiscoverMiceAsync()
    {
        try { await _readLock.WaitAsync(_cts.Token); }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { return Array.Empty<string>(); }
        try
        {
            var found = await _device.DiscoverReachableAsync(_cts.Token);
            ProcessReading(await _device.ReadAsync(_cts.Token)); // same acquisition: no one can slip between
            return found;
        }
        catch (OperationCanceledException) { return Array.Empty<string>(); }
        finally { _readLock.Release(); }
    }

    /// <summary>Refresh behind an EXPLICIT user action (a mouse pick). Identical to RefreshNowAsync
    /// except that it WAITS for the read lock instead of skipping when one is busy — the skip is
    /// right for a timer tick and wrong for a user, who would see a perfectly reachable mouse
    /// reported as unreachable because a sweep happened to be in flight (spec §4.5).</summary>
    public Task RefreshNowWaitingAsync() => PollAsync(reconnect: true, waitForLock: true);

    private async Task PollAsync(bool reconnect = false, bool waitForLock = false)
    {
        if (waitForLock)
        {
            try { await _readLock.WaitAsync(_cts.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { return; }
        }
        else if (!await _readLock.WaitAsync(0)) return; // a read is already in flight; skip
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

        SetState(DeviceState.Online(r.Percent, r.IsCharging, r.IsWired, r.Model));
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
