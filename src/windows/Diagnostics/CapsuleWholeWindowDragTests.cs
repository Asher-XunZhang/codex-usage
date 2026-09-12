using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexUsage;

/// <summary>Native capture with injected coordinates through the production surface gesture entry points.</summary>
internal static class CapsuleWholeWindowDragTests
{
    internal static async Task<JsonObject> RunAsync(string? frames = null)
    {
        var checks = new JsonArray(); var actions = new List<(string name, string? value)>();
        TaskCompletionSource? pendingPosition = null;
        CapsuleWindow? window = null;
        var oldShutdown = Application.Current.ShutdownMode;
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        void Check(bool okay, string name)
        {
            if (!okay) throw new InvalidOperationException("Whole-window capsule drag: " + name);
            checks.Add(name);
        }
        Point Center(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);
        try
        {
            window = new CapsuleWindow((name, value) =>
            {
                actions.Add((name, value));
                return name == "position" && pendingPosition != null ? pendingPosition.Task : Task.CompletedTask;
            }, () => Task.FromResult(new JsonObject()), () => window == null ? null : Center(window.VisualPixelBounds), _ => true)
            { ShowActivated = false, Topmost = false };
            // Native capture asks WPF to replay the real cursor's MouseMove. The
            // fixture uses injected coordinates, so suppress that unrelated input
            // at the window while exercising the same production pointer methods.
            window.PreviewMouseMove += (_, e) => e.Handled = true;
            var work = SystemParameters.WorkArea;
            window.Left = work.Left + work.Width * .55; window.Top = work.Top + work.Height * .3;
            window.Show(); window.UpdateLayout();
            var surface = window.Surface;
            var anchor = window.CompactPixelBounds.TopLeft;
            var scale = VisualTreeHelper.GetDpi(surface);
            JsonObject Fixture(string page)
            {
                var state = TaskMonitorDemo.State(); var floating = state.O("settings").O("floating");
                floating["content"] = page; floating["pinned"] = false; floating["keepExpanded"] = false; floating["edgeAutoHide"] = false;
                var watches = state.O("monitor").A("watches").Rows().ToArray();
                state.O("monitor")["watches"] = new JsonArray(watches.First(x => x.S("status") == "running").Copy(), watches.First(x => x.S("status") == "completed").Copy());
                state["busy"] = true;
                return state;
            }
            void Reset(JsonObject state)
            {
                window.Update(state);
                window.Restore(J.Obj(("pixelLeft", anchor.X), ("pixelTop", anchor.Y), ("edgeAutoHide", false)));
                window.Expand(true, false); window.PointerEntered(); window.Expand(true, false); window.UpdateLayout();
                actions.Clear();
            }
            Point Region(string name) => Center(surface.Regions().Single(x => x.name == name).bounds);
            void Stable(string label)
            {
                Check(Math.Abs(surface.Expansion - 1) < .0001 && !window.IsAnimating, label + " keeps the complete expanded shape");
                Check(!window.KeepsExpanded && !surface.State.O("settings").O("floating").B("keepExpanded"), label + " does not change keep-expanded preference");
            }
            async Task Drag(Point local, string label, bool skipMove = false)
            {
                Rect before = window.VisualPixelBounds; Point screen = surface.PointToScreen(local);
                Check(surface.PointerDown(local, screen) && surface.IsMouseCaptured, label + " accepts native capture");
                Vector delta = new(18 * scale.DpiScaleX, 10 * scale.DpiScaleY);
                if (!skipMove)
                {
                    foreach (double fraction in new[] { .25, .55, 1d })
                    {
                        surface.PointerMove(local, screen + delta * fraction); Stable(label + " moving");
                    }
                }
                await surface.PointerUpAsync(skipMove ? local + new Vector(18, 10) : local, screen + delta);
                Rect after = window.VisualPixelBounds;
                Check(Math.Abs(after.X - before.X - delta.X) <= 2 && Math.Abs(after.Y - before.Y - delta.Y) <= 2 &&
                    Math.Abs(after.Width - before.Width) <= 1 && Math.Abs(after.Height - before.Height) <= 1, label + " translates the whole panel by screen delta");
                Stable(label + " release");
                Check(actions.Count == 1 && actions[0].name == "position" && window.ActiveMenu == null, label + " saves once without clicking the underlying action");
                Check(!surface.PointerGestureActive && !surface.IsMouseCaptured && !window.InteractionActive, label + " releases native capture and gesture ownership");
            }

            foreach (string page in new[] { "usage", "budget", "monitor" })
            {
                var state = Fixture(page);
                foreach (string name in new[] { "details", "main", "more", "collapse" })
                {
                    Reset(state); await Drag(Region(name), page + " / " + name);
                }
                Reset(state);
                await Drag(new Point(surface.DrawingBounds.Left + 8, surface.DrawingBounds.Top + 132), page + " / blank area");
                Reset(state);
                string disabled = page == "usage" ? "refresh" : page == "budget" ? "budgetScope" :
                    surface.Regions().First(x => x.name.StartsWith("monitorStop:", StringComparison.Ordinal) && !surface.ActionEnabled(x.name)).name;
                Check(!surface.ActionEnabled(disabled), page + " disabled fixture is non-actionable");
                await Drag(Region(disabled), page + " / disabled area");
                if (page == "monitor")
                {
                    Reset(state);
                    string cancel = surface.Regions().First(x => x.name.StartsWith("monitorStop:", StringComparison.Ordinal) && surface.ActionEnabled(x.name)).name;
                    await Drag(Region(cancel), "monitor / cancel monitoring");
                    Reset(state); await Drag(Region("monitorClearEnded"), "monitor / clear ended");
                }
            }

            var monitor = Fixture("monitor"); Reset(monitor);
            Point main = Region("main"), start = surface.PointToScreen(main);
            Check(surface.PointerDown(main, start), "small move captures main action");
            surface.PointerMove(main + new Vector(1, 1), start + new Vector(scale.DpiScaleX, scale.DpiScaleY));
            await surface.PointerUpAsync(main + new Vector(1, 1), start + new Vector(scale.DpiScaleX, scale.DpiScaleY));
            Check(actions.Count == 1 && actions[0].name == "main", "sub-threshold movement clicks the intended button exactly once; actions=" + string.Join(',', actions.Select(x => x.name)) + "; hit=" + surface.Hit(main + new Vector(1, 1)) + "; expanded=" + surface.Expansion + "; active=" + window.InteractionActive);

            Reset(monitor); main = Region("main"); start = surface.PointToScreen(main);
            Check(surface.PointerDown(main, start), "return-to-origin gesture starts");
            surface.PointerMove(main, start + new Vector(12 * scale.DpiScaleX, 7 * scale.DpiScaleY));
            surface.PointerMove(main, start);
            await surface.PointerUpAsync(main, start);
            Check(actions.Count == 1 && actions[0].name == "position", "crossing threshold then returning cannot become a click");
            Stable("return-to-origin");

            Reset(monitor); await Drag(Region("main"), "up without a preceding move", skipMove: true);

            Reset(monitor); main = Region("main"); start = surface.PointToScreen(main);
            Check(surface.PointerDown(main, start), "capture-loss gesture starts");
            surface.PointerMove(main, start + new Vector(12 * scale.DpiScaleX, 7 * scale.DpiScaleY));
            surface.ReleaseMouseCapture();
            await surface.CaptureLostAsync(); await surface.CaptureLostAsync(); await surface.PointerUpAsync(main, start);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(actions.Count == 1 && actions[0].name == "position" && !surface.PointerGestureActive, "capture loss finishes and saves only once without a click");

            Reset(monitor);
            string selectedStop = surface.Regions().First(x => x.name.StartsWith("monitorStop:", StringComparison.Ordinal) && surface.ActionEnabled(x.name)).name;
            Point oldStop = Region(selectedStop); start = surface.PointToScreen(oldStop);
            Check(surface.PointerDown(oldStop, start), "task replacement gesture starts on its original ID");
            var replaced = monitor.Copy();
            foreach (var watch in replaced.O("monitor").A("watches").Rows()) { watch["id"] = "replacement-" + watch.S("id"); watch["title"] = new string('W', 120); }
            window.Update(replaced);
            await surface.PointerUpAsync(oldStop, start);
            Check(actions.Count == 0, "snapshot replacement cannot cancel a new task occupying the old button bounds");

            Reset(monitor);
            var outside = new Point(surface.DrawingBounds.Left - 5, surface.DrawingBounds.Top + 100);
            Check(!surface.PointerDown(outside, surface.PointToScreen(outside)) && !surface.IsMouseCaptured && !surface.PointerGestureActive,
                "transparent envelope outside the shape cannot capture a gesture");

            Reset(monitor); main = Region("main"); start = surface.PointToScreen(main);
            pendingPosition = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Check(surface.PointerDown(main, start), "pending-save first gesture starts");
            surface.PointerMove(main, start + new Vector(12 * scale.DpiScaleX, 7 * scale.DpiScaleY));
            Task oldUp = surface.PointerUpAsync(main, start + new Vector(12 * scale.DpiScaleX, 7 * scale.DpiScaleY));
            Check(!oldUp.IsCompleted && !surface.PointerGestureActive, "pending position save releases old gesture synchronously");
            Point next = Region("main"), nextScreen = surface.PointToScreen(next);
            Check(surface.PointerDown(next, nextScreen), "new gesture can start while old position save is pending");
            var saving = pendingPosition; pendingPosition = null; saving.SetResult(); await oldUp;
            Check(surface.PointerGestureActive && surface.IsMouseCaptured && window.InteractionActive, "old save completion cannot release or overwrite the next gesture");
            surface.PointerMove(next, nextScreen + new Vector(13 * scale.DpiScaleX, 8 * scale.DpiScaleY));
            await surface.PointerUpAsync(next, nextScreen + new Vector(13 * scale.DpiScaleX, 8 * scale.DpiScaleY));
            Check(actions.Count == 2 && actions.All(x => x.name == "position"), "two independent drags each save once without a late click");
            Stable("second gesture after delayed save");

            // Persist both shapes, including the user's chosen expansion side.
            Rect savedPanel = window.VisualPixelBounds;
            var savedPosition = J.Parse(actions.Last().value!);
            Check(savedPosition.N("panelOffsetX") == null && savedPosition.N("panelOffsetY") == null, "position does not persist expansion direction");
            window.Expand(false, false); window.Expand(true, false);
            Check(window.VisualPixelBounds == savedPanel, "collapse and reexpand retain the dragged panel position");
            window.Restore(savedPosition); window.Expand(true, false);
            Check(window.VisualPixelBounds == savedPanel, "restoring saved position retains panel placement and expansion direction");

            foreach (string cancellation in new[] { "collapse", "hide", "display", "restore" })
            {
                Reset(monitor); main = Region("main"); start = surface.PointToScreen(main);
                Check(surface.PointerDown(main, start), cancellation + " captures a drag");
                surface.PointerMove(main, start + new Vector(-100000, -100000));
                if (cancellation == "collapse") { window.Collapse(); window.Expand(false, false); }
                else if (cancellation == "hide") { window.Hide(); window.Show(); }
                else if (cancellation == "display") { window.DisplayChanged(null, EventArgs.Empty); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); }
                else window.Restore(savedPosition);
                await surface.PointerUpAsync(main, start);
                Check(!surface.PointerGestureActive && !surface.IsMouseCaptured && !window.InteractionActive, cancellation + " cancels capture without a stuck gesture");
                Check(actions.All(x => x.name == "position"), cancellation + " never invokes the underlying main button");
                Check(window.CompactPixelBounds.Left > -90000 && window.CompactPixelBounds.Top > -90000, cancellation + " brings the latent compact anchor back on screen");
                if (cancellation == "restore")
                {
                    window.Expand(true, false);
                    Check(window.VisualPixelBounds == savedPanel, "restoration wins over cancellation of the old offscreen drag");
                }
            }

            Reset(monitor); main = Region("main"); start = surface.PointToScreen(main);
            Check(surface.PointerDown(main, start, 2), "second double-click press can still start a drag");
            surface.PointerMove(main, start + new Vector(12 * scale.DpiScaleX, 7 * scale.DpiScaleY));
            await surface.PointerUpAsync(main, start + new Vector(12 * scale.DpiScaleX, 7 * scale.DpiScaleY));
            Check(actions.Count == 1 && actions[0].name == "position", "double-click drag never duplicates a button action");

            Reset(monitor); main = Region("main"); start = surface.PointToScreen(main);
            Check(surface.PointerDown(main, start), "mixed keyboard and pointer gesture starts");
            surface.PointerMove(main, start + new Vector(12 * scale.DpiScaleX, 7 * scale.DpiScaleY));
            await surface.HandleKeyboardAsync(Key.Tab, ModifierKeys.None);
            await surface.HandleKeyboardAsync(Key.Enter, ModifierKeys.None);
            await surface.HandleKeyboardAsync(Key.Space, ModifierKeys.Control);
            surface.FocusAction("main", true); await surface.ActivateActionAsync("main", true);
            Check(!surface.KeyboardInteraction && actions.Count == 0 && surface.PointerGestureActive, "keyboard and accessibility actions cannot take ownership of an active pointer drag");
            await surface.PointerUpAsync(main, start + new Vector(12 * scale.DpiScaleX, 7 * scale.DpiScaleY));
            Check(actions.Count == 1 && actions[0].name == "position" && !window.KeepsExpanded, "mixed input release only saves position without changing expansion preference");

            window.Expand(false, false); actions.Clear();
            Point compact = Center(surface.DrawingBounds); start = surface.PointToScreen(compact);
            Check(surface.PointerDown(compact, start), "compact drag remains available after an expanded drag");
            surface.PointerMove(compact, start + new Vector(12 * scale.DpiScaleX, 7 * scale.DpiScaleY));
            await surface.PointerUpAsync(compact, start + new Vector(12 * scale.DpiScaleX, 7 * scale.DpiScaleY));
            var compactPosition = J.Parse(actions.Last(x => x.name == "position").value!);
            Check(compactPosition.N("panelOffsetX") == null && compactPosition.N("panelOffsetY") == null, "compact dragging restores automatic expansion direction");
            return J.Obj(("success", true), ("checks", checks), ("scope", "Visible synthetic native WPF capture; production pointer methods receive injected coordinates; no cursor movement, user settings, live monitoring or notifications."));
        }
        finally
        {
            pendingPosition?.TrySetResult();
            if (window != null) { window.Surface.ReleaseMouseCapture(); await window.Surface.CaptureLostAsync(); window.Close(); }
            Application.Current.ShutdownMode = oldShutdown;
        }
    }
}
