using System;
using System.Text.Json.Nodes;
using System.Windows;

namespace CodexUsage;

internal static class CapsuleExpandedDragPlacementTests
{
    internal static JsonObject Run()
    {
        var checks = new JsonArray(); var failures = new JsonArray();
        void Check(bool condition, string id) { checks.Add(id); if (!condition) failures.Add(id); }
        bool Near(double a, double b) => Math.Abs(a - b) < .000001;
        bool Same(Rect a, Rect b) => !a.IsEmpty && !b.IsEmpty && Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Width, b.Width) && Near(a.Height, b.Height);
        var offsets = new[] { new Vector(0, 0), new Vector(260, 0), new Vector(0, 334), new Vector(260, 334) };
        var grip = new Point(117.25, 286.75);
        foreach (double scale in new[] { 1d, 1.25, 2, 3 }) foreach (var offset in offsets)
        {
            var dpi = new DpiScale(scale, scale); var screen = new Point(-1276.5, -92.25);
            var placed = CapsuleExpandedDragPlacement.AtPointer(screen, grip, offset, dpi);
            string id = $"dpi-{scale}-anchor-{offset}";
            Check(Near(placed.Panel.Left + grip.X * scale, screen.X) && Near(placed.Panel.Top + grip.Y * scale, screen.Y), id + "-same-dip-grip-stays-under-physical-pointer");
            Check(Near(placed.Panel.Width, 336 * scale) && Near(placed.Panel.Height, 410 * scale) &&
                Near(placed.Compact.Width, 76 * scale) && Near(placed.Compact.Height, 76 * scale), id + "-logical-dimensions-retained");
            Check(Near(placed.Compact.Left - placed.Panel.Left, offset.X * scale) && Near(placed.Compact.Top - placed.Panel.Top, offset.Y * scale), id + "-all-four-compact-anchors-stay-relative");
            var work = new Rect(-2300 * scale, -1100 * scale, 1920 * scale, 1040 * scale);
            var safe = new Rect(work.X + 12 * scale, work.Y + 12 * scale, work.Width - 24 * scale, work.Height - 24 * scale);
            foreach (var at in new[] { work.TopLeft - new Vector(40, 90), work.TopRight + new Vector(40, -90), work.BottomLeft + new Vector(-40, 90), work.BottomRight + new Vector(40, 90) })
            {
                var before = CapsuleExpandedDragPlacement.AtPointer(at, grip, offset, dpi);
                var after = CapsuleExpandedDragPlacement.Clamp(before, work, dpi);
                Check(safe.Contains(after.Panel) && work.Contains(after.Compact), id + "-negative-monitor-edge-clamps-full-panel-and-compact-" + at);
                Check(Near(after.Compact.X - after.Panel.X, offset.X * scale) && Near(after.Compact.Y - after.Panel.Y, offset.Y * scale), id + "-edge-clamp-never-flips-anchor-" + at);
                Check(Same(after.Panel, CapsuleExpandedDragPlacement.Clamp(after, work, dpi).Panel) &&
                    Same(after.Compact, CapsuleExpandedDragPlacement.Clamp(after, work, dpi).Compact), id + "-repeated-clamp-is-stable-" + at);
            }
        }
        var differentAxes = new DpiScale(2, 1.25);
        var anisotropic = CapsuleExpandedDragPlacement.AtPointer(new(-80, 950), new(50, 200), new(260, 334), differentAxes);
        Check(Same(anisotropic.Panel, new(-180, 700, 672, 512.5)) && Same(anisotropic.Compact, new(340, 1117.5, 152, 95)), "different-dpi-axes-keep-each-logical-grip-coordinate");
        var small = CapsuleExpandedDragPlacement.Clamp(anisotropic, new(-100, -50, 180, 200), differentAxes);
        Check(Same(small.Panel, new(-76, -35, 672, 512.5)) && Same(small.Compact, new(-72, 55, 152, 95)), "undersized-work-area-keeps-panel-size-and-leading-inset-with-compact-visible");
        var tiny = CapsuleExpandedDragPlacement.Clamp(anisotropic, new(-10, -20, 20, 30), differentAxes);
        Check(Same(tiny.Panel, new(14, -5, 672, 512.5)) && Same(tiny.Compact, new(-10, -20, 152, 95)), "work-area-smaller-than-compact-keeps-dimensions-at-leading-anchor");

