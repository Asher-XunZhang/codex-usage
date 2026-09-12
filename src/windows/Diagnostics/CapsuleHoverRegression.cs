using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace CodexUsage;

/// <summary>
/// Replays the stationary-pointer occlusion missed by the original hover test.
/// Windows are real, but pointer position and HWND ownership are injected. This
/// fixture never moves the desktop pointer, reads account data, or starts a host.
/// </summary>
internal static class CapsuleHoverRegression
{
    public static async Task<JsonObject> RunAsync()
    {
        var app = Application.Current ?? throw new InvalidOperationException("Capsule hover regression requires a WPF application dispatcher.");
        if (!app.Dispatcher.CheckAccess()) throw new InvalidOperationException("Capsule hover regression must run on the WPF dispatcher.");
        var previousShutdown = app.ShutdownMode;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Point? pointer = null;
        bool unoccluded = true;
        var windows = new List<CapsuleWindow>();
        var closedWindows = new HashSet<CapsuleWindow>();
        var checks = new JsonArray();
        var failures = new JsonArray();
        var measurements = new JsonObject();
        var tooltipOwners = new HashSet<string>();
        var enabledTooltips = new HashSet<string>();
        var inaccurateHelp = new HashSet<string>();
        int tooltipAudits = 0;
        const string exactTotal = "9,007,199,254,740,993";

        JsonObject State()
        {
            var state = DemoData.State();
            state.O("settings")["refresh"] = 0;
            state.O("settings").O("floating")["pinned"] = false;
            state.O("settings").O("floating")["content"] = "usage";
            state.O("settings").O("floating")["days"] = "30";
            var summary = state.O("filtered").O("summary");
            summary["total_tokens"] = 9007199254740993L;
            summary["input_tokens"] = 9007199254740000L;
            summary["output_tokens"] = 993L;
            state["status"] = "悬停回归验证 · 合成数据";
            return state;
        }
        CapsuleWindow Create()
        {
            var window = new CapsuleWindow((_, _) => Task.CompletedTask,
                readPointer: () => pointer, ownsPointer: _ => unoccluded)
            {
                ShowActivated = false,
                Topmost = false,
                IsHitTestVisible = false,
                Left = SystemParameters.WorkArea.Left + Math.Min(500, Math.Max(40, SystemParameters.WorkArea.Width - 120)),
                Top = SystemParameters.WorkArea.Top + 100
            };
            windows.Add(window);
            window.Closed += (_, _) => closedWindows.Add(window);
            window.Update(State());
            return window;
        }
        void Check(bool condition, string id, string detail)
        {
            checks.Add(J.Obj(("id", id), ("success", condition), ("detail", detail)));
            if (!condition) failures.Add(id + ": " + detail);
        }
        void Audit(CapsuleWindow window, string phase)
        {
            tooltipAudits++;
            foreach (var node in VisualTree(window).Concat(new DependencyObject[] { window.Surface }).Distinct())
                if (ToolTipService.GetToolTip(node) is not null)
                    tooltipOwners.Add(phase + ": " + node.GetType().Name);
            if (window.ToolTip is not null) tooltipOwners.Add(phase + ": Window.ToolTip");
            if (window.Surface.ToolTip is not null) tooltipOwners.Add(phase + ": Surface.ToolTip");
            if (ToolTipService.GetIsEnabled(window)) enabledTooltips.Add(phase + ": Window");
            if (ToolTipService.GetIsEnabled(window.Surface)) enabledTooltips.Add(phase + ": Surface");
            if (!AutomationProperties.GetHelpText(window.Surface).Contains(exactTotal, StringComparison.Ordinal))
                inaccurateHelp.Add(phase);
        }
        void Event(CapsuleWindow window, RoutedEvent kind, string phase)
        {
            window.Surface.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = kind });
            Audit(window, phase);
        }
        static bool Terminal(CapsuleWindow window, bool expanded) =>
            !window.IsAnimating && window.Surface.AnimationBounds is null &&
            Math.Abs(window.Surface.Expansion - (expanded ? 1 : 0)) < .00001;
        async Task<double> WaitFor(CapsuleWindow window, bool expanded, int timeoutMs, string phase)
        {
            var clock = Stopwatch.StartNew();
            while (!Terminal(window, expanded) && clock.Elapsed.TotalMilliseconds < timeoutMs)
            {
                Audit(window, phase);
                await Task.Delay(10);
            }
            Audit(window, phase);
            return clock.Elapsed.TotalMilliseconds;
        }
        static Point Center(CapsuleWindow window)
        {
            var bounds = window.CompactPixelBounds;
            return new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        }
        async Task EnterAtCenter(CapsuleWindow window, string phase)
        {
            pointer = Center(window);
            unoccluded = true;
            window.PointerMoved(pointer.Value);
            Event(window, UIElement.MouseEnterEvent, phase);
            await WaitFor(window, true, 600, phase);
        }

        try
        {
            var window = Create();
            Audit(window, "created");
            window.Show();
            await Task.Delay(80);
            window.UpdateLayout();
            Check(PresentationSource.FromVisual(window.Surface) is HwndSource,
                "real-layered-window", "The fixture surface belongs to an actual WPF HWND.");
            var fixedHost = window.PixelBounds;
            var originalCenter = Center(window);
            pointer = originalCenter;
            window.PointerMoved(pointer.Value);
            Event(window, UIElement.MouseEnterEvent, "initial-enter");
            await WaitFor(window, true, 600, "initial-expansion");
            Check(Terminal(window, true), "initial-expanded-state", "The capsule is fully expanded before occlusion begins.");

            int beforeStorm = window.ExpansionTransitions;
            int eventsBeforeStorm = window.HoverEventCount;
            unoccluded = false;
            var stormClock = Stopwatch.StartNew();
            for (int i = 0; i < 30; i++)
            {
                Event(window, UIElement.MouseLeaveEvent, "stationary-occlusion");
                Event(window, UIElement.MouseEnterEvent, "stationary-occlusion");
                await Task.Delay(10);
            }
            await WaitFor(window, false, 350, "stationary-occlusion-settle");
            measurements["occlusionStormMs"] = stormClock.Elapsed.TotalMilliseconds;
            measurements["occlusionStormEvents"] = window.HoverEventCount - eventsBeforeStorm;
            measurements["occlusionStormTransitions"] = window.ExpansionTransitions - beforeStorm;
            Check(Terminal(window, false) && window.ExpansionTransitions == beforeStorm + 1,
                "stationary-occlusion-collapses-once",
                "Thirty Leave/Enter pairs at the unchanged original ring center produce exactly one collapse and no reopening.");
            Check(window.PixelBounds == fixedHost, "hover-storm-preserves-native-envelope",
                "Expansion and occlusion collapse preserve the native HWND bounds; only logical geometry morphs.");

            int beforeUncover = window.ExpansionTransitions;
            unoccluded = true;
            for (int i = 0; i < 10; i++)
            {
                Event(window, UIElement.MouseEnterEvent, "uncovered-without-motion");
                await Task.Delay(10);
            }
            await Task.Delay(240);
            Audit(window, "uncovered-without-motion-settle");
            Check(Terminal(window, false) && window.ExpansionTransitions == beforeUncover,
                "synthetic-reenter-after-uncover-stays-collapsed",
                "Restoring HWND ownership without physical movement cannot reopen the collapsed capsule.");

            pointer = new Point(originalCenter.X + 2, originalCenter.Y);
            int beforeMovement = window.ExpansionTransitions;
            window.PointerMoved(pointer.Value);
            await WaitFor(window, true, 600, "physical-two-pixel-movement");
            Check(Terminal(window, true) && window.ExpansionTransitions == beforeMovement + 1,
                "physical-move-reopens", "A real two-physical-pixel movement clears the stationary reentry suppression.");

            Event(window, UIElement.MouseEnterEvent, "brief-occlusion-baseline");
            int beforeBriefLoss = window.ExpansionTransitions;
            var briefClock = Stopwatch.StartNew();
            unoccluded = false;
            Event(window, UIElement.MouseLeaveEvent, "brief-occlusion");
            await Task.Delay(15);
            unoccluded = true;
            double briefLossMs = briefClock.Elapsed.TotalMilliseconds;
            Event(window, UIElement.MouseEnterEvent, "brief-occlusion-restored");
            await Task.Delay(250);
            Audit(window, "brief-occlusion-settle");
            measurements["briefOwnershipLossMs"] = briefLossMs;
            Check(briefLossMs < 60, "brief-occlusion-test-duration", "Injected ownership loss lasted " + briefLossMs.ToString("F1") + " ms, below 60 ms.");
            Check(Terminal(window, true) && window.ExpansionTransitions == beforeBriefLoss,
                "brief-occlusion-does-not-collapse", "Ownership restored before confirmation leaves the expanded state and transition count unchanged.");

            var visible = window.VisualPixelBounds;
            pointer = new Point(visible.Right + 100, visible.Bottom + 100);
            unoccluded = false;
            int beforeDeparture = window.ExpansionTransitions;
            var departureClock = Stopwatch.StartNew();
            window.PointerMoved(pointer.Value);
            Event(window, UIElement.MouseLeaveEvent, "physical-departure");
            await WaitFor(window, false, 500, "physical-departure-settle");
            double departureMs = departureClock.Elapsed.TotalMilliseconds;
            measurements["physicalDepartureMs"] = departureMs;
            Check(Terminal(window, false) && departureMs <= 500 && window.ExpansionTransitions == beforeDeparture + 1,
                "physical-departure-under-500ms", "A pointer outside the panel completes the 90 ms confirmation and 220 ms collapse once; observed completion " + departureMs.ToString("F1") + " ms.");

            await EnterAtCenter(window, "before-hide");
            window.Press(true);
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window.Surface)!, Environment.TickCount, Key.Escape)
            { RoutedEvent = UIElement.PreviewKeyDownEvent };
            window.RaiseEvent(escape);
            await WaitFor(window, false, 350, "escape-during-press");
            Event(window, UIElement.MouseEnterEvent, "escape-stationary-reenter");
            await Task.Delay(150);
            Check(escape.Handled && !window.InteractionActive && !window.Surface.IsMouseCaptured && Terminal(window, false),
                "escape-cancels-press", "Escape clears a pressed gesture and collapses without reopening at the unchanged pointer position.");
            pointer = new Point(pointer!.Value.X + 2, pointer.Value.Y);
            window.PointerMoved(pointer.Value);
            await Task.Delay(60);
            Check(Terminal(window, false), "escape-requires-leave-before-reentry",
                "A two-pixel movement inside the circle cannot undo an explicit Escape collapse.");
            // Escape now uses the same explicit-collapse contract as the footer:
            // leave the ring before reentering. Establish that independent baseline
            // before testing Hide cancellation rather than carrying dismissal state.
            var escapedRing = window.CompactPixelBounds;
            pointer = new Point(escapedRing.Right + 80, escapedRing.Bottom + 80);
            Event(window, UIElement.MouseLeaveEvent, "after-escape-physical-leave");
            await EnterAtCenter(window, "after-escape-physical-reentry");
            Check(Terminal(window, true), "hide-test-expanded-state", "The hide case starts with a stable expanded capsule.");
            unoccluded = false;
            Event(window, UIElement.MouseLeaveEvent, "pending-confirmation-before-hide");
            await Task.Delay(15);
            window.Hide();
            int afterHide = window.ExpansionTransitions;
            Audit(window, "hidden");
            Check(!window.IsVisible && !window.InteractionActive && Terminal(window, false),
                "hide-cancels-pending-confirmation", "Hiding cancels confirmation, animation and interaction, restoring compact geometry.");
            await Task.Delay(180);
            Audit(window, "hidden-after-confirmation-deadline");
            Check(Terminal(window, false) && window.ExpansionTransitions == afterHide,
                "hidden-confirmation-cannot-transition", "No delayed confirmation changes direction while hidden.");
            window.Show();
            await EnterAtCenter(window, "reopened-after-hide");
            Check(Terminal(window, true), "show-after-hide-reopens", "A visible capsule can expand normally after pending-confirmation cancellation.");

            window.Collapse();
            await WaitFor(window, false, 350, "explicit-collapse-before-hide");
            window.Hide();
            pointer = new Point(window.CompactPixelBounds.Right + 80, window.CompactPixelBounds.Bottom + 80);
            window.Show();
            await EnterAtCenter(window, "reshown-after-explicit-collapse");
            Check(Terminal(window, true), "hide-ends-explicit-collapse-interaction",
                "Showing a previously hidden capsule starts a fresh hover interaction without carrying the old explicit-collapse reentry requirement.");

            unoccluded = false;
            Event(window, UIElement.MouseLeaveEvent, "pending-confirmation-before-close");
            await Task.Delay(15);
            window.Close();
            int afterClose = window.ExpansionTransitions;
            await Task.Delay(180);
            Audit(window, "closed-after-confirmation-deadline");
            Check(closedWindows.Contains(window) && !window.IsAnimating && window.ExpansionTransitions == afterClose,
                "close-cancels-pending-confirmation", "Closing disposes the window without a delayed transition or surviving animation.");

            pointer = null;
            unoccluded = true;
            var replacement = Create();
            replacement.Show();
            await Task.Delay(60);
            await EnterAtCenter(replacement, "replacement-window");
            Check(Terminal(replacement, true), "new-window-after-close-reopens", "A new capsule instance starts a fresh working hover lifecycle.");
            Audit(replacement, "replacement-expanded");

            Check(tooltipOwners.Count == 0, "no-visual-tooltip-owners",
                tooltipOwners.Count == 0 ? "No phase assigns a tooltip anywhere in the capsule visual tree." : string.Join("; ", tooltipOwners));
            Check(enabledTooltips.Count == 0, "tooltips-disabled",
                enabledTooltips.Count == 0 ? "Window and surface keep ToolTipService.IsEnabled=false in every phase." : string.Join("; ", enabledTooltips));
            Check(inaccurateHelp.Count == 0, "exact-accessibility-count",
                inaccurateHelp.Count == 0 ? "Accessibility HelpText retains " + exactTotal + " in every phase." : string.Join("; ", inaccurateHelp));
            measurements["tooltipAudits"] = tooltipAudits;
            measurements["dpiScaleX"] = VisualTreeHelper.GetDpi(replacement).DpiScaleX;
            return J.Obj(("success", failures.Count == 0), ("version", 1), ("checks", checks), ("failures", failures), ("measurements", measurements),
                ("conditions", "Real unactivated WPF windows with synthetic data, injected physical pointer coordinates and HWND ownership; no desktop pointer movement."));
        }
        finally
        {
            foreach (var window in windows)
                if (!closedWindows.Contains(window)) window.Close();
            app.ShutdownMode = previousShutdown;
        }
    }

    private static IEnumerable<DependencyObject> VisualTree(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in VisualTree(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
