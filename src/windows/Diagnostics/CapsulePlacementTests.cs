using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Windows;

namespace CodexUsage;

internal static class CapsulePlacementTests
{
    public static JsonObject Run()
    {
        var checks = new JsonArray();
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("Capsule placement test: " + description);
            checks.Add(description);
        }
        bool Near(double actual, double expected) => Math.Abs(actual - expected) < .000001;
        bool Same(Rect actual, Rect expected) => !actual.IsEmpty && !expected.IsEmpty
            && Near(actual.X, expected.X) && Near(actual.Y, expected.Y)
            && Near(actual.Width, expected.Width) && Near(actual.Height, expected.Height);
        var dpi = new DpiScale(1, 1);
        var work = new Rect(0, 0, 1920, 1040);
        var monitor = new CapsuleMonitor("primary", new(0, 0, 1920, 1080), work);
        CapsuleEdge Dock(Rect compact, CapsuleMonitor? selected = null, IReadOnlyList<CapsuleMonitor>? monitors = null, DpiScale? scale = null)
            => CapsulePlacement.Dock(compact, selected ?? monitor, monitors ?? new[] { monitor }, scale ?? dpi);

        Check(Same(CapsulePlacement.Expanded(new(900, 100, 76, 76), work, dpi), new(640, 100, 336, 410)),
            "expanded panel prefers compact right and top anchors");
        Check(Same(CapsulePlacement.Expanded(new(50, 100, 76, 76), work, dpi), new(50, 100, 336, 410)),
            "insufficient space on the left expands right from compact left");
        Check(Same(CapsulePlacement.Expanded(new(900, 900, 76, 76), work, dpi), new(640, 566, 336, 410)),
            "insufficient space below expands upward from compact bottom");
        Check(Same(CapsulePlacement.Expanded(new(0, 964, 76, 76), work, dpi), new(12, 618, 336, 410)),
            "bottom left expansion combines direction changes with the twelve DIP inset");
        Check(Same(CapsulePlacement.Expanded(new(1844, 0, 76, 76), work, dpi), new(1572, 12, 336, 410)),
            "top right expansion respects both work area insets");
        Check(Dock(new(16, 300, 76, 76)) == CapsuleEdge.Left && Dock(new(16.001, 300, 76, 76)) == CapsuleEdge.None,
            "docking includes sixteen DIP threshold and excludes points beyond it");
        Check(Dock(new(-10, 300, 76, 76)) == CapsuleEdge.Left,
            "small overshoot can dock back inside the work area");
        Check(Dock(new(1828, 300, 76, 76)) == CapsuleEdge.Right && Dock(new(700, 16, 76, 76)) == CapsuleEdge.Top
            && Dock(new(700, 948, 76, 76)) == CapsuleEdge.Bottom, "all four work area edges accept docking");
        Check(Dock(new(4, 9, 76, 76)) == CapsuleEdge.Left && Dock(new(9, 4, 76, 76)) == CapsuleEdge.Top,
            "corner docking chooses the physically nearest edge in DIPs");
        Check(Dock(new(4, 4, 76, 76)) == CapsuleEdge.Left && Dock(new(1840, 4, 76, 76)) == CapsuleEdge.Right,
            "equal corner distances use a stable edge preference");
        Check(Dock(new(700, 300, 76, 76)) == CapsuleEdge.None, "interior position remains undocked");

