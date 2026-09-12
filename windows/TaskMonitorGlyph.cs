using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;

namespace CodexUsage;

/// <summary>Static task-state symbols. Quota meters and unread counts never drive these shapes.</summary>
internal static class TaskMonitorGlyph
{
    internal static readonly string[] States = ["running", "waiting", "completed", "failed", "interrupted", "unknown", "idle"];
    public static string Label(string status) => status switch
    {
        "running" => "正在执行", "waiting" => "等待处理", "completed" => "本轮已结束",
        "failed" => "本轮执行失败", "interrupted" => "本轮已中断", "unknown" => "状态待确认",
        "idle" => "等待新一轮", _ => "尚未选择监控任务"
    };
    public static SolidColorBrush Brush(string status, bool light)
    {
        if (SystemParameters.HighContrast) return SystemColors.WindowTextBrush;
        var color = status switch
        {
            "running" or "completed" => light ? Color.FromRgb(25, 99, 193) : Color.FromRgb(123, 184, 255),
            "waiting" => light ? Color.FromRgb(155, 101, 0) : Color.FromRgb(243, 191, 88),
            "failed" => light ? Color.FromRgb(188, 45, 57) : Color.FromRgb(255, 139, 151),
            _ => light ? Color.FromRgb(98, 108, 103) : Color.FromRgb(173, 182, 178)
        };
        var brush = new SolidColorBrush(color); brush.Freeze(); return brush;
    }
    public static void Draw(DrawingContext dc, Rect bounds, string status, bool light)
    {
        if (!States.Contains(status) || bounds.Width < 2 || bounds.Height < 2 || bounds.IsEmpty) return;
        double side = Math.Min(bounds.Width, bounds.Height);
        dc.PushTransform(new TranslateTransform(bounds.X + (bounds.Width - side) / 2, bounds.Y + (bounds.Height - side) / 2));
        dc.PushTransform(new ScaleTransform(side / 10, side / 10));
        var brush = Brush(status, light);
        var pen = new Pen(brush, 1.25) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        void Lines(params Point[] points)
        {
            var geometry = new StreamGeometry(); using (var path = geometry.Open())
            { path.BeginFigure(points[0], false, false); for (int i = 1; i < points.Length; i++) path.LineTo(points[i], true, false); }
            dc.DrawGeometry(null, pen, geometry);
        }
        if (status == "running")
        {
            var triangle = new StreamGeometry(); using (var path = triangle.Open())
            { path.BeginFigure(new(2.1, 1.35), true, true); path.LineTo(new(8.4, 5), true, false); path.LineTo(new(2.1, 8.65), true, false); }
            dc.DrawGeometry(brush, null, triangle);
        }
        else if (status == "completed") Lines(new(1.25, 5.1), new(3.85, 7.65), new(8.7, 2.35));
        else if (status == "failed") { Lines(new(2, 2), new(8, 8)); Lines(new(8, 2), new(2, 8)); }
        else if (status == "interrupted") dc.DrawRoundedRectangle(brush, null, new(2, 2, 6, 6), .6, .6);
        else
        {
            dc.DrawEllipse(null, new Pen(brush, 1), new(5, 5), 4, 4);
            if (status == "waiting") { Lines(new(5, 2.65), new(5, 5.55)); dc.DrawEllipse(brush, null, new(5, 7.3), .65, .65); }
            else if (status == "idle") Lines(new(5, 2.55), new(5, 5.1), new(6.85, 6.15));
            else
            {
                var question = new StreamGeometry(); using (var path = question.Open())
                { path.BeginFigure(new(3.7, 3.25), false, false); path.BezierTo(new(3.85, 1.6), new(7.2, 2), new(6.25, 4.15), true, false); path.BezierTo(new(5.85, 4.8), new(5, 4.7), new(5, 5.65), true, false); }
                dc.DrawGeometry(null, pen, question); dc.DrawEllipse(brush, null, new(5, 7.3), .65, .65);
            }
        }
        dc.Pop(); dc.Pop();
    }
}

internal static class TaskMonitorVisual
{
    public static string SummaryStatus(JsonObject state) => state.O("monitor").O("summary").S("status");
    public static string SummaryText(JsonObject state)
    {
        var summary = state.O("monitor").O("summary");
        return $"监控中 {summary.I("active")} · 需处理 {summary.I("attention")} · 未读 {summary.I("unread")}";
    }
    public static IReadOnlyList<JsonObject> Featured(JsonObject state)
    {
        var monitor = state.O("monitor");
        var unread = monitor.A("messages").Rows().Where(x => !x.B("read")).Select(x => x.S("taskID")).ToHashSet(StringComparer.Ordinal);
        return monitor.A("watches").Rows().Where(x => x.S("id").Length > 0)
            .OrderBy(x => x.S("status") == "waiting" ? 0 : unread.Contains(x.S("id")) ? 1 : x.S("status") == "running" ? 2 : 3)
            .ThenByDescending(x => x.N("updatedAt") ?? 0).ThenBy(x => x.S("id"), StringComparer.Ordinal).Take(2).ToArray();
    }
    internal static string FocusText(JsonObject state)
    {
        var monitor = state.O("monitor"); var id = monitor.O("settings").S("focusID");
        string title = monitor.A("watches").Rows().FirstOrDefault(x => x.S("id") == id).S("title");
        return id.Length == 0 || title.Length == 0 ? "常驻关注：全部已选任务" : "常驻关注：" + title;
    }
    internal static string Metadata(JsonObject row)
    {
        string project = row.S("project", "本地任务");
        double? stamp = row.N("updatedAt") ?? row.N("startedAt");
        string time = stamp is double value && double.IsFinite(value) ? J.Date(value, "HH:mm") : "时间待确认";
        return project + " · " + time;
    }
    internal static string SourceText(JsonObject state)
    {
        var monitor = state.O("monitor"); string error = monitor.S("error");
        if (error.Length > 0) return "监控来源异常 · 可检查连接";
        return monitor.O("sourceStatus").S("status") switch
        {
            "available" => "本地监控来源可用",
            "checking" or "loading" => "正在检查监控来源…",
            "partial" => "部分监控来源待确认",
            "unavailable" => "监控来源异常 · 可检查连接",
            _ => "监控来源待检查"
        };
    }
}
