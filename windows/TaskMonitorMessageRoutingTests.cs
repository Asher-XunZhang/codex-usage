using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexUsage;

/// <summary>Exercise message-list navigation without reading messages or changing subscriptions.</summary>
internal static class TaskMonitorMessageRoutingTests
{
    internal static async Task<JsonObject> RunAsync()
    {
        var checks = new JsonArray();
        void Check(bool okay, string name) { if (!okay) throw new InvalidOperationException("Monitor message routing: " + name); checks.Add(name); }
        var state = TaskMonitorDemo.State(); int commands = 0;
        var view = new TaskMonitorView(_ => { commands++; return Task.FromResult(state.Copy()); }, _ => { });
        view.Update(state); view.Select("waiting-task"); view.Select();
        view.Measure(new Size(700, 520)); view.Arrange(new Rect(0, 0, 700, 520)); view.UpdateLayout();
        var search = Descendants<TextBox>(view).Single(x => AutomationProperties.GetName(x) == "搜索监控任务名称或项目");
        search.Text = "a previous search that matches no message";
        string stored = state.O("monitor").ToJsonString();
        view.SelectMessages(); var selected = view.Inspect();
        Check(selected.B("messages") && !selected.B("detail") && selected.S("filter") == "unread", "header entry selects unread message list instead of previous task detail");
        Check(selected.S("search").Length == 0, "header entry clears unrelated search so unread messages remain visible");
        Check(commands == 0 && state.O("monitor").ToJsonString() == stored, "opening message list preserves read flags and subscriptions without sending a monitor command");
        view.SelectMessages(); Check(commands == 0, "repeated entry does not acknowledge any message");
        foreach (var message in state.O("monitor").A("messages").Rows()) message["read"] = true;
        // Deliberately leave summary.unread stale: actual stored messages decide the initial list.
        view.Update(state); view.SelectMessages();
        Check(view.Inspect().S("filter") == "all" && commands == 0, "no unread messages opens all history despite stale summary count");

        string? oldBackground = Environment.GetEnvironmentVariable("CODEX_USAGE_TEST_BACKGROUND");
        MainWindow? startup = null, existing = null;
        try
        {
            Environment.SetEnvironmentVariable("CODEX_USAGE_TEST_BACKGROUND", "1");
            var unread = TaskMonitorDemo.State();
            startup = new MainWindow(unread, "monitor", monitorList: "messages");
            startup.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var first = await startup.Handle(J.Obj(("action", "inspect")));
            Check(first.S("page") == "monitor" && first.O("monitor").B("messages") && first.O("monitor").S("filter") == "unread", "new main-window launch applies explicit messages route on load");
            Check(first.O("monitor").S("taskID").Length == 0 && first.O("monitor").S("messageID").Length == 0, "message-list route uses no synthetic task or message ID");
            existing = new MainWindow(unread, "usage") { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            existing.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            await existing.Handle(J.Obj(("action", "focus"), ("page", "monitor"), ("monitorID", "waiting-task")));
            await existing.Handle(J.Obj(("action", "focus"), ("page", "monitor"), ("monitorList", "messages")));
            var focused = await existing.Handle(J.Obj(("action", "inspect")));
            Check(focused.O("monitor").B("messages") && !focused.O("monitor").B("detail") && focused.O("monitor").S("filter") == "unread", "existing-window focus switches from task detail to unread list");
            foreach (var message in unread.O("monitor").A("messages").Rows()) message["read"] = true;
            // The main panel is intentionally still showing the old unread snapshot.
            // The host's focus envelope must supply and apply the latest state first.
            await existing.Handle(J.Obj(("action", "focus"), ("page", "monitor"), ("monitorList", "messages"), ("state", unread)));
            var history = await existing.Handle(J.Obj(("action", "inspect")));
            Check(history.O("monitor").S("filter") == "all", "same-envelope fresh state selects history even when main still held unread messages");
        }
        finally
        {
            if (existing != null) await existing.CompleteClose();
            if (startup != null) await startup.CompleteClose();
            Environment.SetEnvironmentVariable("CODEX_USAGE_TEST_BACKGROUND", oldBackground);
        }
        return J.Obj(("success", true), ("checks", checks), ("nativeActivation", false));
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
    {
        if (node is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(node, i))) yield return child;
    }
}
