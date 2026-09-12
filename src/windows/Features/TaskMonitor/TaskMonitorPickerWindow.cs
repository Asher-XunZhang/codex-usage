using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace CodexUsage;

/// <summary>Selections retain the observed turn ID until the host validates them at save time.</summary>
internal sealed class TaskMonitorPickerWindow : Window
{
    private readonly Func<JsonObject> read;
    private readonly Func<JsonObject, Task<JsonObject>> send;
    private readonly Dictionary<string, JsonObject> selections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> errors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CandidateRow> candidates = new(StringComparer.Ordinal);
    private readonly DispatcherTimer snapshotTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private JsonObject state;
    private readonly TextBox search = new() { MinHeight = 34 };
    private readonly StackPanel list = new();
    private readonly TextBlock feedback = TaskMonitorUi.Text("", 12);
    private readonly TextBlock discoveryStatus = TaskMonitorUi.Text("正在核对本机任务…", 12);
    private readonly TextBlock scope = TaskMonitorUi.Text("", 12);
    private readonly ScrollViewer scroller;
    private readonly Border empty;
    private readonly TextBlock emptyText = TaskMonitorUi.Text("", 13);
    private readonly ComboBox mode = new() { MinWidth = 210, MinHeight = 34 };
    private readonly Button save, cancel, viewResult;
    private string resultTask = "", resultMessage = "";
    private bool busy, closed;
    internal event EventHandler? Changed;
    internal Action<string, string>? ViewMessage;

