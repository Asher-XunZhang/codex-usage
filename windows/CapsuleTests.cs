using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexUsage;

internal static class CapsuleTests
{
    /// <summary>Measures a real, visible layered HWND; no accounts, index or preferences are touched.</summary>
    public static async Task<JsonObject> BenchmarkAsync(JsonObject? state = null, int cycles = 4)
    {
        cycles = Math.Clamp(cycles, 1, 20);
        state = state?.Copy() ?? J.Obj(("settings", J.Obj(("refresh", 0), ("floating", J.Obj(("theme", "dark"), ("pinned", false), ("content", "usage"), ("days", "30"))))),
            ("quota", J.Obj(("capsuleFraction", .5), ("capsuleName", "周余"), ("stale", false), ("detail", "5h余 75% · 周余 50%"))),
            ("filtered", J.Obj(("summary", J.Obj(("total_tokens", 12848200), ("input_tokens", 12005200), ("output_tokens", 843000), ("cached_input_tokens", 9850400))))),
            ("status", "动画性能测量 · 合成数据"));
        var app = Application.Current ?? throw new InvalidOperationException("Capsule benchmark requires a WPF application dispatcher.");
        var shutdown = app.ShutdownMode; app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var delayedChoices = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new CapsuleWindow((_, _) => Task.CompletedTask, () => delayedChoices.Task, () => null) { ShowActivated = false, Topmost = false, IsHitTestVisible = false, Left = SystemParameters.WorkArea.Left + 40, Top = SystemParameters.WorkArea.Top + 40 };
        var results = new JsonArray();
        async Task AwaitEndpoint(bool expanded)
        {
            var watch = Stopwatch.StartNew();
            while ((window.IsAnimating || Math.Abs(window.Surface.Expansion - (expanded ? 1 : 0)) > .00001) && watch.ElapsedMilliseconds < 1000)
                await Task.Delay(10);
        }
        try
        {
            window.Update(state); window.Topmost = false; window.Show();
            await Task.Delay(300); window.UpdateLayout();
            var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!;
            async Task Measure(bool expand, int cycle)
            {
                var frames = new List<double>(); var progress = new List<double>();
                int layouts = 0, sizes = 0, positions = 0; var watch = Stopwatch.StartNew(); TimeSpan lastRenderingTime = TimeSpan.MinValue;
                int beforeRenders = window.Surface.RenderCount, beforeTexts = window.Surface.TextShapeCount;
                double beforeRenderMs = window.Surface.RenderMilliseconds;
                var hostBefore = window.PixelBounds;
                void Render(object? sender, EventArgs args)
                {
                    if (args is not RenderingEventArgs rendering || rendering.RenderingTime == lastRenderingTime) return;
                    lastRenderingTime = rendering.RenderingTime; frames.Add(watch.Elapsed.TotalMilliseconds); progress.Add(window.Surface.Expansion);
                }
                void Layout(object? sender, EventArgs args) { layouts++; }
                IntPtr Hook(IntPtr hwnd, int message, IntPtr w, IntPtr l, ref bool handled) { if (message == 0x5) sizes++; if (message == 0x47) positions++; return IntPtr.Zero; }
                CompositionTarget.Rendering += Render; window.LayoutUpdated += Layout; source.AddHook(Hook);
                try
                {
                    window.Expand(expand);
                    while ((window.IsAnimating || Math.Abs(window.Surface.Expansion - (expand ? 1 : 0)) > .00001) && watch.ElapsedMilliseconds < 3000) await Task.Delay(5);
                    double completed = watch.Elapsed.TotalMilliseconds; await Task.Delay(34);
                    var intervals = frames.Zip(frames.Skip(1), (a, b) => b - a).ToArray(); var sorted = intervals.Order().ToArray();
                    var changes = frames.Where((x, i) => i == 0 || Math.Abs(progress[i] - progress[i - 1]) > .000001).ToArray();
                    var changeIntervals = changes.Zip(changes.Skip(1), (a, b) => b - a).ToArray();
                    var sortedChanges = changeIntervals.Order().ToArray();
                    double Percentile(double p) => sorted.Length == 0 ? 0 : sorted[(int)Math.Clamp(Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
                    results.Add(J.Obj(("cycle", cycle), ("direction", expand ? "expand" : "collapse"), ("completedMs", completed),
                        ("frames", frames), ("progress", progress), ("intervalsMs", intervals), ("frameCount", frames.Count),
                        ("visualChangeTimesMs", changes), ("visualChangeIntervalsMs", changeIntervals), ("visualChangeCount", changes.Length),
                        ("visualChangeP95Ms", sortedChanges.Length == 0 ? 0 : sortedChanges[(int)Math.Ceiling(.95 * sortedChanges.Length) - 1]),
                        ("visualChangeMaxMs", sortedChanges.LastOrDefault()), ("visualGapsOver25Ms", changeIntervals.Count(x => x > 25)),
                        ("renderCalls", window.Surface.RenderCount - beforeRenders), ("renderCpuMs", window.Surface.RenderMilliseconds - beforeRenderMs), ("textShapeCalls", window.Surface.TextShapeCount - beforeTexts),
                        ("p50Ms", Percentile(.5)), ("p95Ms", Percentile(.95)), ("maxMs", sorted.LastOrDefault()),
                        ("gapsOver25Ms", intervals.Count(x => x > 25)), ("gapsOver40Ms", intervals.Count(x => x > 40)),
                        ("layouts", layouts), ("nativeResizeMessages", sizes), ("nativePositionMessages", positions),
                        ("finalWidthDip", window.VisualPixelBounds.Width / VisualTreeHelper.GetDpi(window).DpiScaleX),
                        ("finalHeightDip", window.VisualPixelBounds.Height / VisualTreeHelper.GetDpi(window).DpiScaleY),
                        ("hostWidthDip", window.Width), ("hostHeightDip", window.Height), ("nativeBoundsStable", window.PixelBounds == hostBefore),
                        ("finalProgress", window.Surface.Expansion)));
                    if (sizes != 0 || window.PixelBounds != hostBefore) throw new InvalidOperationException("Ordinary capsule hover resized or moved the fixed native envelope.");
                    if (window.IsAnimating || window.Surface.Expansion != (expand ? 1 : 0)) throw new InvalidOperationException("Capsule morph did not reach its endpoint.");
                }
                finally { source.RemoveHook(Hook); CompositionTarget.Rendering -= Render; window.LayoutUpdated -= Layout; }
            }
            for (int cycle = 0; cycle < cycles; cycle++) { await Measure(true, cycle); await Task.Delay(80); await Measure(false, cycle); await Task.Delay(80); }
            var interactionChecks = new JsonArray();
            void Verify(bool condition, string message) { if (!condition) throw new InvalidOperationException("Capsule animation: " + message); interactionChecks.Add(message); }
            if (SystemParameters.ClientAreaAnimation)
            {
                window.Expand(true); await Task.Delay(60);
                double progressBefore = window.Surface.Expansion; var boundsBefore = window.VisualPixelBounds;
                window.Expand(false);
                Verify(window.Surface.Expansion == progressBefore && window.VisualPixelBounds == boundsBefore, "midflight reversal preserves the exact visible rectangle and progress");
                await Task.Delay(15); progressBefore = window.Surface.Expansion; boundsBefore = window.VisualPixelBounds;
                window.Expand(true);
                Verify(window.Surface.Expansion == progressBefore && window.VisualPixelBounds == boundsBefore, "rapid reverse back preserves continuity");
                await AwaitEndpoint(true);
                Verify(window.Surface.Expansion == 1 && !window.IsAnimating && window.Surface.PanelBounds is not null, "expanded endpoint releases the rendering subscription and preserves the panel endpoint");
                window.Expand(false); await Task.Delay(45); boundsBefore = window.VisualPixelBounds;
                window.Press(true);
                Verify(!window.IsAnimating && window.VisualPixelBounds == boundsBefore, "press during morph freezes the exact logical visible rectangle");
                var compact = window.CompactPixelBounds;
                var dragOrigin = window.BeginDrag(new Point(compact.X + compact.Width / 2, compact.Y + compact.Height / 2));
                window.MoveBy(12, 8, dragOrigin, null);
                Verify(Math.Abs(window.CompactPixelBounds.X - dragOrigin.X - 12) <= 1 && Math.Abs(window.CompactPixelBounds.Y - dragOrigin.Y - 8) <= 1,
                    "compact drag follows physical pointer displacement");
                await window.FinishDrag(); await AwaitEndpoint(false);
                window.Expand(true); await Task.Delay(55); window.Hide(); window.Show(); await Task.Delay(50);
                Verify(window.Surface.Expansion == 0 && !window.IsAnimating && window.VisualPixelBounds == window.CompactPixelBounds,
                    "hide and reopen midflight restores compact logical geometry without another expand command");
            }
            window.Expand(true, false); await window.ShowMenu("period"); window.Expand(false); await Task.Delay(70);
            Verify(window.InteractionActive && window.Surface.Expansion == 1 && !window.IsAnimating, "open native menu retains expanded geometry");
            window.Hide(); Verify(!window.InteractionActive && !window.IsAnimating, "hide closes the menu and detaches animation");
            window.Show(); window.Expand(false, false); await Task.Delay(50);
            Verify(window.Surface.Expansion == 0 && !window.IsAnimating && window.Surface.AnimationBounds == null, "nonanimated transition restores the compact terminal size");
            window.Expand(true, false); var oldMenu = window.ShowMenu("model");
            window.Hide(); window.Show(); window.Expand(true, false); await window.ShowMenu("period");
            delayedChoices.SetResult(J.Obj(("choices", J.Obj(("models", new JsonArray("synthetic-model"))))));
            await oldMenu;
            Verify(window.InteractionActive && window.Surface.Expansion == 1, "stale asynchronous menu result preserves the newer open menu");
            window.Hide();
            var dpi = VisualTreeHelper.GetDpi(window);
            return J.Obj(("version", 2), ("measurement", "Visible WPF CompositionTarget.Rendering and distinct visual progress intervals, layout events and native HWND messages; synthetic data. These are UI rendering submissions, not DWM presentation timestamps."),
                ("dpiScaleX", dpi.DpiScaleX), ("dpiScaleY", dpi.DpiScaleY), ("clientAreaAnimation", SystemParameters.ClientAreaAnimation), ("renderTier", RenderCapability.Tier >> 16), ("samples", results), ("interactionChecks", interactionChecks));
        }
        finally { window.Close(); app.ShutdownMode = shutdown; }
    }

    public static async Task<JsonObject> TestHoverAsync()
    {
        Point? pointer = null; bool unoccluded = true;
        var window = new CapsuleWindow((_, _) => Task.CompletedTask, readPointer: () => pointer, ownsPointer: _ => unoccluded)
        { ShowActivated = false, Topmost = false, IsHitTestVisible = false, Left = 500, Top = 160 };
        var checks = new JsonArray();
        var waits = new JsonArray();
        const int terminalTimeoutMs = 1500;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Capsule hover: " + message); checks.Add(message); }
        void Event(RoutedEvent kind) => window.Surface.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = kind });
        JsonObject Snapshot() => J.Obj(("progress", window.Surface.Expansion), ("isAnimating", window.IsAnimating),
            ("transitions", window.ExpansionTransitions), ("hoverEvents", window.HoverEventCount));
        async Task AwaitEndpoint(double target, string phase)
        {
            // Nominal animation durations are not dispatcher deadlines. Retain
            // endpoint/transition assertions and expose actual scheduling time.
            var initial = Snapshot();
            var watch = Stopwatch.StartNew();
            JsonObject? legacyDeadline = null;
            while ((window.Surface.Expansion != target || window.IsAnimating) && watch.ElapsedMilliseconds < terminalTimeoutMs)
            {
                if (watch.ElapsedMilliseconds >= 600 && legacyDeadline is null) legacyDeadline = Snapshot();
                await Task.Delay(10);
            }
            waits.Add(J.Obj(("phase", phase), ("target", target), ("timeoutMs", terminalTimeoutMs),
                ("elapsedMs", watch.Elapsed.TotalMilliseconds), ("before", initial), ("after", Snapshot()),
                ("stateAtLegacy600ms", legacyDeadline)));
        }
        try
        {
            window.Update(DemoData.State()); window.Topmost = false; window.Show(); await Task.Delay(100);
            foreach (int angle in new[] { 0, 45, 90, 135, 180, 225, 270, 315 })
            {
                window.Expand(false, false); window.UpdateLayout(); var bounds = window.CompactPixelBounds;
                double radians = angle * Math.PI / 180;
                pointer = new Point(bounds.X + bounds.Width * (.5 + .48 * Math.Cos(radians)), bounds.Y + bounds.Height * (.5 + .48 * Math.Sin(radians)));
                int transitions = window.ExpansionTransitions;
                Event(System.Windows.UIElement.MouseEnterEvent);
                // Reproduce repeated WPF enter/leave notifications with a stationary physical pointer.
                for (int i = 0; i < 42; i++) { Event(System.Windows.UIElement.MouseLeaveEvent); Event(System.Windows.UIElement.MouseEnterEvent); await Task.Delay(7); }
                await AwaitEndpoint(1, "stationary-edge-expand-" + angle);
                Check(window.Surface.Expansion == 1 && window.ExpansionTransitions == transitions + 1 && !window.IsAnimating, "stationary circle edge " + angle + " completes once despite repeated enter/leave");
                var expanded = window.VisualPixelBounds; pointer = new Point(expanded.Right + 30, expanded.Bottom + 30);
                Event(System.Windows.UIElement.MouseLeaveEvent); await AwaitEndpoint(0, "physical-departure-" + angle);
                Check(window.Surface.Expansion == 0 && window.ExpansionTransitions == transitions + 2 && !window.IsAnimating, "physical departure " + angle + " collapses exactly once");
            }
            window.UpdateLayout(); var origin = window.CompactPixelBounds;
            pointer = new Point(origin.X, origin.Y); int before = window.ExpansionTransitions;
            Event(System.Windows.UIElement.MouseEnterEvent); await Task.Delay(40);
            Check(window.ExpansionTransitions == before && window.Surface.Expansion == 0, "transparent circular corner does not start expansion");
            pointer = new Point(origin.X + origin.Width / 2, origin.Y + origin.Height / 2);
            int midflightTransitions = window.ExpansionTransitions;
            bool departed = false;
            var startWatch = Stopwatch.StartNew();
            JsonObject? departureState = null;
            // Observe an actual intermediate rendering frame. A fixed delay can
            // run before the first frame or after the entire morph on a busy host.
            EventHandler departDuringFrame = (_, _) =>
            {
                if (departed || !window.IsAnimating || window.Surface.Expansion <= 0 || window.Surface.Expansion >= 1) return;
                departureState = Snapshot();
                pointer = new Point(origin.Right + 600, origin.Bottom + 600);
                departed = true;
            };
            Event(System.Windows.UIElement.MouseEnterEvent);
            CompositionTarget.Rendering += departDuringFrame;
            try
            {
                if (SystemParameters.ClientAreaAnimation)
                {
                    while (!departed && startWatch.ElapsedMilliseconds < terminalTimeoutMs) await Task.Delay(10);
                    waits.Add(J.Obj(("phase", "midflight-departure-injection"), ("elapsedMs", startWatch.Elapsed.TotalMilliseconds),
                        ("observedIntermediateFrame", departed), ("departureState", departureState), ("after", Snapshot())));
                    Check(departed && departureState is not null && departureState.N("transitions") == midflightTransitions + 1,
                        "physical departure is injected only after one expansion starts at an actual intermediate frame");
                }
                else
                {
                    await AwaitEndpoint(1, "animation-disabled-expand");
                    Check(window.Surface.Expansion == 1 && !window.IsAnimating, "animation-disabled hover reaches its endpoint before departure");
                    // No intermediate frame exists with system animation disabled.
                    waits.Add(J.Obj(("phase", "midflight-departure-injection"), ("skipped", "System client-area animation is disabled; no intermediate frame exists.")));
                    window.Expand(false, false);
                }
            }
            finally { CompositionTarget.Rendering -= departDuringFrame; }
            if (SystemParameters.ClientAreaAnimation)
            {
                int departureHoverEvents = window.HoverEventCount;
                // Deliberately inject no MouseMove or MouseLeave. The rendering/
                // departure path must detect the changed physical point.
                await AwaitEndpoint(0, "midflight-physical-departure");
                Check(window.Surface.Expansion == 0 && !window.IsAnimating, "physical departure during transition is detected without a MouseLeave event");
                Check(window.ExpansionTransitions == midflightTransitions + 2 && window.HoverEventCount == departureHoverEvents,
                    "midflight departure collapses exactly once without another surface event");
            }
            window.UpdateLayout(); origin = window.CompactPixelBounds; pointer = new Point(origin.X + origin.Width / 2, origin.Y + origin.Height / 2);
            Event(System.Windows.UIElement.MouseEnterEvent); await AwaitEndpoint(1, "occlusion-expand"); unoccluded = false;
            Event(System.Windows.UIElement.MouseLeaveEvent); await AwaitEndpoint(0, "occlusion-collapse");
            Check(window.Surface.Expansion == 0 && !window.IsAnimating, "another window covering the expanded capsule ends hover even at the same coordinates");
            unoccluded = true; origin = window.CompactPixelBounds;
            pointer = new Point(origin.X + origin.Width / 2, origin.Y + origin.Height / 2);
            window.SetKeepsExpanded(true); window.Expand(true, false);
            var fixedHost = window.PixelBounds;
            window.Collapse(); await AwaitEndpoint(0, "explicit-collapse");
            Check(window.Surface.Expansion == 0 && window.KeepsExpanded && window.PixelBounds == fixedHost,
                "active collapse preserves retention preference and fixed native envelope");
            for (int i = 0; i < 8; i++) Event(System.Windows.UIElement.MouseEnterEvent);
            pointer = pointer.Value + new Vector(1, 0); window.PointerMoved(pointer.Value); await Task.Delay(50);
            Check(window.Surface.Expansion == 0 && !window.IsAnimating, "active collapse ignores stationary events and movement that never leaves the ring");
            pointer = new Point(origin.Right + 80, origin.Bottom + 80); Event(System.Windows.UIElement.MouseLeaveEvent);
            pointer = new Point(origin.X + origin.Width / 2, origin.Y + origin.Height / 2); Event(System.Windows.UIElement.MouseEnterEvent);
            await AwaitEndpoint(1, "physical-reentry");
            Check(window.Surface.Expansion == 1 && !window.IsAnimating && window.KeepsExpanded, "physical leave and reentry restore retained expansion");
            return J.Obj(("success", true), ("checks", checks), ("waits", waits), ("hoverEvents", window.HoverEventCount));
        }
        catch (Exception error)
        {
            return J.Obj(("success", false), ("checks", checks), ("waits", waits), ("error", error.ToString()), ("terminal", Snapshot()));
        }
        finally { window.Close(); }
    }

