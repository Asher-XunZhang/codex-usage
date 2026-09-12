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
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexUsage;

/// <summary>Uses the actual WPF drawing surface; fixtures never read a user's tasks or activate a window.</summary>
internal static class TaskMonitorFloatingTests
{
    public static JsonObject Run(string? directory = null)
    {
        var app = Application.Current; var shutdown = app.ShutdownMode;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var failures = new JsonArray(); var images = new JsonArray();
        int frames = 0, labels = 0, symbols = 0, actions = 0;
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
                            foreach (double progress in new[] { 0d, .08, .12, .5, .86, .90, 1d })
                            {
                                surface.Expansion = progress;
                                var before = surface.DrawingBounds;
                                state.O("monitor").O("summary")["status"] = status;
                                state.O("monitor").O("summary")["unread"] = index % 2 == 0 ? 100 : 0;
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
                                    Check(surface.VisibleTextBounds.Count == 1, "middle-morph-only-primary-quota-no-compact-ghost");
                                if (progress == 1)
                                {
                                    var controls = surface.Regions().Where(x => x.name is not ("details" or "context")).ToArray();
                                    var lastCard = CapsuleSurface.MonitorTaskBounds(1); lastCard.Offset(surface.PanelBounds.Value.X, surface.PanelBounds.Value.Y);
                                    Check(controls.Single(x => x.name == "monitorManage").bounds.Top - lastCard.Bottom >= 6, "last-task-card-keeps-gap-before-view-all");
                                    Check(controls.All(x => surface.KeyboardOrder().Contains(x.name)), "all-monitor-actions-reachable-by-keyboard");
                                    foreach (var control in controls)
                                        foreach (var other in controls.Where(x => x.name != control.name))
                                        { var intersection = Rect.Intersect(control.bounds, other.bounds); Check(intersection.IsEmpty || intersection.Width * intersection.Height == 0, "monitor-actions-overlap-" + control.name + "-" + other.name); }
                                    foreach (var item in new[] { ("contentUsage", "用量"), ("contentBudget", "预算"), ("contentMonitor", index % 2 == 0 ? "监控 9+" : "监控"),
                                        ("main", "打开主面板"), ("collapse", "收起"), ("monitorManage", "查看全部任务与消息"), ("monitorCheck", "检查连接") })
                                    {
                                        var area = controls.Single(x => x.name == item.Item1).bounds;
                                        var ink = surface.VisibleTextBounds.Single(x => x.Text == item.Item2).Bounds;
                                        Check(area.Contains(ink) && Math.Abs(ink.X + ink.Width / 2 - area.X - area.Width / 2) < .02 && Math.Abs(ink.Y + ink.Height / 2 - area.Y - area.Height / 2) < .02,
                                            "button-not-ink-centered-" + item.Item1); actions++;
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
                    Check(AutomationProperties.GetHelpText(surface).Contains(TaskMonitorGlyph.Label("idle"), StringComparison.Ordinal), "uia-help-includes-current-state");
                    string actionID = surface.Regions().First(x => x.name.StartsWith("monitorDetail:", StringComparison.Ordinal)).name;
                    surface.ActivateActionAsync(actionID, false).GetAwaiter().GetResult();
                    Check(commands.Last() == ("monitorDetail", actionID[14..]), "view-action-keeps-task-identity");
                    surface.ActivateActionAsync("contentMonitor", false).GetAwaiter().GetResult();
                    Check(commands.Last() == ("content", "monitor"), "monitor-tab-command-route");
                    state.O("settings").O("floating")["quotaContent"] = "budget"; state.O("settings").O("floating")["budgetID"] = "daily";
                    surface.Update(state);
                    Check(surface.BudgetQuota && CapsuleEdgeDisplay.From(state).Name == CapsuleBudgetDisplay.From(state).Name, "monitor-retains-last-budget-ring-and-edge-context");
                    state.O("settings").O("floating")["quotaContent"] = "usage";
                    if (directory != null && dpi is 1 or 2)
                    {
                        state.O("monitor").A("watches").Rows().First()["status"] = "running";
                        state.O("monitor").O("summary")["status"] = "waiting";
                        state.O("monitor").O("summary")["unread"] = 2;
                        surface.CompactBounds = new Rect(272, 12, 76, 76); surface.Update(state);
                        Save(Bitmap(root, 360, 434, dpi), "monitor-panel-" + (light ? "light" : "dark") + "-" + (dpi * 100).ToString(CultureInfo.InvariantCulture) + ".png");
                    }
                    state.O("monitor")["watches"] = new JsonArray(); state.O("monitor").O("summary")["status"] = "";
                    surface.Update(state);
                    Check(surface.Regions().All(x => !x.name.StartsWith("monitorDetail:", StringComparison.Ordinal)), "empty-monitor-removes-task-actions");
                    Check(surface.Regions().Single(x => x.name == "monitorManage").label == "选择正在执行的任务", "empty-monitor-offers-selection");
                    Check(surface.VisibleTextBounds.All(x => CapsuleMorph.FullyContainsRounded(x.Bounds, surface.DrawingBounds, surface.CurrentRadius, 0)), "empty-content-fits");
                    root.Child = null;
                }
                finally { window.Close(); }
                foreach (var edge in new[] { CapsuleEdge.Left, CapsuleEdge.Right, CapsuleEdge.Top, CapsuleEdge.Bottom })
                {
                    Size size = edge is CapsuleEdge.Left or CapsuleEdge.Right ? new(28, 72) : new(76, 28);
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
            return J.Obj(("success", failures.Count == 0), ("frames", frames), ("glyphs", symbols), ("textBounds", labels), ("centeredActions", actions), ("failures", failures), ("images", images));
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
                dc.DrawRectangle(light ? Brushes.White : new SolidColorBrush(Color.FromRgb(12, 15, 14)), null, new(0, 0, 784, 284));
                for (int index = 0; index < TaskMonitorGlyph.States.Length; index++)
                {
                    string status = TaskMonitorGlyph.States[index]; var state = Fixture(light); state.O("monitor").O("summary")["status"] = status;
                    double left = index * 112;
                    dc.DrawText(new FormattedText(TaskMonitorGlyph.Label(status), CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Segoe UI, Microsoft YaHei UI"), 11,
                        light ? Brushes.Black : Brushes.White, 1), new(left + 8, 8));
                    var window = new CapsuleWindow((_, _) => Task.CompletedTask, readPointer: () => null) { ShowActivated = false, Topmost = false };
                    try
                    {
                        var surface = window.Surface; window.Content = null; surface.HostSize = new Size(76, 76);
                        surface.CompactBounds = new Rect(0, 0, 76, 76); surface.PanelBounds = new Rect(0, 0, 336, 410); surface.Expansion = 0;
                        surface.Measure(new Size(76, 76)); surface.Arrange(new Rect(0, 0, 76, 76)); surface.Update(state);
                        dc.DrawImage(Bitmap(surface, 76, 76, 2), new(left + 18, 34, 76, 76));
                    }
                    finally { window.Close(); }
                    foreach (var entry in new[] { (CapsuleEdge.Left, new Rect(left + 19, 122, 28, 72)), (CapsuleEdge.Right, new Rect(left + 65, 122, 28, 72)),
                        (CapsuleEdge.Top, new Rect(left + 18, 210, 76, 28)), (CapsuleEdge.Bottom, new Rect(left + 18, 248, 76, 28)) })
                    {
                        dc.PushTransform(new TranslateTransform(entry.Item2.X, entry.Item2.Y)); CapsuleEdgeIndicator.Draw(dc, entry.Item2.Size, entry.Item1, state, 2); dc.Pop();
                    }
                }
            }
            Save(Bitmap(sheet, 784, 284, 2), "monitor-compact-" + (light ? "light" : "dark") + ".png");
        }
    }
    private static JsonObject Fixture(bool light)
    {
        var state = DemoData.State(); var floating = state.O("settings").O("floating");
        floating["theme"] = light ? "light" : "dark"; floating["content"] = "monitor"; floating["quotaContent"] = "usage";
        floating["pinned"] = false; floating["keepExpanded"] = false; floating["edgeAutoHide"] = false;
        state["monitor"] = J.Obj(("summary", J.Obj(("active", 2), ("attention", 1), ("unread", 2), ("status", "running"))),
            ("settings", J.Obj(("focusID", ""))), ("sourceStatus", J.Obj(("status", "available"))),
            ("watches", new JsonArray(
                J.Obj(("id", "task-alpha"), ("turnID", "turn-a"), ("title", "检查 Windows 浮窗中的长任务名称与按钮文字是否完整居中 / ButtonAlignmentReview"), ("project", "codex-usage · 很长的项目目录与中文说明"), ("status", "running"), ("mode", "once"), ("active", true), ("updatedAt", 1789272900)),
                J.Obj(("id", "task-beta"), ("turnID", "turn-b"), ("title", "核对监控的数据来源与恢复流程"), ("project", "codex-usage"), ("status", "waiting"), ("mode", "each"), ("active", true), ("updatedAt", 1789272800)))),
            ("messages", new JsonArray(J.Obj(("id", "message-b"), ("taskID", "task-beta"), ("read", false)))));
        return state;
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
