using NagaBatteryTray.Ui;
using Xunit;

public class PopupViewModelTests
{
    [Fact]
    public void SetProfile_null_hides_the_line()
    {
        var vm = new PopupViewModel();
        vm.SetProfile(null);
        Assert.False(vm.HasProfile);
    }

    [Fact]
    public void SetProfile_shows_slot_identity()
    {
        var vm = new PopupViewModel();
        vm.SetProfile(3);
        Assert.True(vm.HasProfile);
        Assert.Equal("Profile 3 · green", vm.ProfileText);
    }
}
