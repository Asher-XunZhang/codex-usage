using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexUsage;

internal static class CapsuleButtonLayoutTests
{
    internal static JsonObject Run(string? directory = null)
    {
        var app = Application.Current; var shutdown = app.ShutdownMode; app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var failures = new JsonArray(); var measurements = new JsonArray(); var images = new JsonArray();
        int frames = 0, labels = 0, morphSamples = 0; double maxHorizontalError = 0, maxVerticalError = 0;
        if (directory != null) Directory.CreateDirectory(directory);
        try
        {
            foreach (double dpi in new[] { 1d, 1.25, 1.5, 2, 3 }) foreach (bool light in new[] { false, true })
                foreach (string scenario in new[] { "usage", "filters", "budget", "paused" }) foreach (bool retained in new[] { false, true })
                {
                    bool budget = scenario is "budget" or "paused";
                    var state = DemoData.State(); var floating = state.O("settings").O("floating");
                    floating["theme"] = light ? "light" : "dark"; floating["content"] = budget ? "budget" : "usage";
                    floating["budgetID"] = "daily"; floating["pinned"] = false; floating["keepExpanded"] = retained; floating["days"] = "30";
                    state["busy"] = retained;
                    var selected = state.O("budgets").A("summaries").Rows().First(x => x.S("id") == "daily");
                    selected["paused"] = scenario == "paused"; selected["name"] = "较长预算 Budget 01234567890123456789";
                    if (scenario == "filters") { floating["model"] = "很长的模型 model-01234567890123456789"; floating["task"] = "很长的任务 task-01234567890123456789"; }
                    var window = new CapsuleWindow((_, _) => Task.CompletedTask, readPointer: () => null) { ShowActivated = false, Topmost = false };
                    try
                    {
                        var surface = window.Surface; window.Content = null;
                        var root = new Border { Child = surface, Width = 360, Height = 434 };
                        VisualTreeHelper.SetRootDpi(root, new DpiScale(dpi, dpi));
                        window.Update(state); if (scenario == "filters") surface.ToggleFilters();
                        surface.CompactBounds = new Rect(272, 12, 76, 76); surface.PanelBounds = new Rect(12, 12, 336, 410);
                        surface.HostSize = new Size(360, 434); surface.CaptureTextBounds = true; surface.Expansion = 1;
                        root.Measure(new Size(360, 434)); root.Arrange(new Rect(0, 0, 360, 434)); root.UpdateLayout(); surface.Redraw();
                        frames++; string id = $"{dpi * 100:0}-{(light ? "light" : "dark")}-{scenario}-{retained}";
                        var controls = surface.Regions().ToDictionary(x => x.name, x => x.bounds);
                        var expected = new List<(string Action, string Text, bool Center)> {
                            ("contentUsage", "用量", true), ("contentBudget", "预算", true), ("keepExpanded", (retained ? "✓ " : "") + "保持展开", true),
                            ("more", "更多 ···", true), ("refresh", retained ? "更新中" : "刷新", true), ("main", "打开主面板", true), ("collapse", "收起", true) };
                        if (budget)
                        {
                            expected.Add(("view-budget", "查看预算 ↗", true)); expected.Add(("budget", selected.S("name"), false));
                            expected.Add((scenario == "paused" ? "budgetResume" : "budgetPause", scenario == "paused" ? "恢复提醒" : "暂停提醒 ⌄", true));
                        }
                        else
                        {
                            expected.Add(("period", "30天 ⌄", true)); expected.Add(("filters", scenario == "filters" ? "收拢 ·" : "筛选 ⌄", true));
                            if (scenario == "filters")
                            {
                                expected.Add(("model", floating.S("model") + " ⌄", false));
                                expected.Add(("task", state.O("filtered").O("filters").O("selected_task").S("label", floating.S("task")) + " ⌄", false));
                                expected.Add(("filtersReset", "清除条件", true));
                            }
                        }
                        foreach (var item in expected)
                        {
                            var text = surface.VisibleTextBounds.Single(x => x.Text == item.Text).Bounds; var control = controls[item.Action]; labels++;
                            double horizontal = Math.Abs(text.X + text.Width / 2 - control.X - control.Width / 2);
                            double vertical = Math.Abs(text.Y + text.Height / 2 - control.Y - control.Height / 2);
                            if (item.Center) maxHorizontalError = Math.Max(maxHorizontalError, horizontal);
                            maxVerticalError = Math.Max(maxVerticalError, vertical);
                            bool centered = vertical < .02 && (item.Center ? horizontal < .02 : Math.Abs(text.Left - control.Left - (item.Action == "budget" ? 10 : 8)) < .02);
                            if (!centered || !control.Contains(text)) failures.Add(id + ":" + item.Action + $" dx={horizontal:F3}, dy={vertical:F3}, contained={control.Contains(text)}");
                            if (dpi == 1 && !light && !retained) measurements.Add(J.Obj(("scenario", scenario), ("action", item.Action), ("dx", horizontal), ("dy", vertical), ("centered", centered)));
                        }
                        if (budget)
                        {
                            var arrow = surface.VisibleTextBounds.Single(x => x.Text == "⌄").Bounds; var control = controls["budget"];
                            if (Math.Abs(arrow.X + arrow.Width / 2 - control.Right + 14) > .02 || Math.Abs(arrow.Y + arrow.Height / 2 - control.Y - control.Height / 2) > .02)
                                failures.Add(id + ":selector-arrow-not-centered");
                        }
                        var update = controls["updateStatus"];
                        foreach (var (prefix, row) in new[] { ("本地 ", 0), ("账号 ", 1) })
                        {
                            var stamp = surface.VisibleTextBounds.Single(x => x.Text.StartsWith(prefix, StringComparison.Ordinal)).Bounds;
                            if (!update.Contains(stamp) || Math.Abs(stamp.Left - update.Left - 4) > .02 || Math.Abs(stamp.Y + stamp.Height / 2 - update.Top - update.Height * (row + .5) / 2) > .02)
                                failures.Add(id + ":source-status-row-not-aligned-" + row);
                        }
                        var cold = surface.VisibleTextBounds.ToArray(); surface.Redraw();
                        if (!cold.SequenceEqual(surface.VisibleTextBounds)) failures.Add(id + ":cached-text-bounds-changed");
                        // Build detail text from a cold cache during the morph, when
                        // interactive hit regions intentionally expose only the grip.
                        foreach (var compact in new[] { new Rect(272, 12, 76, 76), new Rect(12, 12, 76, 76), new Rect(272, 346, 76, 76), new Rect(12, 346, 76, 76) })
                        {
                            surface.CompactBounds = compact; surface.Expansion = .9; surface.Update(state); morphSamples++;
                            if (surface.VisibleTextBounds.Any(x => !CapsuleMorph.FullyContainsRounded(x.Bounds, surface.DrawingBounds, surface.CurrentRadius, 0)))
                                failures.Add(id + ":cold-morph-text-outside-body-" + compact.TopLeft);
                            surface.Expansion = 1; surface.Redraw();
                            if (!cold.SequenceEqual(surface.VisibleTextBounds)) failures.Add(id + ":cold-morph-cache-changed-final-alignment-" + compact.TopLeft);
                        }
                        surface.CompactBounds = new Rect(272, 12, 76, 76); surface.Redraw();
                        if (window.IsVisible || window.IsActive) failures.Add(id + ":fixture-activated-window");
                        if (directory != null && dpi == 2 && !retained && scenario is "usage" or "budget")
                        {
                            var bitmap = new RenderTargetBitmap(720, 868, 192, 192, PixelFormats.Pbgra32); bitmap.Render(root);
                            string file = "capsule-buttons-" + scenario + "-" + (light ? "light" : "dark") + ".png";
                            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(directory, file)); encoder.Save(stream); images.Add(file);
                        }
                    }
                    finally { window.Close(); }
                }
            return J.Obj(("success", failures.Count == 0), ("frames", frames), ("labels", labels), ("coldMorphSamples", morphSamples), ("maxHorizontalErrorDip", maxHorizontalError), ("maxVerticalErrorDip", maxVerticalError),
                ("failures", failures), ("measurements", measurements), ("images", images), ("scope", "Unshown production surface, actual glyph ink against action bounds, five DPI scales, themes, usage/filter/budget states and cached rendering."));
        }
        finally { app.ShutdownMode = shutdown; }
    }
}
