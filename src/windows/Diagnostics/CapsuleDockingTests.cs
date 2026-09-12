using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace CodexUsage;

/// <summary>
/// Real-window docking regressions with synthetic state and injected physical
/// pointer coordinates. No desktop pointer movement, account access or host IPC.
/// Pure multi-monitor placement and DPI arithmetic have a separate fixture.
/// </summary>
internal static class CapsuleDockingTests
{
    public static async Task<JsonObject> RunAsync()
    {
        var app = Application.Current ?? throw new InvalidOperationException("Docking tests require a WPF application.");
        if (!app.Dispatcher.CheckAccess()) throw new InvalidOperationException("Docking tests require the WPF dispatcher.");
        var shutdown = app.ShutdownMode;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var windows = new List<CapsuleWindow>();
        var closed = new HashSet<CapsuleWindow>();
        var checks = new JsonArray();
        var failures = new JsonArray();
        var measurements = new JsonObject();
        var tooltipProblems = new HashSet<string>();
        Point? pointer = null;
        bool owns = true;
        TaskCompletionSource<JsonObject>? choices = null;
        const string exactTotal = "9,007,199,254,740,993";
        int audits = 0;

        void Check(bool success, string id, string detail)
        {
            checks.Add(J.Obj(("id", id), ("success", success), ("detail", detail)));
            if (!success) failures.Add(id + ": " + detail);
        }
        JsonObject State(bool autoHide = true)
        {
            var state = DemoData.State();
            state.O("settings")["refresh"] = 0;
            var floating = state.O("settings").O("floating");
            floating["pinned"] = false;
            floating["content"] = "usage";
            floating["edgeAutoHide"] = autoHide;
            state.O("filtered").O("summary")["total_tokens"] = 9007199254740993L;
            state["status"] = "贴边生命周期验证 · 合成数据";
            return state;
        }
        void Audit(CapsuleWindow window)
        {
            audits++;
            foreach (var node in Visuals(window).Concat(new DependencyObject[] { window.Surface }).Distinct())
                if (ToolTipService.GetToolTip(node) is not null) tooltipProblems.Add(node.GetType().Name + " owns a tooltip");
            if (ToolTipService.GetIsEnabled(window) || ToolTipService.GetIsEnabled(window.Surface))
                tooltipProblems.Add("Window or surface enables ToolTipService");
            if (!AutomationProperties.GetHelpText(window.Surface).Contains(exactTotal, StringComparison.Ordinal))
                tooltipProblems.Add("Accessibility HelpText lost the exact synthetic token count");
        }
        async Task<bool> Wait(CapsuleWindow window, Func<bool> condition, int timeout = 2200)
        {
            var watch = Stopwatch.StartNew();
            while (!condition() && watch.ElapsedMilliseconds < timeout)
            {
                Audit(window);
                await Task.Delay(10);
            }
            Audit(window);
            return condition();
        }
        static bool Compact(CapsuleWindow window) => !window.IsAnimating && !window.HiddenAtEdge && !window.DockMotionActive && window.Surface.Expansion <= .001;
        static bool Expanded(CapsuleWindow window) => !window.IsAnimating && window.Surface.Expansion >= .999;
        static Point Center(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);
        static bool Near(Rect a, Rect b, double tolerance = 1.1) => Math.Abs(a.X - b.X) <= tolerance && Math.Abs(a.Y - b.Y) <= tolerance
            && Math.Abs(a.Width - b.Width) <= tolerance && Math.Abs(a.Height - b.Height) <= tolerance;
        static System.Windows.Forms.Screen ScreenFor(CapsuleEdge edge) => edge switch
        {
            CapsuleEdge.Left => System.Windows.Forms.Screen.AllScreens.OrderBy(s => s.Bounds.Left).First(),
            CapsuleEdge.Right => System.Windows.Forms.Screen.AllScreens.OrderByDescending(s => s.Bounds.Right).First(),
            CapsuleEdge.Top => System.Windows.Forms.Screen.AllScreens.OrderBy(s => s.Bounds.Top).First(),
            _ => System.Windows.Forms.Screen.AllScreens.OrderByDescending(s => s.Bounds.Bottom).First()
        };
        static Rect Work(System.Windows.Forms.Screen screen) => new(screen.WorkingArea.X, screen.WorkingArea.Y, screen.WorkingArea.Width, screen.WorkingArea.Height);
        async Task<CapsuleWindow> Create()
        {
            pointer = null; owns = true;
            var window = new CapsuleWindow((_, _) => Task.CompletedTask,
                loadChoices: () => choices?.Task ?? Task.FromResult(State()),
                readPointer: () => pointer, ownsPointer: _ => owns)
            {
                ShowActivated = false,
                Topmost = false,
                IsHitTestVisible = false,
                Left = SystemParameters.WorkArea.Left + 100,
                Top = SystemParameters.WorkArea.Top + 100
            };
            windows.Add(window);
            window.Closed += (_, _) => closed.Add(window);
            window.Update(State());
            window.Show();
            await Task.Delay(60);
            window.UpdateLayout();
            return window;
        }
        async Task<Rect> Dock(CapsuleWindow window, CapsuleEdge edge)
        {
            // Move to the target monitor first so per-monitor DPI is established
            // before calculating the 12-DIP release distance and compact size.
            var work = Work(ScreenFor(edge));
            window.Press(true);
            var origin = window.BeginDrag(Center(window.CompactPixelBounds));
            var middle = new Point(work.X + work.Width / 2, work.Y + work.Height / 2);
            window.MoveBy(middle.X - origin.X, middle.Y - origin.Y, origin, null);
            await Task.Delay(40);
            var dpi = VisualTreeHelper.GetDpi(window);
            var size = new Size(76 * dpi.DpiScaleX, 76 * dpi.DpiScaleY);
            double x = work.X + (work.Width - size.Width) / 2, y = work.Y + (work.Height - size.Height) / 2;
            switch (edge)
            {
                case CapsuleEdge.Left: x = work.Left + 12 * dpi.DpiScaleX; break;
                case CapsuleEdge.Right: x = work.Right - size.Width - 12 * dpi.DpiScaleX; break;
                case CapsuleEdge.Top: y = work.Top + 12 * dpi.DpiScaleY; break;
                case CapsuleEdge.Bottom: y = work.Bottom - size.Height - 12 * dpi.DpiScaleY; break;
            }
            window.MoveBy(x - origin.X, y - origin.Y, origin, null);
            pointer = new Point(x + size.Width / 2, y + size.Height / 2);
            await window.FinishDrag();
            // Keep the injected pointer over the snapped compact ring until each
            // test explicitly starts its departure clock.
            pointer = Center(window.CompactPixelBounds);
            window.CheckDockPointer();
            Audit(window);
            return work;
        }
        async Task HideToTab(CapsuleWindow window, string prefix)
        {
            pointer = null;
            var clock = Stopwatch.StartNew();
            window.CheckDockPointer();
            bool observedMotion = false;
            double? firstMotionMs = null;
            bool hidden = await Wait(window, () =>
            {
                if (window.DockMotionActive && !observedMotion) { observedMotion = true; firstMotionMs = clock.Elapsed.TotalMilliseconds; }
                return window.HiddenAtEdge && !window.IsAnimating;
            });
            measurements[prefix + "HideMs"] = clock.Elapsed.TotalMilliseconds;
            measurements[prefix + "HideMotionObserved"] = observedMotion;
            if (firstMotionMs is double start) measurements[prefix + "HideMotionStartMs"] = start;
            Check(hidden && clock.Elapsed.TotalMilliseconds >= 520 && (firstMotionMs is null || firstMotionMs >= 520),
                prefix + "-delayed-hide", "Departure waits for the 550 ms grace period, then reaches a stable edge indicator within the bounded wait.");
        }
        async Task OpenFromRing(CapsuleWindow window, string prefix)
        {
            var center = Center(window.CompactPixelBounds);
            var initial = window.VisualPixelBounds;
            var host = window.PixelBounds;
            pointer = new Point(center.X + 2, center.Y);
            owns = true;
            window.PointerMoved(pointer.Value);
            Check(!SystemParameters.ClientAreaAnimation || window.Surface.Expansion == 0 && window.VisualPixelBounds == initial && window.Surface.RingVisible,
                prefix + "-morph-start-preserves-circle", "Physical entry starts at the existing circle and zero progress without an instantaneous shape swap.");
            Rect? fixedContent = null;
            bool stableContent = true, monotonic = true, stableHost = true;
            double previousProgress = 0, previousWidth = initial.Width, previousHeight = initial.Height;
            int observedFrames = 0;
            bool expanded = await Wait(window, () =>
            {
                var visible = window.VisualPixelBounds;
                monotonic &= window.Surface.Expansion >= previousProgress && visible.Width + .001 >= previousWidth && visible.Height + .001 >= previousHeight;
                previousProgress = window.Surface.Expansion; previousWidth = visible.Width; previousHeight = visible.Height;
                stableHost &= window.PixelBounds == host;
                if (window.Surface.PanelBounds is Rect content)
                {
                    var native = window.PixelBounds;
                    var dpi = VisualTreeHelper.GetDpi(window);
                    var screen = new Rect(native.X + content.X * dpi.DpiScaleX, native.Y + content.Y * dpi.DpiScaleY,
                        content.Width * dpi.DpiScaleX, content.Height * dpi.DpiScaleY);
                    fixedContent ??= screen;
                    stableContent &= Near(screen, fixedContent.Value);
                    observedFrames++;
                }
                return Expanded(window);
            });
            Check(expanded && monotonic && stableHost && stableContent && fixedContent is Rect endpoint && Near(window.VisualPixelBounds, endpoint)
                && !window.Surface.RingVisible,
                prefix + "-continuous-morph-endpoints", "Visible geometry progresses monotonically to the retained panel endpoint while native bounds stay fixed.");
            measurements[prefix + "PanelSamples"] = observedFrames;
        }

        try
        {
            foreach (var edge in new[] { CapsuleEdge.Left, CapsuleEdge.Right, CapsuleEdge.Top, CapsuleEdge.Bottom })
            {
                string name = edge.ToString().ToLowerInvariant();
                var window = await Create();
                var work = await Dock(window, edge);
                var compact = window.CompactPixelBounds;
                var dpi = VisualTreeHelper.GetDpi(window);
                Check(PresentationSource.FromVisual(window.Surface) is HwndSource && window.DockEdge == edge && Compact(window)
                    && window.AwaitingRingEntry && !window.InteractionActive,
                    name + "-dock-release", "Release 12 DIPs from the outer work edge snaps a real HWND without opening the panel.");
                await HideToTab(window, name);
                var tab = window.VisualPixelBounds;
                bool vertical = edge is CapsuleEdge.Left or CapsuleEdge.Right;
                double boundaryError = edge switch
                {
                    CapsuleEdge.Left => Math.Abs(tab.Left - work.Left),
                    CapsuleEdge.Right => Math.Abs(tab.Right - work.Right),
                    CapsuleEdge.Top => Math.Abs(tab.Top - work.Top),
                    _ => Math.Abs(tab.Bottom - work.Bottom)
                };
                Check(window.HiddenAtEdge && boundaryError <= 1.1 && Math.Abs(tab.Width - (vertical ? 28 : 76) * dpi.DpiScaleX) <= 1.1
                    && Math.Abs(tab.Height - (vertical ? 72 : 28) * dpi.DpiScaleY) <= 1.1 && Near(window.CompactPixelBounds, compact),
                    name + "-indicator-geometry", "The 28×72 or 76×28 DIP indicator touches its work edge and preserves compact origin.");

                pointer = Center(tab); window.CheckDockPointer();
                pointer = null; window.CheckDockPointer();
                await Task.Delay(180); Audit(window);
                Check(window.HiddenAtEdge && !window.IsAnimating, name + "-leaving-cancels-dwell", "Leaving the local hotzone cancels a pending reveal deadline.");

                // A point far along the same edge must not wake a local tab.
                pointer = vertical ? new Point(tab.X + tab.Width / 2, tab.Bottom + 40 * dpi.DpiScaleY)
                    : new Point(tab.Right + 40 * dpi.DpiScaleX, tab.Y + tab.Height / 2);
                window.CheckDockPointer(); await Task.Delay(260); Audit(window);
                Check(window.HiddenAtEdge && !window.IsAnimating, name + "-local-hotzone-only", "The rest of the monitor edge is not an invisible activation strip.");
                pointer = Center(tab); owns = false;
                window.CheckDockPointer(); await Task.Delay(240); Audit(window);
                Check(window.HiddenAtEdge && !window.IsAnimating, name + "-occluded-tab-stays-hidden", "A covering window prevents proximity wake.");

                // This lies outside the logical tab but inside its local +8-DIP area.
                pointer = edge switch
                {
                    CapsuleEdge.Left => new Point(tab.Right + 7 * dpi.DpiScaleX, tab.Y + tab.Height / 2),
                    CapsuleEdge.Right => new Point(tab.Left - 7 * dpi.DpiScaleX, tab.Y + tab.Height / 2),
                    CapsuleEdge.Top => new Point(tab.X + tab.Width / 2, tab.Bottom + 7 * dpi.DpiScaleY),
                    _ => new Point(tab.X + tab.Width / 2, tab.Top - 7 * dpi.DpiScaleY)
                };
                owns = true;
                var wakeClock = Stopwatch.StartNew();
                window.CheckDockPointer();
                double? wakeStart = null;
                bool revealed = await Wait(window, () =>
                {
                    if (window.DockMotionActive && wakeStart is null) wakeStart = wakeClock.Elapsed.TotalMilliseconds;
                    return Compact(window);
                });
                measurements[name + "RevealMs"] = wakeClock.Elapsed.TotalMilliseconds;
                if (wakeStart is double start) measurements[name + "RevealMotionStartMs"] = start;
                Check(revealed && wakeClock.Elapsed.TotalMilliseconds >= 105 && (wakeStart is null || wakeStart >= 105),
                    name + "-proximity-dwell-reveals", "A local +7-DIP point waits for the 120 ms dwell and reveals the compact ring.");
                int directions = window.ExpansionTransitions;
                for (int i = 0; i < 12; i++) { window.PointerEntered(); await Task.Delay(10); }
                Audit(window);
                Check(Compact(window) && window.AwaitingRingEntry && window.ExpansionTransitions == directions,
                    name + "-stationary-reveal-does-not-expand", "Synthetic Enter events at the reveal point cannot open the panel.");
                await OpenFromRing(window, name);
                pointer = null; window.PointerLeft();
                bool collapsed = await Wait(window, () => Compact(window));
                Check(collapsed && Near(window.VisualPixelBounds, compact), name + "-close-restores-anchor", "Leaving returns to the original compact bounds before auto-hide.");
                window.Close();
                Check(!window.DockTimersActive && !window.IsAnimating, name + "-close-disposes", "Closing removes all docking timers and frame callbacks.");
            }

            var interaction = await Create();
            var interactionWork = await Dock(interaction, CapsuleEdge.Left);
            pointer = null; interaction.CheckDockPointer(); interaction.Press(true);
            await Task.Delay(820); Audit(interaction);
            Check(Compact(interaction) && interaction.InteractionActive, "pressed-blocks-auto-hide", "A held gesture cancels the pending hide through the entire grace interval.");
            var dragOrigin = interaction.BeginDrag(Center(interaction.CompactPixelBounds));
            var dragTarget = new Point(interactionWork.X + interactionWork.Width / 2, interactionWork.Y + interactionWork.Height / 2);
            interaction.MoveBy(dragTarget.X - dragOrigin.X, dragTarget.Y - dragOrigin.Y, dragOrigin, null);
            pointer = Center(interaction.CompactPixelBounds);
            await interaction.FinishDrag();
            Check(interaction.DockEdge == CapsuleEdge.None && Compact(interaction) && !interaction.InteractionActive && !interaction.DockTimersActive,
                "drag-away-clears-docking", "Dragging a compact ring into the work area clears docking, timers and press state.");

            await Dock(interaction, CapsuleEdge.Left);
            pointer = null;
            await interaction.Invoke("keepExpanded");
            await Wait(interaction, () => Expanded(interaction));
            interaction.CheckDockPointer(); await Task.Delay(820); Audit(interaction);
            Check(interaction.KeepsExpanded && Expanded(interaction) && !interaction.HiddenAtEdge,
                "keep-expanded-blocks-auto-hide", "The explicit keep-expanded action remains expanded without any pointer.");
            await interaction.Invoke("keepExpanded");
            await Wait(interaction, () => Compact(interaction));

            choices = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task pendingMenu = interaction.ShowMenu("model");
            interaction.CheckDockPointer(); await Task.Delay(820); Audit(interaction);
            Check(interaction.InteractionActive && Compact(interaction), "menu-blocks-auto-hide", "An asynchronous menu owns the capsule and cancels its hide deadline.");
            interaction.Hide();
            choices.SetResult(State()); await pendingMenu; choices = null;
            Check(!interaction.IsVisible && !interaction.InteractionActive && !interaction.DockTimersActive && !interaction.IsAnimating,
                "hide-cancels-menu-and-docking", "Hide cancels menu ownership and all timers; an old choices continuation cannot restore them.");
            await Task.Delay(240);
            pointer = Center(interaction.CompactPixelBounds); interaction.Show();
            await Task.Delay(60);
            pointer = null; interaction.CheckDockPointer(); interaction.Hide();
            await Task.Delay(650); Audit(interaction);
            Check(!interaction.IsVisible && Compact(interaction) && !interaction.DockTimersActive,
                "hide-cancels-departure-deadline", "A hide deadline cannot create an edge indicator after the window is hidden.");
            pointer = Center(interaction.CompactPixelBounds); interaction.Show();
            await Task.Delay(60);
            await OpenFromRing(interaction, "show-after-hide");
            pointer = null; interaction.PointerLeft(); await Wait(interaction, () => Compact(interaction));
            // Start a fresh full grace interval instead of inheriting the collapse deadline.
            pointer = Center(interaction.CompactPixelBounds); interaction.CheckDockPointer();
            await HideToTab(interaction, "settings");
            interaction.Update(State(false));
            await Task.Delay(820); Audit(interaction);
            Check(Compact(interaction) && interaction.DockEdge == CapsuleEdge.None && !interaction.DockTimersActive,
                "disable-auto-hide-restores-ring", "Disabling auto-hide while hidden immediately restores the compact ring and stops docking timers.");
            pointer = Center(interaction.CompactPixelBounds);
            interaction.Update(State()); interaction.CheckDockPointer();
            await HideToTab(interaction, "reenabled");
            pointer = Center(interaction.VisualPixelBounds); interaction.CheckDockPointer();
            interaction.Close();
            int closedDirections = interaction.ExpansionTransitions;
            await Task.Delay(300); Audit(interaction);
            Check(closed.Contains(interaction) && !interaction.DockTimersActive && !interaction.IsAnimating && interaction.ExpansionTransitions == closedDirections,
                "close-cancels-proximity-dwell", "Close during a pending dwell cannot reveal a detached window or leave a timer running.");

            if (SystemParameters.ClientAreaAnimation)
            {
                var reversal = await Create();
                await Dock(reversal, CapsuleEdge.Left);
                var original = reversal.CompactPixelBounds;
                pointer = null; reversal.CheckDockPointer();
                bool started = await Wait(reversal, () => reversal.DockMotionActive);
                await Task.Delay(50);
                bool midSlide = started && reversal.DockMotionActive && !reversal.HiddenAtEdge;
                Check(midSlide, "hide-reversal-starts-mid-slide", "The injected return occurs 50 ms after observing the sliding-out animation, before its edge-indicator endpoint.");
                var beforeReturn = reversal.VisualPixelBounds;
                int beforeDirections = reversal.ExpansionTransitions;
                pointer = Center(original);
                var dpi = VisualTreeHelper.GetDpi(reversal);
                Check(pointer.Value.X > original.Left + (28 + 8) * dpi.DpiScaleX,
                    "hide-reversal-outside-tab-hotzone", "The return point is inside the compact ring but beyond the future indicator's +8-DIP hotzone.");
                Rect? firstReturnFrame = null;
                bool reachedHiddenEndpoint = reversal.HiddenAtEdge;
                EventHandler observeReturn = (_, _) =>
                {
                    firstReturnFrame ??= reversal.VisualPixelBounds;
                    reachedHiddenEndpoint |= reversal.HiddenAtEdge;
                };
                CompositionTarget.Rendering += observeReturn;
                bool returned;
                try
                {
                    // No Surface event: the animation itself must notice pointer
                    // reentry into the original compact region and reverse.
                    returned = await Wait(reversal, () =>
                    {
                        reachedHiddenEndpoint |= reversal.HiddenAtEdge;
                        return Compact(reversal);
                    });
                }
                finally { CompositionTarget.Rendering -= observeReturn; }
                Check(firstReturnFrame is Rect first && Near(first, beforeReturn),
                    "hide-reversal-preserves-current-frame", "The reversal starts at the current visible rectangle without jumping to the offscreen endpoint.");
                Check(returned && !reachedHiddenEndpoint && reversal.Surface.RingVisible && Near(reversal.VisualPixelBounds, original),
                    "hide-reversal-restores-ring-directly", "Sliding out reverses directly into the original ring without first becoming a hidden indicator.");
                for (int i = 0; i < 12; i++) { reversal.PointerEntered(); await Task.Delay(10); }
                Audit(reversal);
                Check(Compact(reversal) && reversal.AwaitingRingEntry && reversal.ExpansionTransitions == beforeDirections,
                    "hide-reversal-stationary-enter-stays-compact", "Reversal completion and synthetic Enter events cannot open a panel until the physical pointer moves again.");
                reversal.Close();
            }
            else
                checks.Add(J.Obj(("id", "hide-reversal-starts-mid-slide"), ("success", true), ("skipped", true),
                    ("detail", "The system disables animation, so no intermediate sliding-out frame exists to reverse.")));

            var bridge = await Create();
            var bridgeWork = await Dock(bridge, CapsuleEdge.Right);
            var bridgeCompact = bridge.CompactPixelBounds;
            pointer = new Point(bridgeWork.Right - 2, Center(bridgeCompact).Y);
            owns = true;
            bridge.PointerMoved(pointer.Value);
            bool bridgeExpanded = await Wait(bridge, () => Expanded(bridge));
            Check(bridgeExpanded && bridge.IsHotspot(pointer.Value) && !bridge.VisualPixelBounds.Contains(pointer.Value),
                "outer-ring-hotspot-bridges-panel-gap", "The extreme-right ring point remains a valid original hotspot beyond the logical panel's 12-DIP inset.");
            owns = false; // The point is outside the visible shape in the transparent envelope.
            await Task.Delay(180); Audit(bridge);
            Check(Expanded(bridge), "stationary-gap-retains-panel", "The small bridge from the original ring keeps the panel open while the pointer is stationary there.");
            var bridgeDpi = VisualTreeHelper.GetDpi(bridge);
            pointer = new Point(bridgeWork.Right - 2, bridgeCompact.Bottom + 100 * bridgeDpi.DpiScaleY);
            var bridgeDeparture = Stopwatch.StartNew();
            // Deliberately send no Surface MouseMove, MouseLeave or explicit
            // CheckDockPointer: this path never crosses the visible panel.
            bool bridgeCollapsed = await Wait(bridge, () => Compact(bridge), 500);
            measurements["bridgeDepartureMs"] = bridgeDeparture.Elapsed.TotalMilliseconds;
            Check(bridgeCollapsed && bridgeDeparture.Elapsed.TotalMilliseconds <= 500 && Near(bridge.VisualPixelBounds, bridgeCompact),
                "gap-departure-without-surface-events-collapses", "The gap watcher detects physical departure without any surface event and restores the ring within 500 ms.");
            bridge.Close();

            var footer = await Create();
            var footerWork = await Dock(footer, CapsuleEdge.Right);
            footer.Press(true);
            var footerOrigin = footer.BeginDrag(Center(footer.CompactPixelBounds));
            var footerDpi = VisualTreeHelper.GetDpi(footer);
            var footerTarget = new Point(footerWork.Right - 76 * footerDpi.DpiScaleX, footerWork.Bottom - 76 * footerDpi.DpiScaleY);
            footer.MoveBy(footerTarget.X - footerOrigin.X, footerTarget.Y - footerOrigin.Y, footerOrigin, null);
            pointer = Center(footer.CompactPixelBounds);
            await footer.FinishDrag();
            pointer = new Point(pointer.Value.X - 2, pointer.Value.Y);
            footer.PointerMoved(pointer.Value);
            bool footerExpanded = await Wait(footer, () => Expanded(footer));
            var footerPanel = footer.Surface.PanelBounds ?? Rect.Empty;
            var closePoint = new Point(footerPanel.Left + 306, footerPanel.Top + 384);
            var hostBounds = footer.PixelBounds;
            var closeScreen = new Point(hostBounds.X + closePoint.X * footerDpi.DpiScaleX, hostBounds.Y + closePoint.Y * footerDpi.DpiScaleY);
            Check(footerExpanded && footer.IsHotspot(closeScreen), "footer-overlaps-original-circle",
                "The bottom-right close control overlaps the original circular hover hotspot in a real window.");
            footer.Surface.Expansion = .9; footer.Surface.Redraw(); Audit(footer);
            Check(footer.Surface.Hit(closePoint) is null, "morph-footer-not-clickable-before-endpoint",
                "Partially revealed footer text cannot activate a hidden control or the old ring action.");
            footer.Surface.Expansion = 1; footer.Surface.Redraw(); Audit(footer);
            Check(footer.Surface.Hit(closePoint) == "collapse", "footer-action-wins-over-old-ring-hotspot",
                "At the completed panel, the collapse control remains collapse even where it overlaps the original ring.");
            footer.Close();

            Check(tooltipProblems.Count == 0, "no-tooltips-and-exact-help", tooltipProblems.Count == 0
                ? "Every audited phase disables visual tooltips and keeps exact accessibility counts." : string.Join("; ", tooltipProblems));
            measurements["audits"] = audits;
            measurements["clientAreaAnimation"] = SystemParameters.ClientAreaAnimation;
            measurements["monitorCount"] = System.Windows.Forms.Screen.AllScreens.Length;
            return J.Obj(("success", failures.Count == 0), ("version", 1), ("checks", checks), ("failures", failures), ("measurements", measurements),
                ("conditions", "Real unactivated WPF HWNDs; synthetic records; injected physical pointer and ownership; actual work-area edges and DPI; bounded waits, no desktop pointer movement or performance thresholds."));
        }
        finally
        {
            foreach (var window in windows) if (!closed.Contains(window)) window.Close();
            choices?.TrySetResult(State());
            app.ShutdownMode = shutdown;
        }
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
