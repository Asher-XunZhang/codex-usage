using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace CodexUsage;

/// <summary>Expanded drag placement in physical pixels, with a stable DIP grip and compact anchor.</summary>
internal sealed record CapsuleExpandedDragPlacement(Rect Panel, Rect Compact)
{
    internal static CapsuleEdge DropEdge(Rect panel, Point pointer, CapsuleMonitor monitor, IReadOnlyList<CapsuleMonitor> monitors, DpiScale dpi, Rect? compact = null)
    {
        var work = monitor.Work; double sx = Scale(dpi.DpiScaleX), sy = Scale(dpi.DpiScaleY);
        var candidates = new[]
        {
            (CapsuleEdge.Left, panel.Left <= work.Left + 16 * sx, Math.Abs(pointer.X - work.Left) / sx),
            (CapsuleEdge.Right, panel.Right >= work.Right - 16 * sx, Math.Abs(pointer.X - work.Right) / sx),
            (CapsuleEdge.Top, panel.Top <= work.Top + 16 * sy, Math.Abs(pointer.Y - work.Top) / sy),
            (CapsuleEdge.Bottom, panel.Bottom >= work.Bottom - 16 * sy, Math.Abs(pointer.Y - work.Bottom) / sy)
        };
        foreach (var (edge, near, _) in candidates.OrderBy(x => x.Item3))
        {
            if (!near) continue;
            // The release pointer ranks the selected display's edges, including shared seams.
            return edge;
        }
        return CapsuleEdge.None;
    }

    internal static CapsuleExpandedDragPlacement AtPointer(Point screen, Point gripDip, Vector compactOffsetDip, DpiScale dpi)
    {
        if (!Finite(screen.X, screen.Y, gripDip.X, gripDip.Y, compactOffsetDip.X, compactOffsetDip.Y)) return Empty;
        double sx = Scale(dpi.DpiScaleX), sy = Scale(dpi.DpiScaleY);
        double left = screen.X - gripDip.X * sx, top = screen.Y - gripDip.Y * sy;
        double cx = left + compactOffsetDip.X * sx, cy = top + compactOffsetDip.Y * sy;
        double width = 336 * sx, height = 410 * sy, cw = 76 * sx, ch = 76 * sy;
        if (!Finite(left, top, cx, cy, width, height, cw, ch)) return Empty;
        var result = new CapsuleExpandedDragPlacement(new(left, top, width, height), new(cx, cy, cw, ch));
        return Usable(result.Panel) && Usable(result.Compact) ? result : Empty;
    }

    internal static CapsuleExpandedDragPlacement Clamp(CapsuleExpandedDragPlacement placement, Rect work, DpiScale dpi)
    {
        if (!Usable(placement.Panel) || !Usable(placement.Compact) || !Usable(work)) return Empty;
        double sx = Scale(dpi.DpiScaleX), sy = Scale(dpi.DpiScaleY);
        double left = work.Left + 12 * sx, top = work.Top + 12 * sy;
        double lastLeft = Math.Max(left, work.Right - 12 * sx - placement.Panel.Width);
        double lastTop = Math.Max(top, work.Bottom - 12 * sy - placement.Panel.Height);
        if (!Finite(left, top, lastLeft, lastTop)) return Empty;
        double x = Math.Clamp(placement.Panel.Left, left, lastLeft), y = Math.Clamp(placement.Panel.Top, top, lastTop);
        // Move the saved compact anchor with the panel; never ask automatic
        // expansion placement to choose a different left/right/up/down direction.
        double cx = placement.Compact.Left + (x - placement.Panel.Left);
        double cy = placement.Compact.Top + (y - placement.Panel.Top);
        if (!Finite(cx, cy)) return Empty;
        cx = Math.Clamp(cx, work.Left, Math.Max(work.Left, work.Right - placement.Compact.Width));
        cy = Math.Clamp(cy, work.Top, Math.Max(work.Top, work.Bottom - placement.Compact.Height));
        var result = new CapsuleExpandedDragPlacement(new(x, y, placement.Panel.Width, placement.Panel.Height),
            new(cx, cy, placement.Compact.Width, placement.Compact.Height));
        return Usable(result.Panel) && Usable(result.Compact) ? result : Empty;
    }

    private static CapsuleExpandedDragPlacement Empty => new(Rect.Empty, Rect.Empty);
    private static double Scale(double value) => double.IsFinite(value) && value > 0 ? value : 1;
    private static bool Finite(params double[] values) => Array.TrueForAll(values, double.IsFinite);
    private static bool Usable(Rect rect) => !rect.IsEmpty && rect.Width > 0 && rect.Height > 0 &&
        Finite(rect.X, rect.Y, rect.Width, rect.Height, rect.Right, rect.Bottom);
}
