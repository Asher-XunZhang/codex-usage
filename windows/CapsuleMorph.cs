using System;
using System.Windows;

namespace CodexUsage;

internal sealed record CapsuleMorphFrame(Rect Shape, double Radius, Rect PrimaryBounds, Point PrimaryCenter,
    double PrimaryFontSize, double SecondaryAlpha, double DetailsAlpha, double ArcAlpha);

/// <summary>One reversible progress value; every coordinate is in the surface's DIPs.</summary>
internal static class CapsuleMorph
{
    public const double ExpandMilliseconds = 280;
    public const double CollapseMilliseconds = 220;
    public const double TextMargin = 2;
    private const double Epsilon = .000000001;

    public static CapsuleMorphFrame Frame(Rect compact, Rect panel, double progress)
    {
        if (!Usable(compact) || !Usable(panel)) return new(Rect.Empty, 0, Rect.Empty, new Point(), 18, 0, 0, 0);
        double p = double.IsNaN(progress) ? 0 : Math.Clamp(progress, 0, 1);
        double wx = Smooth((p - .14) / .62), hy = Smooth((p - .14) / .70);
        // Interpolate both sides of an axis together. Faster width with a slower center
        // could overshoot the fixed HWND; interpolated sides stay within both endpoints.
        double left = Mix(compact.Left, panel.Left, wx), right = Mix(compact.Right, panel.Right, wx);
        double top = Mix(compact.Top, panel.Top, hy), bottom = Mix(compact.Bottom, panel.Bottom, hy);
        var shape = new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
        double radius = Math.Min(Mix(38, 22, hy), Math.Min(shape.Width, shape.Height) / 2);
        var center = new Point(Mix(compact.Left + compact.Width / 2, panel.Left + 52, hy),
            Mix(compact.Top + compact.Height / 2 - 3, panel.Top + 27, hy));
        var primary = SafeText(new(center.X - 32, center.Y - 15, 64, 30), shape, radius);
        var safeCenter = primary.IsEmpty ? new Point(shape.Left + shape.Width / 2, shape.Top + shape.Height / 2)
            : new Point(primary.Left + primary.Width / 2, primary.Top + primary.Height / 2);
        double compactAlpha = 1 - Smooth(p / .12);
        return new(shape, radius, primary, safeCenter, Mix(18, 20, hy), compactAlpha,
            Smooth((p - .86) / .14), compactAlpha);
    }

    /// <summary>
    /// Keeps a text box's width and height. If no placement fits, returns Rect.Empty so
    /// the caller can defer the text instead of clipping it or making it unreadably small.
    /// </summary>
    public static Rect SafeText(Rect preferred, Rect shape, double radius, double margin = TextMargin)
    {
        if (!Usable(preferred) || !TryInterior(shape, radius, margin, out var interior, out _)) return Rect.Empty;
        if (preferred.Width > interior.Width + Epsilon || preferred.Height > interior.Height + Epsilon) return Rect.Empty;
        if (FullyContainsRounded(preferred, shape, radius, margin)) return preferred;
        var centered = new Rect(shape.Left + (shape.Width - preferred.Width) / 2,
            shape.Top + (shape.Height - preferred.Height) / 2, preferred.Width, preferred.Height);
        // The feasible centers form a convex, centrally symmetric set. If the centered
        // box cannot fit, no translated box of these dimensions can fit either.
        if (!FullyContainsRounded(centered, shape, radius, margin)) return Rect.Empty;
        var desired = new Rect(Math.Clamp(preferred.Left, interior.Left, Math.Max(interior.Left, interior.Right - preferred.Width)),
            Math.Clamp(preferred.Top, interior.Top, Math.Max(interior.Top, interior.Bottom - preferred.Height)), preferred.Width, preferred.Height);
        if (FullyContainsRounded(desired, shape, radius, margin)) return desired;
        double low = 0, high = 1;
        var best = centered;
        for (int i = 0; i < 40; i++)
        {
            double t = (low + high) / 2;
            var candidate = new Rect(Mix(centered.Left, desired.Left, t), Mix(centered.Top, desired.Top, t), preferred.Width, preferred.Height);
            if (FullyContainsRounded(candidate, shape, radius, margin)) { low = t; best = candidate; }
            else high = t;
        }
        return best;
    }

    /// <summary>Four corners suffice because the inset rounded rectangle is convex.</summary>
    public static bool FullyContainsRounded(Rect item, Rect shape, double radius, double margin = TextMargin)
    {
        if (!Usable(item) || !TryInterior(shape, radius, margin, out var interior, out double innerRadius)) return false;
        bool Inside(Point point)
        {
            if (point.X < interior.Left - Epsilon || point.X > interior.Right + Epsilon
                || point.Y < interior.Top - Epsilon || point.Y > interior.Bottom + Epsilon) return false;
            // At a circular axis the two corner centers coincide mathematically, but
            // independently rounded additions/subtractions can invert them by one ULP.
            // Both clamp intervals contain the same computed midpoint, so they stay
            // ordered even while the circle slides through fractional negative origins.
            double centerX = interior.Left + (interior.Right - interior.Left) / 2;
            double centerY = interior.Top + (interior.Bottom - interior.Top) / 2;
            double x = Math.Clamp(point.X, Math.Min(interior.Left + innerRadius, centerX), Math.Max(interior.Right - innerRadius, centerX));
            double y = Math.Clamp(point.Y, Math.Min(interior.Top + innerRadius, centerY), Math.Max(interior.Bottom - innerRadius, centerY));
            double dx = point.X - x, dy = point.Y - y;
            return dx * dx + dy * dy <= innerRadius * innerRadius + Epsilon;
        }
        return Inside(item.TopLeft) && Inside(item.TopRight) && Inside(item.BottomLeft) && Inside(item.BottomRight);
    }

    private static bool TryInterior(Rect shape, double radius, double margin, out Rect interior, out double innerRadius)
    {
        interior = Rect.Empty; innerRadius = 0;
        if (!Usable(shape) || !double.IsFinite(radius) || radius < 0 || !double.IsFinite(margin) || margin < 0
            || shape.Width <= margin * 2 || shape.Height <= margin * 2) return false;
        double outerRadius = Math.Min(radius, Math.Min(shape.Width, shape.Height) / 2);
        interior = new(shape.Left + margin, shape.Top + margin, shape.Width - margin * 2, shape.Height - margin * 2);
        double actualWidth = interior.Right - interior.Left, actualHeight = interior.Bottom - interior.Top;
        innerRadius = Math.Min(Math.Max(0, outerRadius - margin), Math.Min(actualWidth, actualHeight) / 2); return true;
    }
    private static bool Usable(Rect rect) => !rect.IsEmpty && double.IsFinite(rect.Left) && double.IsFinite(rect.Top)
        && double.IsFinite(rect.Right) && double.IsFinite(rect.Bottom) && rect.Width > 0 && rect.Height > 0;
    private static double Mix(double from, double to, double p) => p <= 0 ? from : p >= 1 ? to : from + (to - from) * p;
    private static double Smooth(double value) { double t = Math.Clamp(value, 0, 1); return t * t * (3 - 2 * t); }
}
