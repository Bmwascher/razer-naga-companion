using System.Diagnostics;
using System.IO;
using NagaBatteryTray.Hid;
using NagaBatteryTray.Monitoring;
using NagaBatteryTray.Settings;
using Xunit;

/// <summary>Repeatable timing harness, NOT a unit test — the same "magnifying glass, not coverage"
/// bargain DashboardScreenshotProbe strikes, and gated the same way (NAGA_PERF=1) so an ordinary
/// test run never touches it. It exists so a perf claim can be checked instead of asserted: every
/// scenario reports wall clock AND the round-trip count behind it, because a change that shortens a
/// wait and a change that removes work are different things, and the counts tell them apart.
/// <para>Output: one key=value line per measurement to NAGA_PERF_OUT (default
/// %TEMP%/naga-perf.txt), tagged with NAGA_PERF_LABEL, so a before/after pair is a diff of two
/// runs.</para></summary>
public sealed class PerfBenchIo
{
    [Fact]
    public async Task Io_pipeline()
    {
        if (Environment.GetEnvironmentVariable("NAGA_PERF") != "1") return;
        using var r = PerfReport.Open("io");

        // 25 ms turnaround models the awake mouse on the desk, so each scenario pays the device's
        // real latency plus whatever the app's wait policy adds on top. That margin is the subject.
        await Measure(r, "startup_battery_read", d => d.ReadAsync(default));
        await Measure(r, "dpi_read", d => d.GetDpiAsync(default));
        await Measure(r, "profile_refresh", async d =>
        {
            await d.GetProfileListAsync(default);
            await d.GetActiveProfileAsync(default);
        });
        await Measure(r, "grid_sweep_12", Sweep);
        await Measure(r, "discover_mice", d => d.DiscoverReachableAsync(default));
        await MeasureWith(r, "discover_mice_silent_peer", SilentPeerSource,
                          d => d.DiscoverReachableAsync(default));

        // a second read on a live connection: the background poll's steady-state cost
        await Measure(r, "battery_poll_warm", d => d.ReadAsync(default),
                      warmup: d => d.ReadAsync(default));

        // the whole AppHost.OpenDashboard seeding sequence, driven through BatteryMonitor so the
        // read-lock hops are in the number too: discover, DPI, profile list + active, 12-button sweep.
        await MeasureSeed(r, "dashboard_seed_total");

        // a sleepy link, to prove a policy tuned for the fast case does not fall apart on a slow one
        await MeasureAt(r, "startup_battery_read_slow_link", 120, d => d.ReadAsync(default));
        await MeasureAt(r, "grid_sweep_12_slow_link", 120, Sweep);
    }

    private static async Task Sweep(RazerDevice d)
    {
        for (int pos = 1; pos <= NagaV2ProButtons.Count; pos++)
            await d.GetButtonAsync(1, NagaV2ProButtons.IdForPosition(pos), default);
    }

    private const int Reps = 3;

    private static Task Measure(PerfReport r, string key, Func<RazerDevice, Task> body,
                                Func<RazerDevice, Task>? warmup = null) =>
        MeasureAt(r, key, 25, body, warmup);

    private static Task MeasureAt(PerfReport r, string key, int readyAfterMs,
                                  Func<RazerDevice, Task> body, Func<RazerDevice, Task>? warmup = null) =>
        MeasureWith(r, key, () => NewSource(readyAfterMs), body, warmup);

    /// <summary>Run body on a fresh device Reps times and report the median. An optional warmup runs
    /// first, outside the stopwatch and outside the counters, so a steady-state measurement is not
    /// charged for the connect that had to precede it.</summary>
    private static async Task MeasureWith(PerfReport r, string key, Func<BenchControlCollectionSource> newSource,
                                          Func<RazerDevice, Task> body, Func<RazerDevice, Task>? warmup = null)
    {
        var times = new List<double>();
        int exchanges = 0, features = 0;
        for (int i = 0; i < Reps; i++)
        {
            var source = newSource();
            using var device = new RazerDevice(StoreWithCachedTid(), source);
            if (warmup is not null) { await warmup(device); source.ResetCounters(); }
            var sw = Stopwatch.StartNew();
            await body(device);
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
            exchanges = source.Exchanges;
            features = source.FeatureCalls;
        }
        r.Write(key, Median(times), exchanges, features);
    }

