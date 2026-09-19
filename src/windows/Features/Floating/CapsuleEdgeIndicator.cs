using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;

namespace CodexUsage;

internal sealed record CapsuleEdgeDisplay(string Label, string Number, string Percent, string Name,
    double? RemainingFraction, double? Fraction, bool Used, bool Stale, bool Light, string TaskStatus = "", int Unread = 0, bool ShowsMonitor = false)
{
    // A used-percent view must still turn red when little quota remains.
    public Color HealthColor => RemainingFraction is double remaining ? CapsuleColors.Color(remaining, Light) :
        Light ? Color.FromRgb(105, 116, 133) : Color.FromRgb(157, 168, 186);

    public static CapsuleEdgeDisplay From(JsonObject state)
    {
        var floating = state.O("settings").O("floating");
        bool budget = floating.S("content", "usage") == "budget" || floating.S("content") == "monitor" && floating.S("quotaContent", "usage") == "budget";
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
            remaining, fraction, used, budget ? display!.Stale : quota.B("stale", true), light, TaskMonitorVisual.SummaryStatus(state), CapsuleMonitorBadge.Unread(state), CapsuleMonitorBadge.ShowsDockedMonitor(state));
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

    public static Geometry Shape(Size size, CapsuleEdge edge, double docking = 1) => Valid(size) && edge != CapsuleEdge.None ? Shape(new Rect(size), edge, docking) : Geometry.Empty;

    public static void Draw(DrawingContext dc, Size size, CapsuleEdge edge, JsonObject state, double pixelsPerDip, double docking = 1)
    {
        if (!Valid(size) || edge == CapsuleEdge.None) return;
        double dpi = double.IsFinite(pixelsPerDip) && pixelsPerDip > 0 ? pixelsPerDip : 1;
        if (docking < 1) { Paint(dc, size, edge, CapsuleEdgeDisplay.From(state), dpi, docking); return; }
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

    private static Geometry Shape(Rect bounds, CapsuleEdge edge, double docking = 1)
    {
        // Canonical bottom-docked crest; the same six cubic segments define paint and hit testing.
        Point Map(double x, double y)
        {
            (double px, double py) = edge switch
            {
                CapsuleEdge.Top => (x, 1 - y), CapsuleEdge.Left => (1 - y, x),
                CapsuleEdge.Right => (y, x), _ => (x, y)
            };
            return new(bounds.Left + px * bounds.Width, bounds.Top + py * bounds.Height);
        }
        double t = Math.Clamp(docking, 0, 1);
        var ends = new (double x, double y)[] { (.20, .28), (.5, 0), (.80, .28), (1, 1), (.5, 1), (0, 1) };
        var controls = new (double x1, double y1, double x2, double y2)[] { (.10, 1, .11, .54), (.29, .02, .39, 0), (.61, 0, .71, .02), (.89, .54, .90, 1), (.84, 1, .67, 1), (.33, 1, .16, 1) };
        var angles = new[] { Math.PI, Math.PI * 1.25, Math.PI * 1.5, Math.PI * 1.75, Math.PI * 2, Math.PI * 2.5, Math.PI * 3 };
        Point Mixed(double x, double y, double tx, double ty) => Map(x + (tx - x) * t, y + (ty - y) * t);
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(Mixed(0, .5, 0, 1), true, true);
            for (int i = 0; i < 6; i++)
            {
                double a = angles[i], b = angles[i + 1], k = 4d / 3 * Math.Tan((b - a) / 4); var c = controls[i]; var e = ends[i];
                path.BezierTo(Mixed(.5 + (Math.Cos(a) - k * Math.Sin(a)) / 2, .5 + (Math.Sin(a) + k * Math.Cos(a)) / 2, c.x1, c.y1),
                    Mixed(.5 + (Math.Cos(b) + k * Math.Sin(b)) / 2, .5 + (Math.Sin(b) - k * Math.Cos(b)) / 2, c.x2, c.y2),
                    Mixed(.5 + Math.Cos(b) / 2, .5 + Math.Sin(b) / 2, e.x, e.y), true, false);
            }
        }
        geometry.Freeze(); return geometry;
    }

    private static SolidColorBrush Brush(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    private static SolidColorBrush Brush(byte r, byte g, byte b) => Brush(Color.FromRgb(r, g, b));

    private static void Paint(DrawingContext dc, Size size, CapsuleEdge edge, CapsuleEdgeDisplay display, double dpi, double docking = 1)
    {
        var background = display.Light ? Brush(250, 251, 253) : Brush(27, 30, 36);
        var border = display.Light ? Brush(199, 207, 217) : Brush(65, 73, 87);
        var ink = display.Light ? Brush(32, 40, 35) : Brush(245, 247, 246);
        double pixel = 1 / dpi;
        var shape = Shape(size, edge, docking);
        dc.PushClip(shape); dc.DrawRectangle(background, null, new Rect(size));
        var strokeBounds = new Rect(size); strokeBounds.Inflate(-pixel / 2, -pixel / 2);
        if (strokeBounds.Width > 0 && strokeBounds.Height > 0) dc.DrawGeometry(null, new Pen(border, pixel), Shape(strokeBounds, edge, docking));
        dc.PushOpacity(Math.Clamp(docking * 2 - 1, 0, 1));
        bool vertical = edge is CapsuleEdge.Left or CapsuleEdge.Right;
        double centerY = size.Height / 2 + (vertical && display.ShowsMonitor ? -9 : vertical ? 0 : edge == CapsuleEdge.Top ? -1 : 1);
        string value = display.Percent + (display.Stale && display.Fraction is not null ? "*" : "");
        double fontSize = vertical ? 11 : 12;
        var measured = new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, ValueFont, fontSize, ink, dpi);
        double valueWidth = Math.Ceiling(measured.WidthIncludingTrailingWhitespace) + 2;
        double badgeWidth = display.Unread > 0 ? 36 : 20;
        double groupWidth = valueWidth + (display.ShowsMonitor && !vertical ? badgeWidth + 6 : 0);
        double valueX = (size.Width - (vertical ? valueWidth : groupWidth)) / 2;
        DrawText(dc, value, new(valueX, centerY - 8, valueWidth, 16), fontSize, ValueFont, ink, dpi);
        if (display.ShowsMonitor)
        {
            var badge = vertical ? new Rect((size.Width - badgeWidth) / 2, size.Height / 2 + 4, badgeWidth, 13)
                : new Rect(valueX + valueWidth + 6, centerY - 6.5, badgeWidth, 13);
            CapsuleMonitorBadge.Draw(dc, badge, display.TaskStatus, display.Unread, display.Light, dpi);
        }
        dc.Pop(); dc.Pop();
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
