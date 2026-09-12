using System;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CodexUsage;

/// <summary>Replay a late move from the expanded panel into its original circle bridge, after initial hover has settled.</summary>
internal static class CapsuleLateDepartureTests
{
    internal static async Task<JsonObject> RunAsync()
    {
        var app = Application.Current; var shutdown = app.ShutdownMode;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var checks = new JsonArray(); var failures = new JsonArray(); var observations = new JsonArray();
        Point? pointer = null; bool owns = true;
        void Check(bool okay, string id) { checks.Add(J.Obj(("id", id), ("success", okay))); if (!okay) failures.Add(id); }
        bool Expanded(CapsuleWindow window) => window.Surface.Expansion == 1 && !window.IsAnimating;
        bool Compact(CapsuleWindow window) => window.Surface.Expansion == 0 && !window.IsAnimating;
        async Task<bool> Until(Func<bool> ready, int milliseconds = 1000)
        {
            long started = Environment.TickCount64;
            while (!ready() && Environment.TickCount64 - started < milliseconds) await Task.Delay(10);
            return ready();
        }
        async Task<(CapsuleWindow window, Point inside, Point gap, Point outside)> Create(string id)
        {
            pointer = null; owns = true;
            var state = DemoData.State(); var floating = state.O("settings").O("floating");
            floating["pinned"] = false; floating["keepExpanded"] = false; floating["edgeAutoHide"] = false;
            var window = new CapsuleWindow((_, _) => Task.CompletedTask, readPointer: () => pointer, ownsPointer: _ => owns)
            { ShowActivated = false, Topmost = false, IsHitTestVisible = false };
            window.Update(state); window.Show(); await Task.Delay(40);
            var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(window).Handle);
            var work = screen.WorkingArea; var dpi = VisualTreeHelper.GetDpi(window);
            window.Restore(J.Obj(("pixelLeft", work.Right - 76 * dpi.DpiScaleX), ("pixelTop", work.Bottom - 76 * dpi.DpiScaleY), ("dockEdge", "None")));
            var circle = window.CompactPixelBounds;
            var inside = new Point(circle.X + circle.Width / 2, circle.Y + circle.Height / 2);
            var gap = new Point(circle.Right - 2 * dpi.DpiScaleX, inside.Y);
            var outside = new Point(circle.Right + 80 * dpi.DpiScaleX, circle.Bottom + 80 * dpi.DpiScaleY);
            pointer = inside; window.PointerEntered();
            Check(await Until(() => Expanded(window)), id + "-fixture-expands-from-circle-panel-overlap");
            Check(window.IsHotspot(inside) && window.VisualPixelBounds.Contains(inside) && window.IsHotspot(gap) && !window.VisualPixelBounds.Contains(gap), id + "-fixture-has-late-accessible-circle-gap");
            // Initial expansion finishes with the pointer inside the actual panel;
            // its initial gap watcher is not running. Do not move to panel center:
            // that would legitimately clear the original-circle hotspot.
            window.TrackPointer(inside); window.PointerMoved(inside); await Task.Delay(180);
            return (window, inside, gap, outside);
        }
        try
        {
            foreach (bool confirmationReentry in new[] { false, true })
            {
                string id = confirmationReentry ? "confirmation-retains-late-gap" : "leave-retains-late-gap";
                CapsuleWindow? window = null;
                try
                {
                    var fixture = await Create(id); window = fixture.window;
                    if (confirmationReentry)
                    {
                        // One MouseLeave due to occlusion while still in the shared
                        // circle/panel region starts the 90 ms departure confirmation.
                        pointer = fixture.inside; owns = false;
                        window.TrackPointer(pointer.Value); window.PointerLeft();
                        // Before confirmation, the pointer reaches the circle-only
                        // gap. No second Surface event is available there.
                        pointer = fixture.gap;
                    }
                    else
                    {
                        pointer = fixture.gap; owns = false;
                        window.TrackPointer(pointer.Value); window.PointerLeft();
                    }
                    await Task.Delay(230);
                    Check(Expanded(window), id + "-stationary-bridge-is-retained");
                    int transitions = window.ExpansionTransitions;
                    int events = window.HoverEventCount;
                    pointer = fixture.outside;
                    var watch = Stopwatch.StartNew();
                    bool collapsed = await Until(() => Compact(window));
                    observations.Add(J.Obj(("case", id), ("elapsedMilliseconds", watch.Elapsed.TotalMilliseconds), ("collapsed", collapsed),
                        ("hoverEventsAfterDeparture", window.HoverEventCount - events), ("extraTransitions", window.ExpansionTransitions - transitions)));
                    Check(collapsed && window.ExpansionTransitions == transitions + 1, id + "-leaving-bridge-without-further-surface-events-collapses-once");
                    Check(window.HoverEventCount == events, id + "-regression-does-not-inject-a-second-leave-or-move");
                }
                catch (Exception error) { failures.Add(id + ": " + error); }
                finally { window?.Close(); }
            }

            foreach (string retention in new[] { "keep-expanded", "keyboard", "menu" })
            {
                CapsuleWindow? window = null;
                try
                {
                    var fixture = await Create(retention); window = fixture.window;
                    pointer = fixture.gap; owns = false;
                    window.TrackPointer(pointer.Value); window.PointerLeft(); await Task.Delay(130);
                    Check(Expanded(window), retention + "-starts-with-live-gap-retention");
                    if (retention == "keep-expanded") window.SetKeepsExpanded(true);
                    else if (retention == "keyboard")
                    {
                        window.Surface.FocusAction("details", true);
                        Check(await Until(() => window.Surface.KeyboardInteraction), "keyboard-retention-requires-real-wpf-focus");
                    }
                    else
                    {
                        window.Surface.FocusAction("more", false);
                        await window.ShowMenu("more");
                        Check(await Until(() => window.ActiveMenu?.IsOpen == true && window.InteractionActive), "menu-retention-requires-live-popup");
                    }
                    pointer = fixture.outside; window.PointerLeft();
                    await Task.Delay(550);
                    Check(Expanded(window), retention + "-late-gap-watcher-does-not-override-explicit-retention");
                    if (retention == "menu" && window.ActiveMenu != null) window.ActiveMenu.IsOpen = false;
                }
                catch (Exception error) { failures.Add(retention + ": " + error); }
                finally { window?.Close(); }
            }
            return J.Obj(("success", failures.Count == 0), ("checks", checks), ("failures", failures), ("observations", observations),
                ("scope", "Synthetic native WPF windows with injected physical pointer/ownership readings. The pointer leaves a late circle bridge without further Surface events; real WPF keyboard focus and a real menu check intentional retention. No real user data or cursor movement."));
        }
        finally { app.ShutdownMode = shutdown; }
    }
}