    private static async Task MeasureSeed(PerfReport r, string key)
    {
        var times = new List<double>();
        int exchanges = 0, features = 0;
        for (int i = 0; i < Reps; i++)
        {
            var source = NewSource(25);
            using var device = new RazerDevice(StoreWithCachedTid(), source);
            using var monitor = new BatteryMonitor(device, StoreWithCachedTid(), a => a());
            var sw = Stopwatch.StartNew();
            await monitor.DiscoverMiceAsync();
            await monitor.GetDpiAsync();
            await monitor.GetProfileListAsync();
            byte slot = await monitor.GetActiveProfileAsync() ?? 1;
            for (int pos = 1; pos <= NagaV2ProButtons.Count; pos++)
                await monitor.GetButtonAsync(slot, NagaV2ProButtons.IdForPosition(pos));
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
            exchanges = source.Exchanges;
            features = source.FeatureCalls;
        }
        r.Write(key, Median(times), exchanges, features);
    }

    /// <summary>The user's actual machine, as --probe-dpi reported it on 2026-10-15: a V3 Pro awake
    /// on its receiver, and a V2 Pro receiver still plugged in whose mouse is asleep and answers
    /// status 0x04. The asleep peer is what makes discovery expensive, so it belongs in the model —
    /// and it REFUSES rather than going silent, which is the timing case that decides whether polling
    /// early helps or hurts.</summary>
    private static BenchControlCollectionSource NewSource(int readyAfterMs)
    {
        var s = new BenchControlCollectionSource { ReadyAfterMs = readyAfterMs };
        s.Present.Add(RazerProtocol.MousePidV3Wireless);
        s.Answering.Add(RazerProtocol.MousePidV3Wireless);
        s.Present.Add(RazerProtocol.MousePidWireless);
        s.Refusing.Add(RazerProtocol.MousePidWireless);
        return s;
    }

    /// <summary>Worst case for a poll-early policy: the peer receiver never writes a reply at all, so
    /// every rung of a ladder is spent and nothing is learned. Benched separately so a win on the
    /// ordinary case cannot hide a loss here.</summary>
    private static BenchControlCollectionSource SilentPeerSource()
    {
        var s = new BenchControlCollectionSource { ReadyAfterMs = 25 };
        s.Present.Add(RazerProtocol.MousePidV3Wireless);
        s.Answering.Add(RazerProtocol.MousePidV3Wireless);
        s.Present.Add(RazerProtocol.MousePidWireless);   // present, never answers
        return s;
    }

    private static ISettingsStore StoreWithCachedTid()
    {
        var store = new JsonSettingsStore(Path.Combine(Path.GetTempPath(), $"naga-perf-{Guid.NewGuid():N}.json"));
        store.SetCachedTransactionId(0x1f);   // steady state: resolved once on first ever run, then cached
        return store;
    }

    private static double Median(List<double> xs)
    {
        xs.Sort();
        return xs.Count % 2 == 1 ? xs[xs.Count / 2] : (xs[xs.Count / 2 - 1] + xs[xs.Count / 2]) / 2;
    }
}

/// <summary>Appends "key=ms exchanges=n features=n" lines so two runs diff cleanly.</summary>
internal sealed class PerfReport : IDisposable
{
    private readonly StreamWriter _w;

    private PerfReport(StreamWriter w, string section)
    {
        _w = w;
        string label = Environment.GetEnvironmentVariable("NAGA_PERF_LABEL") ?? "unlabelled";
        _w.WriteLine($"# {section} label={label}");
    }

    internal static PerfReport Open(string section)
    {
        string path = Environment.GetEnvironmentVariable("NAGA_PERF_OUT")
            ?? Path.Combine(Path.GetTempPath(), "naga-perf.txt");
        return new PerfReport(new StreamWriter(path, append: true), section);
    }

    internal void Write(string key, double ms, int exchanges = -1, int features = -1)
    {
        string extra = exchanges < 0 ? "" : $" exchanges={exchanges} features={features}";
        _w.WriteLine($"{key}={ms:F1}{extra}");
        _w.Flush();
    }

    public void Dispose() => _w.Dispose();
}
