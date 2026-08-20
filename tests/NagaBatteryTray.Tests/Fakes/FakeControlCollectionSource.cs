using NagaBatteryTray.Hid;

/// <summary>A scriptable stand-in for the Windows HID layer: which PIDs exist, which of them answer,
/// and which blow up. Records every collection it hands out so a test can assert disposal.
/// <para>internal, not public: it implements the internal IControlCollectionSource, and a public type
/// cannot implement a less accessible interface. The test project reaches it through
/// InternalsVisibleTo, same as every other internal it touches.</para></summary>
internal sealed class FakeControlCollectionSource : IControlCollectionSource
{
    internal sealed class FakeCollection : IControlCollection
    {
        public FakeCollection(int pid) => Pid = pid;
        public int Pid { get; }
        public bool Disposed { get; private set; }
        public bool IsOpen { get; private set; } = true;

        /// <summary>When true every feature call fails and closes the collection, like a dead handle.</summary>
        public bool FailFeature { get; set; }

        /// <summary>How many feature calls this collection actually received.</summary>
        public int FeatureCalls { get; private set; }

        /// <summary>True for a collection whose mouse is awake: its replies parse.</summary>
        public bool Answers { get; set; }

        private byte[] _lastRequest = Array.Empty<byte>();

        public bool SetFeature(byte[] buffer)
        {
            if (!Feature()) return false;
            _lastRequest = (byte[])buffer.Clone();
            return true;
        }

        public bool GetFeature(byte[] buffer)
        {
            if (!Feature()) return false;
            if (!Answers) return true;   // leaves the buffer as it found it: nothing will parse
            WriteCompletedReply(buffer);
            return true;
        }

        /// <summary>Echo the request back as a completed reply the production parsers accept: status
        /// 0x02, the class/cmd echo they check, a mid-scale data byte, and a valid CRC. A zero-filled
        /// buffer parses as nothing, so without this an "answering" collection can never be adopted.</summary>
        private void WriteCompletedReply(byte[] buffer)
        {
            Array.Copy(_lastRequest, buffer, Math.Min(_lastRequest.Length, buffer.Length));
            buffer[1] = 0x02;    // status: command completed
            buffer[10] = 0x80;   // ~50% for a power query; harmless filler for other classes

            var report = new byte[RazerProtocol.ReportLength];
            Array.Copy(buffer, 1, report, 0, RazerProtocol.ReportLength);
            report[88] = RazerProtocol.ComputeCrc(report);
            Array.Copy(report, 0, buffer, 1, RazerProtocol.ReportLength);
        }

        private bool Feature()
        {
            FeatureCalls++;
            if (!FailFeature) return true;
            IsOpen = false;
            return false;
        }

        public void Dispose() { Disposed = true; IsOpen = false; }
    }

    /// <summary>PIDs present on the machine. Order here does not matter: Open honours the caller's.</summary>
    public HashSet<int> Present { get; } = new();

    /// <summary>PIDs whose collection verifies (i.e. the mouse is awake and answering).</summary>
    public HashSet<int> Answering { get; } = new();

    /// <summary>PIDs whose collection fails every feature call, standing in for a dead handle.</summary>
    public HashSet<int> FeatureFailing { get; } = new();

    /// <summary>Thrown from Open when set, to exercise the rollback path.</summary>
    public Exception? ThrowOnOpen { get; set; }

    public List<FakeCollection> Handed { get; } = new();
    public List<int> OpenedPidOrder { get; } = new();

    public IEnumerable<IControlCollection> Open(int[] pids)
    {
        if (ThrowOnOpen is { } ex) throw ex;
        foreach (int pid in pids)
        {
            if (!Present.Contains(pid)) continue;
            OpenedPidOrder.Add(pid);
            var c = new FakeCollection(pid)
            {
                FailFeature = FeatureFailing.Contains(pid),
                Answers = Answering.Contains(pid),
            };
            Handed.Add(c);
            yield return c;
        }
    }
}
