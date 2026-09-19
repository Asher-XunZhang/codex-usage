using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace CodexUsage;

// Shares the host's desktop COM activation registration with TaskNotifications.
internal static class BudgetNotifications
{
    internal static string BuildXml(JsonObject[] alerts)
    {
        string ids = string.Join(',', alerts.Select(x => TaskNotifications.TagFor(x.S("id"))));
        string Args(string operation) => "budget=1&op=" + operation + "&ids=" + Uri.EscapeDataString(ids);
        string body = string.Join("；", alerts.Take(3).Select(x => x.S("name") + " · " + x.S("message")));
        if (alerts.Length > 3) body += $"；另有 {alerts.Length - 3} 项预算触发提醒";
        if (body.Length > 600) body = body[..597] + "…";
        XElement Action(string title, string operation, string activation = "background") => new("action",
            new XAttribute("content", title), new XAttribute("arguments", Args(operation)), new XAttribute("activationType", activation));
        return new XElement("toast", new XAttribute("launch", Args("view")),
            new XElement("visual", new XElement("binding", new XAttribute("template", "ToastGeneric"),
                new XElement("text", "Codex 用量 · 预算提醒"), new XElement("text", body))),
            new XElement("audio", new XAttribute("silent", "true")),
            new XElement("actions", Action("查看预算", "view", "foreground"), Action("暂停提醒 30 分钟", "pause30"), Action("本周期不再弹出", "pauseCycle")))
            .ToString(SaveOptions.DisableFormatting);
    }

    internal static JsonObject Send(JsonObject[] alerts, bool enabled)
    {
        if (!enabled) return J.Obj(("status", "suppressed"), ("reason", "当前运行不发送系统通知"));
        string blocked = NotificationPolicy.SystemSuppression();
        if (blocked.Length > 0) return J.Obj(("status", "suppressed"), ("reason", blocked));
        try
        {
            var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
            if (notifier.Setting != NotificationSetting.Enabled) return J.Obj(("status", "suppressed"), ("reason", "Windows 通知设置：" + notifier.Setting));
            var xml = new XmlDocument(); xml.LoadXml(BuildXml(alerts));
            notifier.Show(new ToastNotification(xml) { Group = "Budgets", Tag = TaskNotifications.TagFor(alerts[0].S("id")), ExpirationTime = DateTimeOffset.Now.AddDays(1) });
            return J.Obj(("status", "sent"), ("reason", "已提交 Windows；是否显示取决于系统通知设置"));
        }
        catch (Exception e) { return J.Obj(("status", "failed"), ("reason", "预算通知发送失败：" + e.Message)); }
    }
}
