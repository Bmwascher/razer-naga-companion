using NagaBatteryTray.Hid;
using Xunit;

public sealed class MouseConnectionTests
{
    // never retype these: the V2's are 0x00A7/0x00A8, and a wrong literal makes every scenario below
    // silently unreachable through ScanPidsFor
    private const int V3Wired = RazerProtocol.MousePidV3Wired, V3Wireless = RazerProtocol.MousePidV3Wireless;
    private const int V2Wired = RazerProtocol.MousePidWired, V2Wireless = RazerProtocol.MousePidWireless;

    private static MouseConnection Build(FakeControlCollectionSource source, Func<int, bool>? answers = null)
    {
        answers ??= pid => source.Answering.Contains(pid);
        return new MouseConnection(source,
            (c, ct) => Task.FromResult(answers(c.Pid) ? (byte)0x1f : (byte)0));
    }

    [Fact]
    public async Task ItAdoptsTheFirstCollectionThatAnswers()
    {
        var source = new FakeControlCollectionSource();
        source.Present.Add(V3Wireless); source.Answering.Add(V3Wireless);
        var conn = Build(source);

        Assert.Equal((byte)0x1f, await conn.EnsureAsync(CancellationToken.None));
        Assert.Equal("Naga V3 Pro", conn.Model);
        Assert.False(conn.Wired);
        Assert.Equal(1, conn.ConnectionId);
    }

    [Fact]
    public async Task ACollectionThatDoesNotAnswerIsDisposedAndSkipped()
    {
        var source = new FakeControlCollectionSource();
        source.Present.Add(V3Wireless);                 // present but asleep
        source.Present.Add(V2Wireless); source.Answering.Add(V2Wireless);
        var conn = Build(source);

        await conn.EnsureAsync(CancellationToken.None);

        Assert.Equal("Naga V2 Pro", conn.Model);
        Assert.True(source.Handed.Single(c => c.Pid == V3Wireless).Disposed);
        Assert.False(source.Handed.Single(c => c.Pid == V2Wireless).Disposed);
    }

    [Fact]
    public async Task ALiveConnectionIsReusedWithoutOpeningAnything()
    {
        var source = new FakeControlCollectionSource();
        source.Present.Add(V3Wireless); source.Answering.Add(V3Wireless);
        var conn = Build(source);
        await conn.EnsureAsync(CancellationToken.None);
        source.OpenedPidOrder.Clear();

        await conn.EnsureAsync(CancellationToken.None);

        Assert.Empty(source.OpenedPidOrder);
        Assert.Equal(1, conn.ConnectionId);   // no adoption, so no increment
    }

    // the sticky-selection defect, as a test
    [Fact]
    public async Task TheBatteryPathHopsBackToThePreferredMouse()
    {
        var source = new FakeControlCollectionSource();
        source.Present.Add(V2Wireless); source.Answering.Add(V2Wireless);
        var conn = Build(source);
        conn.PreferredModel = "Naga V3 Pro";
        await conn.EnsureAsync(CancellationToken.None);
        Assert.Equal("Naga V2 Pro", conn.Model);

        source.Present.Add(V3Wireless); source.Answering.Add(V3Wireless);   // the V3 wakes up
        await conn.EnsureAsync(CancellationToken.None, reselect: true);

        Assert.Equal("Naga V3 Pro", conn.Model);
        Assert.Equal(2, conn.ConnectionId);
    }

    [Fact]
    public async Task ANonBatteryCallDoesNotHopBack()
    {
        var source = new FakeControlCollectionSource();
        source.Present.Add(V2Wireless); source.Answering.Add(V2Wireless);
        var conn = Build(source);
        conn.PreferredModel = "Naga V3 Pro";
        await conn.EnsureAsync(CancellationToken.None);
        source.Present.Add(V3Wireless); source.Answering.Add(V3Wireless);

        await conn.EnsureAsync(CancellationToken.None);   // reselect defaults to false

        Assert.Equal("Naga V2 Pro", conn.Model);
    }

    [Fact]
    public async Task AFailedHopKeepsTheLiveConnection()
    {
        var source = new FakeControlCollectionSource();
        source.Present.Add(V2Wireless); source.Answering.Add(V2Wireless);
        source.Present.Add(V3Wireless);                  // present but asleep
        var conn = Build(source);
        conn.PreferredModel = "Naga V3 Pro";
        await conn.EnsureAsync(CancellationToken.None);
        var live = source.Handed.Single(c => c.Pid == V2Wireless);

        byte tid = await conn.EnsureAsync(CancellationToken.None, reselect: true);

        Assert.Equal((byte)0x1f, tid);
        Assert.Equal("Naga V2 Pro", conn.Model);
        Assert.False(live.Disposed);      // the live handle survived the failed probe
        Assert.Equal(1, conn.ConnectionId);
    }

