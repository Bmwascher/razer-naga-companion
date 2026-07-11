using System.ComponentModel;
using System.Runtime.CompilerServices;
using NagaBatteryTray.Hid;

namespace NagaBatteryTray.Ui.Dashboard;

/// <summary>One thumb-grid button chip: instant-apply state machine
/// (Idle → Capturing → Writing → Confirmed | Failed) with a one-shot undo window after each
/// verified write. The write delegate is AppHost's slot pipeline; Kind=Default means
/// "write the factory action and drop the table entry".</summary>
public sealed class CalloutViewModel : INotifyPropertyChanged
{
    public delegate Task<bool> WriteBinding(int position, ButtonActionKind kind, byte modifiers, byte usage);

    private readonly WriteBinding _write;
    private readonly Func<Task> _undoWindow; // one-shot delay; injectable for tests (default 5 s)

    private ButtonActionKind _kind = ButtonActionKind.Default;
    private byte _mods, _usage;
    private (ButtonActionKind Kind, byte Mods, byte Usage) _prev;
    private int _undoVersion;
    private bool _isCapturing, _isBusy, _canUndo, _isHighlighted;
    private string _status = "";

    public CalloutViewModel(int position, WriteBinding write, Func<Task>? undoWindow = null)
    {
        Position = position;
        _write = write;
        _undoWindow = undoWindow ?? (() => Task.Delay(TimeSpan.FromSeconds(5)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Position { get; }
    public string Label => Position.ToString();

    public string BindingText => _kind switch
    {
        ButtonActionKind.Disabled => "Disabled",
        ButtonActionKind.Key => KeyToHidUsage.Describe(_mods, _usage),
        _ => KeyToHidUsage.Describe(0, NagaV2ProButtons.FactoryBindingForPosition(Position).HidUsage),
    };

    public bool IsCapturing { get => _isCapturing; private set { if (Set(ref _isCapturing, value)) Notify(nameof(BindingText)); } }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public bool CanUndo { get => _canUndo; private set => Set(ref _canUndo, value); }
    public bool IsHighlighted { get => _isHighlighted; set => Set(ref _isHighlighted, value); }
    public string Status { get => _status; set => Set(ref _status, value); }

    /// <summary>Seed from the persisted table (dashboard open).</summary>
    public void SetApplied(ButtonActionKind kind, byte modifiers, byte usage)
    {
        _kind = kind; _mods = modifiers; _usage = usage;
        Notify(nameof(BindingText));
    }

    public void BeginCapture() { Status = ""; IsCapturing = true; }
    public void CancelCapture() => IsCapturing = false;

    public Task CaptureAsync(byte modifiers, byte usage)
    {
        IsCapturing = false;
        return ApplyAsync(ButtonActionKind.Key, modifiers, usage, offerUndo: true);
    }

    public Task DisableAsync() => ApplyAsync(ButtonActionKind.Disabled, 0, 0, offerUndo: true);
    public Task DefaultAsync() => ApplyAsync(ButtonActionKind.Default, 0, 0, offerUndo: true);

    public Task UndoAsync()
    {
        if (!CanUndo) return Task.CompletedTask;
        CanUndo = false;
        var (k, m, u) = _prev;
        return ApplyAsync(k, m, u, offerUndo: false);
    }

    private async Task ApplyAsync(ButtonActionKind kind, byte modifiers, byte usage, bool offerUndo)
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "Writing…";
        bool ok = await _write(Position, kind, modifiers, usage);
        IsBusy = false;
        if (!ok) { Status = "Not applied — wiggle the mouse and retry"; return; }

        _prev = (_kind, _mods, _usage);
        SetApplied(kind, modifiers, usage);
        Status = "Applied";
        if (offerUndo) _ = OpenUndoWindowAsync();
    }

    private async Task OpenUndoWindowAsync()
    {
        int version = ++_undoVersion;
        CanUndo = true;
        await _undoWindow();
        if (version == _undoVersion) CanUndo = false;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }

    private void Notify(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
