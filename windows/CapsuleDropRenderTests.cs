using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexUsage;

/// <summary>Offline production drawing at intermediate expanded-panel recovery positions.</summary>
internal static class CapsuleDropRenderTests
{
    internal static JsonObject Run(string? directory = null)
    {
        var app = Application.Current ?? throw new InvalidOperationException("Drop rendering requires a WPF dispatcher.");
        if (!app.Dispatcher.CheckAccess()) throw new InvalidOperationException("Drop rendering must run on the WPF dispatcher.");
        var shutdown = app.ShutdownMode; app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var checks = new JsonArray(); var failures = new JsonArray(); var images = new JsonArray();
        int frames = 0, labels = 0, regions = 0;
        if (directory != null) Directory.CreateDirectory(directory);
        void Check(bool okay, string id, string detail)
        {
            checks.Add(J.Obj(("id", id), ("success", okay), ("detail", detail)));
            if (!okay) failures.Add(id + ": " + detail);
        }
        bool Near(Rect first, Rect second) => Math.Abs(first.X - second.X) < .01 && Math.Abs(first.Y - second.Y) < .01 &&
            Math.Abs(first.Width - second.Width) < .01 && Math.Abs(first.Height - second.Height) < .01;
        Rect Relative(Rect box, Rect shape) { box.Offset(-shape.X, -shape.Y); return box; }
        var panel = new Rect(110, 52, 336, 410);
        const double width = 556, height = 514;
        try
        {
            foreach (string content in new[] { "usage", "budget", "monitor" })
                foreach (bool light in new[] { true, false })
                    foreach (double dpi in new[] { 1d, 2d })
                    {
                        var state = TaskMonitorDemo.State(); var floating = state.O("settings").O("floating");
                        floating["content"] = content; floating["theme"] = light ? "light" : "dark";
                        floating["budgetID"] = "daily"; floating["pinned"] = false; floating["keepExpanded"] = false;
                        floating["edgeAutoHide"] = false; state.O("settings")["refresh"] = 0;
                        var window = new CapsuleWindow((_, _) => Task.CompletedTask, readPointer: () => null) { ShowActivated = false, Topmost = false };
                        try
                        {
                            var surface = window.Surface; window.Content = null;
                            var root = new Border { Child = surface, Width = width, Height = height };
                            VisualTreeHelper.SetRootDpi(root, new DpiScale(dpi, dpi));
                            window.Update(state);
                            surface.HostSize = new Size(width, height); surface.PanelBounds = panel;
                            surface.CompactBounds = new Rect(panel.Right - 76, panel.Top, 76, 76);
                            surface.CaptureTextBounds = true; surface.Expansion = 1;
                            root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
                            surface.AnimationBounds = null; surface.Redraw();
                            var baselineText = surface.VisibleTextBounds.Select(x => (x.Text, Bounds: Relative(x.Bounds, panel))).ToArray();
                            var baselineRegions = surface.Regions().Select(x => (x.name, Bounds: Relative(x.bounds, panel))).ToArray();
                            foreach (var direction in new[] { (Name: "from-right-below", Delta: new Vector(80, 24)), (Name: "from-left-above", Delta: new Vector(-80, -24)) })
                            {
                                string id = $"{content}-{(light ? "light" : "dark")}-{dpi * 100:0}-{direction.Name}";
                                var current = panel; current.Offset(direction.Delta);
                                surface.AnimationBounds = current; surface.Redraw(); frames++;
                                var ink = surface.VisibleTextBounds.ToArray(); var actions = surface.Regions().ToArray();
                                labels += ink.Length; regions += actions.Length;
                                bool translatedText = ink.Length == baselineText.Length && ink.Select((text, index) =>
                                    text.Text == baselineText[index].Text && Near(Relative(text.Bounds, current), baselineText[index].Bounds)).All(x => x);
                                Check(translatedText, id + "-whole-content-translates-with-shell",
                                    "Every glyph retains its baseline position relative to the moving rounded panel; the endpoint cannot leak through the moving clip.");
                                var clipped = ink.Where(x => !CapsuleMorph.FullyContainsRounded(x.Bounds, current, surface.CurrentRadius, 0)).Select(x => x.Text).ToArray();
                                Check(clipped.Length == 0, id + "-moving-content-remains-complete", string.Join("; ", clipped));
                                bool translatedRegions = actions.Length == baselineRegions.Length && actions.Select((item, index) =>
                                    item.name == baselineRegions[index].name && Near(Relative(item.bounds, current), baselineRegions[index].Bounds)).All(x => x);
                                Check(translatedRegions, id + "-all-action-regions-follow-content", "Recovery retains all baseline action rectangles with exactly the same translation.");
                                var missed = actions.Where(x => x.name is not ("details" or "context" or "budgetScope") && surface.ActionEnabled(x.name))
                                    .Where(x => !current.Contains(x.bounds) || surface.Hit(new Point(x.bounds.X + x.bounds.Width / 2, x.bounds.Y + x.bounds.Height / 2)) != x.name)
                                    .Select(x => x.name).ToArray();
                                Check(missed.Length == 0, id + "-visible-button-centers-route-to-their-own-action", string.Join("; ", missed));
                                // A press/menu freezes the current drawing override. Repainting
                                // that same frame must preserve full content and its warm cache.
                                surface.Redraw();
                                Check(ink.SequenceEqual(surface.VisibleTextBounds), id + "-frozen-frame-repaint-is-stable", "The same intermediate frame survives a warm-cache repaint without moving its content.");
                                if (directory != null && dpi == 1 && direction.Delta.X > 0)
                                {
                                    string filename = $"drop-recovery-{content}-{(light ? "light" : "dark")}.png";
                                    var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                                    using var file = File.Create(Path.Combine(directory, filename)); encoder.Save(file); images.Add(filename);
                                }
                            }
                            root.Child = null;
                        }
                        finally { window.Close(); }
                    }
            return J.Obj(("success", failures.Count == 0), ("checks", checks), ("failures", failures), ("images", images),
                ("frames", frames), ("textBoxes", labels), ("regions", regions),
                ("scope", "Offline production WPF drawing; three pages, light/dark, 100%/200% DPI, two intermediate recovery directions, real glyph bounds and action hit testing. No native window is shown."));
        }
        finally { app.ShutdownMode = shutdown; }
    }
}
