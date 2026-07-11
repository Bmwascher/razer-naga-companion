using NagaBatteryTray.Hid;
using NagaBatteryTray.Ui.Dashboard;
using Xunit;

public class ProfileLivenessTests
{
    private static readonly (byte, byte[]) CtrlC = ((byte)0x02, new byte[] { 0x01, 0x06 });

    [Fact] public void No_slot_is_NotAdopted() =>
        Assert.Equal(ProfileLivenessState.NotAdopted, ProfileLiveness.Evaluate(null, CtrlC, null));

    [Fact] public void No_expected_binding_is_Unchecked() =>
        Assert.Equal(ProfileLivenessState.Unchecked, ProfileLiveness.Evaluate(3, null, null));

    [Fact] public void Unreadable_effective_is_Unknown() =>
        Assert.Equal(ProfileLivenessState.Unknown, ProfileLiveness.Evaluate(3, CtrlC, null));

    [Fact] public void Matching_effective_is_Live() =>
        Assert.Equal(ProfileLivenessState.Live,
            ProfileLiveness.Evaluate(3, CtrlC, new RawButtonAction(0x02, new byte[] { 0x01, 0x06 })));

    [Fact] public void Mismatched_effective_is_NotLive() =>
        Assert.Equal(ProfileLivenessState.NotLive,
            ProfileLiveness.Evaluate(3, CtrlC, new RawButtonAction(0x02, new byte[] { 0x00, 0x1e })));
}
