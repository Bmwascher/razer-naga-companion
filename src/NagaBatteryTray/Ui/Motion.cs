using System.Windows;
using System.Windows.Media.Animation;

namespace NagaBatteryTray.Ui;

/// <summary>A CSS-style cubic-bezier(x1,y1,x2,y2) timing curve: solves x(t)=progress for t
/// (Newton-Raphson with a bisection fallback) and returns y(t). WPF's EasingFunctionBase mirrors
/// EaseInCore to synthesize EaseOut/EaseInOut around whatever curve you give it — but our bezier
/// curves already encode their full shape (they're not meant to be mirrored), so every instance
/// is constructed with EasingMode.EaseIn, which makes the base class call EaseInCore directly,
/// unmirrored. Don't change EasingMode on an instance; that's the trap this class exists to avoid.</summary>
public sealed class BezierEase : EasingFunctionBase
{
    private readonly double _x1, _y1, _x2, _y2;

    public BezierEase(double x1, double y1, double x2, double y2)
    {
        _x1 = x1; _y1 = y1; _x2 = x2; _y2 = y2;
        EasingMode = EasingMode.EaseIn;
    }

    protected override double EaseInCore(double normalizedTime) =>
        SampleCurve(SolveTForX(normalizedTime), _y1, _y2);

    protected override Freezable CreateInstanceCore() => new BezierEase(_x1, _y1, _x2, _y2);

    private static double SampleCurve(double t, double p1, double p2)
    {
        double mt = 1 - t;
        return 3 * mt * mt * t * p1 + 3 * mt * t * t * p2 + t * t * t;
    }

    private static double SampleCurveDerivative(double t, double p1, double p2)
    {
        double mt = 1 - t;
        return 3 * mt * mt * p1 + 6 * mt * t * (p2 - p1) + 3 * t * t * (1 - p2);
    }

    private double SolveTForX(double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;

        double t = x; // initial guess: x(t) is close to identity for typical easing control points
        for (int i = 0; i < 8; i++)
        {
            double d = SampleCurveDerivative(t, _x1, _x2);
            if (Math.Abs(d) < 1e-6) break;
            double next = t - (SampleCurve(t, _x1, _x2) - x) / d;
            if (next is < 0 or > 1) break; // Newton stepped out of range - fall through to bisection
            t = next;
        }

        if (Math.Abs(SampleCurve(t, _x1, _x2) - x) < 1e-5) return t;

        double lo = 0, hi = 1;
        for (int i = 0; i < 20; i++)
        {
            double mid = (lo + hi) / 2;
            if (SampleCurve(mid, _x1, _x2) < x) lo = mid; else hi = mid;
        }
        return (lo + hi) / 2;
    }
}

/// <summary>Motion design tokens shared by every animated dashboard surface: bezier curves,
/// durations, the reduced-motion signal, and a To-only/Compose animation helper. Hard rule (from
/// the approved motion audit): animate only RenderTransform (Translate/Scale) and Opacity - never
/// layout properties (Width/Height/Margin) - so motion never touches WPF's measure/arrange pass.</summary>
public static class Motion
{
    /// <summary>Strong ease-out, used for entrances/exits/press-feedback. No ease-in anywhere.</summary>
    public static readonly BezierEase EaseOut = Frozen(new BezierEase(0.23, 1, 0.32, 1));

    /// <summary>The settings drawer's own curve.</summary>
    public static readonly BezierEase Drawer = Frozen(new BezierEase(0.32, 0.72, 0, 1));

    public static readonly Duration Press = new(TimeSpan.FromMilliseconds(120));
    public static readonly Duration Micro = new(TimeSpan.FromMilliseconds(150));
    public static readonly Duration Fade = new(TimeSpan.FromMilliseconds(180));
    public static readonly Duration DrawerIn = new(TimeSpan.FromMilliseconds(280));
    public static readonly Duration DrawerOut = new(TimeSpan.FromMilliseconds(220));

    /// <summary>True when Windows' "Play animations in Windows" / "Turn off all unnecessary
    /// animations" setting is off. Reduced motion doesn't remove feedback - positional motion
    /// (slides/scales) is replaced by gentler opacity fades instead.</summary>
    public static bool Reduced => !SystemParameters.ClientAreaAnimation;

    /// <summary>One-shot, event-driven, To-only animation with HandoffBehavior.SnapshotAndReplace:
    /// From is left unset so it starts at the current presentation value — for a To-only tween the
    /// snapshot makes an in-flight animation retarget just as smoothly as Compose, but the old
    /// clock is RELEASED. (Compose retained every prior clock in the property's composition chain,
    /// so a session of chip presses accumulated clocks unboundedly — against the perf gate.)
    /// IAnimatable covers both UIElements and Transforms/Brushes with one overload.</summary>
    public static void Animate(IAnimatable target, DependencyProperty prop, double to, Duration d, IEasingFunction ease)
    {
        var anim = new DoubleAnimation { To = to, Duration = d, EasingFunction = ease };
        target.BeginAnimation(prop, anim, HandoffBehavior.SnapshotAndReplace);
    }

    private static BezierEase Frozen(BezierEase ease)
    {
        ease.Freeze();
        return ease;
    }
}
