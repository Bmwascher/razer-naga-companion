using NagaBatteryTray.Monitoring;
using Media = System.Windows.Media;

namespace NagaBatteryTray.Ui;

public sealed class PopupViewModel : ObservableObject
{
    public const double BarTrackWidth = 234;

    private string _percentText = "-";
    private string _status = "no response";
    private double _barFraction;
    private Media.Brush _accent = Media.Brushes.Gray;
    private bool _charging;
    private string _profileText = "";
    private bool _hasProfile;

    public string PercentText { get => _percentText; private set => Set(ref _percentText, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public double BarFraction
    {
        get => _barFraction;
        private set { if (Set(ref _barFraction, value)) Notify(nameof(BarPixelWidth)); }
    }
    public double BarPixelWidth => BarFraction * BarTrackWidth;
    public Media.Brush Accent { get => _accent; private set => Set(ref _accent, value); }
    public bool Charging { get => _charging; private set => Set(ref _charging, value); }
    public string ProfileText { get => _profileText; private set => Set(ref _profileText, value); }
    public bool HasProfile { get => _hasProfile; private set => Set(ref _hasProfile, value); }

    public void SetProfile(int? slot)
    {
        HasProfile = slot is not null;
        ProfileText = slot is { } n ? $"Profile {n} · {Dashboard.DashboardViewModel.SlotColour(n)}" : "";
    }

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
        // Top-right shows the active link (the charging pill already conveys charge state, so don't duplicate
        // it). Wired -> "Wired"; wireless on battery keeps "On battery"; wireless while charging (dock puck)
        // reads "Wireless" rather than the contradictory "On battery".
        Status = s.Wired ? "Wired" : s.Charging ? "Wireless" : "On battery";
        BarFraction = s.Percent / 100.0;
        Charging = s.Charging;
        var c = IconRenderer.ColorForLevel(s.Percent, s.Charging); // System.Drawing.Color
        Accent = new Media.SolidColorBrush(Media.Color.FromRgb(c.R, c.G, c.B));
    }
}
