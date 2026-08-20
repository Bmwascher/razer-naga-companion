using NagaBatteryTray.Monitoring;
using Xunit;

public sealed class DeviceScopedStateTests
{
    [Fact]
    public void FirstObservationIsNotAChange()
    {
        var s = new DeviceScopedState();
        Assert.False(s.Observe("Naga V3 Pro"));
    }

    [Fact]
    public void SameModelObservedAgainKeepsTheCache()
    {
        var s = new DeviceScopedState();
        s.Observe("Naga V3 Pro");
        s.ActiveSlot = 1;
        s.CreatedSlots = new byte[] { 1 };

        Assert.False(s.Observe("Naga V3 Pro"));
        Assert.Equal((byte)1, s.ActiveSlot);
        Assert.Equal(new byte[] { 1 }, s.CreatedSlots);
    }

    [Fact]
    public void ADifferentModelClearsEverything()
    {
        var s = new DeviceScopedState();
        s.Observe("Naga V2 Pro");
        s.ActiveSlot = 3;              // the V2's active slot
        s.CreatedSlots = new byte[] { 1, 2, 3 };

        Assert.True(s.Observe("Naga V3 Pro"));
        Assert.Null(s.ActiveSlot);     // the V3 has no slot 3 — this is the whole defect
        Assert.Null(s.CreatedSlots);
    }

    [Fact]
    public void GoingOfflineAndBackClearsToo()
    {
        var s = new DeviceScopedState();
        s.Observe("Naga V2 Pro");
        s.ActiveSlot = 3;

        Assert.True(s.Observe(null));
        Assert.Null(s.ActiveSlot);

        // Re-seed the cache after going offline, then observe going back online.
        s.ActiveSlot = 3;
        s.CreatedSlots = new byte[] { 1, 2, 3 };

        Assert.True(s.Observe("Naga V2 Pro"));
        Assert.Null(s.ActiveSlot);
        Assert.Null(s.CreatedSlots);
    }

    [Fact]
    public void UnknownInventoryNeverBlocksAWrite()
    {
        var s = new DeviceScopedState();
        s.Observe("Naga V3 Pro");
        Assert.False(s.IsBlocked(1));
        Assert.False(s.IsBlocked(3));
    }

    [Fact]
    public void KnownInventoryBlocksASlotTheMouseDoesNotHave()
    {
        var s = new DeviceScopedState();
        s.Observe("Naga V3 Pro");
        s.CreatedSlots = new byte[] { 1 };

        Assert.False(s.IsBlocked(1));
        Assert.True(s.IsBlocked(3));
    }
}