        foreach (double scale in new[] { 1d, 1.25, 1.5, 2, 3 })
        {
            var currentDpi = new DpiScale(scale, scale);
            var negative = new Rect(-2000 * scale, -300 * scale, 1920 * scale, 1040 * scale);
            var compact = new Rect(-1960 * scale, 550 * scale, 76 * scale, 76 * scale);
            var result = CapsulePlacement.Expanded(compact, negative, currentDpi);
            var safe = new Rect(negative.Left + 12 * scale, negative.Top + 12 * scale,
                negative.Width - 24 * scale, negative.Height - 24 * scale);
            Check(Near(result.Width / scale, 336) && Near(result.Height / scale, 410)
                && Near(result.Left, compact.Left) && Near(result.Bottom, compact.Bottom) && safe.Contains(result),
                "negative-origin upward/right expansion preserves DIP size and inset at scale " + scale);
            var scaledMonitor = new CapsuleMonitor("negative", negative, negative);
            var near = new Rect(negative.Left + 16 * scale, negative.Top + 200 * scale, 76 * scale, 76 * scale);
            Check(Dock(near, scaledMonitor, new[] { scaledMonitor }, currentDpi) == CapsuleEdge.Left
                && Dock(new(near.X + .001 * scale, near.Y, near.Width, near.Height), scaledMonitor, new[] { scaledMonitor }, currentDpi) == CapsuleEdge.None,
                "negative-origin docking threshold is DPI independent at scale " + scale);
            foreach (var edge in new[] { CapsuleEdge.Left, CapsuleEdge.Right, CapsuleEdge.Top, CapsuleEdge.Bottom })
            {
                var indicator = CapsulePlacement.Indicator(compact, negative, edge, currentDpi);
                bool vertical = edge is CapsuleEdge.Left or CapsuleEdge.Right;
                Check(negative.Contains(indicator) && Near(indicator.Width / scale, vertical ? 28 : 76)
                    && Near(indicator.Height / scale, vertical ? 72 : 28),
                    "indicator remains inside negative work area at scale " + scale + " edge " + edge);
            }
        }
        var anisotropic = new DpiScale(2, 1.25);
        Check(Dock(new(24, 20, 152, 95), scale: anisotropic) == CapsuleEdge.Left,
            "edge comparison converts each physical axis with its own DPI");
        Check(Same(CapsulePlacement.Expanded(new(700, 100, 152, 95), work, anisotropic), new(180, 100, 672, 512.5)),
            "fractional and unequal DPI axes preserve exact panel DIP dimensions");

        var right = new CapsuleMonitor("right", new(1920, 200, 1920, 1080), new(1920, 200, 1920, 1040));
        Check(Dock(new(1844, 300, 76, 76), monitors: new[] { monitor, right }) == CapsuleEdge.None,
            "shared right monitor seam is not a docking edge");
        Check(Dock(new(1844, 80, 76, 76), monitors: new[] { right, monitor }) == CapsuleEdge.Right,
            "exposed part above a staggered right monitor remains dockable");
        Check(Dock(new(1844, 162, 76, 76), monitors: new[] { right }) == CapsuleEdge.None,
            "seam filtering follows the compact center rather than its top edge");
        var left = new CapsuleMonitor("left", new(-1920, -300, 1920, 1080), new(-1920, -300, 1920, 1040));
        var top = new CapsuleMonitor("top", new(400, -1080, 1920, 1080), new(400, -1080, 1920, 1040));
        var bottom = new CapsuleMonitor("bottom", new(400, 1080, 1920, 1080), new(400, 1080, 1920, 1040));
        var full = monitor with { Work = monitor.Bounds };
        Check(Dock(new(0, 300, 76, 76), monitors: new[] { left }) == CapsuleEdge.None
            && Dock(new(700, 0, 76, 76), monitors: new[] { top }) == CapsuleEdge.None
            && Dock(new(700, 1004, 76, 76), full, new[] { bottom }) == CapsuleEdge.None,
            "left top and bottom physical seams are excluded without requiring the current monitor in the list");
        Check(Dock(new(700, 964, 76, 76), monitors: new[] { bottom }) == CapsuleEdge.Bottom,
            "bottom taskbar work edge remains dockable before a physical monitor seam");
        var taskbarRight = monitor with { Work = new Rect(0, 0, 1872, 1040) };
        Check(Dock(new(1796, 300, 76, 76), taskbarRight, new[] { right }) == CapsuleEdge.Right,
            "right taskbar work edge remains dockable before a physical monitor seam");
        var taskbarLeft = monitor with { Work = new Rect(48, 0, 1872, 1040) };
        var taskbarTop = monitor with { Work = new Rect(0, 48, 1920, 992) };
        Check(Dock(new(48, 300, 76, 76), taskbarLeft, new[] { left }) == CapsuleEdge.Left
            && Dock(new(700, 48, 76, 76), taskbarTop, new[] { top }) == CapsuleEdge.Top,
            "left and top taskbar work edges remain dockable beside adjacent monitors");
        var lowerRight = new CapsuleMonitor("lower-right", new(1920, 700, 1920, 1080), new(1920, 700, 1920, 1040));
        var upperRight = right with { Bounds = new Rect(1920, 0, 1920, 500) };
        Check(Dock(new(1844, 550, 76, 76), monitors: new[] { lowerRight, upperRight }) == CapsuleEdge.Right
            && Dock(new(1844, 762, 76, 76), monitors: new[] { upperRight, lowerRight }) == CapsuleEdge.None,
            "multiple neighboring monitors suppress only their covered seam segments");
        var gap = right with { Bounds = new Rect(1921, 0, 1920, 1080) };
        var diagonal = right with { Bounds = new Rect(1920, 1080, 1920, 1080) };
        Check(Dock(new(1844, 300, 76, 76), monitors: new[] { gap }) == CapsuleEdge.Right
            && Dock(new(1844, 964, 76, 76), monitors: new[] { diagonal }) == CapsuleEdge.Right,
            "a physical pixel gap or diagonal corner contact is not a shared seam");
        var completeLeft = left with { Bounds = new Rect(-1920, 0, 1920, 1080) };
        Check(Dock(new(0, 0, 76, 76), monitors: new[] { completeLeft }) == CapsuleEdge.Top,
            "corner docking falls back to the exposed edge after excluding its closer seam");

