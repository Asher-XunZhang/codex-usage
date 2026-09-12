using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace CodexUsage;

internal static class TaskNotificationTests
{
    internal static void Run()
    {
        static void Check(bool value, string label) { if (!value) throw new InvalidOperationException("Task notification: " + label); }
        var settings = J.Obj(("delivery", "system"), ("sound", false));
        var message = J.Obj(("id", "abc-123:456"), ("taskID", "task-1"), ("title", "标题 <script>&\""), ("status", "completed"));
        var xml = XElement.Parse(TaskNotifications.BuildXml([message], settings));
        Check(xml.Descendants("text").ElementAt(1).Value == message.S("title"), "notification content is escaped text");
        Check(xml.Element("audio")?.Attribute("silent")?.Value == "true", "silent by default");
        var actions = xml.Descendants("action").ToArray(); Check(actions.Length == 2, "explicit view and read actions");
        var read = TaskNotifications.ParseActivation(actions[1].Attribute("arguments")!.Value)!;
        Check(read.S("operation") == "read" && read.A("ids")[0]!.GetValue<string>() == message.S("id"), "read roundtrip only passes message identity");
        Check(actions[1].Attribute("activationType")?.Value == "background", "reading does not request foreground");
        Check(TaskNotifications.ParseActivation("op=view&ids=abc") is null && TaskNotifications.ParseActivation("monitor=1&op=execute&ids=abc") is null, "reject unrelated or executable activation");
        Check(TaskNotifications.ParseActivation("monitor=1&op=view&ids=%2e%2e%2fsecret") is null, "reject path in activation identity");
        settings["hideNames"] = true;
        Check(!TaskNotifications.BuildXml([message], settings).Contains(message.S("title"), StringComparison.Ordinal), "private notification omits task title");
        settings["delivery"] = "markers"; settings["sound"] = true;
        Check(NotificationPolicy.Suppression(settings, "") == "仅应用内标记", "markers suppress system sound and notification");
        settings["delivery"] = "system"; settings["pausedUntil"] = -1;
        Check(NotificationPolicy.Suppression(settings, "").Length > 0, "indefinite pause suppresses");
        settings["pausedUntil"] = J.Now - 1;
        Check(NotificationPolicy.Suppression(settings, "") == "", "expired pause allows only new delivery");
        Check(NotificationPolicy.Suppression(settings, "全屏").Length > 0, "fullscreen never bypassed");
        Check(NotificationPolicy.StateSuppression(2).Length > 0 && NotificationPolicy.StateSuppression(2, manualTest: true) == "",
            "explicit channel test reaches Windows despite ambiguous BUSY; automatic notifications do not");
        Check(new[] { 1, 3, 4, 6, 7, 0 }.All(x => NotificationPolicy.StateSuppression(x, manualTest: true).Length > 0),
            "channel tests still honor absent, exclusive game, presentation, quiet and unknown system states");
        settings["pausedUntil"] = -1;
        Check(NotificationPolicy.Suppression(settings, NotificationPolicy.StateSuppression(2, manualTest: true)).Length > 0,
            "explicit channel test does not override application notification pause");
        settings["pausedUntil"] = 0; settings["delivery"] = "markers";
        Check(NotificationPolicy.Suppression(settings, NotificationPolicy.StateSuppression(2, manualTest: true)) == "仅应用内标记",
            "explicit channel test does not override markers-only preference");
        Check(TaskNotifications.TagFor("a").Length <= 16 && TaskNotifications.TagFor("a") != TaskNotifications.TagFor("b"), "stable platform-compatible notification tags");
        var batch = new JsonArray(J.Obj(("notificationTag", "batch"), ("read", true)), J.Obj(("notificationTag", "batch"), ("read", false)));
        Check(TaskNotifications.RetainedTags(batch).Contains("batch"), "reading one message keeps a batch containing another unread message");
        ((JsonObject)batch[1]!)["read"] = true;
        Check(TaskNotifications.RetainedTags(batch).Count == 0 && TaskNotifications.RetainedTags(new()).Count == 0,
            "read and cleared history no longer retain notifications");
    }
}
