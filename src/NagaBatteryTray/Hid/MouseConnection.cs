using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NagaBatteryTray.Hid;

/// <summary>Owns which mouse the app is connected to. Every Win32 call it makes goes through
/// IControlCollection, so the parts that used to be untestable — preference order, when a live
/// connection may be reselected, rollback, disposing exactly the losing handle, and connection
/// identity — are ordinary code with tests (spec 2026-08-19 §4.1-§4.4, §4.10).
/// <para>Protocol stays out of here: the caller supplies <see cref="Verify"/>, which resolves a
/// transaction id on a candidate and confirms it answers. 0 = it does not.</para></summary>
internal sealed class MouseConnection : IDisposable
{
    /// <summary>Resolve + confirm a candidate. Returns the working transaction id, or 0.</summary>
    internal delegate Task<byte> Verify(IControlCollection collection, CancellationToken ct);

    private readonly IControlCollectionSource _source;
    private readonly Verify _verify;
    private IControlCollection? _current;
    private string? _preferred;

    internal MouseConnection(IControlCollectionSource source, Verify verify)
    {
        _source = source;
        _verify = verify;
    }

    /// <summary>The live collection, or null. Callers exchange bytes through it.</summary>
    internal IControlCollection? Current => _current;

    /// <summary>Working transaction id on the live collection; 0 = not connected.</summary>
    internal byte TransactionId { get; private set; }

    /// <summary>True when the live collection is a wired PID.</summary>
    internal bool Wired { get; private set; }

    /// <summary>Display name of the connected model, or null. Deliberately NOT cleared by
    /// <see cref="Close"/>: after a dropped handle it is still the best answer to "which mouse was
    /// this", which the write path compares against (spec §4.10).</summary>
    internal string? Model { get; private set; }

    /// <summary>Increases on every adoption, including a reconnect to the same model.</summary>
    internal int ConnectionId { get; private set; }

    /// <summary>Preferred model, normalized on set: a name matching no supported mouse must behave
    /// EXACTLY like no preference, or every battery read detaches and rolls back chasing a mouse
    /// that cannot exist.</summary>
    internal string? PreferredModel
    {
        get => _preferred;
        set => _preferred = RazerProtocol.NormalizeModelName(value);
    }

    /// <summary>Ensure a collection that actually answers, and return its transaction id (0 = none
    /// reachable). reselect is passed ONLY by the battery path: it is what lets the app hop back to
    /// the preferred mouse, and what every other call must not do (spec §4.3).</summary>
    internal async Task<byte> EnsureAsync(CancellationToken ct, bool reselect = false)
    {
        switch (ConnectionPolicy.Decide(_current is not null && TransactionId != 0, Model, _preferred, reselect))
        {
            case ConnectionDecision.KeepLive:
                return TransactionId;
            case ConnectionDecision.ProbePreferred:
                return await AdoptPreferredAsync(ct);
            default:
                Close();
                return await ConnectAsync(RazerProtocol.ScanPidsFor(_preferred), ct);
        }
    }

    /// <summary>Which supported mice answer right now, in SupportedMice order, leaving a normal
    /// connection behind (spec §4.4).</summary>
    internal async Task<IReadOnlyList<string>> DiscoverAsync(CancellationToken ct)
    {
        var reachable = new List<string>();
        Close();
        foreach (var m in RazerProtocol.SupportedMice)
        {
            if (await ConnectAsync(new[] { m.WiredPid, m.WirelessPid }, ct) != 0) reachable.Add(m.DisplayName);
            Close();
        }
        await EnsureAsync(ct);
        return reachable;
    }

    /// <summary>Drop the live collection so the next EnsureAsync re-selects.</summary>
    internal void Close()
    {
        _current?.Dispose();
        _current = null;
        TransactionId = 0;
    }

    public void Dispose() => Close();

    /// <summary>Running on a non-preferred mouse: probe ONLY the preferred model. Adopt it if it
    /// answers, otherwise put the live connection back untouched — a sleeping preference must never
    /// cost the user the mouse that is actually awake.
    /// <para>The rollback is a finally, not an if: an exception or a cancellation between the detach
    /// and the adoption would otherwise leave the app with no connection AND leak the detached
    /// collection.</para></summary>
    private async Task<byte> AdoptPreferredAsync(CancellationToken ct)
    {
        var live = _current;
        byte liveTid = TransactionId;
        bool liveWired = Wired;
        string? liveModel = Model;
        bool adopted = false;

        _current = null;   // detach so ConnectAsync's failure path cannot dispose the live collection
        TransactionId = 0;
        try
        {
            byte tid = await ConnectAsync(RazerProtocol.PidsForModel(_preferred), ct);
            adopted = tid != 0;
            return adopted ? tid : liveTid;
        }
        finally
        {
            if (adopted)
            {
                live?.Dispose();   // the loser, and only the loser
            }
            else
            {
                Close();           // drop anything a partial attempt left behind
                _current = live;
                TransactionId = liveTid;
                Wired = liveWired;
                Model = liveModel;
            }
        }
    }

    /// <summary>Open and verify the first candidate among <paramref name="pids"/> that answers.
    /// 0 = none did, and nothing is left open. The verify step is essential: the wireless receiver
    /// stays enumerated when the mouse switches to wired, so picking by enumeration order alone
    /// would lock onto a collection that can no longer reach the mouse.</summary>
    private async Task<byte> ConnectAsync(int[] pids, CancellationToken ct)
    {
        foreach (var candidate in _source.Open(pids))
        {
            byte tid;
            try { tid = await _verify(candidate, ct); }
            catch { candidate.Dispose(); throw; }

            if (tid != 0)
            {
                _current = candidate;
                TransactionId = tid;
                Wired = RazerProtocol.IsWiredPid(candidate.Pid);
                Model = RazerProtocol.ModelNameForPid(candidate.Pid);
                ConnectionId++;
                return tid;
            }
            candidate.Dispose();
        }
        return 0;
    }
}
