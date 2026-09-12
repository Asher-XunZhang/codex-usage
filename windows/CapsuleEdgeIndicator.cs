using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;

namespace CodexUsage;

internal sealed record CapsuleEdgeDisplay(string Label, string Number, string Percent, string Name,
    double? RemainingFraction, double? Fraction, bool Used, bool Stale, bool Light)
{
    // A used-percent view must still turn red when little quota remains.
    public Color HealthColor => RemainingFraction is double remaining ? CapsuleColors.Color(remaining, Light) :
        Light ? Color.FromRgb(105, 116, 133) : Color.FromRgb(157, 168, 186);

    public static CapsuleEdgeDisplay From(JsonObject state)
    {
        var floating = state.O("settings").O("floating");
        bool budget = floating.S("content", "usage") == "budget";
        bool used = floating.S("edgeMetric", "remaining") == "used";
        bool light = !Theme.Resolve(state.O("settings"), "floating");
        var display = budget ? CapsuleBudgetDisplay.From(state) : null;
        var quota = state.O("quota");
        double? raw = budget ? display!.Fraction : CapsuleUsageDisplay.Fraction(state);
        double? remaining = raw is double value && double.IsFinite(value) ? Math.Clamp(value, 0, 1) : null;
        double? fraction = remaining is double known ? used ? 1 - known : known : null;
        string number = fraction is double ratio ? Math.Floor(ratio * 100 + 1e-9).ToString(CultureInfo.InvariantCulture) : "—";
        string name = budget ? display!.Name : CapsuleUsageDisplay.Name;
        string prefix = budget ? "预算" : CompactName(name);
        return new(prefix + (used ? "用" : "余"), number, number + (fraction is null ? "" : "%"), name,
            remaining, fraction, used, budget ? display!.Stale : quota.B("stale", true), light);
    }

    private static string CompactName(string name)
    {
        if (name.EndsWith("剩余", StringComparison.Ordinal)) name = name[..^2];
        else if (name.EndsWith("余", StringComparison.Ordinal) || name.EndsWith("用", StringComparison.Ordinal)) name = name[..^1];
        name = name.Replace("分钟", "m", StringComparison.Ordinal);
        // Keep arbitrary official windows readable without shrinking a long name to tiny text.
        int width = 0; foreach (char c in name) width += c > 255 ? 2 : 1;
        return name.Length == 0 || width > 4 ? "额度" : name;
    }
}