    [Fact]
    public async Task AThrowingHopKeepsTheLiveConnection()
    {
        var source = new FakeControlCollectionSource();
        source.Present.Add(V2Wireless); source.Answering.Add(V2Wireless);
        var conn = Build(source);
        conn.PreferredModel = "Naga V3 Pro";
        await conn.EnsureAsync(CancellationToken.None);
        var live = source.Handed.Single(c => c.Pid == V2Wireless);
        source.ThrowOnOpen = new InvalidOperationException("enumeration blew up");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => conn.EnsureAsync(CancellationToken.None, reselect: true));

        Assert.Equal("Naga V2 Pro", conn.Model);
        Assert.Equal((byte)0x1f, conn.TransactionId);
        Assert.False(live.Disposed);      // rolled back, not leaked
    }

    [Fact]
    public async Task ACancelledHopKeepsTheLiveConnection()
    {
        var source = new FakeControlCollectionSource();
        source.Present.Add(V2Wireless); source.Answering.Add(V2Wireless);
        var conn = new MouseConnection(source, (c, ct) =>
            c.Pid == V3Wireless ? Task.FromCanceled<byte>(new CancellationToken(true)) : Task.FromResult((byte)0x1f));
        conn.PreferredModel = "Naga V3 Pro";
        await conn.EnsureAsync(CancellationToken.None);   // adopts the V2 while the V3 is absent
        var live = source.Handed.Single(c => c.Pid == V2Wireless);
        source.Present.Add(V3Wireless);                  // only now can the hop be attempted, and cancel

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => conn.EnsureAsync(CancellationToken.None, reselect: true));

        Assert.Equal("Naga V2 Pro", conn.Model);
        Assert.False(live.Disposed);
    }

    [Fact]
    public async Task AWinningHopDisposesExactlyTheLoser()
    {
        var source = new FakeControlCollectionSource();
        source.Present.Add(V2Wireless); source.Answering.Add(V2Wireless);
        var conn = Build(source);
        conn.PreferredModel = "Naga V3 Pro";
        await conn.EnsureAsync(CancellationToken.None);
        var old = source.Handed.Single(c => c.Pid == V2Wireless);
        source.Present.Add(V3Wireless); source.Answering.Add(V3Wireless);

        await conn.EnsureAsync(CancellationToken.None, reselect: true);

        Assert.True(old.Disposed);
        Assert.False(source.Handed.Single(c => c.Pid == V3Wireless).Disposed);
    }

    [Fact]
    public async Task ThePreferredModelIsTriedFirstWiredBeforeWireless()
    {
        var source = new FakeControlCollectionSource();
        foreach (int pid in new[] { V2Wired, V2Wireless, V3Wired, V3Wireless }) source.Present.Add(pid);
        source.Answering.Add(V2Wireless);
        var conn = Build(source);
        conn.PreferredModel = "Naga V2 Pro";

        await conn.EnsureAsync(CancellationToken.None);

        Assert.Equal(new[] { V2Wired, V2Wireless }, source.OpenedPidOrder);
    }

    [Fact]
    public async Task AnUnknownPreferenceBehavesLikeNoPreference()
    {
        var source = new FakeControlCollectionSource();
        source.Present.Add(V2Wireless); source.Answering.Add(V2Wireless);
        var conn = Build(source);
        conn.PreferredModel = "Naga Imaginary";

        Assert.Null(conn.PreferredModel);

        await conn.EnsureAsync(CancellationToken.None);
        source.OpenedPidOrder.Clear();
        await conn.EnsureAsync(CancellationToken.None, reselect: true);

        Assert.Empty(source.OpenedPidOrder);   // no pointless probe on every battery read
    }

    [Fact]
    public async Task DiscoverListsOnlyMiceThatAnswerAndLeavesAWorkingConnection()
    {
        var source = new FakeControlCollectionSource();
        source.Present.Add(V3Wireless);                                  // present but asleep
        source.Present.Add(V2Wireless); source.Answering.Add(V2Wireless);
        var conn = Build(source);

        var found = await conn.DiscoverAsync(CancellationToken.None);

        Assert.Equal(new[] { "Naga V2 Pro" }, found);
        Assert.Equal("Naga V2 Pro", conn.Model);
        Assert.Equal((byte)0x1f, conn.TransactionId);
    }

    [Fact]
    public async Task CloseDisposesTheLiveCollectionAndForcesARescan()
    {
        var source = new FakeControlCollectionSource();
        source.Present.Add(V3Wireless); source.Answering.Add(V3Wireless);
        var conn = Build(source);
        await conn.EnsureAsync(CancellationToken.None);
        var first = source.Handed.Single();

        conn.Close();
        Assert.True(first.Disposed);
        Assert.Equal((byte)0, conn.TransactionId);

        await conn.EnsureAsync(CancellationToken.None);
        Assert.Equal(2, conn.ConnectionId);   // a reconnect is an adoption, even to the same model
    }
}
