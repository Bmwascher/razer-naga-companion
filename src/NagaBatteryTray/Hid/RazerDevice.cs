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
            if (reply[1] == 0x01) { await Task.Delay(200, ct); continue; } // Busy: wait, retry GET once
            return reply;
        }
        return null; // still busy after retries
    }

    private const int FastReadInitialDelayMs = 50;

    /// <summary>Fast SET->GET for idempotent READS: poll early on a doubling ladder (50→100→200→400 ms)
    /// instead of paying the battery-cadence SetReadDelayMs per read — the grid sweep is 12 reads, so
    /// the flat 400 ms wait made it ~5 s (user find). Safe because a premature GET is detectable and
    /// retryable: the device buffer still holds our request echo (status 0x00), a busy marker (0x01),
    /// or the PREVIOUS command's finished reply — the completed-status + class/cmd echo check treats
    /// all three as "not ready, wait longer". Writes and the transaction-id probe stay on
    /// ExchangeAsync: a write ack is not idempotent and the probe is interrogating an UNKNOWN
    /// transaction id, so both keep their proven timing semantics exactly.
    /// <para>budgetMs caps the TOTAL wait. It exists so a read that used to pay one flat
    /// SetReadDelayMs can poll early without ever waiting longer in the worst case than it did
    /// before: with a 400 ms budget the rungs become 50/100/200/50, so an awake mouse answers at
    /// 50 ms and a silent one is given up on at exactly the old 400 ms. Null keeps the original
    /// 4-rung, 750 ms ladder for the button/profile reads that shipped with it.</para></summary>
    private async Task<byte[]?> FastReadAsync(IControlCollection collection, byte[] request, CancellationToken ct,
                                              int echoArgBytes = 0, int? budgetMs = null)
    {
        if (!collection.IsOpen) return null;
        if (!collection.SetFeature(request)) { _conn.Close(); return null; }

        int delay = FastReadInitialDelayMs;
        int spent = 0;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (budgetMs is { } budget)
            {
                if (spent >= budget) break;
                delay = Math.Min(delay, budget - spent);
            }
            await Task.Delay(delay, ct);
            spent += delay;
            var reply = new byte[RazerProtocol.BufferLength];
            reply[0] = 0x00;
            if (!collection.GetFeature(reply)) { _conn.Close(); return null; }
            // ready = a completed status AND the reply echoes OUR class+cmd (buffer[7]/[8] =
            // report class/cmd — every Razer reply echoes them; the parses already rely on it).
            // echoArgBytes extends the check over the request's leading arg bytes (buffer[9..]):
            // the grid sweep issues 12 same-class/cmd requests back-to-back, so a stale finished
            // reply from the PREVIOUS button would otherwise pass and read as a hard failure at
            // the parse layer instead of "wait longer" (review find). Only usable when the
            // request args are echoed rather than replaced by reply data (button reads echo
            // profile/buttonId/hypershift; list/active reads carry data there — pass 0).
            bool pending = reply[1] is 0x00 or 0x01
                || reply[7] != request[7] || reply[8] != request[8];
            for (int i = 0; !pending && i < echoArgBytes; i++)
                if (reply[9 + i] != request[9 + i]) pending = true;
            if (!pending) return reply;
            delay = Math.Min(delay * 2, 400);
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
            ? await FastReadAsync(collection, request, ct, budgetMs: _settings.Settings.SetReadDelayMs)
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
                                            budgetMs: _settings.Settings.SetReadDelayMs);
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
