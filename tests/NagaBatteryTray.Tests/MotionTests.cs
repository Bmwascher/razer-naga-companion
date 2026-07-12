using NagaBatteryTray.Ui;
using Xunit;

public class MotionTests
{
    [Fact]
    public void EaseOut_endpoints_are_0_and_1()
    {
        Assert.Equal(0, Motion.EaseOut.Ease(0), 3);
        Assert.Equal(1, Motion.EaseOut.Ease(1), 3);
    }

    [Fact]
    public void Drawer_endpoints_are_0_and_1()
    {
        Assert.Equal(0, Motion.Drawer.Ease(0), 3);
        Assert.Equal(1, Motion.Drawer.Ease(1), 3);
    }

    [Fact]
    public void EaseOut_is_monotonic_non_decreasing()
    {
        AssertMonotonicNonDecreasing(Motion.EaseOut);
    }

    [Fact]
    public void Drawer_is_monotonic_non_decreasing()
    {
        AssertMonotonicNonDecreasing(Motion.Drawer);
    }

    private static void AssertMonotonicNonDecreasing(BezierEase ease)
    {
        double prev = ease.Ease(0);
        for (int i = 1; i <= 50; i++)
        {
            double t = i / 50.0;
            double y = ease.Ease(t);
            Assert.True(y >= prev - 1e-9, $"regressed at t={t}: {y} < {prev}");
            prev = y;
        }
    }

    [Fact]
    public void EaseOut_is_a_fast_out_curve_past_the_midpoint()
    {
        // bezier(0.23, 1, 0.32, 1): shoots up fast then flattens.
        Assert.True(Motion.EaseOut.Ease(0.5) > 0.8);
    }

    [Fact]
    public void Durations_match_the_spec()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(120), Motion.Press.TimeSpan);
        Assert.Equal(TimeSpan.FromMilliseconds(150), Motion.Micro.TimeSpan);
        Assert.Equal(TimeSpan.FromMilliseconds(180), Motion.Fade.TimeSpan);
        Assert.Equal(TimeSpan.FromMilliseconds(280), Motion.DrawerIn.TimeSpan);
        Assert.Equal(TimeSpan.FromMilliseconds(220), Motion.DrawerOut.TimeSpan);
    }

    [Fact]
    public void Curves_are_frozen_for_safe_sharing()
    {
        Assert.True(Motion.EaseOut.IsFrozen);
        Assert.True(Motion.Drawer.IsFrozen);
    }
}
