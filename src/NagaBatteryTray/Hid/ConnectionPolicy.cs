namespace NagaBatteryTray.Hid;

/// <summary>What a device call should do with the connection it already has.</summary>
internal enum ConnectionDecision
{
    /// <summary>Keep the live collection untouched. Every non-battery call lands here.</summary>
    KeepLive,
    /// <summary>Live, but on the wrong mouse and the caller may reselect: probe the preferred model
    /// only, and keep the live connection if it does not answer.</summary>
    ProbePreferred,
    /// <summary>No usable collection: scan every candidate in preference order.</summary>
    FullScan,
}

/// <summary>The reselect rule, with no handles and no Win32 in sight (spec 2026-08-19 §4.3).</summary>
internal static class ConnectionPolicy
{
    internal static ConnectionDecision Decide(bool hasLiveCollection, string? currentModel, string? preferredModel, bool reselect)
    {
        if (!hasLiveCollection) return ConnectionDecision.FullScan;
        if (!reselect || preferredModel is null) return ConnectionDecision.KeepLive;
        return string.Equals(currentModel, preferredModel, StringComparison.OrdinalIgnoreCase)
            ? ConnectionDecision.KeepLive
            : ConnectionDecision.ProbePreferred;
    }
}
