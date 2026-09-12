using System;
using System.Text.Json.Nodes;

namespace CodexUsage;

// A completed manual check is distinct from the continuously refreshed source snapshot.
internal sealed record TaskMonitorCheckFeedback(string Text, string Detail, bool Failed)
{
    internal static TaskMonitorCheckFeedback From(JsonObject state)
    {
        var monitor = state.O("monitor"); var result = state.O("monitorResult");
        if (!result.B("ok")) return Failure(result.S("error").Length > 0 ? result.S("error") : "检查未完成，请重试");
        string error = monitor.S("error");
        if (error.Length > 0) return Failure(error);
        var source = monitor.O("sourceStatus");
        string text = source.S("status") switch
        {
            "available" => "本地任务来源可用",
            "partial" => "部分任务状态待确认",
            "unavailable" => "未找到可用任务来源",
            _ => "任务来源尚未确认"
        };
        return new(J.Date(J.Now, "HH:mm:ss") + " · " + text,
            text + "；" + source.S("text") + "。此检查只核对本地任务日志，不测试系统通知。",
            source.S("status") != "available");
    }
    internal static TaskMonitorCheckFeedback Failure(string error) =>
        new(J.Date(J.Now, "HH:mm:ss") + " · 检查失败，请重试", error, true);
}