    public static void Run()
    {
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Capsule test: " + message); }
        foreach (double scale in new[] { 1d, 1.25, 1.5, 2, 3 })
        {
            var work = new Rect(-1920, -200, 1920, 1080);
            var small = CapsuleGeometry.Clamp(new Rect(-1950, 900, 76 * scale, 76 * scale), work);
            Check(small.Left == work.Left && small.Bottom == work.Bottom, "collapsed pixel bounds respect negative-origin monitor work area");
            var large = CapsuleGeometry.Clamp(new Rect(-76 * scale, 820, 336 * scale, 410 * scale), work);
            Check(large.Right == work.Right && large.Top >= work.Top && (large.Height > work.Height || large.Bottom <= work.Bottom), "expanded bounds clamp at every DPI");
            Check(!CapsuleGeometry.Dragged(new(0, 0), new(2.99 * scale, 0), new(scale, scale)), "movement below 3 DIPs is a click");
            Check(CapsuleGeometry.Dragged(new(0, 0), new(3 * scale, 0), new(scale, scale)), "movement at 3 DIPs is a drag");
        }
        Check(CapsuleGeometry.Clamp(new Rect(-99, -99, 500, 800), new Rect(0, 0, 320, 200)).TopLeft == new Point(), "oversized display has a valid nonnegative clamp range");
        Check(!CapsuleGeometry.Contains(new(0, 0), new(76, 76), 0) && !CapsuleGeometry.Contains(new(3, 3), new(76, 76), 0), "transparent circular corners are not clickable");
        Check(CapsuleGeometry.Contains(new(38, 38), new(76, 76), 0) && CapsuleGeometry.Contains(new(38, 1), new(76, 76), 0), "visible circular center and arc are clickable");
        Check(!CapsuleGeometry.Contains(new(336, 200), new(336, 410), 1) && CapsuleGeometry.Contains(new(20, 30), new(336, 410), 1), "expanded half-open rounded bounds");

        var rule = J.Obj(("id", "daily"), ("name", "日常开发"), ("kind", "token"), ("amount", 20000000), ("model", "all"), ("task", "all"), ("period", J.Obj(("timezone", "Asia/Shanghai"))));
        var row = rule.Copy(); row["used"] = 18000000; row["remaining"] = 2000000; row["remainingPercent"] = 10; row["remainingFraction"] = .1;
        row["status"] = "warning"; row["dataStatus"] = "updated"; row["message"] = "预算剩余 10%"; row["updatedAt"] = J.Now; row["start"] = 1789142400; row["end"] = 1789228800;
        var state = J.Obj(("settings", J.Obj(("refresh", 5), ("floating", J.Obj(("content", "budget"), ("budgetID", "daily"), ("days", "30"), ("model", "saved-model"), ("task", "saved-task"))))),
            ("quota", J.Obj(("capsuleFraction", .72), ("capsuleName", "周余"), ("stale", false))), ("filtered", J.Obj(("summary", J.Obj(("total_tokens", 300000))))),
            ("budgets", J.Obj(("rules", new JsonArray(rule)), ("summaries", new JsonArray(row)))));
        row = (JsonObject)state.O("budgets").A("summaries")[0]!;
        var display = CapsuleBudgetDisplay.From(state);
        Check(display.Fraction == .1 && display.Used == "18.00M" && display.Remaining == "2,000,000 Token" && display.Scope == "全部模型 · 全部任务", "budget display uses only its own scope, balance and unit");
        Check(display.Period == "09-12 00:00 — 09-13 00:00", "budget period honors IANA zone");
        foreach (var item in new[] { ("disabled", "已停用"), ("ended", "已结束"), ("scheduled", "待开始"), ("source_invalid", "目录变化"), ("scope_invalid", "范围失效"), ("partial", "部分数据"), ("unknown", "待更新") })
        {
            row["status"] = item.Item1;
            Check(CapsuleBudgetDisplay.From(state).Caption == item.Item2, "explicit budget lifecycle caption " + item.Item1);
            if (item.Item1 is "unknown" or "partial" or "source_invalid" or "scope_invalid" or "scheduled") Check(CapsuleBudgetDisplay.From(state).Fraction == null, "unknown never falls back to official quota");
        }
        row["status"] = "partial"; row["kind"] = "money"; row["currency"] = "CNY"; row["remaining"] = null; row["coverage"] = "partial";
        display = CapsuleBudgetDisplay.From(state); Check(display.Remaining == "— CNY" && display.Used.StartsWith("≥¥") && display.Fraction == null, "unknown money displays an explicit dash and partial lower bound");
        row["status"] = "warning"; row["kind"] = "quota"; row["remaining"] = 10; row["amount"] = 100; row["quotaFloor"] = 20; row["windowMinutes"] = 10080; row["coverage"] = "complete";
        display = CapsuleBudgetDisplay.From(state); Check(display.Remaining == "10%" && display.Amount == "20%" && display.AmountLabel == "提醒下限" && display.Caption == "窗口已用", "official floor is not a 100 percent budget limit");
        row["paused"] = true; Check(CapsuleBudgetDisplay.From(state).Status.StartsWith("提醒已暂停"), "pause retains static status");
        state.O("budgets")["error"] = "预算写入失败"; display = CapsuleBudgetDisplay.From(state); Check(display.Stale && display.Status == "预算写入失败", "persistence error is visible"); state.O("budgets").Remove("error");
        state.O("settings").O("floating")["budgetID"] = "deleted"; display = CapsuleBudgetDisplay.From(state); Check(display.Name == "预算已删除" && display.Fraction == null, "removed selection does not switch budgets");
        state.O("settings").O("floating")["budgetID"] = "daily"; row["kind"] = "token"; row["status"] = "warning"; row["remainingFraction"] = .1;

        var previousShutdown = Application.Current.ShutdownMode; Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var actions = new List<string>(); var window = new CapsuleWindow((action, value) => { actions.Add(action); return Task.CompletedTask; });
        try
        {
            var surface = window.Surface; window.Update(state); surface.Expansion = 1;
            surface.CompactBounds = new Rect(260, 0, 76, 76); surface.PanelBounds = new Rect(0, 0, 336, 410);
            surface.Measure(new Size(336, 410)); surface.Arrange(new Rect(0, 0, 336, 410)); surface.UpdateLayout();
            Check(surface.Level == .1 && surface.Hit(new(150, 180)) == null && !surface.ActionEnabled("budgetScope"), "budget scope is read-only for mouse and programmatic actions");
            Check(surface.Hit(new(150, 114)) == "budget" && surface.Hit(new(50, 74)) == "contentUsage" && surface.Hit(new(90, 74)) == "contentBudget", "budget and content navigation hit targets");
            Check(surface.Hit(new(300, 75)) == "more" && surface.Hit(new(300, 353)) == "refresh" && surface.Hit(new(280, 387)) == "collapse", "more refresh and collapse remain reachable");
            Check(surface.Hit(new(200, 75)) == "keepExpanded" && !surface.Regions().Any(x => x.name is "themeDark" or "themeLight" or "interval"), "retention stays visible while low-frequency preferences move to settings");
            surface.PanelBounds = new Rect(44, 25, 336, 410); surface.CompactBounds = new Rect(304, 25, 76, 76);
            Check(surface.Hit(new(330, 99)) == "more" && surface.Hit(new(44, 25)) == null && surface.Hit(new(30, 150)) == null, "animation envelope maps actions and excludes its transparent area");
            surface.PanelBounds = new Rect(0, 0, 336, 410); surface.CompactBounds = new Rect(260, 0, 76, 76);
            surface.Expansion = .5;
            Check(!surface.Regions().Any(x => x.name is "more" or "contentUsage" or "view-budget"), "partially morphed content cannot activate unrevealed controls");
            surface.Expansion = 1;
            Check(surface.Regions().Any(x => x.name == "context") && surface.Regions().Any(x => x.name == "budgetScope") && !surface.Regions().Any(x => x.name is "model" or "task"), "accessibility exposes context menu and disabled budget scope");
            state["busy"] = true; window.Update(state); window.Invoke("refresh").GetAwaiter().GetResult(); window.Invoke("budgetScope").GetAwaiter().GetResult();
            Check(actions.Count == 0, "programmatic actions cannot bypass disabled state");
            surface.ToggleFilters();
            for (int i = 0; i < 20; i++)
            {
                state.O("settings").O("floating")["content"] = "usage"; window.Update(state);
                Check(surface.Level == .72 && surface.Regions().Any(x => x.name == "task"), "usage restores independent official fraction and selectors");
                state.O("settings").O("floating")["content"] = "budget"; window.Update(state);
                Check(surface.Level == .1 && ReferenceEquals(surface, window.Surface), "mode changes reuse the native drawing surface");
            }
            var floating = state.O("settings").O("floating"); Check(floating.S("days") == "30" && floating.S("model") == "saved-model" && floating.S("task") == "saved-task", "budget rendering does not mutate usage filters");
            floating["content"] = "usage"; state.O("filtered").O("summary")["total_tokens"] = 9007199254740993L; window.Update(state);
            Check(System.Windows.Automation.AutomationProperties.GetHelpText(surface).Contains("9,007,199,254,740,993") && surface.ToolTip is null && !System.Windows.Controls.ToolTipService.GetIsEnabled(surface), "accessibility preserves exact integers without a whole-window tooltip");
            surface.Expansion = 0; surface.CompactBounds = new Rect(0, 0, 76, 76);
            surface.Measure(new Size(76, 76)); surface.Arrange(new Rect(0, 0, 76, 76)); surface.UpdateLayout(); surface.Redraw();
            var preview = new RenderTargetBitmap(76, 76, 96, 96, PixelFormats.Pbgra32); preview.Render(surface); var pixels = new byte[76 * 76 * 4]; preview.CopyPixels(pixels, 76 * 4, 0);
            Check(pixels[(38 * 76 + 38) * 4 + 3] > 0 && pixels[3] == 0, "offline compact preview preserves opaque drawing and transparent corners");
            Check(surface.Regions().Count == 2 && !surface.Regions().Any(x => x.name == "budgetEdit"), "collapsed surface releases expanded actions");
            Check(surface.Hit(new(0, 0)) == null && surface.Hit(new(38, 38)) == "details", "compact corner and primary hit targets");
            new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle(); window.UpdateLayout();
            var origin = window.CompactPixelBounds; var host = window.PixelBounds; window.Expand(true, false); window.UpdateLayout();
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
            Check(Math.Abs(window.VisualPixelBounds.Width / dpi.DpiScaleX - 336) <= 1 / dpi.DpiScaleX
                && Math.Abs(window.VisualPixelBounds.Height / dpi.DpiScaleY - 410) <= 1 / dpi.DpiScaleY && surface.Expansion == 1 && window.PixelBounds == host,
                "logical expansion preserves panel DIP dimensions while the native envelope stays fixed");
            window.Expand(false, false); window.UpdateLayout();
            Check(window.VisualPixelBounds == origin && window.CompactPixelBounds == origin && window.PixelBounds == host,
                "the logical circle restores its physical anchor without resizing the native envelope");
        }
        finally { window.Close(); Application.Current.ShutdownMode = previousShutdown; }
        Console.WriteLine("Capsule tests passed: DPI geometry, circular hit testing, drag threshold, disabled actions, budget formatting, lifecycle states and scope isolation.");
    }
}
