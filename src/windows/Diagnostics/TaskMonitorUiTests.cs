using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexUsage;

internal static class TaskMonitorUiTests
{
    internal static async Task<JsonObject> RunAsync(string? frames = null)
    {
        var checks = new JsonArray(); void Check(bool value, string name) { if (!value) throw new InvalidOperationException("Task monitor UI: " + name); checks.Add(name); }
        var state = TaskMonitorDemo.State(); var requests = new List<JsonObject>(); TaskMonitorView? view = null;
        Task<JsonObject> Send(JsonObject request) { requests.Add(request.Copy()); state = TaskMonitorDemo.Apply(state, request); view?.Update(state); return Task.FromResult(state.Copy()); }
        view = new TaskMonitorView(Send, _ => { }); view.Update(state);
        Check(requests.Count == 0, "entering monitor list does not mark messages read or change subscriptions");
        view.Select("waiting-task"); Check(requests.Count == 0, "opening task status does not read all task messages");
        view.Select("waiting-task", "message-waiting");
        Check(requests.Count == 1 && requests[0].S("operation") == "read" && requests[0].O("payload").A("ids").Single()?.ToString() == "message-waiting", "explicit message selection reads only that exact message");
        Check(state.O("monitor").O("summary").I("unread") == 1 && state.O("monitor").O("summary").I("attention") == 1, "reading waiting message preserves unresolved attention and unrelated unread message");
        var newMessage = state.O("monitor").A("messages").Rows().Last().Copy(); newMessage["id"] = "new-incoming"; newMessage["taskID"] = "waiting-task"; state.O("monitor").A("messages").Add(newMessage); state.O("monitor").O("summary")["unread"] = 2;
        view.Update(state); Check(requests.Count == 1 && !newMessage.B("read"), "new messages remain unread while the same task detail is open");
        view.Select();
        var filter = Descendants<TextBox>(view).FirstOrDefault(x => AutomationProperties.GetName(x) == "搜索监控任务名称或项目");
        // Materialize control templates before selecting toolbar controls.
        Layout(view, new Size(700, 520)); filter = Descendants<TextBox>(view).First(x => AutomationProperties.GetName(x) == "搜索监控任务名称或项目"); filter.Text = "no matching task";
        Check(requests.Count == 1 && state.O("monitor").A("watches").Count == 4, "search only filters presentation and never stops hidden subscriptions"); filter.Text = "";
        var focus = Descendants<ComboBox>(view).First(x => AutomationProperties.GetName(x) == "常驻关注任务"); focus.SelectedItem = focus.Items.Cast<TaskMonitorUi.Choice>().First(x => x.Id == "running-task");
        Check(requests.Last().S("operation") == "focus" && state.O("monitor").O("settings").S("focusID") == "running-task", "persistent surface focus is explicit and independent of unread selection");
        Layout(view, new Size(700, 520));
        var existingViewButton = Descendants<Button>(view).First(x => x.Content?.ToString() == "查看"); var existingChoices = focus.ItemsSource;
        state.O("monitor").O("sourceStatus")["checkedAt"] = J.Now + 2; state.O("monitor")["diagnostics"] = J.Obj(("bytesRead", 12345)); view.Update(state);
        Check(ReferenceEquals(existingViewButton, Descendants<Button>(view).First(x => x.Content?.ToString() == "查看")) && ReferenceEquals(existingChoices, focus.ItemsSource), "routine source checks retain list controls and dropdown selection instead of recreating keyboard targets");
        if (frames != null) Directory.CreateDirectory(frames);
        bool originalTheme = Theme.Dark;
        try
        {
            foreach (bool dark in new[] { false, true }) foreach (double dpi in new[] { 1d, 1.25, 1.5, 1.75, 2, 2.5, 3 })
            {
                Theme.Apply(dark); var sample = TaskMonitorDemo.State();
                sample.O("settings")["appearance"] = J.Obj(("theme", dark ? "dark" : "light"));
                sample.O("monitor").A("watches").Rows().First()["title"] = new string('W', 110) + " 一个包含很长中文名称的任务，用于检查最小窗口下按钮与状态的边界";
                sample.O("monitor").O("summary")["unread"] = 120;
                var main = new MainWindow(sample, "monitor"); main.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                var content = (FrameworkElement)main.Content; main.Content = null;
                var root = new Border { Child = content, Background = Theme.Background, UseLayoutRounding = true }; TextElement.SetFontFamily(root, new FontFamily("Segoe UI, Microsoft YaHei UI")); TextElement.SetFontSize(root, 13); VisualTreeHelper.SetRootDpi(root, new DpiScale(dpi, dpi));
                Layout(root, new Size(744, 600));
                Check(ButtonsFit(root), $"main monitor controls fit minimum 760 × 640 window, {(dark ? "dark" : "light")}, {dpi * 100:0}% DPI");
                if (frames != null && dpi == 1) Save(root, Path.Combine(frames, "monitor-main-" + (dark ? "dark" : "light") + ".png"));
                var tabs = Descendants<ToggleButton>(root).Where(x => x.Content?.ToString()?.StartsWith("任务监控") == true).ToList(); Check(tabs.Count == 1 && tabs[0].IsChecked == true, "monitor is a separate top-level page with visible global navigation");
                var navPeer = FrameworkElementAutomationPeer.CreatePeerForElement(tabs[0])!;
                var messageTab = Descendants<ToggleButton>(root).Single(x => x.Content?.ToString()?.StartsWith("消息记录") == true);
                var messagePeer = FrameworkElementAutomationPeer.CreatePeerForElement(messageTab)!;
                Check(navPeer.GetName() == "任务监控 · 99+" && messagePeer.GetName() == "消息记录 · 99+", "navigation and message UIA names include the visible unread badge");
                sample.O("monitor").O("summary")["unread"] = 7;
                await main.Handle(J.Obj(("action", "state"), ("state", sample))); Layout(root, new Size(744, 600));
                Check(navPeer.GetName() == "任务监控 · 7" && messagePeer.GetName() == "消息记录 · 7", "existing navigation UIA peers update their names when unread counts change");
                var layoutSettings = new TaskMonitorSettingsWindow(null, () => sample.Copy(), r => Task.FromResult(TaskMonitorDemo.Apply(sample, r)));
                Check(layoutSettings.Resources["BackgroundBrush"] is SolidColorBrush settingsBrush && settingsBrush.Color == (dark ? Theme.Color("#17191B").Color : Theme.Color("#F5F5F2").Color), "shared reminder settings resolve the explicit " + (dark ? "dark" : "light") + " main theme");
                var settingsBody = (FrameworkElement)layoutSettings.Content; layoutSettings.Content = null; var settingsRoot = new Border { Child = settingsBody, UseLayoutRounding = true }; VisualTreeHelper.SetRootDpi(settingsRoot, new DpiScale(dpi, dpi)); Layout(settingsRoot, new Size(454, 440));
                Check(ButtonsFit(settingsRoot), $"shared reminder settings retain footer actions at minimum size and {dpi * 100:0}% DPI");
                layoutSettings.Close(); root.Child = null; settingsRoot.Child = null; await main.CompleteClose();
            }
            var limited = TaskMonitorDemo.State(); limited.O("monitor")["capabilities"] = J.Obj(("completed", true), ("waiting", false), ("failure", false), ("interrupted", false));
            var settings = new TaskMonitorSettingsWindow(null, () => limited.Copy(), Send); var body = (FrameworkElement)settings.Content; Layout(body, new Size(580, 650));
            Check(!Descendants<CheckBox>(body).First(x => AutomationProperties.GetName(x).StartsWith("等待输入")).IsEnabled && !Descendants<CheckBox>(body).First(x => AutomationProperties.GetName(x).StartsWith("明确失败")).IsEnabled, "unsupported waiting/failure options cannot imply available source capability");
            var delivery = Descendants<ComboBox>(body).First(x => AutomationProperties.GetName(x) == "提醒方式"); delivery.SelectedItem = delivery.Items.Cast<TaskMonitorUi.Choice>().First(x => x.Id == "markers");
            Check(!Descendants<CheckBox>(body).First(x => AutomationProperties.GetName(x) == "播放任务提醒声音").IsEnabled, "markers-only delivery visibly disables sound"); settings.Close();
        }
        finally { Theme.Apply(originalTheme); }
        CheckCoreContract(Check);
        CheckPickerSnapshots(Check);
        await Task.CompletedTask; return J.Obj(("success", true), ("checks", checks));
    }
    private static void CheckPickerSnapshots(Action<bool, string> check)
    {
        var cached = TaskMonitorDemo.State(); var monitor = cached.O("monitor");
        monitor["tasks"] = new JsonArray(); monitor["watches"] = new JsonArray();
        monitor["diagnostics"] = J.Obj(("backfillPending", 1), ("discoveryPending", 1), ("backfillBytes", 0));
        var calls = new List<JsonObject>();
        Task<JsonObject> Send(JsonObject request)
        {
            calls.Add(request.Copy());
            if (request.S("operation") != "add") throw new InvalidOperationException("Snapshot refresh must not request disk discovery");
            var reply = cached.Copy(); reply["monitorResult"] = J.Obj(("ok", false), ("message", "合成轮次变化，保留选择"), ("items", new JsonArray(J.Obj(("id", "cache-keep"), ("ok", false), ("error", "所选轮次需要再次核对"))))); return Task.FromResult(reply);
        }
        JsonObject TaskRow(string id, string status = "running") => J.Obj(("id", id), ("turnID", "turn-" + id), ("title", id == "cache-keep" ? "保留当前选择与键盘位置" : "候选任务 " + id), ("project", "测试项目"), ("status", status), ("selectable", status == "running"), ("startedAt", J.Now - 300));
        var picker = new TaskMonitorPickerWindow(null, () => cached.Copy(), Send);
        try
        {
            var root = (FrameworkElement)picker.Content; var size = new Size(600, 470); Layout(root, size);
            check(Descendants<TextBlock>(root).Any(x => x.Text.StartsWith("正在核对历史记录")), "empty picker explicitly communicates pending history verification");
            monitor["tasks"] = new JsonArray(TaskRow("cache-unknown", "unknown"), TaskRow("cache-keep")); picker.RefreshSnapshot(); Layout(root, size);
            CheckBox Box(string id) => Descendants<CheckBox>(root).Single(x => AutomationProperties.GetAutomationId(x) == "monitor-candidate-" + id);
            var uncertain = Box("cache-unknown"); var keep = Box("cache-keep");
            check(!uncertain.IsEnabled && keep.IsEnabled, "background cache arrival populates the open picker without a manual discovery call");
            keep.IsChecked = true; keep.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            FocusManager.SetFocusedElement(picker, keep);
            monitor["tasks"] = new JsonArray(TaskRow("cache-unknown"), TaskRow("cache-keep"), TaskRow("cache-new"));
            monitor.O("diagnostics")["backfillPending"] = 0; monitor.O("diagnostics")["discoveryPending"] = 0;
            picker.RefreshSnapshot(); Layout(root, size);
            check(ReferenceEquals(uncertain, Box("cache-unknown")) && uncertain.IsEnabled && Box("cache-new").IsEnabled, "a pending task becomes selectable in place and newly confirmed candidates appear automatically");
            check(ReferenceEquals(keep, Box("cache-keep")) && keep.IsChecked == true && ReferenceEquals(FocusManager.GetFocusedElement(picker), keep), "candidate updates retain the selected checkbox and its logical keyboard focus");
            check(Descendants<TextBlock>(root).Any(x => x.Text.StartsWith("已完成当前核对")), "completion of backfill removes the pending status without requiring a reopen");
            var existingCards = Descendants<CheckBox>(root).ToArray();
            monitor.O("diagnostics")["backfillBytes"] = 4000000; monitor.O("sourceStatus")["checkedAt"] = J.Now;
            picker.RefreshSnapshot(); Layout(root, size);
            check(existingCards.SequenceEqual(Descendants<CheckBox>(root)) && calls.Count == 0, "unchanged candidates are not rebuilt and background ticks never send discover requests");
            for (int i = 0; i < 24; i++) monitor.A("tasks").Add(TaskRow("scroll-" + i)); picker.RefreshSnapshot(); Layout(root, size);
            var scroll = Descendants<ScrollViewer>(root).First(x => x.Content is StackPanel); scroll.ScrollToVerticalOffset(210); Layout(root, size);
            double previous = scroll.VerticalOffset;
            monitor.A("tasks").Add(TaskRow("scroll-new")); picker.RefreshSnapshot(); Layout(root, size);
            check(previous > 0 && Math.Abs(scroll.VerticalOffset - previous) < 1, "appending a newly confirmed task preserves the reader's scroll position");
            var selectedTask = monitor.A("tasks").Rows().Single(x => x.S("id") == "cache-keep"); selectedTask["turnID"] = "next-round"; picker.RefreshSnapshot(); Layout(root, size);
            check(keep.IsChecked == true && Descendants<Button>(root).Any(x => x.Content?.ToString() == "改选当前新一轮" && x.Visibility == Visibility.Visible), "background round changes preserve the selection and offer an explicit new-round action");
            Descendants<Button>(root).Single(x => x.Content?.ToString() == "开启 1 项提醒").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(calls.Count == 1 && calls[0].O("payload").A("selections").Rows().Single().S("turnID") == "turn-cache-keep", "saving after background updates still submits the originally selected round");
        }
        finally { picker.Close(); }
    }
    private static void CheckCoreContract(Action<bool, string> check)
    {
        string sandbox = Path.Combine(Path.GetTempPath(), "CodexUsageMonitorUI-" + Guid.NewGuid().ToString("N"));
        string home = Path.Combine(sandbox, "home"); Directory.CreateDirectory(Path.Combine(home, "sessions"));
        try
        {
            void Record(string id, string turn)
            {
                var meta = J.Obj(("type", "session_meta"), ("payload", J.Obj(("id", id), ("cwd", "D:\\Test Project"))));
                var started = J.Obj(("timestamp", J.Now), ("type", "event_msg"), ("payload", J.Obj(("type", "task_started"), ("turn_id", turn), ("started_at", J.Now))));
                File.WriteAllText(Path.Combine(home, "sessions", "rollout-" + id + ".jsonl"), J.Text(meta) + "\n" + J.Text(started) + "\n", new System.Text.UTF8Encoding(false));
            }
            Record("task-pick-a", "turn-pick-a"); Record("task-pick-b", "turn-pick-b");
            using var service = new TaskMonitorService(Path.Combine(sandbox, "monitor.json"), home); service.Poll(home, true);
            bool rejectSettings = false;
            JsonObject Snapshot() => J.Obj(("settings", J.Obj(("appearance", J.Obj(("theme", "dark"))))), ("monitor", service.Snapshot()));
            Task<JsonObject> Send(JsonObject request)
            {
                var payload = request.O("payload").Copy();
                if (rejectSettings && request.S("operation") == "settings") payload.O("patch")["pausedUntil"] = -2;
                var result = service.Apply(request.S("operation"), payload, home); var state = Snapshot(); state["monitorResult"] = result; return Task.FromResult(state);
            }
            var settings = new TaskMonitorSettingsWindow(null, Snapshot, Send); var root = (FrameworkElement)settings.Content; Layout(root, new Size(580, 640));
            var delivery = Descendants<ComboBox>(root).First(x => AutomationProperties.GetName(x) == "提醒方式"); delivery.SelectedItem = delivery.Items.Cast<TaskMonitorUi.Choice>().First(x => x.Id == "markers");
            var privacy = Descendants<CheckBox>(root).First(x => AutomationProperties.GetName(x).StartsWith("系统通知中隐藏")); privacy.IsChecked = true;
            var pause = Descendants<ComboBox>(root).First(x => AutomationProperties.GetName(x) == "暂停任务通知"); pause.SelectedItem = pause.Items.Cast<TaskMonitorUi.Choice>().First(x => x.Id == "manual");
            var save = Descendants<Button>(root).Single(x => x.Content?.ToString() == "保存");
            rejectSettings = true; save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(service.Snapshot().O("settings").S("delivery") == "system" && settings.IsEnabled && privacy.IsChecked == true && delivery.SelectedItem is TaskMonitorUi.Choice c && c.Id == "markers", "a real service validation failure preserves the draft and leaves the prior saved settings unchanged");
            check(Descendants<TextBlock>(root).Any(x => x.Text.StartsWith("未保存：")), "failed save has visible feedback and a retryable form");
            rejectSettings = false; save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var saved = service.Snapshot().O("settings");
            check(saved.S("delivery") == "markers" && saved.B("hideNames") && saved.N("pausedUntil") == -1, "settings save traverses real service Apply contract and persists delivery/privacy/pause");
            check(File.Exists(Path.Combine(sandbox, "monitor.json")), "UI settings were durably saved by the real monitor service");
            settings.Close();
            var picker = new TaskMonitorPickerWindow(null, Snapshot, Send); var pickerRoot = (FrameworkElement)picker.Content; Layout(pickerRoot, new Size(454, 450));
            var boxes = Descendants<CheckBox>(pickerRoot).Where(x => x.IsEnabled).ToList();
            check(boxes.Count == 2, "picker discovers two real zero-token lifecycle tasks through the service snapshot");
            var addBeforeSelection = Descendants<Button>(pickerRoot).Single(x => x.Content?.ToString() == "开启 0 项提醒");
            var addPeer = FrameworkElementAutomationPeer.CreatePeerForElement(addBeforeSelection)!;
            check(addPeer.GetName() == "开启 0 项提醒", "unselected picker button UIA name matches its initial count");
            int selectedCount = 0;
            foreach (var box in boxes)
            {
                box.IsChecked = true; box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); selectedCount++;
                Layout(pickerRoot, new Size(454, 450));
                check(addPeer.GetName() == "开启 " + selectedCount + " 项提醒", "the same picker button UIA peer follows selection count " + selectedCount);
            }
            var add = Descendants<Button>(pickerRoot).Single(x => x.Content?.ToString() == "开启 2 项提醒");
            check(add.IsEnabled && ButtonsFit(pickerRoot), "multi-select updates fixed footer count and actions fit the narrow picker");
            // Finish one observed turn while the selection dialog remains open.
            var ended = J.Obj(("timestamp", J.Now + .02), ("type", "event_msg"), ("payload", J.Obj(("type", "task_complete"), ("turn_id", "turn-pick-b"), ("completed_at", J.Now + .02))));
            File.AppendAllText(Path.Combine(home, "sessions", "rollout-task-pick-b.jsonl"), J.Text(ended) + "\n", new System.Text.UTF8Encoding(false));
            add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var after = service.Snapshot();
            check(after.A("watches").Rows().Single(x => x.S("id") == "task-pick-a").B("active") && !after.A("watches").Rows().Single(x => x.S("id") == "task-pick-b").B("active"), "saving the picker binds observed rounds and preserves a completion race instead of following a new round");
            Layout(pickerRoot, new Size(454, 450));
            check(Descendants<Button>(pickerRoot).Any(x => x.Content?.ToString() == "查看本轮结果" && x.Visibility == Visibility.Visible), "a task ending during selection offers a concrete result entry");
            check(after.A("messages").Rows().Single().S("delivery") != "pending", "completed-during-selection result does not queue a late system notification");
            var done = Descendants<Button>(pickerRoot).Single(x => x.Content?.ToString() == "完成");
            check(FrameworkElementAutomationPeer.CreatePeerForElement(done)?.GetName() == "完成" && addPeer.GetName() == "开启 0 项提醒", "renamed completion action and cleared selection count have current UIA names after saving");
            picker.Close();
        }
        finally
        {
            string full = Path.GetFullPath(sandbox), temp = Path.GetFullPath(Path.GetTempPath());
            if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("CodexUsageMonitorUI-", StringComparison.Ordinal) && Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
    private static void Layout(FrameworkElement root, Size size) { root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout(); }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T item) yield return item;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    private static bool ButtonsFit(FrameworkElement root)
    {
        foreach (var button in Descendants<ButtonBase>(root).Where(x => x.Visibility == Visibility.Visible && x.ActualWidth > 0 && x.Content is string))
        {
            var bounds = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
            // Scrollable list rows may be below the viewport. They must still fit horizontally.
            if (bounds.Left < -1 || bounds.Right > root.ActualWidth + 1) return false;
            foreach (var text in Descendants<TextBlock>(button)) if (text.Text.Length > 0 && text.ActualWidth + .5 < text.DesiredSize.Width) return false;
            if (button.HorizontalContentAlignment != HorizontalAlignment.Center && button is Button) return false;
        }
        return true;
    }
    private static void Save(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
}
