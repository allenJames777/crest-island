using System.Windows;
using System.Windows.Media.Animation;

namespace Crest.Island;

/// <summary>
/// A CSS-style cubic-bezier(x1, y1, x2, y2) easing, so the motion matches the design page exactly.
/// Unlike WPF's KeySpline, the y values may go above 1, which gives the spring's small overshoot.
/// </summary>
public sealed class CubicBezierEase : EasingFunctionBase
{
    public static readonly DependencyProperty X1Property = DependencyProperty.Register(nameof(X1), typeof(double), typeof(CubicBezierEase));
    public static readonly DependencyProperty Y1Property = DependencyProperty.Register(nameof(Y1), typeof(double), typeof(CubicBezierEase));
    public static readonly DependencyProperty X2Property = DependencyProperty.Register(nameof(X2), typeof(double), typeof(CubicBezierEase));
    public static readonly DependencyProperty Y2Property = DependencyProperty.Register(nameof(Y2), typeof(double), typeof(CubicBezierEase));

    public CubicBezierEase() => EasingMode = EasingMode.EaseIn; // EaseIn = use EaseInCore as written.

    public CubicBezierEase(double x1, double y1, double x2, double y2) : this()
    {
        X1 = x1; Y1 = y1; X2 = x2; Y2 = y2;
        Freeze();
    }

    public double X1 { get => (double)GetValue(X1Property); set => SetValue(X1Property, value); }
    public double Y1 { get => (double)GetValue(Y1Property); set => SetValue(Y1Property, value); }
    public double X2 { get => (double)GetValue(X2Property); set => SetValue(X2Property, value); }
    public double Y2 { get => (double)GetValue(Y2Property); set => SetValue(Y2Property, value); }

    protected override double EaseInCore(double normalizedTime)
    {
        if (normalizedTime <= 0) return 0;
        if (normalizedTime >= 1) return 1;
        double s = SolveForX(normalizedTime);
        return Bezier(s, Y1, Y2);
    }

    protected override Freezable CreateInstanceCore() => new CubicBezierEase();

    private static double Bezier(double s, double p1, double p2)
    {
        double u = 1 - s;
        return 3 * u * u * s * p1 + 3 * u * s * s * p2 + s * s * s;
    }

    private static double BezierSlope(double s, double p1, double p2)
    {
        double u = 1 - s;
        return 3 * u * u * p1 + 6 * u * s * (p2 - p1) + 3 * s * s * (1 - p2);
    }

    /// <summary>Finds s where the curve's x equals the given time (Newton steps, then bisection).</summary>
    private double SolveForX(double x)
    {
        double s = x;
        for (int i = 0; i < 8; i++)
        {
            double error = Bezier(s, X1, X2) - x;
            if (Math.Abs(error) < 1e-6) return s;
            double slope = BezierSlope(s, X1, X2);
            if (Math.Abs(slope) < 1e-6) break;
            s -= error / slope;
        }
        double lo = 0, hi = 1;
        s = x;
        for (int i = 0; i < 30; i++)
        {
            double value = Bezier(s, X1, X2);
            if (Math.Abs(value - x) < 1e-6) break;
            if (value < x) lo = s; else hi = s;
            s = (lo + hi) / 2;
        }
        return s;
    }
}
