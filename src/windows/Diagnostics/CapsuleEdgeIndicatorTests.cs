using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexUsage;

internal static class CapsuleEdgeIndicatorTests
{
    // Pure model and offscreen drawing checks; no HWND, account, settings file or pointer is used.
    public static JsonObject Run()
    {
        var checks = new JsonArray();
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Capsule edge indicator: " + message); checks.Add(message); }
        var state = Fixture();
        var display = CapsuleEdgeDisplay.From(state);
        Check(display.Label == "周余" && display.Percent == "72%" && display.RemainingFraction == .72 && display.Fraction == .72 && !display.Stale, "remaining is the default and matches the selected capsule quota");
        state.O("settings").O("floating")["edgeMetric"] = "used";
        var used = CapsuleEdgeDisplay.From(state);
        Check(used.Label == "周用" && used.Percent == "28%" && Math.Abs(used.Fraction!.Value - .28) < .000001 && used.HealthColor == display.HealthColor, "used complements the fraction without inverting quota health color");
        state.O("quota")["capsuleName"] = "5h余";
        Check(CapsuleEdgeDisplay.From(state) is { Label: "周用", Fraction: null }, "a short-window cache cannot replace the weekly capsule quota");
        state.O("quota")["capsuleName"] = "90分钟余";
        Check(CapsuleEdgeDisplay.From(state) is { Label: "周用", Fraction: null }, "minute windows cannot masquerade as weekly quota");
        state.O("quota")["capsuleName"] = "很长的官方额度窗口余";
        Check(CapsuleEdgeDisplay.From(state) is { Label: "周用", Fraction: null }, "unknown window identity stays unknown");
        state.O("quota")["capsuleName"] = "周余";
        state.O("quota")["capsuleFraction"] = null;
        Check(CapsuleEdgeDisplay.From(state) is { Fraction: null, RemainingFraction: null, Percent: "—", Number: "—" }, "unknown used quota stays unknown rather than zero or one hundred");
        state.O("settings").O("floating")["edgeMetric"] = "remaining";
        Check(CapsuleEdgeDisplay.From(state).Percent == "—", "unknown remaining quota has no misleading percent suffix");
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            state.O("quota")["capsuleFraction"] = invalid;
            Check(CapsuleEdgeDisplay.From(state).Percent == "—", "nonfinite " + invalid + " cannot become a known percentage");
        }
        state.O("quota")["capsuleFraction"] = .01999999999999999;
        Check(CapsuleEdgeDisplay.From(state).Percent == "2%", "display rounding matches the capsule tolerance");
        state.O("quota")["capsuleFraction"] = 1.4;
        Check(CapsuleEdgeDisplay.From(state).Percent == "100%", "remaining clamps above one hundred percent");
        state.O("quota")["capsuleFraction"] = -.2;
        Check(CapsuleEdgeDisplay.From(state).Percent == "0%", "remaining clamps below zero");
        state.O("quota")["capsuleFraction"] = .5; state.O("quota")["stale"] = true;
        Check(CapsuleEdgeDisplay.From(state) is { Stale: true, Percent: "50%" }, "stale values remain readable and reserve a separate subtle marker");
        state.O("settings").O("floating")["theme"] = "light";
        Check(CapsuleEdgeDisplay.From(state).HealthColor == CapsuleColors.Color(.5, true), "light theme uses the existing capsule health palette");

        state = BudgetFixture();
        Check(CapsuleEdgeDisplay.From(state) is { Label: "预算余", Percent: "10%", Name: "合成预算", Stale: false }, "budget indicator follows the selected budget and its known remaining data");
        state.O("settings").O("floating")["edgeMetric"] = "used";
        Check(CapsuleEdgeDisplay.From(state) is { Label: "预算用", Percent: "90%" }, "budget used metric complements the same remaining fraction");
        var row = state.O("budgets").A("summaries")[0]!.AsObject();
        foreach (string status in new[] { "unknown", "partial", "source_invalid", "scope_invalid", "scheduled" })
        {
            row["status"] = status;
            Check(CapsuleEdgeDisplay.From(state).Percent == "—", "budget " + status + " cannot display a known fraction");
        }
        row["status"] = "healthy"; row["remainingFraction"] = null; row["remainingPercent"] = 25;
        Check(CapsuleEdgeDisplay.From(state).Percent == "75%", "budget legacy remainingPercent fallback matches capsule semantics");
        state.O("budgets")["error"] = "合成持久化错误";
        Check(CapsuleEdgeDisplay.From(state).Stale, "budget persistence failure marks retained data stale");
        state.O("settings").O("floating")["budgetID"] = "deleted";
        Check(CapsuleEdgeDisplay.From(state).Percent == "—", "deleted selection never silently switches to a different budget");

