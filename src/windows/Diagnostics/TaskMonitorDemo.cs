using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace CodexUsage;

/// <summary>Explicit demo/test fixtures only. Never used to replace an unavailable live source.</summary>
internal static class TaskMonitorDemo
{
    internal static JsonObject State()
    {
        var state = DemoData.State(); double now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        JsonObject Task(string id, string status, string title) => J.Obj(("id", id), ("turnID", "round-" + id), ("status", status), ("title", title), ("project", "codex-usage / Windows 桌面"), ("startedAt", now - 720), ("updatedAt", now));
        var running = Task("running-task", "running", "Windows 多显示器任务提醒与界面布局检查");
        var waiting = Task("waiting-task", "waiting", "等待用户选择后继续：任务监控状态恢复");
        var completed = Task("completed-task", "completed", "Verify English identifiers and 中文按钮对齐");
        var unknown = Task("unknown-task", "unknown", "连接状态无法确认时保留上次执行信息");
        var fresh = Task("new-task", "running", "刚开始、尚未产生 Token 的任务");
        JsonObject Watch(JsonObject task) { var watch = task.Copy(); watch["mode"] = task.S("id") == "running-task" ? "each" : "once"; watch["active"] = task.S("status") != "completed"; return watch; }
        JsonObject Message(JsonObject task, string id) => J.Obj(("id", id), ("taskID", task.S("id")), ("turnID", task.S("turnID")), ("title", task.S("title")), ("project", task.S("project")), ("status", task.S("status")), ("text", TaskMonitorUi.StateLabel(task.S("status"))), ("createdAt", now - 15), ("read", false), ("delivery", "markers"));
        state["monitor"] = J.Obj(("tasks", new JsonArray(running, waiting, completed, unknown, fresh)), ("watches", new JsonArray(Watch(running), Watch(waiting), Watch(completed), Watch(unknown))),
            ("messages", new JsonArray(Message(waiting, "message-waiting"), Message(completed, "message-completed"))),
            ("settings", J.Obj(("delivery", "system"), ("sound", false), ("notifyCompleted", true), ("notifyAttention", true), ("notifyFailures", true), ("hideNames", false), ("defaultMode", "once"), ("retentionDays", 30), ("pausedUntil", 0), ("focusID", ""))),
            ("summary", J.Obj(("active", 3), ("attention", 1), ("unread", 2), ("status", "waiting"))),
            ("capabilities", J.Obj(("completed", true), ("waiting", true), ("failure", true), ("interrupted", true))), ("sourceStatus", J.Obj(("status", "available"), ("text", "演示数据 · 不监控真实任务"), ("checkedAt", now))), ("error", ""));
        return state;
    }
    internal static JsonObject Apply(JsonObject original, JsonObject request)
    {
        var next = original.Copy(); var monitor = next.O("monitor");
        if (monitor.Count == 0) { next["monitorResult"] = J.Obj(("ok", true), ("message", "演示数据未配置监控任务")); return next; }
        var payload = request.O("payload"); string operation = request.S("operation"); var result = J.Obj(("ok", true), ("message", "演示 · 已更新"));
        switch (operation)
        {
            case "settings": foreach (var entry in payload.O("patch")) monitor.O("settings")[entry.Key] = entry.Value?.DeepClone(); break;
            case "focus": monitor.O("settings")["focusID"] = payload.S("id"); break;
            case "read":
                var ids = payload.A("ids").Select(x => x?.ToString()).ToHashSet(); foreach (var message in monitor.A("messages").Rows()) if (payload.B("all") || ids.Contains(message.S("id"))) message["read"] = true; break;
            case "stop": monitor["watches"] = new JsonArray(monitor.A("watches").Rows().Where(x => x.S("id") != payload.S("id")).Select(x => (JsonNode?)x.DeepClone()).ToArray()); break;
            case "mode": foreach (var watch in monitor.A("watches").Rows().Where(x => x.S("id") == payload.S("id"))) watch["mode"] = payload.S("mode"); break;
            case "clear-ended": monitor["watches"] = new JsonArray(monitor.A("watches").Rows().Where(x => x.B("active")).Select(x => (JsonNode?)x.DeepClone()).ToArray()); break;
            case "clear-history": monitor["messages"] = new JsonArray(monitor.A("messages").Rows().Where(x => !x.B("read") || x.S("status") == "waiting").Select(x => (JsonNode?)x.DeepClone()).ToArray()); break;
            case "add":
                var items = new JsonArray();
                foreach (var selection in payload.A("selections").Rows())
                {
                    var task = monitor.A("tasks").Rows().FirstOrDefault(t => t.S("id") == selection.S("id") && t.S("turnID") == selection.S("turnID"));
                    if (task == null) { items.Add(J.Obj(("id", selection.S("id")), ("ok", false), ("error", "所选轮次已变化"))); continue; }
                    if (!monitor.A("watches").Rows().Any(w => w.S("id") == task.S("id"))) { var watch = task.Copy(); watch["mode"] = payload.S("mode"); watch["active"] = true; monitor.A("watches").Add(watch); }
                    items.Add(J.Obj(("id", task.S("id")), ("ok", true)));
                }
                result["items"] = items; break;
        }
        monitor.O("summary")["active"] = monitor.A("watches").Rows().Count(w => w.B("active"));
        monitor.O("summary")["unread"] = monitor.A("messages").Rows().Count(m => !m.B("read")); next["monitorResult"] = result; return next;
    }
}
