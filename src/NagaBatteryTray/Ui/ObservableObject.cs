using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NagaBatteryTray.Ui;

/// <summary>Shared INotifyPropertyChanged base: the one Set/Notify pair every VM previously
/// hand-rolled (popup, dashboard, callouts, preset items), so equality/notify semantics
/// can't drift between them.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }

    protected void Notify(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