        foreach (CapsuleEdge edge in Edges)
        {
            var size = SizeFor(edge); var shape = CapsuleEdgeIndicator.Shape(size, edge);
            var outer = OuterCorner(edge, size); var inner = InnerCorner(edge, size);
            Check(shape.FillContains(outer) && !shape.FillContains(inner) && shape.FillContains(new Point(size.Width / 2, size.Height / 2)), edge + " shape has a flat screen edge and a curved crest with transparent outer corners");
        }
        Check(CapsuleEdgeIndicator.Shape(new(28, 72), CapsuleEdge.None).IsEmpty(), "undocked has no edge hit target");

        int renders = 0;
        foreach (CapsuleEdge edge in Edges)
            foreach (bool light in new[] { false, true })
                foreach (double dpi in new[] { 1d, 1.25, 1.5, 2 })
                    foreach (double? fraction in new double?[] { null, 0, .5, 1 })
                    {
                        state = Fixture(fraction, light); state.O("quota")["stale"] = true;
                        state.O("quota")["capsuleName"] = "预算余";
                        var size = SizeFor(edge); var visual = Visual(size, edge, state, dpi);
                        var bitmap = Bitmap(visual, size, dpi); var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                        int Alpha(Point point)
                        {
                            int x = Math.Clamp((int)(point.X * dpi), 0, bitmap.PixelWidth - 1), y = Math.Clamp((int)(point.Y * dpi), 0, bitmap.PixelHeight - 1);
                            return pixels[(y * bitmap.PixelWidth + x) * 4 + 3];
                        }
                        int centerAlpha = Alpha(new(size.Width / 2, size.Height / 2)), outerAlpha = Alpha(OuterCorner(edge, size)), innerAlpha = Alpha(InnerCorner(edge, size));
                        Point backgroundPoint = edge switch { CapsuleEdge.Left => new(3, size.Height / 2), CapsuleEdge.Right => new(size.Width - 3, size.Height / 2), CapsuleEdge.Top => new(size.Width / 2, 3), _ => new(size.Width / 2, size.Height - 3) };
                        int backgroundAlpha = Alpha(backgroundPoint);
                        // WPF's antialiased glyph compositing can round an opaque text pixel to 254.
                        // A separate text-free background sample must remain fully opaque.
                        if (centerAlpha < 254 || backgroundAlpha != 255 || outerAlpha < 200 || innerAlpha != 0)
                            throw new InvalidOperationException($"Capsule edge opacity/rounding: {edge}, light={light}, dpi={dpi}, fraction={fraction}, alpha(center/background/outer/inner)={centerAlpha}/{backgroundAlpha}/{outerAlpha}/{innerAlpha}");
                        var glyphs = GlyphBounds(visual.Drawing, Matrix.Identity).ToArray();
                        if (glyphs.Length == 0 || glyphs.Any(rect => !new Rect(.5, .5, size.Width - 1, size.Height - 1).Contains(rect)))
                            throw new InvalidOperationException($"Capsule edge label/percentage clipping: {edge}, light={light}, dpi={dpi}, fraction={fraction}");
                        renders++;
                    }
        checks.Add($"{renders} offscreen renders verify both themes, four edges, 100–200% DPI, unknown/zero/half/full values, opaque surfaces and uncut glyphs");
        state = Fixture(.5); var markerSize = SizeFor(CapsuleEdge.Left);
        byte[] Pixels(JsonObject data)
        {
            var bitmap = Bitmap(Visual(markerSize, CapsuleEdge.Left, data, 1), markerSize, 1);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0); return pixels;
        }
        var fresh = Pixels(state); state.O("quota")["stale"] = true; var stale = Pixels(state);
        int changedPixels = Enumerable.Range(0, fresh.Length / 4).Count(i => Enumerable.Range(0, 4).Any(c => fresh[i * 4 + c] != stale[i * 4 + c]));
        Check(changedPixels > 0 && changedPixels < 400, "stale quota adds a visible asterisk without changing the numeric value");
        return J.Obj(("success", true), ("checks", checks), ("renders", renders));
    }

    public static void Render(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (bool light in new[] { false, true })
            foreach (CapsuleEdge edge in Edges)
                foreach (double? fraction in new double?[] { null, .05, .5, 1 })
                {
                    var state = Fixture(fraction, light); state.O("quota")["stale"] = fraction is null;
                    var size = SizeFor(edge); var bitmap = Bitmap(Visual(size, edge, state, 2), size, 2);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    string level = fraction is double ratio ? ((int)(ratio * 100)).ToString() : "unknown";
                    using var file = File.Create(Path.Combine(directory, $"edge-{edge.ToString().ToLowerInvariant()}-{(light ? "light" : "dark")}-{level}.png"));
                    encoder.Save(file);
                }
    }

    private static readonly CapsuleEdge[] Edges = [CapsuleEdge.Left, CapsuleEdge.Right, CapsuleEdge.Top, CapsuleEdge.Bottom];
    private static Size SizeFor(CapsuleEdge edge) => edge is CapsuleEdge.Left or CapsuleEdge.Right ? new(44, 68) : new(88, 28);
    private static Point OuterCorner(CapsuleEdge edge, Size size) => edge switch
    {
        CapsuleEdge.Left => new(.5, size.Height / 2),
        CapsuleEdge.Right => new(size.Width - .5, size.Height / 2),
        CapsuleEdge.Top => new(size.Width / 2, .5),
        _ => new(size.Width / 2, size.Height - .5)
    };
    private static Point InnerCorner(CapsuleEdge edge, Size size) => edge switch
    {
        CapsuleEdge.Left => new(size.Width - .25, .25),
        CapsuleEdge.Right => new(.25, .25),
        CapsuleEdge.Top => new(.25, size.Height - .25),
        _ => new(.25, .25)
    };
    private static JsonObject Fixture(double? fraction = .72, bool light = false) => J.Obj(
        ("settings", J.Obj(("refresh", 5), ("floating", J.Obj(("theme", light ? "light" : "dark"), ("content", "usage"))))),
        ("quota", J.Obj(("capsuleFraction", fraction), ("capsuleName", "周余"), ("stale", false))));
    private static JsonObject BudgetFixture()
    {
        var state = Fixture(); state.O("settings").O("floating")["content"] = "budget"; state.O("settings").O("floating")["budgetID"] = "synthetic";
        var rule = J.Obj(("id", "synthetic"), ("name", "合成预算"), ("kind", "token"), ("amount", 1000));
        var row = rule.Copy(); row["used"] = 900; row["remaining"] = 100; row["remainingFraction"] = .1; row["status"] = "warning"; row["dataStatus"] = "updated";
        state["budgets"] = J.Obj(("rules", new JsonArray(rule)), ("summaries", new JsonArray(row))); return state;
    }
    private static DrawingVisual Visual(Size size, CapsuleEdge edge, JsonObject state, double dpi)
    {
        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) CapsuleEdgeIndicator.Draw(dc, size, edge, state, dpi); return visual;
    }
    private static RenderTargetBitmap Bitmap(DrawingVisual visual, Size size, double dpi)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * dpi), (int)Math.Ceiling(size.Height * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual); return bitmap;
    }
    private static IEnumerable<Rect> GlyphBounds(Drawing drawing, Matrix transform)
    {
        if (drawing is GlyphRunDrawing glyph) { yield return new MatrixTransform(transform).TransformBounds(glyph.Bounds); }
        else if (drawing is DrawingGroup group)
        {
            Matrix next = group.Transform?.Value ?? Matrix.Identity; next.Append(transform);
            foreach (var child in group.Children) foreach (var rect in GlyphBounds(child, next)) yield return rect;
        }
    }
}