        var pathStart = new Point(-300.125, 520.75); var origin = CapsuleExpandedDragPlacement.AtPointer(pathStart, grip, offsets[3], new(1.25, 1.25));
        CapsuleExpandedDragPlacement latest = origin;
        for (int i = 0; i < 1000; i++)
        {
            var at = new Point(pathStart.X + Math.Sin(i) * 251.3, pathStart.Y + Math.Cos(i) * 181.7);
            latest = CapsuleExpandedDragPlacement.AtPointer(at, grip, offsets[3], new(1.25, 1.25));
        }
        latest = CapsuleExpandedDragPlacement.AtPointer(pathStart, grip, offsets[3], new(1.25, 1.25));
        Check(Same(origin.Panel, latest.Panel) && Same(origin.Compact, latest.Compact), "absolute-screen-samples-do-not-accumulate-movement-error");
        foreach (double scale in new[] { 3d, 1d, 2d, 1.25d })
        {
            latest = CapsuleExpandedDragPlacement.AtPointer(pathStart, grip, offsets[3], new(scale, scale));
            Check(Near((pathStart.X - latest.Panel.X) / scale, grip.X) && Near((pathStart.Y - latest.Panel.Y) / scale, grip.Y), "crossing-dpi-monitors-retains-dip-grip-" + scale);
        }
        var fallback = CapsuleExpandedDragPlacement.AtPointer(new(200, 300), new(20, 30), offsets[3], default);
        Check(Same(fallback.Panel, new(180, 270, 336, 410)) && Same(fallback.Compact, new(440, 604, 76, 76)), "unknown-dpi-falls-back-to-one");
        var invalidScale = CapsuleExpandedDragPlacement.AtPointer(new(200, 300), new(20, 30), offsets[3], new(double.NaN, double.PositiveInfinity));
        Check(Same(invalidScale.Panel, fallback.Panel) && Same(invalidScale.Compact, fallback.Compact), "nonfinite-dpi-falls-back-to-one-per-axis");
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Check(CapsuleExpandedDragPlacement.AtPointer(new(invalid, 0), new(), new(), default).Panel.IsEmpty &&
                CapsuleExpandedDragPlacement.AtPointer(new(), new(0, invalid), new(), default).Panel.IsEmpty &&
                CapsuleExpandedDragPlacement.AtPointer(new(), new(), new(invalid, 0), default).Panel.IsEmpty, "nonfinite-pointer-grip-or-anchor-is-rejected-" + invalid);
        }
        Check(CapsuleExpandedDragPlacement.AtPointer(new(double.MaxValue, 0), new(-10, 0), new(), new(double.MaxValue, 1)).Panel.IsEmpty,
            "finite-input-overflow-cannot-create-invalid-window-geometry");
        Check(CapsuleExpandedDragPlacement.Clamp(new(Rect.Empty, origin.Compact), new(0, 0, 1920, 1080), default).Panel.IsEmpty &&
            CapsuleExpandedDragPlacement.Clamp(origin, Rect.Empty, default).Panel.IsEmpty, "missing-panel-or-work-area-does-not-produce-placement");
        Check(CapsuleExpandedDragPlacement.Clamp(origin, new(double.MaxValue, 0, double.MaxValue, 100), default).Panel.IsEmpty,
            "overflowed-work-bounds-are-rejected");
        foreach (double scale in new[] { 1d, 1.25, 2, 3 })
        {
            var dpi = new DpiScale(scale, scale);
            var monitor = new CapsuleMonitor("left", new(-1920 * scale, -200 * scale, 1920 * scale, 1080 * scale), new(-1920 * scale, -200 * scale, 1920 * scale, 1040 * scale));
            var work = monitor.Work;
            foreach (var (edge, pointer) in new[]
            {
                (CapsuleEdge.Left, new Point(work.Left + 5 * scale, work.Top + 500 * scale)),
                (CapsuleEdge.Right, new Point(work.Right - 5 * scale, work.Top + 500 * scale)),
                (CapsuleEdge.Top, new Point(work.Left + 900 * scale, work.Top + 5 * scale)),
                (CapsuleEdge.Bottom, new Point(work.Left + 900 * scale, work.Bottom - 5 * scale))
            })
            {
                var panel = CapsuleExpandedDragPlacement.AtPointer(pointer, new(168, 205), new(), dpi).Panel;
                Check(CapsuleExpandedDragPlacement.DropEdge(panel, pointer, monitor, new[] { monitor }, dpi) == edge, $"drop-recovery-selects-{edge}-before-clamping-at-{scale}");
            }
            var neighbor = new CapsuleMonitor("right", new(0, -200 * scale, 1920 * scale, 1080 * scale), new(0, -200 * scale, 1920 * scale, 1040 * scale));
            var seamPointer = new Point(-5 * scale, 400 * scale);
            var seamPanel = CapsuleExpandedDragPlacement.AtPointer(seamPointer, new(168, 205), new(), dpi).Panel;
            Check(CapsuleExpandedDragPlacement.DropEdge(seamPanel, seamPointer, monitor, new[] { monitor, neighbor }, dpi) == CapsuleEdge.Right,
                $"expanded-drop-docks-at-selected-display-seam-{scale}");
            var interiorPointer = new Point(-1000 * scale, 400 * scale);
            Check(CapsuleExpandedDragPlacement.DropEdge(CapsuleExpandedDragPlacement.AtPointer(interiorPointer, new(168, 205), new(), dpi).Panel,
                interiorPointer, monitor, new[] { monitor }, dpi) == CapsuleEdge.None, $"interior-drop-retains-free-position-{scale}");
        }
        var staggeredMain = new CapsuleMonitor("main", new(0, 0, 1920, 1080), new(0, 0, 1920, 1080));
        var staggeredNeighbor = new CapsuleMonitor("short", new(1920, 0, 1200, 540), new(1920, 0, 1200, 540));
        Check(CapsuleExpandedDragPlacement.DropEdge(new(1680, 300, 336, 410), new(1890, 690), staggeredMain, new[] { staggeredMain, staggeredNeighbor }, new(1, 1), new(1844, 300, 76, 76)) == CapsuleEdge.Right,
            "staggered-monitor-seam-remains-dockable-for-footer-drag");
        Check(CapsuleExpandedDragPlacement.DropEdge(new(1680, 300, 336, 410), new(1890, 330), staggeredMain, new[] { staggeredMain, staggeredNeighbor }, new(1, 1), new(1844, 634, 76, 76)) == CapsuleEdge.Right,
            "staggered-monitor-exposed-edge-uses-final-compact-anchor-not-header-pointer");
        return J.Obj(("success", failures.Count == 0), ("checks", checks), ("failures", failures));
    }
}
