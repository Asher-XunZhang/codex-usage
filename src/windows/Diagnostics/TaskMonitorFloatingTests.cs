using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexUsage;

/// <summary>Uses the actual WPF drawing surface; fixtures never read a user's tasks or activate a window.</summary>
internal static class TaskMonitorFloatingTests
{
    public static JsonObject Run(string? directory = null)
    {
        var app = Application.Current; var shutdown = app.ShutdownMode;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var failures = new JsonArray(); var images = new JsonArray();
        int frames = 0, labels = 0, symbols = 0, actions = 0, headers = 0, messageActions = 0;
        if (directory != null) Directory.CreateDirectory(directory);
        void Check(bool okay, string id) { if (!okay && failures.Count < 100) failures.Add(id); }
        try
        {
            foreach (bool light in new[] { true, false }) foreach (double dpi in new[] { 1d, 1.25, 1.5, 2, 3 })
            {
                var masks = new HashSet<string>();
                foreach (string status in TaskMonitorGlyph.States)
                {
                    var drawing = new DrawingGroup(); using (var dc = drawing.Open()) TaskMonitorGlyph.Draw(dc, new(4, 4, 10, 10), status, light);
                    Check(new Rect(4, 4, 10, 10).Contains(drawing.Bounds), $"glyph-slot-{status}-{light}-{dpi}");
                    var ringGlyph = new DrawingGroup(); using (var dc = ringGlyph.Open()) TaskMonitorGlyph.Draw(dc, new(44, 56.5, 10, 10), status, light);
                    var arcInterior = new EllipseGeometry(new Point(38, 38), 30.95, 30.95);
                    // Raster alpha outside the quota arc's inner radius must remain absent.
                    var isolated = new DrawingVisual(); using (var dc = isolated.RenderOpen()) dc.DrawDrawing(ringGlyph);
                    var ringBitmap = Bitmap(isolated, 76, 76, 3); var ringPixels = Pixels(ringBitmap);
                    int outside = 0;
                    for (int y = 0; y < ringBitmap.PixelHeight; y++) for (int x = 0; x < ringBitmap.PixelWidth; x++)
                        if (ringPixels[(y * ringBitmap.PixelWidth + x) * 4 + 3] > 48 && !arcInterior.FillContains(new Point((x + .5) / 3, (y + .5) / 3))) outside++;
                    Check(outside == 0, "task-glyph-must-not-cover-quota-arc-" + status);
                    var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) dc.DrawDrawing(drawing);
                    var bitmap = Bitmap(visual, 18, 18, dpi); var rgba = Pixels(bitmap);
                    string mask = Convert.ToHexString(rgba.Where((_, index) => index % 4 == 3).ToArray());
                    Check(masks.Add(mask), $"glyph-shape-must-differ-in-grayscale-{status}-{light}-{dpi}"); symbols++;
                }
                var absent = new DrawingGroup(); using (var dc = absent.Open()) TaskMonitorGlyph.Draw(dc, new(0, 0, 10, 10), "", light);
                Check(absent.Bounds.IsEmpty, "no-watches-no-task-symbol");
                var state = Fixture(light); var commands = new List<(string Name, string? Value)>();
                var window = new CapsuleWindow((name, value) => { commands.Add((name, value)); return Task.CompletedTask; }, readPointer: () => null) { ShowActivated = false, Topmost = false };
                try
                {
                    var surface = window.Surface; window.Content = null;
                    var root = new Border { Child = surface, Width = 360, Height = 434 };
                    VisualTreeHelper.SetRootDpi(root, new DpiScale(dpi, dpi));
                    surface.HostSize = new Size(360, 434); surface.PanelBounds = new Rect(12, 12, 336, 410);
                    surface.CaptureTextBounds = true;
                    root.Measure(new Size(360, 434)); root.Arrange(new Rect(0, 0, 360, 434)); root.UpdateLayout();
                    foreach (var compact in new[] { new Rect(272, 12, 76, 76), new Rect(12, 12, 76, 76), new Rect(272, 346, 76, 76), new Rect(12, 346, 76, 76) })
                    {
                        surface.CompactBounds = compact;
                        for (int index = 0; index < TaskMonitorGlyph.States.Length; index++)
                        {
                            string status = TaskMonitorGlyph.States[index];
                            SetUnread(state, index % 2 == 0 ? 137 : 0);
                            foreach (double progress in new[] { 0d, .08, .12, .5, .86, .90, 1d })
                            {
                                surface.Expansion = progress;
                                var before = surface.DrawingBounds;
                                state.O("monitor").O("summary")["status"] = status;
                                var task = state.O("monitor").A("watches").Rows().First(); task["status"] = status;
                                int transitions = window.ExpansionTransitions;
                                window.Update(state); frames++;
                                Check(surface.Expansion == progress && surface.DrawingBounds == before && window.ExpansionTransitions == transitions,
                                    $"state-update-must-not-restart-morph-{progress}-{status}");
                                foreach (var ink in surface.VisibleTextBounds)
                                {
                                    labels++;
                                    Check(CapsuleMorph.FullyContainsRounded(ink.Bounds, surface.DrawingBounds, surface.CurrentRadius, 0),
                                        $"text-clipped-{light}-{dpi}-{compact.TopLeft}-{progress}-{ink.Text}-{ink.Bounds}");
                                }
                                if (progress >= .12 && progress <= .86)
                                    Check(surface.VisibleTextBounds.Count == 0, "monitor-middle-morph-has-no-quota-or-task-content-ghost");
                                if (progress == 0)
                                    Check(surface.VisibleTextBounds.Any(x => x.Text == surface.TopValue) &&
                                        surface.VisibleTextBounds.Any(x => x.Text == "今日"), "monitor-compact-keeps-usage-quota-context");
                                if (progress == 1)
                                {
                                    CheckHeader($"{light}-{dpi}-{compact.TopLeft}-{status}");
                                    var controls = surface.Regions().Where(x => x.name is not ("details" or "context")).ToArray();
                                    var lastCard = CapsuleSurface.MonitorTaskBounds(1); lastCard.Offset(surface.PanelBounds.Value.X, surface.PanelBounds.Value.Y);
                                    Check(controls.Single(x => x.name == "monitorManage").bounds.Top - lastCard.Bottom >= 6, "last-task-card-keeps-gap-before-view-all");
                                    Check(controls.All(x => surface.ActionEnabled(x.name) == surface.KeyboardOrder().Contains(x.name)), "enabled-monitor-actions-reachable-by-keyboard");
                                    foreach (var control in controls)
                                        foreach (var other in controls.Where(x => x.name != control.name))
                                        { var intersection = Rect.Intersect(control.bounds, other.bounds); Check(intersection.IsEmpty || intersection.Width * intersection.Height == 0, "monitor-actions-overlap-" + control.name + "-" + other.name); }
                                    foreach (var item in new[] { ("contentUsage", "用量"), ("contentBudget", "预算"), ("contentMonitor", index % 2 == 0 ? "监控 9+" : "监控"),
                                        ("main", "打开主面板"), ("collapse", "收起"), ("monitorManage", "查看全部任务与消息"), ("monitorClearEnded", "清除已结束结果"), ("monitorCheck", "检查任务") })
                                    {
                                        var area = controls.Single(x => x.name == item.Item1).bounds;
                                        var ink = surface.VisibleTextBounds.Single(x => x.Text == item.Item2).Bounds;
                                        Check(area.Contains(ink) && Math.Abs(ink.X + ink.Width / 2 - area.X - area.Width / 2) < .02 && Math.Abs(ink.Y + ink.Height / 2 - area.Y - area.Height / 2) < .02,
                                            "button-not-ink-centered-" + item.Item1); actions++;
                                    }
                                    foreach (var control in controls.Where(x => x.name.StartsWith("monitorDetail:", StringComparison.Ordinal) || x.name.StartsWith("monitorStop:", StringComparison.Ordinal)))
                                    {
                                        string label = control.name.StartsWith("monitorStop:", StringComparison.Ordinal) ? "取消监控" : "查看";
                                        var ink = surface.VisibleTextBounds.Single(x => x.Text == label && control.bounds.Contains(x.Bounds)).Bounds;
                                        Check(Math.Abs(ink.X + ink.Width / 2 - control.bounds.X - control.bounds.Width / 2) < .02 &&
                                            Math.Abs(ink.Y + ink.Height / 2 - control.bounds.Y - control.bounds.Height / 2) < .02, "task-action-is-centered-" + control.name); actions++;
                                        Check(surface.VisibleTextBounds.Where(x => x.Bounds != ink).All(x => Rect.Intersect(x.Bounds, control.bounds).IsEmpty),
                                            "task-action-does-not-cover-title-metadata-or-another-control-" + control.name);
                                    }
                                }
                            }
                        }
                    }
                    surface.Expansion = 1; surface.Redraw();
                    var peer = FrameworkElementAutomationPeer.CreatePeerForElement(surface)!;
                    var pages = peer.GetChildren().Where(x => x.GetAutomationControlType() == AutomationControlType.RadioButton).ToArray();
                    Check(pages.Length == 3 && ((ISelectionItemProvider)pages.Single(x => x.GetAutomationId() == "contentMonitor").GetPattern(PatternInterface.SelectionItem)).IsSelected,
                        "uia-has-three-pages-with-monitor-selected");
                    Check(AutomationProperties.GetHelpText(surface).Contains(TaskMonitorHeaderDisplay.From(state).AccessibleText, StringComparison.Ordinal), "uia-help-describes-all-monitor-subscriptions-and-separate-unread");
                    var messagePeer = peer.GetChildren().Single(x => x.GetAutomationId() == "monitorMessages");
                    Check(messagePeer.GetAutomationControlType() == AutomationControlType.Button && messagePeer.IsEnabled() &&
                        surface.KeyboardOrder().Contains("monitorMessages"), "messages-have-one-independent-accessible-button");
                    Check(messagePeer.GetName().Contains("137 条未读", StringComparison.Ordinal) && messagePeer.GetName().Contains("不会标为已读", StringComparison.Ordinal), "message-accessible-name-keeps-exact-count-and-open-semantics");
                    string messagesBefore = surface.State.O("monitor").A("messages").ToJsonString();
                    var messageBounds = surface.Regions().Single(x => x.name == "monitorMessages").bounds;
                    string? messageHit = surface.Hit(new(messageBounds.X + messageBounds.Width / 2, messageBounds.Y + messageBounds.Height / 2));
                    Check(messageHit == "monitorMessages", "unread-button-hit-does-not-fall-through-to-header");
                    surface.ActivateActionAsync(messageHit!, false).GetAwaiter().GetResult();
                    Check(commands.Last() == ("monitorMessages", null), "pointer-messages-open-message-records"); messageActions++;
                    surface.FocusAction("monitorMessages", false);
                    surface.HandleKeyboardAsync(Key.Enter, ModifierKeys.None).GetAwaiter().GetResult();
                    Check(commands.Last() == ("monitorMessages", null), "keyboard-messages-share-pointer-command"); messageActions++;
                    int beforeUiaMessages = commands.Count;
                    ((IInvokeProvider)messagePeer.GetPattern(PatternInterface.Invoke)).Invoke();
                    var messageFrame = new DispatcherFrame();
                    surface.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => messageFrame.Continue = false));
                    Dispatcher.PushFrame(messageFrame);
                    Check(commands.Count == beforeUiaMessages + 1 && commands.Last() == ("monitorMessages", null), "uia-messages-share-pointer-command"); messageActions++;
                    Check(surface.State.O("monitor").A("messages").ToJsonString() == messagesBefore, "opening-messages-does-not-mark-them-read");
                    surface.EndKeyboardNavigation();
                    string actionID = surface.Regions().First(x => x.name.StartsWith("monitorDetail:", StringComparison.Ordinal)).name;
                    surface.ActivateActionAsync(actionID, false).GetAwaiter().GetResult();
                    Check(commands.Last() == ("monitorDetail", actionID[14..]), "view-action-keeps-task-identity");
                    foreach (string stopID in surface.Regions().Where(x => x.name.StartsWith("monitorStop:", StringComparison.Ordinal)).Select(x => x.name).ToArray())
                    {
                        surface.ActivateActionAsync(stopID, false).GetAwaiter().GetResult();
                        Check(commands.Last() == ("monitorStop", stopID[12..]), "cancel-action-keeps-own-task-identity-" + stopID);
                    }
                    surface.ActivateActionAsync("contentMonitor", false).GetAwaiter().GetResult();
                    Check(commands.Last() == ("content", "monitor"), "monitor-tab-command-route");
                    var cleanupPeer = peer.GetChildren().Single(x => x.GetAutomationId() == "monitorClearEnded");
                    int beforeCleanup = commands.Count;
                    surface.ActivateActionAsync("monitorClearEnded", false).GetAwaiter().GetResult();
                    Check(!cleanupPeer.IsEnabled() && commands.Count == beforeCleanup && !surface.KeyboardOrder().Contains("monitorClearEnded"), "no-ended-results-disables-cleanup-for-pointer-keyboard-uia");
                    state.O("monitor").A("watches").Rows().First()["active"] = false; surface.Update(state);
                    string endedStopID = "monitorStop:" + state.O("monitor").A("watches").Rows().First().S("id");
                    Check(!surface.ActionEnabled(endedStopID) && !surface.KeyboardOrder().Contains(endedStopID) &&
                        surface.VisibleTextBounds.Any(x => x.Text == "监控已结束"), "ended-monitor-has-readable-disabled-cancel-state");
                    Check(cleanupPeer.IsEnabled() && surface.KeyboardOrder().Contains("monitorClearEnded"), "ended-result-enables-direct-cleanup");
                    surface.ActivateActionAsync("monitorClearEnded", false).GetAwaiter().GetResult();
                    Check(commands.Last() == ("monitorClearEnded", null), "cleanup-routes-explicit-list-command");
                    state.O("monitor")["error"] = "合成监控存储错误"; surface.Update(state);
                    Check(!cleanupPeer.IsEnabled(), "damaged-monitor-state-disables-cleanup");
                    state.O("monitor")["error"] = ""; state.O("monitor").A("watches").Rows().First()["active"] = true; surface.Update(state);
                    state.O("settings").O("floating")["quotaContent"] = "budget"; state.O("settings").O("floating")["budgetID"] = "daily";
                    surface.Update(state);
                    Check(surface.BudgetQuota && CapsuleEdgeDisplay.From(state).Name == CapsuleBudgetDisplay.From(state).Name, "monitor-retains-last-budget-ring-and-edge-context");
                    CheckHeader($"budget-context-{light}-{dpi}");
                    Check(surface.VisibleTextBounds.All(x => x.Text != CapsuleBudgetDisplay.From(state).Name && x.Text != "剩余" && x.Text != "预算剩余"), "monitor-expanded-never-reuses-budget-name-or-battery-label");
                    surface.Expansion = 0; surface.Redraw();
                    Check(surface.VisibleTextBounds.Any(x => x.Text == CapsuleBudgetDisplay.From(state).Name) && surface.VisibleTextBounds.Any(x => x.Text == "余量"), "monitor-compact-still-displays-last-budget-context");
                    var overdrawn = state.Copy(); SetUnread(overdrawn, 137);
                    var overdrawnBudget = overdrawn.O("budgets").A("summaries").Rows().First();
                    overdrawn.O("settings").O("floating")["budgetID"] = overdrawnBudget.S("id");
                    overdrawnBudget["kind"] = "money"; overdrawnBudget["currency"] = "USD"; overdrawnBudget["remaining"] = -900230000;
                    overdrawnBudget["remainingFraction"] = -.9; overdrawnBudget["status"] = "exceeded";
                    surface.Update(overdrawn);
                    const string fullAmount = "$−900.23M";
                    var amountBounds = surface.VisibleTextBounds.Single(x => x.Text == fullAmount).Bounds;
                    var sourceBounds = surface.VisibleTextBounds.Single(x => x.Text == "余量").Bounds;
                    var actualDrawing = ((DrawingVisual)VisualTreeHelper.GetChild(surface, 0)).Drawing;
                    string drawn = DrawnCharacters(actualDrawing);
                    Check(drawn.Contains(fullAmount, StringComparison.Ordinal) && drawn.Contains("99+", StringComparison.Ordinal)
                        && !amountBounds.IntersectsWith(sourceBounds) && CapsuleMorph.FullyContainsRounded(amountBounds, surface.DrawingBounds, surface.CurrentRadius, 0),
                        "long-negative-budget-and-99-plus-retain-every-digit-and-source-" + light + "-" + dpi);
                    if (directory != null && dpi == 2)
                        Save(Bitmap(root, 360, 434, dpi), "monitor-negative-budget-99-plus-" + (light ? "light" : "dark") + ".png");
                    surface.Update(state);
                    surface.Expansion = 1;
                    state.O("settings").O("floating")["quotaContent"] = "usage";
                    if (directory != null && dpi is 1 or 2)
                    {
                        state.O("monitor").A("watches").Rows().First()["status"] = "running";
                        state.O("monitor").O("summary")["status"] = "waiting";
                        SetUnread(state, 2);
                        surface.CompactBounds = new Rect(272, 12, 76, 76); surface.Update(state);
                        Save(Bitmap(root, 360, 434, dpi), "monitor-panel-" + (light ? "light" : "dark") + "-" + (dpi * 100).ToString(CultureInfo.InvariantCulture) + ".png");
                    }
                    state.O("monitor")["watches"] = new JsonArray(); state.O("monitor").O("summary")["status"] = "";
                    surface.Update(state);
                    Check(surface.Regions().All(x => !x.name.StartsWith("monitorDetail:", StringComparison.Ordinal) && !x.name.StartsWith("monitorStop:", StringComparison.Ordinal)), "empty-monitor-removes-task-actions");
                    Check(surface.Regions().Single(x => x.name == "monitorManage").label == "查看全部任务与消息", "cleared-results-keep-message-history-reachable");
                    state.O("monitor")["messages"] = new JsonArray(); surface.Update(state);
                    Check(surface.Regions().Single(x => x.name == "monitorManage").label == "选择正在执行的任务", "empty-monitor-without-history-offers-selection");
                    Check(surface.VisibleTextBounds.All(x => CapsuleMorph.FullyContainsRounded(x.Bounds, surface.DrawingBounds, surface.CurrentRadius, 0)), "empty-content-fits");
                    foreach (string scenario in new[] { "empty", "source-unknown", "storage-error", "long-count", "completed", "focus-is-ended" })
                    {
                        var scenarioState = Fixture(light); var monitor = scenarioState.O("monitor");
                        foreach (var task in monitor.A("watches").Rows()) task["status"] = "running";
                        SetUnread(scenarioState, 3);
                        if (scenario == "empty") { monitor["watches"] = new JsonArray(); SetUnread(scenarioState, 0); }
                        else if (scenario == "source-unknown") monitor.O("sourceStatus")["status"] = "unavailable";
                        else if (scenario == "storage-error") monitor["error"] = "合成监控存储损坏，请检查重试";
                        else if (scenario == "long-count")
                        {
                            var longTask = monitor.A("watches").Rows().First().Copy();
                            longTask["title"] = "超长项目名与任务说明 / " + string.Concat(Enumerable.Repeat("VeryLongTaskIdentifier_中文步骤与确认事项 ", 12));
                            monitor["watches"] = J.Array(Enumerable.Range(0, 123).Select(i => { var row = longTask.Copy(); row["id"] = "many-task-" + i; return row; }));
                            SetUnread(scenarioState, 137);
                        }
                        else if (scenario == "completed") foreach (var task in monitor.A("watches").Rows()) { task["status"] = "completed"; task["active"] = false; }
                        else if (scenario == "focus-is-ended")
                        {
                            var ended = monitor.A("watches").Rows().First(); ended["status"] = "completed"; ended["active"] = false;
                            monitor.O("settings")["focusID"] = ended.S("id"); monitor.O("summary")["status"] = "completed";
                        }
                        surface.Expansion = 1; surface.Update(scenarioState); frames++;
                        CheckHeader($"{scenario}-{light}-{dpi}");
                        foreach (var ink in surface.VisibleTextBounds)
                        {
                            labels++;
                            Check(CapsuleMorph.FullyContainsRounded(ink.Bounds, surface.DrawingBounds, surface.CurrentRadius, 0), $"scenario-text-clipped-{scenario}-{light}-{dpi}-{ink.Text}");
                        }
                        var cardControls = surface.Regions().Where(x => x.name.StartsWith("monitorDetail:", StringComparison.Ordinal) || x.name.StartsWith("monitorStop:", StringComparison.Ordinal));
                        foreach (var control in cardControls)
                            Check(surface.VisibleTextBounds.Where(x => x.Text is not ("查看" or "取消监控" or "监控已结束")).All(x => Rect.Intersect(x.Bounds, control.bounds).IsEmpty), $"scenario-card-action-covers-content-{scenario}-{light}-{dpi}-{control.name}");
                        if (scenario == "focus-is-ended") Check(surface.VisibleTextBounds.Any(x => x.Text == "1 项正在执行"), "expanded-header-remains-global-when-compact-focus-ended");
                        if (directory != null && dpi is 1 or 2)
                            Save(Bitmap(root, 360, 434, dpi), $"monitor-header-{scenario}-{(light ? "light" : "dark")}-{(dpi * 100).ToString(CultureInfo.InvariantCulture)}.png");
                    }
                    root.Child = null;

                    void CheckHeader(string context)
                    {
                        headers++;
                        var header = TaskMonitorHeaderDisplay.From(surface.State);
                        var top = new Rect(surface.PanelBounds!.Value.X, surface.PanelBounds.Value.Y, 336, 52);
                        var messageArea = surface.Regions().Single(x => x.name == "monitorMessages").bounds;
                        var headerActions = surface.Regions().Where(x => x.name is not ("context" or "monitorMessages"));
                        Check(headerActions.All(x => Rect.Intersect(x.bounds, messageArea).IsEmpty), "unread-area-does-not-overlap-another-action-" + context);
                        foreach (string label in new[] { header.Title, header.Subtitle, header.UnreadText })
                        {
                            var matching = surface.VisibleTextBounds.Where(x => x.Text == label && top.Contains(x.Bounds)).ToArray();
                            Check(matching.Length == 1, "header-text-present-once-in-own-panel-" + context + "-" + label);
                            if (matching.Length != 1) continue;
                            var ink = matching[0].Bounds;
                            if (label == header.UnreadText)
                            {
                                Check(messageArea.Contains(ink) && Math.Abs(ink.X + ink.Width / 2 - messageArea.X - messageArea.Width / 2) < .02 &&
                                    Math.Abs(ink.Y + ink.Height / 2 - messageArea.Y - messageArea.Height / 2) < .02, "unread-label-is-ink-centered-" + context); actions++;
                            }
                            else Check(ink.Right <= messageArea.Left - 6, "header-title-subtitle-keep-gap-from-unread-" + context);
                        }
                        Check(surface.VisibleTextBounds.Where(x => top.IntersectsWith(x.Bounds)).All(x => x.Text == header.Title || x.Text == header.Subtitle || x.Text == header.UnreadText), "expanded-monitor-header-has-no-quota-label-value-or-percent-" + context);
                    }
                }
                finally { window.Close(); }
                foreach (var edge in new[] { CapsuleEdge.Left, CapsuleEdge.Right, CapsuleEdge.Top, CapsuleEdge.Bottom })
                {
                    Size size = edge is CapsuleEdge.Left or CapsuleEdge.Right ? new(44, 96) : new(124, 28);
                    var seen = new HashSet<string>();
                    foreach (string status in TaskMonitorGlyph.States)
                    {
                        state.O("monitor").O("summary")["status"] = status;
                        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) CapsuleEdgeIndicator.Draw(dc, size, edge, state, dpi);
                        var bitmap = Bitmap(visual, size.Width, size.Height, dpi);
                        Check(seen.Add(Convert.ToHexString(Pixels(bitmap))), $"side-sign-cache-failed-to-update-{edge}-{status}-{dpi}"); frames++;
                    }
                }
                if (directory != null && dpi == 1) CompactSheet(light);
            }
            return J.Obj(("success", failures.Count == 0), ("frames", frames), ("glyphs", symbols), ("textBounds", labels), ("centeredActions", actions), ("monitorHeaders", headers), ("messageActions", messageActions), ("failures", failures), ("images", images));
        }
        finally { app.ShutdownMode = shutdown; }

        void Save(BitmapSource bitmap, string filename)
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(directory!, filename)); encoder.Save(file); images.Add(filename);
        }
        void CompactSheet(bool light)
        {
            var sheet = new DrawingVisual(); using (var dc = sheet.RenderOpen())
            {
                dc.DrawRectangle(light ? Brushes.White : new SolidColorBrush(Color.FromRgb(12, 15, 14)), null, new(0, 0, 1120, 306));
                for (int index = 0; index < TaskMonitorGlyph.States.Length; index++)
                {
                    string status = TaskMonitorGlyph.States[index]; var state = Fixture(light); state.O("monitor").O("summary")["status"] = status;
                    double left = index * 160;
                    dc.DrawText(new FormattedText(TaskMonitorGlyph.Label(status), CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Segoe UI, Microsoft YaHei UI"), 11,
                        light ? Brushes.Black : Brushes.White, 1), new(left + 8, 8));
                    var window = new CapsuleWindow((_, _) => Task.CompletedTask, readPointer: () => null) { ShowActivated = false, Topmost = false };
                    try
                    {
                        var surface = window.Surface; window.Content = null; surface.HostSize = new Size(76, 76);
                        surface.CompactBounds = new Rect(0, 0, 76, 76); surface.PanelBounds = new Rect(0, 0, 336, 410); surface.Expansion = 0;
                        surface.Measure(new Size(76, 76)); surface.Arrange(new Rect(0, 0, 76, 76)); surface.Update(state);
                        dc.DrawImage(Bitmap(surface, 76, 76, 2), new(left + 42, 34, 76, 76));
                    }
                    finally { window.Close(); }
                    foreach (var entry in new[] { (CapsuleEdge.Left, new Rect(left + 27, 122, 44, 96)), (CapsuleEdge.Right, new Rect(left + 89, 122, 44, 96)),
                        (CapsuleEdge.Top, new Rect(left + 18, 224, 124, 28)), (CapsuleEdge.Bottom, new Rect(left + 18, 268, 124, 28)) })
                    {
                        dc.PushTransform(new TranslateTransform(entry.Item2.X, entry.Item2.Y)); CapsuleEdgeIndicator.Draw(dc, entry.Item2.Size, entry.Item1, state, 2); dc.Pop();
                    }
                }
            }
            Save(Bitmap(sheet, 1120, 306, 2), "monitor-compact-" + (light ? "light" : "dark") + ".png");
        }
    }
    private static string DrawnCharacters(Drawing? drawing) => drawing switch
    {
        GlyphRunDrawing glyph when glyph.GlyphRun.Characters is not null => new string(glyph.GlyphRun.Characters.ToArray()),
        DrawingGroup group => string.Concat(group.Children.Select(DrawnCharacters)),
        _ => ""
    };
    private static JsonObject Fixture(bool light)
    {
        var state = DemoData.State(); var floating = state.O("settings").O("floating");
        floating["theme"] = light ? "light" : "dark"; floating["content"] = "monitor"; floating["quotaContent"] = "usage";
        floating["pinned"] = false; floating["keepExpanded"] = false; floating["edgeAutoHide"] = false;
        state["monitor"] = J.Obj(("summary", J.Obj(("active", 2), ("attention", 1), ("unread", 2), ("status", "running"))),
            ("settings", J.Obj(("focusID", ""))), ("sourceStatus", J.Obj(("status", "available"))), ("capabilities", J.Obj(("waiting", true))),
            ("watches", new JsonArray(
                J.Obj(("id", "task-alpha"), ("turnID", "turn-a"), ("title", "检查 Windows 浮窗中的长任务名称与按钮文字是否完整居中 / ButtonAlignmentReview"), ("project", "codex-usage · 很长的项目目录与中文说明"), ("status", "running"), ("mode", "once"), ("active", true), ("updatedAt", 1789272900)),
                J.Obj(("id", "task-beta"), ("turnID", "turn-b"), ("title", "核对监控的数据来源与恢复流程"), ("project", "codex-usage"), ("status", "waiting"), ("mode", "each"), ("active", true), ("updatedAt", 1789272800)))),
            ("messages", new JsonArray(J.Obj(("id", "message-b"), ("taskID", "task-beta"), ("read", false)))));
        return state;
    }
    private static void SetUnread(JsonObject state, int count)
    {
        state.O("monitor")["messages"] = J.Array(Enumerable.Range(0, count).Select(i => J.Obj(("id", "message-" + i), ("taskID", "task-beta"), ("read", false))));
        state.O("monitor").O("summary")["unread"] = count;
    }
    private static RenderTargetBitmap Bitmap(Visual visual, double width, double height, double dpi)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * dpi), (int)Math.Ceiling(height * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
    private static byte[] Pixels(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0); return pixels;
    }
}
