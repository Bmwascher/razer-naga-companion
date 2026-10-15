using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NagaBatteryTray.Settings;

namespace NagaBatteryTray.Hid;

/// <summary>
/// Reads battery/charging from a supported Razer Naga (V2 Pro / V3 Pro, RazerProtocol.SupportedMice).
/// The 90-byte Razer feature report lives on the mouse HID collection (mi_00 on V2, mi_03 on V3 —
/// never key on interface number; GetMaxFeatureReportLength == 91 is the selector), which Windows owns — so it must be
/// opened with CreateFile(dwDesiredAccess = 0) and exchanged via HidD_SetFeature/HidD_GetFeature.
/// (No collection exposes usage page 0xFF00 on this device; verified empirically.)
/// </summary>
public sealed class RazerDevice : IRazerDevice
{
    private readonly ISettingsStore _settings;
    private readonly MouseConnection _conn;
    private bool _loggedError;

    public RazerDevice(ISettingsStore settings)
        : this(settings, new HidControlCollectionSource()) { }

    internal RazerDevice(ISettingsStore settings, IControlCollectionSource source)
    {
        _settings = settings;
        _conn = new MouseConnection(source, VerifyCollectionAsync);
    }

    public string? PreferredModel
    {
        get => _conn.PreferredModel;
        set => _conn.PreferredModel = value;
    }

    public int ConnectionId => _conn.ConnectionId;

    public string? ConnectedModel => _conn.Model;

    /// <summary>MouseConnection's protocol hook: resolve a transaction id on a candidate and confirm
    /// it answers a battery query. Runs against the candidate directly, because it is not the live
    /// collection yet.</summary>
    private async Task<byte> VerifyCollectionAsync(IControlCollection candidate, CancellationToken ct)
    {
        byte tid = await ResolveTransactionIdAsync(candidate, ct);
        if (tid == 0) return 0;
        return await QueryAsync(candidate, tid, RazerProtocol.CommandIdBattery, ct, fast: true) is not null ? tid : (byte)0;
    }