        Check(Same(CapsulePlacement.CompactAtEdge(new(300, -20, 76, 76), work, CapsuleEdge.Left), new(0, 0, 76, 76))
            && Same(CapsulePlacement.CompactAtEdge(new(300, 1100, 76, 76), work, CapsuleEdge.Right), new(1844, 964, 76, 76)),
            "vertical edge docking is flush and clamps its alignment axis");
        Check(Same(CapsulePlacement.CompactAtEdge(new(-20, 300, 76, 76), work, CapsuleEdge.Top), new(0, 0, 76, 76))
            && Same(CapsulePlacement.CompactAtEdge(new(2000, 300, 76, 76), work, CapsuleEdge.Bottom), new(1844, 964, 76, 76)),
            "horizontal edge docking is flush and clamps its alignment axis");
        Check(Same(CapsulePlacement.Indicator(new(500, 300, 76, 76), work, CapsuleEdge.Left, dpi), new(0, 302, 28, 72))
            && Same(CapsulePlacement.Indicator(new(500, 300, 76, 76), work, CapsuleEdge.Right, dpi), new(1892, 302, 28, 72)),
            "vertical indicators retain the compact center along the edge");
        Check(Same(CapsulePlacement.Indicator(new(500, 300, 76, 76), work, CapsuleEdge.Top, dpi), new(500, 0, 76, 28))
            && Same(CapsulePlacement.Indicator(new(500, 300, 76, 76), work, CapsuleEdge.Bottom, dpi), new(500, 1012, 76, 28)),
            "horizontal indicators retain the compact center along the edge");
        Check(Same(CapsulePlacement.Indicator(new(1844, 1100, 76, 76), work, CapsuleEdge.Right, dpi), new(1892, 968, 28, 72))
            && Same(CapsulePlacement.Indicator(new(-100, 100, 76, 76), work, CapsuleEdge.Top, dpi), new(0, 0, 76, 28)),
            "indicator alignment clamps at both ends of the work area");
        foreach (var edge in new[] { CapsuleEdge.None, (CapsuleEdge)99 })
        {
            Check(Same(CapsulePlacement.CompactAtEdge(new(-20, 1100, 76, 76), work, edge), new(0, 964, 76, 76))
                && CapsulePlacement.Indicator(new(500, 300, 76, 76), work, edge, dpi).IsEmpty,
                "absent or unknown edge clamps the compact window without inventing an indicator: " + edge);
        }
        Check(CapsulePlacement.Expanded(new(0, 0, 76, 76), new(0, 0, 180, 200), dpi) == new Rect(12, 12, 336, 410),
            "undersized work area retains the fixed panel size with a valid leading anchor");
        Check(CapsulePlacement.Expanded(Rect.Empty, work, dpi).IsEmpty
            && CapsulePlacement.Indicator(new(0, 0, 76, 76), Rect.Empty, CapsuleEdge.Left, dpi).IsEmpty
            && Dock(Rect.Empty) == CapsuleEdge.None, "unavailable geometry does not produce invalid windows or docking");
        Check(Same(CapsulePlacement.Expanded(new(900, 100, 76, 76), work, default), new(640, 100, 336, 410)),
            "unknown DPI falls back to one physical pixel per DIP");
        return new JsonObject { ["success"] = true, ["checks"] = checks };
    }
}
