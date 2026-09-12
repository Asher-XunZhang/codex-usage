using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace CodexUsage;

// Desktop COM activation keeps notification clicks independent of tray visibility.
// No prompt, task output or path is carried in an activation argument: only local message IDs.
internal sealed class TaskNotifications : IDisposable
{
    internal const string Group = "TaskMonitor";
    private readonly Action<JsonObject> activate;
    private readonly bool enabled;
    private bool registered;
    private string error = "";
    private string systemSetting = "unknown", systemSuppression = "";
    private double lastStatus;
    private string reconciled = "";
    internal TaskNotifications(Action<JsonObject> activate, bool enabled = true)
    {
        this.activate = activate; this.enabled = enabled;
        if (!enabled) return;
        try { ToastNotificationManagerCompat.OnActivated += Activated; registered = true; }
        catch (Exception e) { error = "系统通知初始化失败：" + e.Message; }
    }
    internal static bool IsActivationLaunch()
    {
        try { return ToastNotificationManagerCompat.WasCurrentProcessToastActivated(); }
        catch { return Environment.GetCommandLineArgs().Any(x => x.Equals("-ToastActivated", StringComparison.OrdinalIgnoreCase)); }
    }
    private void Activated(ToastNotificationActivatedEventArgsCompat args)
    {
        try { var request = ParseActivation(args.Argument); if (request is not null) activate(request); }
        catch (Exception e) { error = "通知操作未完成：" + e.Message; }
    }
    internal static JsonObject? ParseActivation(string arguments)
    {
        if (arguments.Length > 8192) return null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string part in arguments.Split('&'))
        {
            int equal = part.IndexOf('='); if (equal < 1) continue;
            values[Uri.UnescapeDataString(part[..equal])] = Uri.UnescapeDataString(part[(equal + 1)..]);
        }
        if (values.GetValueOrDefault("monitor") != "1") return null;
        string operation = values.GetValueOrDefault("op", "view");
        if (operation is not ("view" or "read")) return null;
        string[] ids = values.GetValueOrDefault("ids", "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length <= 160 && x.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':' or '.')).Distinct().Take(32).ToArray();
        if (ids.Length == 0) return null;
        return J.Obj(("action", "monitor-notification"), ("operation", operation), ("ids", ids));
    }
    internal static string TagFor(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..16];
    internal static string BuildXml(JsonObject[] messages, JsonObject settings, bool test = false)
    {
        string ids = string.Join(',', messages.Select(x => x.S("id")));
        string Args(string op) => "monitor=1&op=" + op + "&ids=" + Uri.EscapeDataString(ids);
        string kind = messages.Length == 1 ? messages[0].S("status") : "multiple";
        string headline = test ? "任务提醒测试" : kind switch
        {
            "completed" => "本轮已结束", "waiting" => "有任务需要你处理", "failed" => "本轮执行失败", "interrupted" => "本轮已中断",
            _ => $"{messages.Length} 条任务消息"
        };
        string Label(JsonObject row) => row.S("status") switch { "completed" => "本轮结束", "waiting" => "需要处理", "failed" => "执行失败", "interrupted" => "已中断", _ => "状态更新" };
        string title = settings.B("hideNames") ? "你关注的 Codex 任务有新状态" : string.Join("；", messages.Take(2).Select(x => x.S("title", "Codex 任务") + (messages.Length > 1 ? " · " + Label(x) : "")));
        if (title.Length > 160) title = title[..157] + "…";
        var toast = new XElement("toast", new XAttribute("launch", Args("view")), new XAttribute("duration", test ? "long" : "short"),
            new XElement("visual", new XElement("binding", new XAttribute("template", "ToastGeneric"),
                new XElement("text", "Codex 用量 · " + headline), new XElement("text", title),
                new XElement("text", test ? "点击此通知打开任务监控。" : "查看本轮记录；当前任务状态以监控详情为准。"))),
            new XElement("audio", new XAttribute("silent", settings.B("sound") ? "false" : "true")));
        if (settings.B("sound")) toast.Element("audio")!.Add(new XAttribute("src", "ms-winsoundevent:Notification.Default"));
        if (!test)
            toast.Add(new XElement("actions",
                new XElement("action", new XAttribute("content", "查看详情"), new XAttribute("arguments", Args("view")), new XAttribute("activationType", "foreground")),
                new XElement("action", new XAttribute("content", "标为已读"), new XAttribute("arguments", Args("read")), new XAttribute("activationType", "background"))));
        return toast.ToString(SaveOptions.DisableFormatting);
    }
    internal JsonObject Status()
    {
        if (!enabled) systemSetting = "disabled-for-test";
        if (J.Now - lastStatus > 30)
        {
            lastStatus = J.Now; systemSuppression = NotificationPolicy.SystemSuppression();
            if (registered) try { systemSetting = ToastNotificationManagerCompat.CreateToastNotifier().Setting.ToString(); } catch (Exception e) { error = e.Message; }
        }
        return J.Obj(("available", registered), ("error", error), ("systemSetting", systemSetting), ("suppression", systemSuppression));
    }
    internal JsonObject Send(JsonObject[] messages, JsonObject settings, bool test = false)
    {
        string blocked = NotificationPolicy.Suppression(settings, test ? NotificationPolicy.SystemSuppression(manualTest: true) : null);
        if (blocked.Length > 0) return J.Obj(("status", "suppressed"), ("reason", blocked));
        if (!enabled || !registered) return J.Obj(("status", "failed"), ("reason", error.Length > 0 ? error : "系统通知通道不可用"));
        try
        {
            var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
            if (notifier.Setting != NotificationSetting.Enabled) return J.Obj(("status", "suppressed"), ("reason", "Windows 通知设置：" + notifier.Setting));
            var xml = new XmlDocument(); xml.LoadXml(BuildXml(messages, settings, test));
            string tag = test ? "TaskMonitorTest" : TagFor(messages[0].S("id"));
            var toast = new ToastNotification(xml) { Tag = tag, Group = Group, ExpirationTime = DateTimeOffset.Now.AddDays(1) };
            notifier.Show(toast); error = ""; reconciled = "";
            return J.Obj(("status", "sent"), ("reason", "已提交 Windows；是否显示取决于系统通知设置"), ("notificationTag", tag));
        }
        catch (Exception e) { error = "系统通知发送失败：" + e.Message; return J.Obj(("status", "failed"), ("reason", error)); }
    }
    internal void Reconcile(JsonArray messages)
    {
        if (!registered) return;
        var retained = RetainedTags(messages);
        string signature = string.Join('|', retained.Order(StringComparer.Ordinal));
        if (reconciled == "ok:" + signature) return;
        try
        {
            // History also catches notifications whose read records were cleared or expired
            // while the app was closed; other apps and budget alerts are untouched.
            foreach (var toast in ToastNotificationManagerCompat.History.GetHistory())
                if (toast.Group == Group && toast.Tag != "TaskMonitorTest" && !retained.Contains(toast.Tag))
                    ToastNotificationManagerCompat.History.Remove(toast.Tag, Group);
            reconciled = "ok:" + signature;
        }
        catch (Exception e) { error = "系统通知清理失败：" + e.Message; }
    }
    internal static HashSet<string> RetainedTags(JsonArray messages) => messages.Rows()
        .Where(x => x.S("notificationTag").Length > 0 && !x.B("read") && !x.B("resolved"))
        .Select(x => x.S("notificationTag")).ToHashSet(StringComparer.Ordinal);
    public void Dispose() { if (registered) ToastNotificationManagerCompat.OnActivated -= Activated; registered = false; }
}