    public async Task<BatteryReading> ReadAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        try
        {
            byte tid = await _conn.EnsureAsync(ct, reselect: true);
            if (tid == 0 || _conn.Current is not { } c) return BatteryReading.Absent(now);

            var battery = await QueryAsync(c, tid, RazerProtocol.CommandIdBattery, ct, fast: true);
            // A live handle that stops answering (e.g. the wireless receiver after the mouse switches to
            // wired) replies with a timeout status, not a HID error — so drop it here to force the next
            // poll to re-select whichever collection is now live.
            if (battery is null) { _conn.Close(); return BatteryReading.Absent(now); }

            var charging = await QueryAsync(c, tid, RazerProtocol.CommandIdCharging, ct, fast: true);
            int percent = RazerProtocol.ScaleBattery(battery.Value);
            bool isCharging = charging is not null && charging.Value != 0;
            _loggedError = false;
            return new BatteryReading(percent, isCharging, true, now, _conn.Wired, _conn.Model);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _conn.Close();
            LogOnce(ex);
            return BatteryReading.Absent(now);
        }
    }

    /// <summary>Returns cached id, else probes the set and caches the winner. 0 = could not resolve.</summary>
    private async Task<byte> ResolveTransactionIdAsync(IControlCollection collection, CancellationToken ct)
    {
        var cached = _settings.GetCachedTransactionId();
        if (cached is not null) return cached.Value;

        foreach (byte tid in RazerProtocol.TransactionIdProbeSet)
        {
            if (!collection.IsOpen) return 0;   // transport is gone; this candidate is finished
            var value = await QueryAsync(collection, tid, RazerProtocol.CommandIdBattery, ct);
            if (value is not null && RazerProtocol.ScaleBattery(value.Value) is >= 0 and <= 100)
            {
                _settings.SetCachedTransactionId(tid);
                return tid;
            }
        }
        return 0;
    }

    /// <summary>One SET->wait->GET round-trip with one busy retry. Returns the raw 91-byte reply or null on failure.
    /// On a failed HID call the collection is closed so the next EnsureAsync re-acquires.</summary>
    private async Task<byte[]?> ExchangeAsync(IControlCollection collection, byte[] request, CancellationToken ct)
    {
        if (!collection.IsOpen) return null;
        if (!collection.SetFeature(request)) { _conn.Close(); return null; }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            await Task.Delay(_settings.Settings.SetReadDelayMs, ct);
            var reply = new byte[RazerProtocol.BufferLength];
            reply[0] = 0x00;
            if (!collection.GetFeature(reply)) { _conn.Close(); return null; }
            if (reply[1] == 0x01) { await Task.Delay(ExchangeBusyWaitMs, ct); continue; } // Busy: wait, retry GET once
            return reply;
        }
        return null; // still busy after retries
    }

    /// <summary>Waits BETWEEN GETs for one fast read. Measured on a V3 Pro (--probe-latency,
    /// 2026-10-15): every command class - battery, charging, DPI, profile list, active slot, and the
    /// grid's own button reads - was ready within 22-24 ms. That is an upper bound, since the probe
    /// cannot observe sooner than Windows' ~15.6 ms timer tick. The first rung used to be 50 ms, so
    /// every read waited about twice as long as the mouse actually needed, twelve times per sweep.
    /// <para>30 ms is the practical floor rather than a guess: Task.Delay rounds up to that same
    /// ~15.6 ms tick for anything from 16 to 31 ms, and raising the system timer resolution to beat
    /// it would cost idle power (hard rule 1). The later rungs are then chosen so the CUMULATIVE
    /// wait after each one still matches the old ladder's checkpoints - 30, 50, 150, 350, 750 - and
    /// the last rung carries the total to 1000 ms, which is what ExchangeAsync's flat wait PLUS its
    /// busy retry really gave a converted read (see ReadPatience). So this is strictly
    /// faster for a mouse that answers promptly and never slower for one that does not. The price is
    /// one extra GET on a read that needs the whole schedule.</para></summary>
    private static readonly int[] FastReadRungs = { 30, 20, 100, 200, 400, 250 };

    /// <summary>The wait ExchangeAsync inserts after a BUSY (0x01) reply before retrying its GET.
    /// Shared with ConvertedReadBudgetMs so the two policies cannot drift apart.</summary>
    private const int ExchangeBusyWaitMs = 200;

    /// <summary>The patience a read converted from ExchangeAsync gets, as the TWO numbers
    /// ExchangeAsync really had. It waited SetReadDelayMs once and then looked: on anything but a
    /// BUSY (0x01) reply it stopped there and the caller got a failed read, and only a 0x01 bought
    /// another ExchangeBusyWaitMs and a SECOND SetReadDelayMs wait.
    /// <para>One cap cannot be both, and either one alone is a defect. SetReadDelayMs alone cuts a
    /// busy mouse's patience from ~1000 ms to 400 ms - and busy/asleep is the state this mouse is in
    /// most of the time - so a would-be success becomes a null read, which ReadAsync then treats as
    /// a dead handle (Fable diff r1, F1). The full 1000 ms alone makes a collection that answers
    /// NOTHING cost 1000 ms where it used to cost 400, because FastReadAsync rightly reads a 0x00
    /// echo as "not ready yet" where ExchangeAsync took it as the final answer; that is the
    /// dashboard's discovery path with a dead receiver still plugged in, and the bench measured the
    /// 600 ms it costs. So the budget starts at BaseMs and is raised to AfterBusyMs only once a 0x01
    /// has actually been seen, which reproduces both behaviours instead of averaging
    /// them.</para></summary>
    private readonly record struct ReadPatience(int BaseMs, int AfterBusyMs);

    private ReadPatience ConvertedReadPatience =>
        new(_settings.Settings.SetReadDelayMs,
            _settings.Settings.SetReadDelayMs * 2 + ExchangeBusyWaitMs);

    /// <summary>Fast SET->GET for idempotent READS: poll early on the FastReadRungs schedule
    /// instead of paying the battery-cadence SetReadDelayMs per read — the grid sweep is 12 reads, so
    /// the flat 400 ms wait made it ~5 s (user find). Safe because a premature GET is detectable and
    /// retryable: the device buffer still holds our request echo (status 0x00), a busy marker (0x01),
    /// or the PREVIOUS command's finished reply — the completed-status + class/cmd echo check treats
    /// all three as "not ready, wait longer". Writes and the transaction-id probe stay on
    /// ExchangeAsync: a write ack is not idempotent and the probe is interrogating an UNKNOWN
    /// transaction id, so both keep their proven timing semantics exactly.
    /// <para>patience caps the TOTAL wait, so a converted read polls early without ever being LESS
    /// patient OR slower to give up than the ExchangeAsync policy it replaced - see ReadPatience for
    /// why that takes two numbers rather than one. A budget at or above the schedule's 1000 ms sum
    /// runs every rung; a smaller one is clamped rung by rung and always gets at least one GET; a
    /// larger one is honoured by the last rung. Null keeps the plain schedule for the button/profile
    /// reads, which never used ExchangeAsync and so have no old behaviour to match.</para></summary>
    private async Task<byte[]?> FastReadAsync(IControlCollection collection, byte[] request, CancellationToken ct,
                                              int echoArgBytes = 0, ReadPatience? patience = null)
    {
        if (!collection.IsOpen) return null;
        if (!collection.SetFeature(request)) { _conn.Close(); return null; }

        int spent = 0;
        bool sawBusy = false;
        for (int i = 0; i < FastReadRungs.Length; i++)
        {
            int delay = FastReadRungs[i];
            if (patience is { } p)
            {
                int budget = sawBusy ? p.AfterBusyMs : p.BaseMs;
                // i > 0, so a budget of 0 or less still gets ONE poll: ExchangeAsync always did
                // Task.Delay(it) and then a GET, and a configured 0 must not mean "never read"
                // (Fable diff r1, F5).
                if (i > 0 && spent >= budget) break;
                delay = i == FastReadRungs.Length - 1
                    ? Math.Max(0, budget - spent)        // last rung absorbs a budget above the schedule
                    : Math.Clamp(budget - spent, 0, delay);
            }
            await Task.Delay(delay, ct);
            spent += delay;
            var reply = new byte[RazerProtocol.BufferLength];
            reply[0] = 0x00;
            if (!collection.GetFeature(reply)) { _conn.Close(); return null; }
            // the readiness rule itself lives in RazerProtocol, shared with --probe-latency so the
            // probe measures against the same predicate these rungs are tuned to (F8)
            if (RazerProtocol.IsReplyReady(request, reply, echoArgBytes)) return reply;
            if (reply[1] == 0x01) sawBusy = true;   // busy earns the second half of the patience
        }
        return null; // never became ready — caller treats like any failed read
    }

    /// <summary>SET->GET a power-class query and return the data byte, or null on failure.
    /// <para>fast polls early (FastReadAsync) within the same total budget the flat wait used. A
    /// power query is an idempotent read, and the class/cmd echo is what makes early polling safe
    /// here: battery (0x07/0x80) and charging (0x07/0x84) are issued back to back, so a premature GET
    /// that still held the battery reply would otherwise be read as the charging flag. Hardware
    /// confirms the echo — --probe-dpi on 2026-10-15 showed reply[7]/[8] echoed on both an awake and
    /// an asleep mouse. The transaction-id probe passes false: it is resolving an unknown id, where a
    /// stale finished reply from the PREVIOUS candidate id would read as that id working.</para></summary>
    private async Task<byte?> QueryAsync(IControlCollection collection, byte transactionId, byte commandId,
                                         CancellationToken ct, bool fast = false)
    {
        var request = RazerProtocol.BuildFeatureBuffer(transactionId, commandId);
        var reply = fast
            ? await FastReadAsync(collection, request, ct, patience: ConvertedReadPatience)
            : await ExchangeAsync(collection, request, ct);
        if (reply is null) return null;
        return RazerProtocol.ParseReply(reply, out byte value) == ReplyResult.Success ? value : null;
    }

    public async Task<DpiSetting?> GetDpiAsync(CancellationToken ct)
    {
        try
        {
            byte tid = await _conn.EnsureAsync(ct);
            if (tid == 0 || _conn.Current is not { } c) return null;
            // reply data REPLACES the request args, so echoArgBytes stays 0: the class/cmd echo is
            // the whole readiness check here, exactly as for the profile list/active reads.
            var reply = await FastReadAsync(c, RazerProtocol.BuildGetDpiBuffer(tid), ct,
                                            patience: ConvertedReadPatience);
            if (reply is null) return null;
            if (RazerProtocol.ParseDpiReply(reply, out int x, out int y) != ReplyResult.Success) return null;
            return new DpiSetting(x, y);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _conn.Close();
            LogOnce(ex);
            return null;
        }
    }

    public async Task<bool> SetDpiAsync(int dpiX, int dpiY, CancellationToken ct)
    {
        try
        {
            byte tid = await _conn.EnsureAsync(ct);
            if (tid == 0 || _conn.Current is not { } c) return false;
            var reply = await ExchangeAsync(c, RazerProtocol.BuildSetDpiBuffer(tid, dpiX, dpiY), ct);
            if (reply is null) return false;
            return RazerProtocol.ParseDpiReply(reply, out _, out _) == ReplyResult.Success;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _conn.Close();
            LogOnce(ex);
            return false;
        }
    }

    public async Task<bool> SetButtonAsync(byte profile, byte buttonId, byte category, byte[] data, CancellationToken ct)
    {
        try
        {
            string? before = _conn.Model;
            byte tid = await _conn.EnsureAsync(ct);
            if (tid == 0 || _conn.Current is not { } c) return false;
            // a reconnect inside this call may have landed on ANOTHER mouse, and the caller's profile
            // number was computed for the previous one. Refuse rather than write it there. Close()
            // leaves Model set, so a dropped handle still leaves a name to compare against.
            if (before is not null && !string.Equals(before, _conn.Model, StringComparison.OrdinalIgnoreCase))
                return false;
            var reply = await ExchangeAsync(c, RazerProtocol.BuildSetButtonBuffer(
                tid, profile, buttonId, 0x00, category, data), ct);
            // SET ack: status-only (the spike-proven check); correctness is covered by read-back verify.
            return reply is not null && reply[1] == 0x02;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _conn.Close();
            LogOnce(ex);
            return false;
        }
    }

    public async Task<RawButtonAction?> GetButtonAsync(byte profile, byte buttonId, CancellationToken ct)
    {
        try
        {
            byte tid = await _conn.EnsureAsync(ct);
            if (tid == 0 || _conn.Current is not { } c) return null;
            var reply = await FastReadAsync(c, RazerProtocol.BuildGetButtonBuffer(
                tid, profile, buttonId, 0x00), ct, echoArgBytes: 3); // profile/buttonId/hypershift echo
            if (reply is null) return null;
            if (RazerProtocol.ParseButtonReply(reply, profile, buttonId, 0x00,
                    out byte category, out byte[] data) != ReplyResult.Success)
                return null;
            return new RawButtonAction(category, data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _conn.Close();
            LogOnce(ex);
            return null;
        }
    }

    public async Task<ProfileList?> GetProfileListAsync(CancellationToken ct)
    {
        try
        {
            byte tid = await _conn.EnsureAsync(ct);
            if (tid == 0 || _conn.Current is not { } c) return null;
            var reply = await FastReadAsync(c, RazerProtocol.BuildGetProfileListBuffer(tid), ct);
            if (reply is null) return null;
            if (RazerProtocol.ParseProfileListReply(reply, out byte capacity, out byte[] slots) != ReplyResult.Success)
                return null;
            return new ProfileList(capacity, slots);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _conn.Close();
            LogOnce(ex);
            return null;
        }
    }

    public async Task<bool> CreateProfileAsync(byte slot, CancellationToken ct)
    {
        try
        {
            byte tid = await _conn.EnsureAsync(ct);
            if (tid == 0 || _conn.Current is not { } c) return false;
            var reply = await ExchangeAsync(c, RazerProtocol.BuildNewProfileBuffer(tid, slot), ct);
            return reply is not null && reply[1] == 0x02;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _conn.Close();
            LogOnce(ex);
            return false;
        }
    }

    public async Task<byte?> GetActiveProfileAsync(CancellationToken ct)
    {
        try
        {
            byte tid = await _conn.EnsureAsync(ct);
            if (tid == 0 || _conn.Current is not { } c) return null;
            var reply = await FastReadAsync(c, RazerProtocol.BuildGetActiveProfileBuffer(tid), ct);
            if (reply is null) return null;
            if (RazerProtocol.ParseActiveProfileReply(reply, out byte slot) != ReplyResult.Success) return null;
            return slot;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _conn.Close();
            LogOnce(ex);
            return null;
        }
    }

    public async Task<bool> SetActiveProfileAsync(byte slot, CancellationToken ct)
    {
        try
        {
            byte tid = await _conn.EnsureAsync(ct);
            if (tid == 0 || _conn.Current is not { } c) return false;
            var reply = await ExchangeAsync(c, RazerProtocol.BuildSetActiveProfileBuffer(tid, slot), ct);
            return reply is not null && reply[1] == 0x02;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _conn.Close();
            LogOnce(ex);
            return false;
        }
    }

    public Task<IReadOnlyList<string>> DiscoverReachableAsync(CancellationToken ct) => _conn.DiscoverAsync(ct);

    private void LogOnce(Exception ex)
    {
        if (_loggedError) return;
        _loggedError = true;
        Console.Error.WriteLine($"[RazerDevice] {ex.GetType().Name}: {ex.Message}");
    }

    /// <summary>Drop the cached connection so the next call re-selects whichever interface is now live. Cheap;
    /// the next <see cref="MouseConnection.EnsureAsync"/> re-acquires (wired-first, verifying it answers).</summary>
    public void Reset() => _conn.Close();

    public void Dispose() => _conn.Dispose();
}
