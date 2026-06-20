using System.ComponentModel;
using NagaBatteryTray.Monitoring;
using Media = System.Windows.Media;

namespace NagaBatteryTray.Ui;

public sealed class PopupViewModel : INotifyPropertyChanged
{
    public const double BarTrackWidth = 232;

    public event PropertyChangedEventHandler? PropertyChanged;

    private string _percentText = "-";
    private string _status = "no response";
    private double _barFraction;
    private Media.Brush _accent = Media.Brushes.Gray;
    private bool _charging;

    public string PercentText { get => _percentText; private set => Set(ref _percentText, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public double BarFraction
    {
        get => _barFraction;
        private set { if (Set(ref _barFraction, value)) OnChanged(nameof(BarPixelWidth)); }
    }
    public double BarPixelWidth => BarFraction * BarTrackWidth;
    public Media.Brush Accent { get => _accent; private set => Set(ref _accent, value); }
    public bool Charging { get => _charging; private set => Set(ref _charging, value); }

    public void Apply(DeviceState s)
    {
        if (s.Status == DeviceStatus.Unknown)
        {
            PercentText = "-";
            Status = "no response";
            BarFraction = 0;
            Accent = Media.Brushes.Gray;
            Charging = false;
            return;
        }

        PercentText = $"{s.Percent}%";
        Status = s.Charging ? "Charging" : "On battery";
        BarFraction = s.Percent / 100.0;
        Charging = s.Charging;
        var c = IconRenderer.ColorForLevel(s.Percent, s.Charging); // System.Drawing.Color
        Accent = new Media.SolidColorBrush(Media.Color.FromRgb(c.R, c.G, c.B));
    }

    private bool Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnChanged(name);
        return true;
    }

    private void OnChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