internal static class NotificationPolicy
{
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int length);
    internal static string Suppression(JsonObject settings, string? system = null)
    {
        if (settings.S("delivery", "system") == "markers") return "仅应用内标记";
        double paused = settings.N("pausedUntil") ?? 0;
        if (paused < 0 || paused > J.Now) return "任务通知已暂停";
        return system ?? SystemSuppression();
    }
    internal static string StateSuppression(int state, bool manualTest = false) => state switch
    {
        5 => "",
        // Legacy BUSY can also be caused by a noninteractive fullscreen overlay.
        // An explicitly requested channel test may reach the standard toast API;
        // Windows still owns DND/banner policy. Automatic delivery stays suppressed.
        2 when manualTest => "",
        2 => "Windows 报告忙碌（全屏应用、叠加层或演示状态）",
        1 => "系统当前锁定、屏保中或用户不在场",
        3 => "系统当前运行全屏 Direct3D 应用",
        4 => "系统当前处于演示模式",
        6 => "系统当前处于初始化静默期",
        7 => "Windows 报告应用占用通知状态",
        _ => "无法确认系统是否允许打扰"
    };
    internal static string SystemSuppression(bool manualTest = false)
    {
        if (Environment.GetEnvironmentVariable("CODEX_USAGE_TEST_BACKGROUND") == "1") return "后台验收不发送系统通知";
        try
        {
            if (SHQueryUserNotificationState(out int state) != 0) return "无法确认系统是否允许打扰";
            string blocked = StateSuppression(state, manualTest);
            if (blocked.Length > 0) return blocked;
            IntPtr foreground = GetForegroundWindow();
            if (foreground != IntPtr.Zero && GetWindowRect(foreground, out var r))
            {
                var name = new StringBuilder(256); GetClassName(foreground, name, name.Capacity);
                var bounds = System.Windows.Forms.Screen.FromHandle(foreground).Bounds;
                if (name.ToString() is not ("Progman" or "WorkerW") && r.Left <= bounds.Left && r.Top <= bounds.Top && r.Right >= bounds.Right && r.Bottom >= bounds.Bottom) return "前台应用正在全屏";
            }
        }
        catch (Exception) { return "无法确认系统通知状态"; }
        return "";
    }
}
