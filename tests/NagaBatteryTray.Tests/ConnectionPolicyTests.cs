using NagaBatteryTray.Hid;
using Xunit;

public sealed class ConnectionPolicyTests
{
    [Fact]
    public void NoHandleAlwaysScans()
    {
        Assert.Equal(ConnectionDecision.FullScan,
            ConnectionPolicy.Decide(false, null, "Naga V3 Pro", reselect: true));
        Assert.Equal(ConnectionDecision.FullScan,
            ConnectionPolicy.Decide(false, null, null, reselect: false));
    }

    // the sticky-selection fix: the battery path is the ONLY caller that may reselect
    [Fact]
    public void ANonBatteryCallNeverReselectsALiveHandle()
    {
        Assert.Equal(ConnectionDecision.KeepLive,
            ConnectionPolicy.Decide(true, "Naga V2 Pro", "Naga V3 Pro", reselect: false));
    }

    [Fact]
    public void TheBatteryPathProbesWhenTheLiveMouseIsNotThePreferredOne()
    {
        Assert.Equal(ConnectionDecision.ProbePreferred,
            ConnectionPolicy.Decide(true, "Naga V2 Pro", "Naga V3 Pro", reselect: true));
    }

    [Fact]
    public void AlreadyOnThePreferredMouseCostsNothing()
    {
        Assert.Equal(ConnectionDecision.KeepLive,
            ConnectionPolicy.Decide(true, "Naga V3 Pro", "Naga V3 Pro", reselect: true));
        Assert.Equal(ConnectionDecision.KeepLive,
            ConnectionPolicy.Decide(true, "naga v3 pro", "Naga V3 Pro", reselect: true));
    }

    [Fact]
    public void NoPreferenceNeverProbes()
    {
        Assert.Equal(ConnectionDecision.KeepLive,
            ConnectionPolicy.Decide(true, "Naga V2 Pro", null, reselect: true));
    }

    // MouseConnection.PreferredModel normalizes on set, so an unknown settings value arrives here as
    // null and cannot cause a detach-and-rollback on every battery read
    [Fact]
    public void AnUnknownPreferenceNormalisesToNoPreference()
    {
        Assert.Equal(ConnectionDecision.KeepLive,
            ConnectionPolicy.Decide(true, "Naga V2 Pro",
                RazerProtocol.NormalizeModelName("Naga Imaginary"), reselect: true));
    }
}