internal static class CapsuleEdgeIndicator
{
    private static readonly FontFamily Fonts = new("Segoe UI, Microsoft YaHei UI");
    private static readonly Typeface LabelFont = new(Fonts, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface ValueFont = new(Fonts, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private readonly record struct CacheKey(Size Size, CapsuleEdge Edge, CapsuleEdgeDisplay Display, double Dpi);
    private static readonly Dictionary<CacheKey, DrawingGroup> Cache = new();

    public static Geometry Shape(Size size, CapsuleEdge edge) => Valid(size) && edge != CapsuleEdge.None ? Shape(new Rect(size), edge) : Geometry.Empty;

    public static void Draw(DrawingContext dc, Size size, CapsuleEdge edge, JsonObject state, double pixelsPerDip)
    {
        if (!Valid(size) || edge == CapsuleEdge.None) return;
        double dpi = double.IsFinite(pixelsPerDip) && pixelsPerDip > 0 ? pixelsPerDip : 1;
        var key = new CacheKey(size, edge, CapsuleEdgeDisplay.From(state), dpi);
        if (!Cache.TryGetValue(key, out var drawing))
        {
            drawing = new DrawingGroup();
            using (var context = drawing.Open()) Paint(context, size, edge, key.Display, dpi);
            drawing.Freeze();
            // Only a few states are normally live. Bound the cache when quotas, DPI or theme change.
            if (Cache.Count >= 64) Cache.Clear();
            Cache[key] = drawing;
        }
        dc.DrawDrawing(drawing);
    }

    private static bool Valid(Size size) => double.IsFinite(size.Width) && double.IsFinite(size.Height) && size.Width > 0 && size.Height > 0;

    private static Geometry Shape(Rect bounds, CapsuleEdge edge)
    {
        double radius = Math.Min(8, Math.Min(bounds.Width, bounds.Height) / 2);
        double tl = edge is CapsuleEdge.Right or CapsuleEdge.Bottom ? radius : 0;
        double tr = edge is CapsuleEdge.Left or CapsuleEdge.Bottom ? radius : 0;
        double br = edge is CapsuleEdge.Left or CapsuleEdge.Top ? radius : 0;
        double bl = edge is CapsuleEdge.Right or CapsuleEdge.Top ? radius : 0;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(new(bounds.Left + tl, bounds.Top), true, true);
            path.LineTo(new(bounds.Right - tr, bounds.Top), true, false);
            Corner(new(bounds.Right, bounds.Top + tr), tr);
            path.LineTo(new(bounds.Right, bounds.Bottom - br), true, false);
            Corner(new(bounds.Right - br, bounds.Bottom), br);
            path.LineTo(new(bounds.Left + bl, bounds.Bottom), true, false);
            Corner(new(bounds.Left, bounds.Bottom - bl), bl);
            path.LineTo(new(bounds.Left, bounds.Top + tl), true, false);
            Corner(new(bounds.Left + tl, bounds.Top), tl);
            void Corner(Point end, double r)
            {
                if (r > 0) path.ArcTo(end, new(r, r), 0, false, SweepDirection.Clockwise, true, false);
                else path.LineTo(end, true, false);
            }
        }
        geometry.Freeze(); return geometry;
    }

    private static SolidColorBrush Brush(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    private static SolidColorBrush Brush(byte r, byte g, byte b) => Brush(Color.FromRgb(r, g, b));

    private static void Paint(DrawingContext dc, Size size, CapsuleEdge edge, CapsuleEdgeDisplay display, double dpi)
    {
        var background = display.Light ? Brush(250, 251, 253) : Brush(27, 30, 36);
        var border = display.Light ? Brush(199, 207, 217) : Brush(65, 73, 87);
        var secondary = display.Light ? Brush(99, 111, 129) : Brush(157, 168, 186);
        var track = display.Light ? Brush(225, 230, 237) : Brush(51, 59, 72);
        var accent = Brush(display.HealthColor);
        double pixel = 1 / dpi;
        var shape = Shape(size, edge);
        dc.PushClip(shape);
        dc.DrawRectangle(background, null, new Rect(size));
        var strokeBounds = new Rect(size); strokeBounds.Inflate(-pixel / 2, -pixel / 2);
        if (strokeBounds.Width > 0 && strokeBounds.Height > 0) dc.DrawGeometry(null, new Pen(border, pixel), Shape(strokeBounds, edge));
        bool vertical = edge is CapsuleEdge.Left or CapsuleEdge.Right;
        Rect label, value, meter;
        Point stale;
        if (vertical)
        {
            double contentLeft = edge == CapsuleEdge.Left ? 4 : 2;
            double contentWidth = Math.Max(1, size.Width - 6);
            label = new(contentLeft, size.Height * .19, contentWidth, 15);
            value = new(contentLeft, size.Height * .43, contentWidth, 19);
            stale = new(contentLeft + contentWidth / 2, size.Height - 12);
            meter = new(edge == CapsuleEdge.Left ? 1 : size.Width - 3, 10, 2, Math.Max(1, size.Height - 20));
        }
        else
        {
            double top = edge == CapsuleEdge.Top ? 6 : 3;
            label = new(5, top, size.Width * .39, 18);
            value = new(size.Width * .44, top - 1, size.Width * .46, 20);
            stale = new(size.Width - 5, edge == CapsuleEdge.Top ? 8 : size.Height - 8);
            meter = new(9, edge == CapsuleEdge.Top ? 1 : size.Height - 3, Math.Max(1, size.Width - 18), 2);
        }
        dc.DrawRoundedRectangle(track, null, meter, 1, 1);
        if (display.Fraction is double fraction && fraction > 0)
        {
            var fill = meter;
            if (vertical) { fill.Height *= fraction; fill.Y = meter.Bottom - fill.Height; }
            else fill.Width *= fraction;
            dc.DrawRoundedRectangle(accent, null, fill, 1, 1);
        }
        DrawText(dc, display.Label, label, vertical ? 8.5 : 9, LabelFont, secondary, dpi);
        DrawText(dc, display.Percent, value, vertical ? 11.5 : 13, ValueFont, accent, dpi);
        if (display.Stale) dc.DrawEllipse(secondary, null, stale, 1.2, 1.2);
        dc.Pop();
    }

    private static void DrawText(DrawingContext dc, string text, Rect bounds, double size, Typeface font, Brush color, double dpi)
    {
        FormattedText Format(double fontSize) => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, font, fontSize, color, dpi);
        var formatted = Format(size);
        // Fit the actual glyph outlines as well as layout width: 100% stays on one line at every DPI.
        var ink = formatted.BuildGeometry(new()).Bounds;
        double width = Math.Max(formatted.WidthIncludingTrailingWhitespace, ink.Width);
        double height = Math.Max(formatted.Height, ink.Height);
        double scale = Math.Min(1, Math.Min(bounds.Width / Math.Max(1, width), bounds.Height / Math.Max(1, height)));
        if (scale < 1) { formatted = Format(size * scale * .98); ink = formatted.BuildGeometry(new()).Bounds; }
        dc.DrawText(formatted, new(bounds.X + (bounds.Width - ink.Width) / 2 - ink.X, bounds.Y + (bounds.Height - ink.Height) / 2 - ink.Y));
    }
}
