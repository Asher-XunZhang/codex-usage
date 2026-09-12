using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace CodexUsage;

/// <summary>Checks actual subscription and message combinations without a desktop window or a live user's state.</summary>
internal static class TaskMonitorHeaderTests
{
    internal static JsonObject Run()
    {
        var failures = new JsonArray(); int checks = 0;
        void Check(bool okay, string id) { checks++; if (!okay) failures.Add(id); }
        JsonObject Watch(string id, string status, bool active = true, string mode = "once") =>
            J.Obj(("id", id), ("status", status), ("active", active), ("mode", mode));
        JsonObject State(params JsonObject[] watches) => J.Obj(("monitor", J.Obj(
            ("watches", J.Array(watches)), ("messages", new JsonArray()), ("capabilities", J.Obj(("waiting", true))),
            ("sourceStatus", J.Obj(("status", "available"))), ("settings", J.Obj(("focusID", ""))), ("error", ""))));
        var state = State(Watch("ended", "completed", false), Watch("running", "running"));
        state.O("monitor").O("settings")["focusID"] = "ended";
        state.O("monitor")["summary"] = J.Obj(("status", "completed"), ("unread", 91), ("active", 93));
        var header = TaskMonitorHeaderDisplay.From(state);
        Check(header.Status == "running" && header.Title == "1 项正在执行", "all-subscriptions-override-compact-focus-and-stale-summary");
        Check(header.Subtitle == "监控中 1 · 已结束 1", "subscription-count-is-distinct-from-task-status");
        Check(header.UnreadCount == 0 && header.UnreadText == "暂无未读", "unread-uses-stored-messages-independently-of-summary");

        state = State(Watch("unknown", "unknown"), Watch("wait", "waiting"), Watch("running", "running"));
        header = TaskMonitorHeaderDisplay.From(state);
        Check(header.Status == "waiting" && header.Title == "1 项等待处理", "reliable-waiting-remains-first-action");
        state.O("monitor").O("sourceStatus")["status"] = "partial";
        header = TaskMonitorHeaderDisplay.From(state);
        Check(header.Status == "waiting" && header.Subtitle == "部分来源待确认", "waiting-does-not-hide-partial-source-warning");
        foreach (string source in new[] { "unavailable", "loading" })
        {
            state.O("monitor").O("sourceStatus")["status"] = source;
            Check(TaskMonitorHeaderDisplay.From(state).Status == "unknown", "unconfirmed-source-cannot-present-cached-waiting-as-live-" + source);
        }
        state.O("monitor").O("sourceStatus")["status"] = "partial";
        state.O("monitor").O("capabilities")["waiting"] = false;
        header = TaskMonitorHeaderDisplay.From(state);
        Check(header.Status == "unknown" && header.AccessibleText.Contains("等待处理 0 项", StringComparison.Ordinal), "unsupported-waiting-is-not-presented-as-actionable");
        state.O("monitor").O("sourceStatus")["status"] = "available";
        Check(TaskMonitorHeaderDisplay.From(state).Title == "2 项状态待确认", "unsupported-waiting-counts-as-unknown-even-with-healthy-source");
        state.O("monitor").O("capabilities")["waiting"] = true;
        state.O("monitor").A("watches").Rows().Single(x => x.S("id") == "wait")["sourceError"] = "轮次不可确认";
        Check(TaskMonitorHeaderDisplay.From(state).Status == "unknown", "waiting-with-unreliable-own-source-is-not-actionable");

        foreach (string source in new[] { "partial", "unavailable", "checking", "loading", "" })
        {
            state = State(Watch("running", "running")); state.O("monitor").O("sourceStatus")["status"] = source;
            header = TaskMonitorHeaderDisplay.From(state);
            Check(header.Status == "unknown" && !header.Title.Contains("正在执行", StringComparison.Ordinal) && header.AccessibleText.Contains("不代表所有任务的实时状态", StringComparison.Ordinal), "source-warning-cannot-masquerade-as-running-" + source);
        }
        state = State(); header = TaskMonitorHeaderDisplay.From(state);
        Check(header.Status == "none" && header.Title == "选择要关注的任务", "valid-empty-state-invites-selection");
        state.O("monitor").A("messages").Add(J.Obj(("id", "old"), ("taskID", "removed"), ("read", false)));
        header = TaskMonitorHeaderDisplay.From(state);
        Check(header.Status == "none" && header.UnreadCount == 1 && header.Subtitle == "已保留历史消息", "cleared-subscriptions-preserve-unread-entry");
        state.O("monitor").A("messages").Rows().First()["read"] = true;
        Check(TaskMonitorHeaderDisplay.From(state).UnreadCount == 0, "reading-message-updates-only-unread");
        Check(TaskMonitorHeaderDisplay.From(new JsonObject()).Status == "unknown", "missing-monitor-is-unknown-not-empty");

        state = State(Watch("waiting", "waiting"));
        state.O("monitor")["error"] = "存储失败";
        state.O("monitor").A("messages").Add(J.Obj(("id", "saved"), ("read", false)));
        header = TaskMonitorHeaderDisplay.From(state);
        Check(header.Status == "unknown" && header.Title == "监控记录待恢复" && header.UnreadCount == 1, "storage-error-takes-precedence-but-keeps-existing-messages");
        state.O("monitor")["error"] = ""; state.O("monitor")["recovery"] = J.Obj(("required", true));
        Check(TaskMonitorHeaderDisplay.From(state).Status == "unknown", "recovery-required-cannot-look-healthy");

        state = State(Watch("once", "completed", false), Watch("each", "idle", true, "each"));
        header = TaskMonitorHeaderDisplay.From(state);
        Check(header.Status == "idle" && header.Title == "1 项等待新一轮" && header.Subtitle == "监控中 1 · 已结束 1", "each-round-subscription-survives-completed-round");
        state.O("monitor").A("watches").Rows().Last()["status"] = "completed";
        Check(TaskMonitorHeaderDisplay.From(state).Status == "idle", "continuous-terminal-snapshot-is-not-ended-subscription");
        foreach (var pair in new[] { ("unknown", "failed"), ("failed", "running"), ("running", "idle"), ("idle", "interrupted"), ("interrupted", "completed") })
        {
            state = State(Watch("first", pair.Item1, pair.Item1 is not ("failed" or "interrupted" or "completed")), Watch("second", pair.Item2, pair.Item2 is not ("failed" or "interrupted" or "completed")));
            Check(TaskMonitorHeaderDisplay.From(state).Status == pair.Item1, "attention-priority-" + pair.Item1 + "-before-" + pair.Item2);
        }

        state = State(Enumerable.Range(0, 123).Select(x => Watch("task-" + x, "running")).ToArray());
        for (int i = 0; i < 145; i++) state.O("monitor").A("messages").Add(J.Obj(("id", "message-" + i), ("read", i >= 137)));
        header = TaskMonitorHeaderDisplay.From(state);
        Check(header.Title == "99+ 项正在执行" && header.Subtitle == "监控中 99+ · 已结束 0" && header.UnreadText == "99+ 未读", "visual-counts-are-bounded");
        Check(header.UnreadCount == 137 && header.AccessibleText.Contains("共选择 123 项任务", StringComparison.Ordinal) && header.AccessibleText.Contains("未读消息 137 条", StringComparison.Ordinal), "accessible-counts-stay-exact");
        string before = state.ToJsonString(); TaskMonitorHeaderDisplay.From(state);
        Check(state.ToJsonString() == before, "presentation-does-not-change-source-or-read-messages");
        return J.Obj(("success", failures.Count == 0), ("checks", checks), ("failures", failures));
    }
}
