using System;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;

namespace CodexUsage;

internal static class CapsuleMonitorBadge
{
    internal static int Unread(JsonObject state) => Math.Max(0, state.O("monitor").O("summary").I("unread"));
    internal static bool ShowsDockedMonitor(JsonObject state) => Unread(state) > 0 || state.O("monitor").O("summary").I("active") > 0
        || state.O("monitor").A("watches").Rows().Any(x => x.B("active")) || TaskMonitorVisual.SummaryStatus(state) is "running" or "waiting" or "unknown" or "idle";
    internal static string Count(int unread) => unread > 99 ? "99+" : Math.Max(0, unread).ToString(CultureInfo.InvariantCulture);
    internal static void Draw(DrawingContext dc, Rect bounds, string status, int unread, bool light, double dpi)
    {
        var tone = TaskMonitorGlyph.Brush(status is "" or "none" ? "idle" : status, light);
        dc.DrawRoundedRectangle(new SolidColorBrush(tone.Color) { Opacity = light ? .09 : .16 }, null, bounds, bounds.Height / 2, bounds.Height / 2);
        var glyph = new Rect(unread > 0 ? bounds.Left + 3 : bounds.Left + (bounds.Width - 9) / 2, bounds.Top + (bounds.Height - 9) / 2, 9, 9);
        TaskMonitorGlyph.Draw(dc, glyph, status is "" or "none" ? "idle" : status, light);
        if (unread <= 0) return;
        var text = new FormattedText(Count(unread), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 9, tone, dpi);
        var ink = text.BuildGeometry(new()).Bounds; var label = new Rect(bounds.Left + 13, bounds.Top, bounds.Width - 15, bounds.Height);
        dc.DrawText(text, new(label.Left + (label.Width - ink.Width) / 2 - ink.X, label.Top + (label.Height - ink.Height) / 2 - ink.Y));
    }
}
