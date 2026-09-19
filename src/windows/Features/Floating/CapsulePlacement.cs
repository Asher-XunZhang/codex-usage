using System;
using System.Collections.Generic;
using System.Windows;

namespace CodexUsage;

internal enum CapsuleEdge { None, Left, Right, Top, Bottom }

internal sealed record CapsuleMonitor(string Id, Rect Bounds, Rect Work);

/// <summary>Pure placement in physical desktop pixels; only dimensions and distances use DIPs.</summary>
internal static class CapsulePlacement
{
    private const double Epsilon = .000001;

    public static Rect Expanded(Rect compact, Rect work, DpiScale dpi)
    {
        if (!Usable(compact) || !Usable(work)) return Rect.Empty;
        double sx = Scale(dpi.DpiScaleX), sy = Scale(dpi.DpiScaleY);
        double width = 336 * sx, height = 410 * sy;
        double left = work.Left + 12 * sx, top = work.Top + 12 * sy;
        double right = work.Right - 12 * sx, bottom = work.Bottom - 12 * sy;
        double x = compact.Right - width, y = compact.Top;
        if (x < left) x = compact.Left;
        if (y + height > bottom) y = compact.Bottom - height;
        // Preserve the panel's DIP size on unusually small work areas. As with the
        // existing capsule clamp, an oversized axis uses its leading anchor.
        return new(Math.Clamp(x, left, Math.Max(left, right - width)),
            Math.Clamp(y, top, Math.Max(top, bottom - height)), width, height);
    }

    public static CapsuleEdge Dock(Rect compact, CapsuleMonitor monitor, IReadOnlyList<CapsuleMonitor> monitors, DpiScale dpi)
    {
        if (!Usable(compact) || !Usable(monitor.Work) || !Usable(monitor.Bounds)) return CapsuleEdge.None;
        double sx = Scale(dpi.DpiScaleX), sy = Scale(dpi.DpiScaleY);
        var work = monitor.Work;
        var candidates = new (CapsuleEdge Edge, double Distance)[]
        {
            (CapsuleEdge.Left, Math.Abs(compact.Left - work.Left) / sx),
            (CapsuleEdge.Right, Math.Abs(compact.Right - work.Right) / sx),
            (CapsuleEdge.Top, Math.Abs(compact.Top - work.Top) / sy),
            (CapsuleEdge.Bottom, Math.Abs(compact.Bottom - work.Bottom) / sy)
        };
        var selected = CapsuleEdge.None;
        double nearest = double.PositiveInfinity;
        // Strict comparison gives corners a stable Left, Right, Top, Bottom tie order.
        foreach (var candidate in candidates)
        {
            if (candidate.Distance > 16 + Epsilon || candidate.Distance >= nearest) continue;
            nearest = candidate.Distance;
            selected = candidate.Edge;
        }
        return selected;
    }

    public static Rect CompactAtEdge(Rect compact, Rect work, CapsuleEdge edge)
    {
        if (!Usable(compact) || !Usable(work)) return Rect.Empty;
        double x = Math.Clamp(compact.Left, work.Left, Math.Max(work.Left, work.Right - compact.Width));
        double y = Math.Clamp(compact.Top, work.Top, Math.Max(work.Top, work.Bottom - compact.Height));
        switch (edge)
        {
            case CapsuleEdge.Left: x = work.Left; break;
            case CapsuleEdge.Right: x = Math.Max(work.Left, work.Right - compact.Width); break;
            case CapsuleEdge.Top: y = work.Top; break;
            case CapsuleEdge.Bottom: y = Math.Max(work.Top, work.Bottom - compact.Height); break;
        }
        return new(x, y, compact.Width, compact.Height);
    }

    public static Rect Indicator(Rect compact, Rect work, CapsuleEdge edge, DpiScale dpi, bool showsMonitor = false)
    {
        if (!Usable(compact) || !Usable(work)
            || edge is not (CapsuleEdge.Left or CapsuleEdge.Right or CapsuleEdge.Top or CapsuleEdge.Bottom)) return Rect.Empty;
        bool vertical = edge is CapsuleEdge.Left or CapsuleEdge.Right;
        double width = (vertical ? 44 : showsMonitor ? 124 : 88) * Scale(dpi.DpiScaleX);
        double height = (vertical ? showsMonitor ? 96 : 68 : 28) * Scale(dpi.DpiScaleY);
        return CompactAtEdge(new(compact.Left + compact.Width / 2 - width / 2,
            compact.Top + compact.Height / 2 - height / 2, width, height), work, edge);
    }

    private static double Scale(double value) => double.IsFinite(value) && value > 0 ? value : 1;
    private static bool Usable(Rect rect) => !rect.IsEmpty && double.IsFinite(rect.X) && double.IsFinite(rect.Y)
        && double.IsFinite(rect.Width) && double.IsFinite(rect.Height) && rect.Width > 0 && rect.Height > 0;
}
