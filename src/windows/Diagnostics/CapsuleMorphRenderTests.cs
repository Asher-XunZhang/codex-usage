using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexUsage;

/// <summary>Offline rendering of the production drawing surface at controlled DPI and raw morph progress.</summary>
internal static class CapsuleMorphRenderTests
{
    public static JsonObject Run(string? directory = null)
    {
        var app = Application.Current ?? throw new InvalidOperationException("Morph rendering requires a WPF dispatcher.");
        if (!app.Dispatcher.CheckAccess()) throw new InvalidOperationException("Morph rendering must run on the WPF dispatcher.");
        if (directory is not null) Directory.CreateDirectory(directory);
        var shutdown = app.ShutdownMode;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var checks = new JsonArray();
        var failures = new JsonArray();
        var images = new JsonArray();
        int samples = 0, textBoxes = 0;
        var steps = Enumerable.Range(0, 101).Select(n => n / 100d).Concat(new[] { .001, .119, .121, .859, .861, .999 }).Distinct().Order().ToArray();
        int[] imageSteps = [0, 10, 25, 50, 75, 90, 100];
        var scenarios = new[] { (Id: "usage-100-stale", Budget: false, Unknown: false), (Id: "usage-unknown", Budget: false, Unknown: true),
            (Id: "budget-100-stale", Budget: true, Unknown: false), (Id: "budget-unknown", Budget: true, Unknown: true) };
        var panel = new Rect(12, 12, 336, 410);
        var directions = new[] { (Id: "left-down", Circle: new Rect(272, 12, 76, 76)), (Id: "right-down", Circle: new Rect(12, 12, 76, 76)),
            (Id: "left-up", Circle: new Rect(272, 346, 76, 76)), (Id: "right-up", Circle: new Rect(12, 346, 76, 76)) };
        const double width = 360, height = 434, cellWidth = 368, cellHeight = 462;

        void Check(bool success, string id, string detail)
        {
            checks.Add(J.Obj(("id", id), ("success", success), ("detail", detail)));
            if (!success) failures.Add(id + ": " + detail);
        }
        JsonObject State(bool light, bool budget, bool unknown)
        {
            var state = DemoData.State();
            state.O("settings")["refresh"] = 0;
            var floating = state.O("settings").O("floating");
            floating["theme"] = light ? "light" : "dark"; floating["pinned"] = false;
            floating["content"] = budget ? "budget" : "usage"; floating["budgetID"] = "daily";
            floating["edgeAutoHide"] = false;
            state.O("quota")["capsuleFraction"] = unknown ? null : JsonValue.Create(1d);
            state.O("quota")["capsuleName"] = "周余";
            state.O("quota")["windows"] = unknown ? new JsonArray() : new JsonArray(J.Obj(("duration_minutes", 10080), ("remaining", 100), ("label", "周")));
            state.O("quota")["stale"] = true;
            state.O("quota")["detail"] = "合成额度数据 · 状态尚未更新";
            state["status"] = "合成形变验证 · 未读取账号或本机记录";
            var summary = state.O("budgets").A("summaries").Rows().First(x => x.S("id") == "daily");
            summary["name"] = "包含中文与很长名称的每日预算";
            summary["status"] = unknown ? "unknown" : "healthy";
            summary["dataStatus"] = "stale";
            summary["used"] = 0;
            summary["remaining"] = unknown ? null : JsonValue.Create(20000000d);
            summary["remainingFraction"] = unknown ? null : JsonValue.Create(1d);
            return state;
        }

        try
        {
            foreach (bool light in new[] { false, true })
                foreach (double dpi in new[] { 1d, 2d })
                    foreach (var direction in directions)
                    {
                        string group = (light ? "light" : "dark") + "-" + (int)(dpi * 100) + "-" + direction.Id;
                        var sheet = new DrawingVisual();
                        using (var sheetContext = sheet.RenderOpen())
                        {
                            sheetContext.DrawRectangle(light ? Brushes.White : new SolidColorBrush(Color.FromRgb(12, 15, 14)), null,
                                new Rect(0, 0, cellWidth * imageSteps.Length, cellHeight * scenarios.Length));
                            for (int row = 0; row < scenarios.Length; row++)
                            {
                                var scenario = scenarios[row];
                                var window = new CapsuleWindow((_, _) => Task.CompletedTask, readPointer: () => null) { ShowActivated = false, Topmost = false };
                                try
                                {
                                    var surface = window.Surface;
                                    window.Content = null;
                                    var root = new Border { Child = surface, Width = width, Height = height };
                                    VisualTreeHelper.SetRootDpi(root, new DpiScale(dpi, dpi));
                                    window.Update(State(light, scenario.Budget, scenario.Unknown));
                                    surface.CompactBounds = direction.Circle; surface.PanelBounds = panel;
                                    surface.HostSize = new Size(width, height); surface.CaptureTextBounds = true;
                                    root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
                                    string expected = scenario.Unknown ? "—*" : "100%*";
                                    var clipped = new List<string>();
                                    var duplicated = new List<double>();
                                    var premature = new List<double>();
                                    var cacheMismatch = new List<double>();
                                    foreach (double progress in steps)
                                    {
                                        surface.Expansion = progress; surface.Redraw();
                                        samples++;
                                        var ink = surface.VisibleTextBounds.ToArray();
                                        if (ink.Count(x => x.Text == expected) != 1) duplicated.Add(progress);
                                        if (progress >= .12 && progress <= .86 && ink.Length != 1) premature.Add(progress);
                                        foreach (var text in ink)
                                        {
                                            textBoxes++;
                                            if (!InsideRounded(text.Bounds, surface.DrawingBounds, surface.CurrentRadius) && clipped.Count < 12)
                                                clipped.Add($"p={progress:F3}, text={text.Text}, ink={text.Bounds}, shape={surface.DrawingBounds}, radius={surface.CurrentRadius:F3}");
                                        }
                                        // A warm DrawingGroup must expose the same real ink boxes as its first draw.
                                        if (progress is 0 or .9 or 1)
                                        {
                                            surface.Redraw();
                                            if (!ink.SequenceEqual(surface.VisibleTextBounds)) cacheMismatch.Add(progress);
                                        }
                                        int percent = (int)Math.Round(progress * 100);
                                        int column = Array.IndexOf(imageSteps, percent);
                                        if (directory is not null && column >= 0 && Math.Abs(progress - percent / 100d) < .000001)
                                        {
                                            var bitmap = new RenderTargetBitmap((int)(width * dpi), (int)(height * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
                                            bitmap.Render(root); bitmap.Freeze();
                                            double x = column * cellWidth, y = row * cellHeight;
                                            var label = new FormattedText(scenario.Id + " · " + percent + "%", CultureInfo.InvariantCulture,
                                                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, light ? Brushes.Black : Brushes.White, dpi);
                                            sheetContext.DrawText(label, new Point(x + 10, y + 6));
                                            sheetContext.DrawImage(bitmap, new Rect(x, y + 24, width, height));
                                        }
                                    }
                                    string id = group + "-" + scenario.Id;
                                    Check(clipped.Count == 0, id + "-ink-contained", clipped.Count == 0
                                        ? "Every alpha-positive glyph ink rectangle fits completely inside the rounded contour at all sampled progress values." : string.Join("; ", clipped));
                                    Check(duplicated.Count == 0, id + "-one-primary", duplicated.Count == 0
                                        ? "Exactly one complete primary percentage or unknown marker is visible at every progress value." : "Invalid primary count at p=" + string.Join(",", duplicated));
                                    Check(premature.Count == 0, id + "-middle-phase-only-primary", premature.Count == 0
                                        ? "After compact labels fade and before details appear, only the same primary run remains." : "Extra text at p=" + string.Join(",", premature));
                                    Check(cacheMismatch.Count == 0, id + "-cached-ink-matches", "Cold and warm production drawing caches expose identical ink diagnostics.");
                                    Check(VisualTreeHelper.GetDpi(surface).DpiScaleX == dpi, id + "-actual-dpi", "The production surface renders with the requested WPF DPI.");
                                    root.Child = null;
                                }
                                finally { window.Close(); }
                            }
                        }
                        if (directory is not null)
                        {
                            string filename = "morph-" + group + ".png";
                            var bitmap = new RenderTargetBitmap((int)(cellWidth * imageSteps.Length * dpi), (int)(cellHeight * scenarios.Length * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
                            bitmap.Render(sheet);
                            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            using var file = File.Create(Path.Combine(directory, filename)); encoder.Save(file);
                            images.Add(filename);
                        }
                    }
            return J.Obj(("success", failures.Count == 0), ("version", 1), ("checks", checks), ("failures", failures), ("images", images),
                ("samples", samples), ("textBoxes", textBoxes), ("conditions", "Offline production drawing surface, synthetic usage/budget data, light/dark, 100%/200% WPF DPI, four directions, 0–100% raw progress and fade boundaries. No native window is shown."));
        }
        finally { app.ShutdownMode = shutdown; }
    }

    // Independent containment arithmetic: a convex rounded rectangle contains an
    // ink rectangle iff it contains all four corners. Use the actual half-pixel
    // inset fill geometry, rather than the helper that positions production text.
    private static bool InsideRounded(Rect ink, Rect shape, double radius)
    {
        if (ink.IsEmpty || shape.IsEmpty) return false;
        shape.Inflate(-.5, -.5);
        double r = Math.Min(radius, Math.Min(shape.Width, shape.Height) / 2);
        bool Inside(Point p)
        {
            const double epsilon = .01;
            if (p.X < shape.Left - epsilon || p.X > shape.Right + epsilon || p.Y < shape.Top - epsilon || p.Y > shape.Bottom + epsilon) return false;
            double x = Math.Clamp(p.X, shape.Left + r, shape.Right - r), y = Math.Clamp(p.Y, shape.Top + r, shape.Bottom - r);
            return (p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y) <= r * r + epsilon;
        }
        return Inside(ink.TopLeft) && Inside(ink.TopRight) && Inside(ink.BottomLeft) && Inside(ink.BottomRight);
    }
}
