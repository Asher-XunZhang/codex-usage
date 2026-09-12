using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace CodexUsage;

/// <summary>Native pointer capture with synthetic coordinates; validates compact versus expanded drop intent.</summary>
internal static class CapsuleDropIntentTests
{
    internal static async Task<JsonObject> RunAsync()
    {
        var checks = new JsonArray(); var skips = new JsonArray();
        var commands = new List<(string name, string? value)>();
        CapsuleWindow? window = null; Point? pointer = null; bool followPanel = false;
        var previousShutdown = Application.Current.ShutdownMode;
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Point Center(Rect rect) => new(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        void Check(bool okay, string name)
        {
            if (!okay) throw new InvalidOperationException("Capsule drop intent: " + name);
            checks.Add(name);
        }
        async Task Until(Func<bool> condition, string name, int milliseconds = 1800)
        {
            long deadline = Environment.TickCount64 + milliseconds;
            while (!condition() && Environment.TickCount64 < deadline) await Task.Delay(8);
            Check(condition(), name);
        }
        bool Near(double a, double b) => Math.Abs(a - b) <= 2;
        bool Fits(Rect panel, Rect work) => panel.Left >= work.Left - 2 && panel.Top >= work.Top - 2 && panel.Right <= work.Right + 2 && panel.Bottom <= work.Bottom + 2;
        try
        {
            var screens = System.Windows.Forms.Screen.AllScreens.Select(screen => new CapsuleMonitor(screen.DeviceName,
                new Rect(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height),
                new Rect(screen.WorkingArea.X, screen.WorkingArea.Y, screen.WorkingArea.Width, screen.WorkingArea.Height))).ToArray();
            var leftMonitor = screens.OrderBy(x => x.Bounds.Left).First();
            var rightMonitor = screens.OrderByDescending(x => x.Bounds.Right).First();
            window = new CapsuleWindow((name, value) => { commands.Add((name, value)); return Task.CompletedTask; },
                () => Task.FromResult(new JsonObject()), () => followPanel && window != null ? Center(window.VisualPixelBounds) : pointer, _ => true)
            { Topmost = false, ShowActivated = false };
            // Capturing the real HWND replays WPF MouseMove at the desktop cursor;
            // the fixture deliberately drives production entry points with its own coordinates.
            window.PreviewMouseMove += (_, e) => e.Handled = true;
            var state = TaskMonitorDemo.State(); var floating = state.O("settings").O("floating");
            floating["content"] = "monitor"; floating["keepExpanded"] = false; floating["pinned"] = false; floating["edgeAutoHide"] = true;
            window.Update(state); window.Show(); window.UpdateLayout();
            var surface = window.Surface;
            async Task Reset(CapsuleMonitor monitor)
            {
                followPanel = false; pointer = null;
                if (surface.PointerGestureActive) await surface.CaptureLostAsync();
                surface.ReleaseMouseCapture();
                window.Expand(false, false);
                var dpi = VisualTreeHelper.GetDpi(window);
                window.Restore(J.Obj(("pixelLeft", monitor.Work.Left + monitor.Work.Width * .5),
                    ("pixelTop", monitor.Work.Top + monitor.Work.Height * .3), ("dockEdge", "None"), ("edgeAutoHide", true)));
                await Task.Delay(40); window.UpdateLayout();
                pointer = Center(window.CompactPixelBounds); commands.Clear();
                Check(surface.Expansion == 0 && window.DockEdge == CapsuleEdge.None, "reset uses an interior compact anchor");
            }
            async Task CircleToEdge(CapsuleMonitor monitor, CapsuleEdge edge)
            {
                await Reset(monitor);
                window.PointerEntered();
                Check(surface.Expansion == 0, edge + " hover delay leaves a grab opportunity before expansion");
                Point start = pointer!.Value; Point local = surface.PointFromScreen(start);
                Check(surface.PointerDown(local, start) && surface.IsMouseCaptured, edge + " circle press takes native capture");
                var compact = window.CompactPixelBounds;
                Point end = new(edge == CapsuleEdge.Left ? monitor.Work.Left + compact.Width / 2 : monitor.Work.Right - compact.Width / 2, start.Y);
                pointer = end; surface.PointerMove(local, end);
                await Task.Delay(180);
                Check(surface.Expansion == 0 && !window.IsAnimating, edge + " held circle drag cannot be interrupted by delayed hover");
                await surface.PointerUpAsync(surface.PointFromScreen(end), end); await Task.Delay(400);
                Check(window.DockEdge == edge && surface.Expansion == 0, edge + " circle release docks without expanding");
                Check(edge == CapsuleEdge.Left ? Near(window.CompactPixelBounds.Left, monitor.Work.Left) : Near(window.CompactPixelBounds.Right, monitor.Work.Right), edge + " circle anchor reaches the work-area edge");
                Check(commands.Count(x => x.name == "position") >= 1 && commands.All(x => x.name == "position"), edge + " drag only saves position and never activates a card");
            }
            await CircleToEdge(leftMonitor, CapsuleEdge.Left);
            await CircleToEdge(rightMonitor, CapsuleEdge.Right);

            await Reset(leftMonitor);
            if (SystemParameters.ClientAreaAnimation)
            {
                followPanel = true; window.Expand(true);
                await Until(() => surface.Expansion > .3 && surface.Expansion < .9, "opening animation presents a partial visible frame");
                Rect shape = surface.DrawingBounds;
                Point grip = new(shape.Left + shape.Width / 2, shape.Bottom - Math.Min(15, shape.Height / 5));
                Check(surface.Hit(grip) != "details", "partial-animation fixture uses visible content outside the old circle/header grip");
                Point start = surface.PointToScreen(grip); followPanel = false; pointer = start;
                Check(surface.PointerDown(grip, start), "partial visible content can take over opening animation");
                var dpi = VisualTreeHelper.GetDpi(surface); Point end = start + new Vector(24 * dpi.DpiScaleX, 14 * dpi.DpiScaleY);
                pointer = end; surface.PointerMove(grip, end);
                Check(surface.Expansion == 0 && !window.IsAnimating, "taking over an incomplete expansion retains circle drag intent");
                await surface.PointerUpAsync(surface.PointFromScreen(end), end);
                Check(window.DockEdge == CapsuleEdge.None && surface.Expansion == 0, "interior partial-expansion drop remains an undocked circle");
            }
            else skips.Add("System client-area animations disabled: real partial-opening takeover was not exercised.");

            async Task PanelToEdge(CapsuleMonitor monitor, CapsuleEdge edge)
            {
                await Reset(monitor); followPanel = true; window.Expand(true, false); window.UpdateLayout();
                Point local = Center(surface.Regions().Single(x => x.name == "main").bounds);
                Point start = surface.PointToScreen(local); followPanel = false; pointer = start;
                Check(surface.PointerDown(local, start), edge + " complete panel starts from a normal button");
                Rect before = window.VisualPixelBounds;
                double destinationLeft = edge == CapsuleEdge.Left ? monitor.Work.Left - before.Width * .3 : monitor.Work.Right - before.Width * .7;
                Point end = start + new Vector(destinationLeft - before.Left, 0);
                pointer = end; surface.PointerMove(local, end);
                Rect outside = window.VisualPixelBounds;
                Check(edge == CapsuleEdge.Left ? outside.Left < monitor.Work.Left - 2 : outside.Right > monitor.Work.Right + 2,
                    edge + " complete panel may follow the pointer partly outside work area");
                Check(surface.Expansion == 1, edge + " free panel drag does not morph into circle");
                followPanel = true; await surface.PointerUpAsync(local, end); await Task.Delay(400);
                Check(surface.Expansion == 1 && Fits(window.VisualPixelBounds, monitor.Work), edge + " drop returns a complete visible panel after settling");
                Check(window.DockEdge == edge, edge + " outside drop selects the nearest real outer edge");
                Check(edge == CapsuleEdge.Left ? Near(window.CompactPixelBounds.Left, monitor.Work.Left) : Near(window.CompactPixelBounds.Right, monitor.Work.Right),
                    edge + " panel recovery leaves the compact anchor at the chosen edge");
                Check(!window.KeepsExpanded, edge + " panel drop does not silently enable keep expanded");
                followPanel = false; pointer = null; window.PointerLeft();
                await Until(() => surface.Expansion == 0 && !window.IsAnimating, edge + " leaving recovered panel collapses it");
                Check(edge == CapsuleEdge.Left ? Near(window.CompactPixelBounds.Left, monitor.Work.Left) : Near(window.CompactPixelBounds.Right, monitor.Work.Right),
                    edge + " collapsed circle still owns the same edge anchor");
                pointer = Center(window.CompactPixelBounds); window.Expand(true, false); followPanel = true;
                Rect expanded = window.VisualPixelBounds, compact = window.CompactPixelBounds;
                Check(edge == CapsuleEdge.Left ? expanded.Right > compact.Right + 50 : expanded.Left < compact.Left - 50,
                    edge + " next expansion recalculates inward direction");
                Check(Fits(expanded, monitor.Work), edge + " next expansion is fully visible");
                Check(commands.All(x => x.name == "position"), edge + " panel grip never clicks its underlying main button");
            }
            await PanelToEdge(leftMonitor, CapsuleEdge.Left);
            await PanelToEdge(rightMonitor, CapsuleEdge.Right);

            await Reset(leftMonitor); followPanel = true; window.Expand(true, false);
            Point interiorLocal = Center(surface.Regions().Single(x => x.name == "main").bounds), interiorStart = surface.PointToScreen(interiorLocal);
            Check(surface.PointerDown(interiorLocal, interiorStart), "interior panel drag begins");
            var interiorDpi = VisualTreeHelper.GetDpi(surface); Point interiorEnd = interiorStart + new Vector(20 * interiorDpi.DpiScaleX, 12 * interiorDpi.DpiScaleY);
            surface.PointerMove(interiorLocal, interiorEnd); await surface.PointerUpAsync(interiorLocal, interiorEnd); await Task.Delay(400);
            Check(window.DockEdge == CapsuleEdge.None, "interior panel drop does not force edge docking");

            if (SystemParameters.ClientAreaAnimation)
            {
                foreach (bool hide in new[] { false, true })
                {
                    await Reset(rightMonitor); followPanel = true; window.Expand(true, false);
                    Point grip = Center(surface.Regions().Single(x => x.name == "main").bounds);
                    Point start = surface.PointToScreen(grip); Rect before = window.VisualPixelBounds;
                    Point end = start + new Vector(rightMonitor.Work.Right - before.Width * .7 - before.Left, 0);
                    Check(surface.PointerDown(grip, start), "recovery interruption starts a complete panel drag");
                    surface.PointerMove(grip, end); await surface.PointerUpAsync(grip, end);
                    Check(window.IsAnimating && surface.AnimationBounds != null, "outside release starts a bounded recovery animation");
                    if (hide)
                    {
                        window.Hide(); window.Show();
                        Check(!window.IsAnimating && surface.AnimationBounds == null && !surface.PointerGestureActive, "hide cancels recovery without retaining a clipped frame or capture");
                    }
                    else
                    {
                        Rect frozen = window.VisualPixelBounds;
                        Point nextGrip = Center(surface.Regions().Single(x => x.name == "main").bounds);
                        Point nextStart = surface.PointToScreen(nextGrip);
                        Check(surface.PointerDown(nextGrip, nextStart) && !window.IsAnimating && window.VisualPixelBounds == frozen,
                            "a new press freezes recovery at the currently visible complete panel");
                        Point nextEnd = nextStart + new Vector(-18, 12);
                        surface.PointerMove(nextGrip, nextEnd); await surface.PointerUpAsync(nextGrip, nextEnd);
                        await Task.Delay(400);
                        Check(!surface.PointerGestureActive && !surface.IsMouseCaptured && Fits(window.VisualPixelBounds, rightMonitor.Work),
                            "second drag owns recovery and finishes at a reachable position");
                        Check(commands.All(x => x.name == "position"), "interrupted recovery never clicks the underlying main button");
                    }
                }
            }

            foreach (var pair in new[] { (monitor: leftMonitor, edge: CapsuleEdge.Left), (monitor: rightMonitor, edge: CapsuleEdge.Right) })
            {
                await Reset(pair.monitor);
                var dpi = VisualTreeHelper.GetDpi(surface); double width = 76 * dpi.DpiScaleX;
                double x = pair.edge == CapsuleEdge.Left ? pair.monitor.Work.Left : pair.monitor.Work.Right - width;
                window.Restore(J.Obj(("pixelLeft", x), ("pixelTop", pair.monitor.Work.Top + pair.monitor.Work.Height * .3),
                    ("dockEdge", pair.edge.ToString()), ("panelOffsetX", pair.edge == CapsuleEdge.Left ? -260 : 260), ("panelOffsetY", -700)));
                followPanel = true; window.Expand(true, false); window.UpdateLayout();
                Rect actual = window.VisualPixelBounds, compact = window.CompactPixelBounds;
                Rect expected = CapsulePlacement.Expanded(compact, pair.monitor.Work, VisualTreeHelper.GetDpi(surface));
                Check(Near(actual.X, expected.X) && Near(actual.Y, expected.Y) && Near(actual.Width, expected.Width) && Near(actual.Height, expected.Height),
                    pair.edge + " ignores legacy persistent panel offsets when restoring expansion");
            }
            return J.Obj(("success", true), ("checks", checks), ("skips", skips),
                ("scope", "Synthetic native WPF windows, production pointer entry points, injected hover position, no real cursor movement or user state."));
        }
        finally
        {
            if (window != null) { window.Surface.ReleaseMouseCapture(); await window.Surface.CaptureLostAsync(); window.Close(); }
            Application.Current.ShutdownMode = previousShutdown;
        }
    }
}
