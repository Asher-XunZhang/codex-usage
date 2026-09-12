using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace CodexUsage;

/// <summary>Shared reminder preferences; only an explicit save updates the host.</summary>
internal sealed class TaskMonitorSettingsWindow : Window
{
    private readonly Func<JsonObject> read;
    private readonly Func<JsonObject, Task<JsonObject>> send;
    private readonly JsonObject original;
    private readonly StackPanel body = new();
    private readonly TextBlock feedback = TaskMonitorUi.Text("", 12);
    private readonly TextBlock deliveryNote = TaskMonitorUi.Text("", 12);
    private readonly TextBlock pausedNote = TaskMonitorUi.Text("", 12);
    private readonly ComboBox delivery, mode, retention, pause;
    private readonly CheckBox sound, completed, attention, failures, hideNames;
    private bool saving;
    internal TaskMonitorSettingsWindow(Window? owner, Func<JsonObject> read, Func<JsonObject, Task<JsonObject>> send)
    {
        this.read = read; this.send = send; original = read().O("monitor").O("settings").Copy();
        if (owner != null) Owner = owner;
        Theme.ApplyTo(Resources, Theme.Resolve(read().O("settings"), "main"));
        Title = "任务提醒设置"; Width = 620; Height = 720; MinWidth = 470; MinHeight = 480;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "BackgroundBrush"); SetResourceReference(ForegroundProperty, "ForegroundBrush");
        var root = new DockPanel { Margin = new Thickness(20) };
        var title = TaskMonitorUi.Text("任务提醒设置", 24, true); title.Margin = new Thickness(0, 0, 0, 10); DockPanel.SetDock(title, Dock.Top); root.Children.Add(title);
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; footer.Children.Add(feedback);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; var cancel = TaskMonitorUi.Button("取消", Close, 82); cancel.Margin = new Thickness(0, 0, 8, 0); buttons.Children.Add(cancel);
        buttons.Children.Add(TaskMonitorUi.Button("保存", async () => await Save(), 86)); footer.Children.Add(buttons); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }); Content = root;
        delivery = Picker("提醒方式", [new("system", "系统通知＋应用内标记"), new("markers", "仅应用内标记")], original.S("delivery", "system"));
        body.Children.Add(deliveryNote); sound = Toggle("播放任务提醒声音", original.B("sound"));
        Divider("提醒事件");
        var caps = read().O("monitor").O("capabilities");
        completed = Toggle("明确本轮结束时提醒", original.B("notifyCompleted", true));
        attention = Toggle("等待输入或审批时提醒", original.B("notifyAttention", true)); attention.IsEnabled = caps.B("waiting");
        if (!attention.IsEnabled) body.Children.Add(TaskMonitorUi.Text("当前来源尚不支持可靠的等待状态，这个选项暂不生效。", 11));
        failures = Toggle("明确失败或中断时提醒", original.B("notifyFailures", true)); failures.IsEnabled = caps.B("failure") || caps.B("interrupted");
        if (!failures.IsEnabled) body.Children.Add(TaskMonitorUi.Text("当前来源尚不支持可靠的失败或中断分类，这个选项暂不生效。", 11));
        else if (!caps.B("failure")) body.Children.Add(TaskMonitorUi.Text("当前仅支持明确中断，尚不支持区分执行失败。", 11));
        body.Children.Add(TaskMonitorUi.Text("事件开关只控制系统提醒；任务状态与消息记录继续保留。", 11));
        Divider("暂停与隐私");
        pause = Picker("暂停任务通知", [new("keep", "保留当前暂停状态"), new("resume", "现在恢复"), new("30", "暂停 30 分钟"), new("60", "暂停 1 小时"), new("manual", "直到手动恢复")], "keep");
        body.Children.Add(pausedNote); hideNames = Toggle("系统通知中隐藏任务名称与项目", original.B("hideNames"));
        body.Children.Add(TaskMonitorUi.Text("仅影响任务提醒，不暂停预算提醒；不会自动展开浮窗或抢走游戏焦点。", 11));
        Divider("新监控与消息记录");
        mode = Picker("默认提醒范围", [new("once", "仅本轮结束后提醒"), new("each", "每轮结束都提醒")], original.S("defaultMode", "once"));
        retention = Picker("已读历史保留", [new("7", "7 天"), new("30", "30 天"), new("90", "90 天")], original.I("retentionDays", 30).ToString());
        body.Children.Add(TaskMonitorUi.Text("未读和仍需处理的消息不自动清理；退出 Codex 用量后停止后台监控，重新启动时核对离线记录。", 11));
        Divider("提醒诊断");
        body.Children.Add(TaskMonitorUi.Text(TaskMonitorUi.Capabilities(caps), 11));
        var diagnostic = new WrapPanel();
        diagnostic.Children.Add(TaskMonitorUi.Button("发送测试通知", async () => await Test(), 122));
        var os = TaskMonitorUi.Button("Windows 通知设置", async () => await Invoke(J.Obj(("action", "monitor-notification-settings"))), 152); os.Margin = new Thickness(8, 0, 0, 0); diagnostic.Children.Add(os); body.Children.Add(diagnostic);
        body.Children.Add(TaskMonitorUi.Text("测试使用已保存的提醒设置。主动测试会向 Windows 提交通知，是否显示横幅由系统设置决定；仅应用内标记、暂停或全屏时仍遵守当前限制。提交系统不代表用户已看见。", 11));
        delivery.SelectionChanged += (_, _) => UpdateNotes(); pause.SelectionChanged += (_, _) => UpdateNotes(); UpdateNotes();
        Closing += (_, e) => { if (saving) e.Cancel = true; }; KeyDown += (_, e) => { if (e.Key == Key.Escape && !saving) { Close(); e.Handled = true; } };
    }
    private ComboBox Picker(string title, TaskMonitorUi.Choice[] choices, string selected)
    {
        body.Children.Add(TaskMonitorUi.Text(title, 13, true));
        var picker = new ComboBox { ItemsSource = choices, MinHeight = 34, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 8) };
        picker.SelectedItem = choices.FirstOrDefault(x => x.Id == selected) ?? choices[0]; AutomationProperties.SetName(picker, title); body.Children.Add(picker); return picker;
    }
    private CheckBox Toggle(string text, bool value)
    {
        var box = new CheckBox { Content = TaskMonitorUi.Text(text, 13), IsChecked = value, HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 5, 0, 6) };
        AutomationProperties.SetName(box, text); body.Children.Add(box); return box;
    }
    private void Divider(string title) { body.Children.Add(new Separator { Margin = new Thickness(0, 13, 0, 12) }); body.Children.Add(TaskMonitorUi.Text(title, 16, true)); }
    private static string Value(ComboBox picker) => (picker.SelectedItem as TaskMonitorUi.Choice)?.Id ?? "";
    private void UpdateNotes()
    {
        bool markers = Value(delivery) == "markers"; sound.IsEnabled = !markers;
        deliveryNote.Text = markers ? "只更新圆环、侧签、托盘和主面板中的静态标记，不发送系统通知或声音。" : "声音默认关闭；系统限制期间不使用自定义弹窗绕过勿扰。";
        if (markers && read().O("settings").S("mode") == "tray") deliveryNote.Text += " 图标若位于托盘折叠区，请展开托盘查看。";
        double until = original.N("pausedUntil") ?? 0;
        pausedNote.Text = until < 0 ? "当前已暂停，直到手动恢复。任务状态和消息仍会更新。" : until > DateTimeOffset.UtcNow.ToUnixTimeSeconds() ? "当前暂停至 " + TaskMonitorUi.Time(until) + "。任务状态和消息仍会更新。" : "当前未暂停任务通知。恢复时不逐条补弹历史消息。";
    }
    private async Task Save()
    {
        if (saving) return; saving = true; IsEnabled = false;
        try
        {
            var patch = J.Obj(("delivery", Value(delivery)), ("sound", sound.IsChecked == true), ("notifyCompleted", completed.IsChecked == true), ("hideNames", hideNames.IsChecked == true), ("defaultMode", Value(mode)), ("retentionDays", int.Parse(Value(retention))));
            if (attention.IsEnabled) patch["notifyAttention"] = attention.IsChecked == true;
            if (failures.IsEnabled) patch["notifyFailures"] = failures.IsChecked == true;
            string pauseValue = Value(pause);
            if (pauseValue != "keep") patch["pausedUntil"] = pauseValue == "manual" ? -1L : pauseValue == "resume" ? 0L : DateTimeOffset.UtcNow.ToUnixTimeSeconds() + int.Parse(pauseValue) * 60L;
            var result = await send(J.Obj(("action", "monitor"), ("operation", "settings"), ("payload", J.Obj(("patch", patch))))); TaskMonitorUi.EnsureSuccess(result);
            saving = false; Close();
        }
        catch (Exception error) { feedback.Text = "未保存：" + error.Message + "。修改仍保留，可重试。"; }
        finally { saving = false; IsEnabled = true; }
    }
    private async Task Test() => await Invoke(J.Obj(("action", "monitor-test")));
    private async Task Invoke(JsonObject request)
    {
        if (saving) return; saving = true; IsEnabled = false;
        try { var result = await send(request); TaskMonitorUi.EnsureSuccess(result); feedback.Text = result.O("monitorResult").S("message", "已打开 Windows 通知设置"); }
        catch (Exception error) { feedback.Text = "操作未完成：" + error.Message; }
        finally { saving = false; IsEnabled = true; }
    }
}
