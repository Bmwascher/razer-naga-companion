using NagaBatteryTray.Hid;

namespace NagaBatteryTray.Ui.Dashboard;

public enum ProfileLivenessState { NotAdopted, Unchecked, Unknown, Live, NotLive }

/// <summary>Pure logic behind the Profile card: is the mouse currently ON the app's slot? Compares
/// one remapped button's EFFECTIVE action (a profile-0 read — reads through to the active profile,
/// hardware-verified in the Phase B spike) against what the app slot should hold.</summary>
public static class ProfileLiveness
{
    public static ProfileLivenessState Evaluate(
        int? slot, (byte Category, byte[] Data)? expected, RawButtonAction? effective)
    {
        if (slot is null) return ProfileLivenessState.NotAdopted;
        if (expected is not { } e) return ProfileLivenessState.Unchecked;
        if (effective is not { } a) return ProfileLivenessState.Unknown;
        return a.Category == e.Category && a.Data.AsSpan().SequenceEqual(e.Data)
            ? ProfileLivenessState.Live
            : ProfileLivenessState.NotLive;
    }
}
