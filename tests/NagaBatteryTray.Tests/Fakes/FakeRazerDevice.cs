using NagaBatteryTray.Hid;

public sealed class FakeRazerDevice : IRazerDevice
{
    private readonly Queue<BatteryReading> _queue = new();
    public void Enqueue(BatteryReading r) => _queue.Enqueue(r);

    /// <summary>Opt-in: when set, ReadAsync completes it the instant it is entered, letting a test
    /// know a read is in flight without polling. Null (the default) is a no-op.</summary>
    public TaskCompletionSource<bool>? ReadEntered { get; set; }

    /// <summary>Opt-in: when set, ReadAsync awaits it before returning, letting a test hold a read
    /// open to create real lock contention. Null (the default) is a no-op — every existing test
    /// that never sets this reads synchronously as before.</summary>
    public TaskCompletionSource<bool>? ReadGate { get; set; }

    /// <summary>How many battery reads the device has actually served — lets a test prove that no
    /// poll slipped into the middle of another operation.</summary>
    public int ReadCount { get; private set; }

    public async Task<BatteryReading> ReadAsync(CancellationToken ct)
    {
        ReadEntered?.TrySetResult(true);
        ReadCount++;
        if (ReadGate is not null) await ReadGate.Task;
        return _queue.Count > 0 ? _queue.Dequeue() : BatteryReading.Absent(DateTimeOffset.Now);
    }

    public DpiSetting? Dpi { get; set; }
    public bool SetDpiResult { get; set; } = true;
    public int LastSetX { get; private set; }
    public int LastSetY { get; private set; }
    public Task<DpiSetting?> GetDpiAsync(CancellationToken ct) => Task.FromResult(Dpi);
    public Task<bool> SetDpiAsync(int dpiX, int dpiY, CancellationToken ct)
    {
        LastSetX = dpiX; LastSetY = dpiY;
        return Task.FromResult(SetDpiResult);
    }

    public sealed record ButtonWrite(byte Profile, byte ButtonId, byte Category, byte[] Data);
    public List<ButtonWrite> ButtonWrites { get; } = new();
    public bool SetButtonResult { get; set; } = true;
    public Dictionary<(byte Profile, byte ButtonId), RawButtonAction> ButtonActions { get; } = new();

    /// <summary>Runs just before a button write — lets a test observe, or disturb, the state of the
    /// world at the moment the write itself is issued.</summary>
    public Action? BeforeSetButton { get; set; }

    public Task<bool> SetButtonAsync(byte profile, byte buttonId, byte category, byte[] data, CancellationToken ct)
    {
        BeforeSetButton?.Invoke();
        ButtonWrites.Add(new ButtonWrite(profile, buttonId, category, data));
        return Task.FromResult(SetButtonResult);
    }

    public int GetButtonCount { get; private set; }

    /// <summary>Runs just before a button read — lets a test simulate the connection changing
    /// in the middle of a multi-step operation.</summary>
    public Action? BeforeGetButton { get; set; }

    public Task<RawButtonAction?> GetButtonAsync(byte profile, byte buttonId, CancellationToken ct)
    {
        BeforeGetButton?.Invoke();
        GetButtonCount++;
        return Task.FromResult(ButtonActions.TryGetValue((profile, buttonId), out var a) ? a : (RawButtonAction?)null);
    }

    public ProfileList? Profiles { get; set; }
    public bool CreateProfileResult { get; set; } = true;
    public List<byte> CreatedSlots { get; } = new();

    public Task<ProfileList?> GetProfileListAsync(CancellationToken ct) => Task.FromResult(Profiles);

    public Task<bool> CreateProfileAsync(byte slot, CancellationToken ct)
    {
        CreatedSlots.Add(slot);
        return Task.FromResult(CreateProfileResult);
    }

    public byte? ActiveProfile { get; set; }
    public bool SetActiveProfileResult { get; set; } = true;
    public List<byte> ActiveProfileSets { get; } = new();

    public Task<byte?> GetActiveProfileAsync(CancellationToken ct) => Task.FromResult(ActiveProfile);

    public Task<bool> SetActiveProfileAsync(byte slot, CancellationToken ct)
    {
        ActiveProfileSets.Add(slot);
        if (SetActiveProfileResult) ActiveProfile = slot; // realistic: a successful set changes the active slot
        return Task.FromResult(SetActiveProfileResult);
    }

    public string? PreferredModel { get; set; }

    /// <summary>What DiscoverReachableAsync returns; tests set it per scenario.</summary>
    public List<string> Reachable { get; } = new();
    public int DiscoverCount { get; private set; }

    public Task<IReadOnlyList<string>> DiscoverReachableAsync(CancellationToken ct)
    {
        DiscoverCount++;
        return Task.FromResult<IReadOnlyList<string>>(Reachable.ToList());
    }

    /// <summary>Settable so a test can simulate the mouse changing underneath an operation.</summary>
    public int ConnectionId { get; set; }

    public int ResetCount { get; private set; }
    public void Reset() => ResetCount++;

    public void Dispose() { }
}
