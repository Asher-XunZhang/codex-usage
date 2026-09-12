using System;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace CodexUsage;

/// <summary>The expanded monitor header describes all subscriptions, independently of the compact badge's focus.</summary>
internal sealed record TaskMonitorHeaderDisplay(string Status, string Title, string Subtitle, string UnreadText, string AccessibleText, int UnreadCount)
{
    internal static TaskMonitorHeaderDisplay From(JsonObject state)
    {
        var monitor = state.O("monitor");
        var watches = monitor.A("watches").Rows().Where(x => x.S("id").Length > 0)
            .GroupBy(x => x.S("id"), StringComparer.Ordinal).Select(x => x.First()).ToArray();
        var messages = monitor.A("messages").Rows().ToArray();
        int unread = messages.Count(x => !x.B("read"));
        int active = watches.Count(x => x.B("active")), ended = watches.Length - active;
        bool waitingSupported = monitor.O("capabilities").B("waiting");
        string source = monitor.O("sourceStatus").S("status");
        bool storageError = monitor.S("error").Length > 0 || monitor.O("recovery").B("required");
        string Status(JsonObject watch)
        {
            string value = watch.S("status");
            if (watch.S("sourceError").Length > 0) return "unknown";
            if (value == "waiting" && (!waitingSupported || !watch.B("active"))) return "unknown";
            // Continuous reminders remain subscribed after a round ends. Their next action is to wait for a new round.
            if (watch.B("active") && watch.S("mode") == "each" && value is "completed" or "failed" or "interrupted") return "idle";
            if (!watch.B("active") && value is "running" or "idle") return "unknown";
            return value is "running" or "waiting" or "completed" or "failed" or "interrupted" or "unknown" or "idle" ? value : "unknown";
        }
        string[] statuses = watches.Select(Status).ToArray();
        int Count(string status) => statuses.Count(x => x == status);
        string[] priority = ["waiting", "unknown", "failed", "running", "idle", "interrupted", "completed"];
        string selected = priority.FirstOrDefault(x => Count(x) > 0) ?? "none";
        string subtitle = $"监控中 {Short(active)} · 已结束 {Short(ended)}";
        string title = selected switch
        {
            "waiting" => $"{Short(Count(selected))} 项等待处理",
            "unknown" => $"{Short(Count(selected))} 项状态待确认",
            "failed" => $"{Short(Count(selected))} 项本轮失败",
            "running" => $"{Short(Count(selected))} 项正在执行",
            "idle" => $"{Short(Count(selected))} 项等待新一轮",
            "interrupted" => $"{Short(Count(selected))} 项本轮中断",
            "completed" => $"{Short(Count(selected))} 项本轮已结束",
            _ => messages.Length > 0 ? "当前没有监控任务" : "选择要关注的任务"
        };
        if (selected == "none") subtitle = messages.Length > 0 ? "已保留历史消息" : "本轮结束后提醒你";

        // A reliable waiting event stays actionable when other sources are partial. Storage failure cannot be presented as healthy.
        if (storageError)
        {
            selected = "unknown"; title = "监控记录待恢复"; subtitle = "记录异常 · 可检查重试";
        }
        else if (source != "available")
        {
            if (selected != "waiting" || source != "partial") { selected = "unknown"; title = source is "checking" or "loading" ? "正在核对任务" : "任务状态待确认"; }
            subtitle = source switch
            {
                "partial" => "部分来源待确认",
                "unavailable" => "任务来源暂不可用",
                "checking" or "loading" => "正在检查任务来源",
                _ => "任务来源尚未确认"
            };
        }
        string exactCounts = $"共选择 {watches.Length} 项任务，监控中 {active} 项，已结束监控 {ended} 项。" +
            $"执行中 {Count("running")} 项，等待处理 {Count("waiting")} 项，状态待确认 {Count("unknown")} 项，等待新一轮 {Count("idle")} 项，" +
            $"本轮已结束 {Count("completed")} 项，本轮失败 {Count("failed")} 项，本轮中断 {Count("interrupted")} 项。";
        string accessible = $"任务监控。{title}。{subtitle}。" +
            (storageError || source != "available" ? "以下为当前保留的记录，不代表所有任务的实时状态。" : "") +
            exactCounts + $"未读消息 {unread} 条。结束只表示本轮结束，不代表整个任务需求完成。";
        return new(selected, title, subtitle, unread > 0 ? $"{Short(unread)} 未读" : "暂无未读", accessible, unread);
    }

    private static string Short(int count) => count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture);
}
