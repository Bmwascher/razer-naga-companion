using System.Diagnostics;
using NagaBatteryTray.Hid;

/// <summary>A device emulator for the perf bench (PerfBench): unlike FakeControlCollectionSource,
/// which answers instantly and only ever has to satisfy the battery verify, this one composes a
/// CORRECT reply per command class and does not become ready until <see cref="ReadyAfterMs"/> has
/// elapsed since the SET — the same "request echo now, answer shortly" behaviour the real mouse
/// has. That is what makes it a timing model: a premature GET reads status 0x00, exactly as on
/// hardware, so the transport's wait policy (flat SetReadDelayMs vs the fast-read rung schedule)
/// is what the measured wall clock actually reflects.
/// <para>internal for the same reason as FakeControlCollectionSource: it implements an internal
/// interface, reached through InternalsVisibleTo.</para></summary>
internal sealed class BenchControlCollectionSource : IControlCollectionSource
{
    /// <summary>Device turnaround: how long after the SET the reply is ready. 25 ms models the
    /// awake mouse on the user's desk; raise it to model a sleepy link.</summary>
    public int ReadyAfterMs { get; set; } = 25;

    public HashSet<int> Present { get; } = new();
    public HashSet<int> Answering { get; } = new();

    /// <summary>How long after the SET this collection answers BUSY (0x01) rather than data - the
    /// status the probe legend calls busy/asleep. 0 = never busy. A reply carries data only once
    /// BOTH this and ReadyAfterMs have elapsed, which is the case the flat path's retry existed for
    /// and the one no scenario could reach before (Fable diff r1, F1/F2).</summary>
    public int BusyUntilMs { get; set; }

    /// <summary>PIDs that reply promptly with a hard-failure status (0x04) instead of data: the
    /// still-plugged receiver whose mouse is asleep. This is what --probe-dpi actually showed on the
    /// user's machine (PID 0x00a8, status=0x04, class/cmd echoed), and it is a different timing case
    /// from a silent collection — it ANSWERS, so an early poll resolves it instead of waiting out a
    /// whole ladder. Both cases are benched, because the policy has to be right for each.</summary>
    public HashSet<int> Refusing { get; } = new();

    /// <summary>Command classes this collection echoes but never answers, so a read of that class
    /// spends its whole schedule and learns nothing. It models a mouse that falls asleep part way
    /// through the grid sweep: the power class still answered when the app connected, and the button
    /// reads afterwards do not. That is the case the sixth rung would have taxed twelve times over
    /// (Fable diff r2, F12).</summary>
    public HashSet<byte> SilentForClasses { get; } = new();

    /// <summary>SET calls issued = round trips the app asked for. Delay-policy independent, so it
    /// is the metric that proves a change removed WORK rather than just shortened a wait.</summary>
    public int Exchanges { get; private set; }

    /// <summary>Every HidD_* call, SETs and GETs both. A ladder that polls early trades GETs for
    /// wall clock, and this is where that trade shows up.</summary>
    public int FeatureCalls { get; private set; }

    public void ResetCounters() { Exchanges = 0; FeatureCalls = 0; }

    public IEnumerable<IControlCollection> Open(int[] pids)
    {
        foreach (int pid in pids)
        {
            if (!Present.Contains(pid)) continue;
            yield return new BenchCollection(this, pid,
                Answering.Contains(pid), Refusing.Contains(pid));
        }
    }

    private sealed class BenchCollection : IControlCollection
    {
        private readonly BenchControlCollectionSource _owner;
        private readonly bool _answers;
        private readonly bool _refuses;
        private readonly Stopwatch _since = new();
        private byte[] _request = Array.Empty<byte>();

        public BenchCollection(BenchControlCollectionSource owner, int pid, bool answers, bool refuses)
        {
            _owner = owner;
            Pid = pid;
            _answers = answers;
            _refuses = refuses;
        }

        public int Pid { get; }
        public bool IsOpen { get; private set; } = true;

        public bool SetFeature(byte[] buffer)
        {
            _owner.Exchanges++;
            _owner.FeatureCalls++;
            _request = (byte[])buffer.Clone();
            _since.Restart();
            return true;
        }

        public bool GetFeature(byte[] buffer)
        {
            _owner.FeatureCalls++;
            if (!_answers && !_refuses) return true;      // nothing parses: a present but silent link
            Array.Copy(_request, buffer, Math.Min(_request.Length, buffer.Length));
            if (_owner.SilentForClasses.Contains(_request[7]))
            {
                buffer[1] = 0x00;                          // echoed, and it will never be more
                return true;
            }
            if (_since.ElapsedMilliseconds < _owner.ReadyAfterMs)
            {
                buffer[1] = 0x00;                          // still our own request echoed back
                return true;
            }
            if (_refuses) { buffer[1] = 0x04; return true; } // answered, and the answer is "no"
            if (_since.ElapsedMilliseconds < _owner.BusyUntilMs)
            {
                buffer[1] = 0x01;                          // busy: ask again later
                return true;
            }
            Compose(buffer);
            return true;
        }

        /// <summary>Turn the echoed request into the completed reply the production parsers accept:
        /// status 0x02, the class/cmd echo the transport checks, per-class data, and a valid CRC.</summary>
        private static void Compose(byte[] b)
        {
            b[1] = 0x02;                                   // command completed
            byte cls = b[7], cmd = b[8];
            switch (cls, cmd)
            {
                case (RazerProtocol.CommandClassPower, RazerProtocol.CommandIdBattery):
                    b[10] = 0x80; break;                   // ~50%
                case (RazerProtocol.CommandClassPower, RazerProtocol.CommandIdCharging):
                    b[10] = 0x00; break;
                case (RazerProtocol.CommandClassDpi, RazerProtocol.CommandIdGetDpi):
                    b[10] = 1600 >> 8; b[11] = unchecked((byte)1600);
                    b[12] = 1600 >> 8; b[13] = unchecked((byte)1600); break;
                case (RazerProtocol.CommandClassProfile, RazerProtocol.CommandIdGetProfileList):
                    b[9] = 3; b[10] = 1; b[11] = 2; b[12] = 3; break;
                case (RazerProtocol.CommandClassProfile, RazerProtocol.CommandIdGetActiveProfile):
                    b[9] = 1; break;
                case (RazerProtocol.CommandClassButton, RazerProtocol.CommandIdGetButton):
                    // b[9..11] = profile/buttonId/hypershift stay echoed: ParseButtonReply checks them
                    b[12] = RazerProtocol.FnKeyboard; b[13] = 2; b[14] = 0x00; b[15] = 0x3A; break;
            }
            var report = new byte[RazerProtocol.ReportLength];
            Array.Copy(b, 1, report, 0, RazerProtocol.ReportLength);
            report[88] = RazerProtocol.ComputeCrc(report);
            Array.Copy(report, 0, b, 1, RazerProtocol.ReportLength);
        }

        public void Dispose() => IsOpen = false;
    }
}
