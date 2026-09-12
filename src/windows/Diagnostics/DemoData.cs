using System;
using System.Text.Json.Nodes;

namespace CodexUsage;

internal static class DemoData
{
    public static JsonObject Quota() => CodexUsage.Quota.Describe(J.Obj(("updated_at", J.Now), ("reset_count", 3), ("windows", new JsonArray(
        J.Obj(("used_percent", 25), ("duration_minutes", 300), ("resets_at", J.Now + 7800)), J.Obj(("used_percent", 50), ("duration_minutes", 10080), ("resets_at", J.Now + 260000))))));
    public static JsonObject Usage()
    {
        JsonObject Metrics(long input, long output, long cache, long requests) => J.Obj(("input_tokens", input), ("output_tokens", output), ("total_tokens", input + output), ("cached_input_tokens", cache), ("noncached_input_tokens", input - cache), ("reasoning_output_tokens", output / 3), ("cache_write_input_tokens", 0), ("requests", requests), ("active_tasks", 6));
        var summary = Metrics(12005200, 843000, 9850400, 386);
        var timeline = new JsonArray(); var now = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).Date;
        for (int i = 0; i < 14; i++) { var row = Metrics(420000 + i % 5 * 98000, 15000 + i % 3 * 20000, 350000 + i % 5 * 69000, 18 + i); row["date"] = now.AddDays(i - 13).ToString("yyyy-MM-dd"); timeline.Add(row); }
        var groups = new JsonArray(); int index = 0; foreach (string model in new[] { "gpt-6-astra", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "codex-auto-review" }) { var row = Metrics(3800000 / (index + 1), 280000 / (index + 1), 2700000 / (index + 1), 140 / (index + 1)); row["id"] = model; row["label"] = model; groups.Add(row); index++; }
        var tasks = new JsonArray(J.Obj(("id", "demo-windows"), ("label", "Windows 版本适配 · 主面板与浮窗")), J.Obj(("id", "demo-budget"), ("label", "预算提醒与周期边界验证")), J.Obj(("id", "demo-long"), ("label", "包含中文、空格与很长名称的任务，用于检查选择器截断及提示完整性")));
        return J.Obj(("meta", J.Obj(("generated_at", DateTimeOffset.UtcNow.ToString("o")), ("time_zone", "UTC+08:00"), ("refresh_seconds", 5), ("loading", false), ("scanned_files", 28), ("excluded_legacy_threads", 0), ("issues", new JsonArray("演示数据 · 未读取本机日志或账号")), ("coverage", new JsonObject()))),
            ("filters", J.Obj(("models", new JsonArray("gpt-6-astra", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "codex-auto-review")), ("tasks", tasks))),
            ("summary", summary), ("timeline", timeline), ("groups", groups));
    }
    public static JsonObject State()
    {
        var usage = Usage(); var summaries = new JsonArray(); var rules = new JsonArray(); var date = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).Date; double midnight = new DateTimeOffset(date, TimeSpan.FromHours(8)).ToUnixTimeSeconds();
        foreach (var pair in new[] { ("daily", "每日 Token", 20000000d, 12848200d), ("week", "每周 Token", 100000000d, 85400000d) })
        {
            var rule = J.Obj(("id", pair.Item1), ("revision", 1), ("name", pair.Item2), ("kind", "token"), ("amount", pair.Item3), ("tokenMetric", "total"), ("model", "all"), ("task", "all"), ("currency", "USD"), ("fx", 1), ("prices", new JsonArray()), ("thresholds", new JsonArray(20, 10, 0)), ("enabled", true), ("source", "demo"), ("windowMinutes", 10080), ("quotaCondition", "floor"), ("period", J.Obj(("type", pair.Item1 == "week" ? "week" : "day"), ("timezone", "Asia/Shanghai"), ("hour", 0), ("minute", 0), ("weekday", 2), ("day", 1)))); rules.Add(rule);
            var row = rule.Copy(); row["used"] = pair.Item4; row["remaining"] = pair.Item3 - pair.Item4; row["remainingPercent"] = (1 - pair.Item4 / pair.Item3) * 100; row["remainingFraction"] = 1 - pair.Item4 / pair.Item3; row["start"] = pair.Item1 == "week" ? midnight - ((int)date.DayOfWeek + 6) % 7 * 86400 : midnight; row["end"] = row.N("start") + (pair.Item1 == "week" ? 7 : 1) * 86400; row["periodStart"] = row["start"]!.DeepClone(); row["periodEnd"] = row["end"]!.DeepClone(); row["periodID"] = "demo-period"; row["updatedAt"] = J.Now; row["dataStatus"] = "updated"; row["coverage"] = "complete"; row["status"] = pair.Item1 == "week" ? "warning" : "healthy"; row["message"] = "预算剩余 " + row.N("remainingPercent")?.ToString("F1") + "%"; row["reason"] = ""; row["overage"] = 0; row["paused"] = false; summaries.Add(row);
        }
        return J.Obj(("demo", true), ("home", "demo"), ("cache", "demo"), ("usage", usage), ("today", J.Obj(("summary", usage.O("summary")))), ("filtered", J.Obj(("summary", usage.O("summary")), ("filters", new JsonObject()))),
            ("settings", J.Obj(("refresh", 5), ("mode", "both"), ("floating", J.Obj(("days", "1"), ("model", "all"), ("task", "all"), ("theme", "dark"), ("pinned", true))))),
            ("quota", Quota()), ("choices", usage.O("filters")), ("budgets", J.Obj(("rules", rules), ("summaries", summaries), ("events", new JsonArray()))), ("status", "演示数据 · 未读取本机日志或账号"));
    }
}
