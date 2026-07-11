using System.ComponentModel;
using System.Runtime.CompilerServices;
using NagaBatteryTray.Hid;

namespace NagaBatteryTray.Ui;

public enum ButtonOpKind { Apply, RestoreDefault }

/// <summary>One staged change for a grid button, produced by the Buttons UI and consumed by AppHost.</summary>
public readonly record struct ButtonOp(int Position, ButtonOpKind OpKind, ButtonActionKind Kind, byte Modifiers, byte HidUsage);

/// <summary>Pending-change model for one grid button row: an applied state (mirrors the persisted
/// table) plus an optional staged edit. Only staged rows produce ops — an untouched button is never
/// written (§3.1 discipline).</summary>
public sealed class ButtonRowViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private ButtonActionKind _appliedKind = ButtonActionKind.Default;
    private byte _appliedModifiers, _appliedUsage;
    private ButtonActionKind? _pendingKind;
    private byte _pendingModifiers, _pendingUsage;
    private string _status = "";
    private bool _isCapturing;

    public ButtonRowViewModel(int position) => Position = position;

    public int Position { get; }
    public string Label => $"Button {Position}";

    public bool IsCapturing
    {
        get => _isCapturing;
        set { if (_isCapturing == value) return; _isCapturing = value; Notify(); Notify(nameof(CurrentText)); }
    }

    public string Status
    {
        get => _status;
        set { if (_status == value) return; _status = value; Notify(); }
    }

    public string CurrentText =>
        IsCapturing ? "press a key…"
        : _pendingKind is { } p ? $"{Describe(p, _pendingModifiers, _pendingUsage)} (pending)"
        : Describe(_appliedKind, _appliedModifiers, _appliedUsage);

    private static string Describe(ButtonActionKind kind, byte modifiers, byte usage) => kind switch
    {
        ButtonActionKind.Default => "Default",
        ButtonActionKind.Disabled => "Disabled",
        _ => KeyToHidUsage.Describe(modifiers, usage),
    };

    /// <summary>Seed the applied state from the persisted table (window open).</summary>
    public void SetApplied(ButtonActionKind kind, byte modifiers, byte usage)
    {
        _appliedKind = kind; _appliedModifiers = modifiers; _appliedUsage = usage;
        _pendingKind = null;
        Notify(nameof(CurrentText));
    }

    public void StageKey(byte modifiers, byte usage) => Stage(ButtonActionKind.Key, modifiers, usage);
    public void StageDisabled() => Stage(ButtonActionKind.Disabled, 0, 0);
    public void StageDefault() => Stage(ButtonActionKind.Default, 0, 0);

    private void Stage(ButtonActionKind kind, byte modifiers, byte usage)
    {
        IsCapturing = false;
        if (kind == _appliedKind && modifiers == _appliedModifiers && usage == _appliedUsage)
            _pendingKind = null; // staged back to what's already applied — nothing to do
        else
        {
            _pendingKind = kind; _pendingModifiers = modifiers; _pendingUsage = usage;
        }
        Status = "";
        Notify(nameof(CurrentText));
    }

    /// <summary>The op Apply should perform for this row; null = nothing staged. Default staged on a
    /// row that was never remapped is a no-op.</summary>
    public ButtonOp? ToOp()
    {
        if (_pendingKind is not { } kind) return null;
        if (kind == ButtonActionKind.Default)
            return new ButtonOp(Position, ButtonOpKind.RestoreDefault, ButtonActionKind.Default, 0, 0);
        return new ButtonOp(Position, ButtonOpKind.Apply, kind, _pendingModifiers, _pendingUsage);
    }

    public void MarkApplied()
    {
        if (_pendingKind is { } k)
        {
            _appliedKind = k; _appliedModifiers = _pendingModifiers; _appliedUsage = _pendingUsage;
            _pendingKind = null;
        }
        Status = "Applied";
        Notify(nameof(CurrentText));
    }

    public void MarkFailed(string message) => Status = message;

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