    internal TaskMonitorPickerWindow(Window? owner, Func<JsonObject> read, Func<JsonObject, Task<JsonObject>> send)
    {
        this.read = read; this.send = send; state = read().Copy();
        if (owner != null) Owner = owner;
        Theme.ApplyTo(Resources, Theme.Resolve(state.O("settings"), "main"));
        Title = "选择任务 · 开启执行提醒"; Width = 690; Height = 660; MinWidth = 470; MinHeight = 490;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "BackgroundBrush"); SetResourceReference(ForegroundProperty, "ForegroundBrush");
        var root = new DockPanel { Margin = new Thickness(20) };
        var heading = new StackPanel(); heading.Children.Add(TaskMonitorUi.Text("选择正在执行的任务", 22, true));
        heading.Children.Add(TaskMonitorUi.Text("支持多选。只开启本工具的提醒，不控制 Codex 的执行。", 12));
        var row = new DockPanel { Margin = new Thickness(0, 9, 0, 8) };
        var refresh = TaskMonitorUi.Button("重新查找", async () => await Discover(), 92); refresh.Margin = new Thickness(10, 0, 0, 0); DockPanel.SetDock(refresh, Dock.Right); row.Children.Add(refresh); row.Children.Add(search); heading.Children.Add(row);
        AutomationProperties.SetName(search, "搜索任务名称或项目"); search.ToolTip = "搜索任务名称或项目";
        TaskMonitorUi.SearchStyle(search);
        discoveryStatus.MinHeight = 36; heading.Children.Add(discoveryStatus);
        heading.Children.Add(feedback); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        mode.ItemsSource = new[] { new TaskMonitorUi.Choice("once", "仅本轮结束后提醒"), new TaskMonitorUi.Choice("each", "每轮结束都提醒") };
        mode.SelectedIndex = state.O("monitor").O("settings").S("defaultMode") == "each" ? 1 : 0;
        AutomationProperties.SetName(mode, "提醒范围"); footer.Children.Add(mode); footer.Children.Add(scope);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        viewResult = TaskMonitorUi.Button("查看本轮结果", () => { Close(); ViewMessage?.Invoke(resultTask, resultMessage); }, 118); viewResult.Visibility = Visibility.Collapsed; viewResult.Margin = new Thickness(0, 0, 8, 0); actions.Children.Add(viewResult);
        cancel = TaskMonitorUi.Button("取消", Close, 82); cancel.Margin = new Thickness(0, 0, 8, 0); actions.Children.Add(cancel);
        save = TaskMonitorUi.Button("开启 0 项提醒", async () => await Save(), 144); actions.Children.Add(save); footer.Children.Add(actions);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        empty = TaskMonitorUi.Card(emptyText); list.Children.Add(empty);
        scroller = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        root.Children.Add(scroller); Content = root;
        search.TextChanged += (_, _) => Render(); mode.SelectionChanged += (_, _) => UpdateFooter();
        Closing += (_, e) => { if (busy) e.Cancel = true; };
        Closed += (_, _) => { closed = true; snapshotTimer.Stop(); };
        snapshotTimer.Tick += (_, _) => RefreshSnapshot();
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !busy) { Close(); e.Handled = true; } };
        Loaded += async (_, _) => { snapshotTimer.Start(); await Discover(); }; Render(); UpdateFooter();
    }

    // The main window receives authoritative host snapshots even while its modal
    // picker is open. Observing that cache must never initiate another disk scan.
    internal void RefreshSnapshot()
    {
        if (busy || closed) return;
        try
        {
            var next = read().Copy();
            bool changed = !JsonNode.DeepEquals(CandidateState(state), CandidateState(next));
            state = next;
            UpdateDiscovery();
            if (changed) { Render(); UpdateFooter(); }
        }
        catch (Exception error) { feedback.Text = "后台状态暂不可用，当前选择已保留：" + error.Message; }
    }
    private static JsonObject CandidateState(JsonObject snapshot)
    {
        var monitor = snapshot.O("monitor");
        return J.Obj(("tasks", monitor.A("tasks").DeepClone()),
            ("watched", new JsonArray(monitor.A("watches").Rows().Where(x => x.B("active")).Select(x => (JsonNode?)JsonValue.Create(x.S("id"))).OrderBy(x => x?.ToString(), StringComparer.Ordinal).ToArray())),
            ("source", monitor.O("sourceStatus").S("status")));
    }
    private void UpdateDiscovery()
    {
        var monitor = state.O("monitor"); var diagnostics = monitor.O("diagnostics");
        int pending = diagnostics.I("backfillPending") + diagnostics.I("discoveryPending");
        int available = monitor.A("tasks").Rows().Count(Supported);
        discoveryStatus.Text = pending > 0
            ? $"正在核对历史记录 · 尚有 {pending} 项待确认\n已确认任务会自动加入列表，当前选择保持不变。"
            : monitor.O("sourceStatus").S("status") == "unavailable"
                ? "目前无法读取监控来源，已保留当前选择。恢复连接后候选任务会自动更新。"
                : monitor.Count == 0 || !monitor.O("sourceStatus").ContainsKey("status")
                    ? "正在连接后台，候选任务会自动显示。"
                    : $"已完成当前核对 · {available} 项任务状态可确认\n仅显示本机可读取的任务，不包含其他设备或未落盘的任务。";
        emptyText.Text = pending > 0 ? "正在核对历史记录。已确认正在执行的任务会自动显示，无需反复重新查找。"
            : monitor.O("sourceStatus").S("status") == "unavailable" ? "目前无法读取任务。检查数据目录或恢复连接后，重新查找。"
            : search.Text.Trim().Length > 0 ? "没有符合搜索条件的任务。已有选择保留，清空搜索即可查看。"
            : "没有找到可选择的正在执行任务。任务需在本机留下可读取的生命周期记录。";
    }
    private bool Supported(JsonObject task) => task.S("status") is "running" or "waiting" && task.S("turnID").Length > 0
        && state.O("monitor").O("sourceStatus").S("status") != "unavailable" && (!task.ContainsKey("selectable") || task.B("selectable"));
    private void Render()
    {
        double previousOffset = scroller.VerticalOffset;
        var anchor = candidates.Values.FirstOrDefault(x => x.Card.Visibility == Visibility.Visible && x.Card.ActualHeight > 0
            && x.Card.TranslatePoint(new Point(), scroller).Y + x.Card.ActualHeight > 0);
        double anchorY = anchor?.Card.TranslatePoint(new Point(), scroller).Y ?? 0;
        string query = search.Text.Trim(); var monitor = state.O("monitor");
        var watched = monitor.A("watches").Rows().Where(x => x.B("active")).Select(x => x.S("id")).ToHashSet();
        var rows = monitor.A("tasks").Rows().Where(x => x.S("status") is "running" or "waiting" or "unknown" || selections.ContainsKey(x.S("id")))
            .OrderBy(x => TaskMonitorUi.Priority(x.S("status"))).ThenByDescending(x => TaskMonitorUi.Timestamp(x["startedAt"])).ToList();
        var present = rows.Select(x => x.S("id")).ToHashSet(StringComparer.Ordinal);
        foreach (var id in selections.Keys.Where(id => !present.Contains(id)).ToArray())
        {
            var missing = candidates.TryGetValue(id, out var previous) ? previous.Task.Copy() : J.Obj(("id", id), ("title", "所选任务暂时不可见"));
            missing["status"] = "unknown"; missing["selectable"] = false; missing["missing"] = true; rows.Add(missing); present.Add(id);
        }
        foreach (var id in candidates.Keys.Where(id => !present.Contains(id)).ToArray())
        {
            // An executing row becoming terminal should not disappear under a
            // keyboard user. Keep its known terminal state until focus moves away.
            var candidate = candidates[id];
            if (candidate.Card.IsKeyboardFocusWithin && monitor.A("tasks").Rows().FirstOrDefault(t => t.S("id") == id) is JsonObject terminal) { rows.Add(terminal); continue; }
            list.Children.Remove(candidate.Card); candidates.Remove(id);
        }
        int visible = 0;
        foreach (var task in rows)
        {
            string id = task.S("id");
            if (!candidates.TryGetValue(id, out var candidate))
            {
                candidate = new CandidateRow(this, id); candidates.Add(id, candidate); list.Children.Add(candidate.Card);
            }
            candidate.Update(task, watched.Contains(id));
            bool matches = query.Length == 0 || (TaskMonitorUi.Title(task) + " " + task.S("project")).Contains(query, StringComparison.CurrentCultureIgnoreCase);
            candidate.Card.Visibility = matches ? Visibility.Visible : Visibility.Collapsed; if (matches) visible++;
        }
        empty.Visibility = visible == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateDiscovery();
        list.UpdateLayout();
        if (anchor != null && candidates.ContainsValue(anchor) && anchor.Card.Visibility == Visibility.Visible)
            scroller.ScrollToVerticalOffset(previousOffset + anchor.Card.TranslatePoint(new Point(), scroller).Y - anchorY);
        else scroller.ScrollToVerticalOffset(previousOffset);
    }

    private sealed class CandidateRow
    {
        private readonly TaskMonitorPickerWindow owner;
        private readonly string id;
        internal readonly Border Card;
        internal readonly CheckBox Selector;
        internal JsonObject Task = new();
        private readonly TextBlock title = TaskMonitorUi.Text("", 14, true), project = TaskMonitorUi.Text("", 11), note = TaskMonitorUi.Text("", 12), error = TaskMonitorUi.Text("", 12, true);
        private readonly ContentControl status = new();
        private readonly Button newRound, remove;
        private string statusStamp = "";
        internal CandidateRow(TaskMonitorPickerWindow owner, string id)
        {
            this.owner = owner; this.id = id;
            var info = new StackPanel(); foreach (var field in new FrameworkElement[] { title, project, status, note, error }) info.Children.Add(field);
            Selector = new CheckBox { Content = info, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top };
            AutomationProperties.SetAutomationId(Selector, "monitor-candidate-" + id);
            Selector.Click += (_, _) =>
            {
                if (Selector.IsChecked == true) owner.selections[id] = J.Obj(("id", id), ("turnID", Task.S("turnID")));
                else { owner.selections.Remove(id); owner.errors.Remove(id); }
                owner.Render(); owner.UpdateFooter();
            };
            var body = new StackPanel(); body.Children.Add(Selector);
            newRound = TaskMonitorUi.Button("改选当前新一轮", () => { owner.selections[id] = J.Obj(("id", id), ("turnID", Task.S("turnID"))); owner.errors.Remove(id); owner.Render(); owner.UpdateFooter(); }, 130);
            remove = TaskMonitorUi.Button("移除此项选择", () => { owner.selections.Remove(id); owner.errors.Remove(id); owner.Render(); owner.UpdateFooter(); }, 118);
            foreach (var button in new[] { newRound, remove }) { button.Margin = new Thickness(24, 6, 0, 0); body.Children.Add(button); }
            Card = TaskMonitorUi.Card(body);
        }
        internal void Update(JsonObject task, bool already)
        {
            Task = task.Copy(); bool supported = owner.Supported(task), selected = owner.selections.ContainsKey(id);
            bool differentTurn = supported && owner.selections.TryGetValue(id, out var captured) && captured.S("turnID") != task.S("turnID");
            title.Text = TaskMonitorUi.Title(task); project.Text = TaskMonitorUi.Project(task);
            string nextStatus = task.S("status") + "|" + task["startedAt"];
            if (statusStamp != nextStatus) { statusStamp = nextStatus; status.Content = TaskMonitorUi.Status(task.S("status"), "开始于 " + TaskMonitorUi.Time(task["startedAt"])); }
            note.Text = task.B("missing") ? "所选任务暂时不可见，已保留原轮次选择。" : already ? "已开启提醒"
                : differentTurn ? "当前任务已换轮次，原选择仍绑定原轮次。"
                : supported ? "" : selected ? "当前状态无法核实，已保留选择；可取消此项选择。" : "当前状态或轮次无法核实，暂不能开启提醒";
            note.Visibility = note.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            error.Text = owner.errors.TryGetValue(id, out string? problem) ? "未添加：" + problem : ""; error.Visibility = error.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            Selector.IsChecked = selected; Selector.IsEnabled = selected || (!already && supported);
            AutomationProperties.SetName(Selector, TaskMonitorUi.Title(task) + "，" + TaskMonitorUi.Project(task) + "，" + TaskMonitorUi.StateLabel(task.S("status")) + (already ? "，已开启提醒" : ""));
            newRound.Visibility = differentTurn ? Visibility.Visible : Visibility.Collapsed;
            remove.Visibility = selected && (!supported || already) ? Visibility.Visible : Visibility.Collapsed;
        }
    }
    private void UpdateFooter()
    {
        save.Content = "开启 " + selections.Count + " 项提醒"; save.IsEnabled = selections.Count > 0 && !busy;
        scope.Text = (mode.SelectedItem as TaskMonitorUi.Choice)?.Id == "each" ? "持续关注所选任务的后续轮次；不会自动关注其他任务。" : "绑定选择时的当前轮次；结束后自动停止本次提醒。";
    }
    private async Task Discover()
    {
        if (busy) return; busy = true; IsEnabled = false;
        try
        {
            var result = await send(J.Obj(("action", "monitor"), ("operation", "discover"), ("payload", new JsonObject()))); TaskMonitorUi.EnsureSuccess(result); state = result.Copy();
            feedback.Text = state.O("monitor").O("sourceStatus").S("text", "已重新查找本机任务") + " · 不包含其他设备或未落盘的任务";
        }
        catch (Exception error) { state = read().Copy(); feedback.Text = "查找失败：" + error.Message; }
        finally { busy = false; IsEnabled = true; Render(); UpdateFooter(); }
    }
    private async Task Save()
    {
        if (busy || selections.Count == 0) return; busy = true; IsEnabled = false; bool close = false;
        try
        {
            var submitted = selections.Values.Select(x => (JsonNode?)x.DeepClone()).ToArray();
            var result = await send(J.Obj(("action", "monitor"), ("operation", "add"), ("payload", J.Obj(("selections", new JsonArray(submitted)), ("mode", (mode.SelectedItem as TaskMonitorUi.Choice)?.Id ?? "once")))));
            state = result.Copy(); var outcome = result.O("monitorResult");
            var items = outcome.A("items").Rows().ToList(); errors.Clear();
            foreach (var item in items)
            {
                if (item.B("ok")) selections.Remove(item.S("id"));
                else errors[item.S("id")] = item.S("error", "当前轮次无法确认，请重新查找后选择");
            }
            if (items.Count == 0) { TaskMonitorUi.EnsureSuccess(result); selections.Clear(); }
            feedback.Text = outcome.S("message", "提醒已开启");
            if (items.FirstOrDefault(x => x.B("ended")) is JsonObject ended)
            {
                feedback.Text += "。所选本轮已结束，可查看结果；没有迟到补弹。";
                resultTask = ended.S("id"); resultMessage = ended.S("messageID"); viewResult.Visibility = Visibility.Visible;
            }
            if (items.Any(x => x.B("ok"))) cancel.Content = "完成";
            Changed?.Invoke(this, EventArgs.Empty); close = selections.Count == 0 && !items.Any(x => x.B("ended"));
        }
        catch (Exception error) { feedback.Text = "未能开启提醒：" + error.Message; }
        finally { busy = false; IsEnabled = true; Render(); UpdateFooter(); }
        if (close) Close();
    }
}
