using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexUsage;

/// <summary>Task monitoring is a separate workspace from usage filters and budget drafts.</summary>
internal sealed class TaskMonitorView : UserControl
{
    private readonly Func<JsonObject, Task<JsonObject>> send;
    private readonly Action<string> report;
    private JsonObject state = new();
    private readonly Grid root = new();
    private readonly WrapPanel headingActions = new();
    private readonly TextBlock heading = TaskMonitorUi.Text("任务监控", 27, true);
    private readonly TextBlock summary = TaskMonitorUi.Text("选择正在执行的任务，结束后提醒你", 12);
    private readonly TextBlock feedback = TaskMonitorUi.Text("", 12);
    private readonly TextBlock source = TaskMonitorUi.Text("正在读取监控来源…", 11);
    private readonly StackPanel list = new();
    private readonly ScrollViewer scroller;
    private readonly StackPanel navigation = new();
    private readonly WrapPanel toolbar = new();
    private readonly ToggleButton watchingTab = new() { Content = "正在监控", MinWidth = 102 };
    private readonly ToggleButton messagesTab = new() { Content = "消息记录", MinWidth = 108 };
    private readonly TextBox search = new() { Width = 270, MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ComboBox filter = new() { Width = 138, MinHeight = 34 };
    private readonly ComboBox focus = new() { Width = 270, MinHeight = 34, MaxDropDownHeight = 300 };
    private readonly HashSet<string> selectedMessages = new(StringComparer.Ordinal);
    private HashSet<string>? notificationMessages;
    private readonly List<(TextBlock text, JsonObject task, string prefix)> elapsedLabels = new();
    private readonly Button markSelected, markAll, clearHistory;
    private bool rebuilding, busy, messages, menuOpen;
    private string selected = "", selectedMessage = "", latestFeedback = "", focusOptionsSignature = "";
    public string SelectedId => selected;
    public string SelectedMessageId => selectedMessage;
    public bool IsViewingDetail => selected.Length > 0;
    public event EventHandler? ViewingChanged;
    private JsonObject Monitor => state.O("monitor");

    internal TaskMonitorView(Func<JsonObject, Task<JsonObject>> send, Action<string> report)
    {
        this.send = send; this.report = report;
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        for (int i = 0; i < 5; i++) root.RowDefinitions.Add(new RowDefinition { Height = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        var header = new StackPanel(); heading.Margin = new Thickness(0, 3, 0, 4); header.Children.Add(heading);
        summary.Margin = new Thickness(0, 0, 0, 8); header.Children.Add(summary); header.Children.Add(headingActions); Add(header, 0);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal }; tabs.Children.Add(watchingTab); tabs.Children.Add(messagesTab);
        navigation.Children.Add(new SegmentedGroup(tabs) { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 6) });
        navigation.Children.Add(toolbar); Add(navigation, 1);
        feedback.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush"); feedback.Margin = new Thickness(0, 2, 0, 6); Add(feedback, 2);
        scroller = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false };
        Add(scroller, 3);
        var footer = new DockPanel { Margin = new Thickness(0, 7, 0, 0) };
        var check = TaskMonitorUi.Button("检查连接", async () => await Check(), 86); DockPanel.SetDock(check, Dock.Right); footer.Children.Add(check);
        source.Margin = new Thickness(0, 0, 12, 0); footer.Children.Add(source); Add(footer, 4);
        Content = root;
        AutomationProperties.SetName(search, "搜索监控任务名称或项目"); search.ToolTip = "只筛选当前列表，不改变监控对象";
        TaskMonitorUi.SearchStyle(search);
        AutomationProperties.SetName(filter, "监控列表状态筛选"); AutomationProperties.SetName(focus, "常驻关注任务");
        TaskMonitorUi.ChoiceTemplate(focus, 245);
        search.TextChanged += (_, _) => { if (!rebuilding) RenderItems(); };
        filter.SelectionChanged += (_, _) => { if (!rebuilding) RenderItems(); };
        focus.SelectionChanged += async (_, _) =>
        {
            if (rebuilding || focus.SelectedItem is not TaskMonitorUi.Choice choice || choice.Id == Monitor.O("settings").S("focusID")) return;
            await Command("focus", J.Obj(("id", choice.Id)));
        };
        focus.DropDownClosed += (_, _) => Render();
        watchingTab.Click += (_, _) => SelectList(false); messagesTab.Click += (_, _) => SelectList(true);
        markSelected = TaskMonitorUi.Button("所选标为已读", async () => { await Command("read", J.Obj(("ids", Strings(selectedMessages)))); selectedMessages.Clear(); RenderItems(); }, 110);
        markAll = TaskMonitorUi.Button("全部标为已读", async () => await Command("read", notificationMessages is null ? J.Obj(("all", true)) : J.Obj(("ids", Strings(notificationMessages)))), 144);
        clearHistory = TaskMonitorUi.Button("清理已读历史", async () => await Command("clear-history"), 110);
        BuildNavigation(); Render();
    }

    private void Add(UIElement element, int row) { Grid.SetRow(element, row); root.Children.Add(element); }
    public void Update(JsonObject next)
    {
        bool changed = !JsonNode.DeepEquals(Presentation(state.O("monitor")), Presentation(next.O("monitor")));
        state = next.Copy();
        UpdateSource();
        if (changed && !menuOpen) Render();
    }
    private static JsonObject Presentation(JsonObject monitor)
    {
        var comparable = monitor.Copy(); comparable.Remove("diagnostics"); comparable.O("sourceStatus").Remove("checkedAt"); return comparable;
    }
    private void UpdateSource()
    {
        var sourceState = Monitor.O("sourceStatus");
        source.Text = sourceState.S("text", Monitor.Count == 0 ? "正在连接后台监控…" : "仅监控这台电脑可读取的任务")
            + (sourceState.N("checkedAt") is double time ? " · 检查于 " + TaskMonitorUi.Time(time) : "");
        foreach (var (text, task, prefix) in elapsedLabels) text.Text = TaskMonitorUi.StateLabel(task.S("status")) + " · " + prefix + TaskMonitorUi.Elapsed(task);
    }
    private FrameworkElement TimedStatus(JsonObject task, string prefix = "")
    {
        var row = TaskMonitorUi.Status(task.S("status"), prefix + TaskMonitorUi.Elapsed(task));
        if (task.S("status") is "running" or "waiting" && row is Grid grid && grid.Children.OfType<TextBlock>().FirstOrDefault() is TextBlock label)
            elapsedLabels.Add((label, task.Copy(), prefix));
        return row;
    }
    public void Select(string taskID = "", string messageID = "")
    {
        if (taskID.Length == 0 && messageID.Length > 0) taskID = Monitor.A("messages").Rows().FirstOrDefault(x => x.S("id") == messageID)?.S("taskID") ?? "";
        selected = taskID; selectedMessage = messageID;
        Render(); scroller.ScrollToTop(); ViewingChanged?.Invoke(this, EventArgs.Empty);
        // Only an explicit message selection marks that message read. Merely opening a task does not.
        if (messageID.Length > 0) _ = Command("read", J.Obj(("ids", Strings([messageID]))));
    }
    public void SelectMessages()
    {
        // The floating unread entry opens a list, not a message selection. It must
        // not acknowledge messages or retain a search that hides the requested list.
        notificationMessages = null; SelectList(true);
        rebuilding = true;
        try
        {
            search.Text = "";
            string mode = Monitor.A("messages").Rows().Any(x => !x.B("read")) ? "unread" : "all";
            filter.SelectedItem = filter.Items.Cast<TaskMonitorUi.Choice>().First(x => x.Id == mode);
        }
        finally { rebuilding = false; }
        RenderItems();
    }
    public void SelectNotificationMessages(IEnumerable<string> ids)
    {
        notificationMessages = ids.Where(x => x.Length > 0).ToHashSet(StringComparer.Ordinal);
        search.Text = ""; SelectList(true);
    }
    private void SelectList(bool showMessages)
    {
        if (!showMessages) notificationMessages = null;
        selected = selectedMessage = ""; messages = showMessages; selectedMessages.Clear();
        BuildNavigation(); Render(); scroller.ScrollToTop(); ViewingChanged?.Invoke(this, EventArgs.Empty);
    }
    private void BuildNavigation()
    {
        rebuilding = true;
        try
        {
            toolbar.Children.Clear(); toolbar.Children.Add(search); search.Margin = new Thickness(0, 0, 8, 6);
            filter.Margin = new Thickness(0, 0, 8, 6);
            filter.ItemsSource = messages
                ? new TaskMonitorUi.Choice[] { new("all", "全部消息"), new("unread", "未读"), new("attention", "仍需处理") }
                : new TaskMonitorUi.Choice[] { new("all", "全部状态"), new("running", "正在执行"), new("waiting", "等待处理"), new("unknown", "状态待确认"), new("idle", "等待新一轮") };
            filter.SelectedIndex = 0; toolbar.Children.Add(filter);
            if (messages)
            {
                markAll.Content = notificationMessages is null ? "全部标为已读" : "本通知全部标为已读";
                foreach (var button in new[] { markSelected, markAll }) { button.Margin = new Thickness(0, 0, 8, 6); toolbar.Children.Add(button); }
                if (notificationMessages is null) toolbar.Children.Add(clearHistory);
                else toolbar.Children.Add(TaskMonitorUi.Button("查看全部历史", () => { notificationMessages = null; SelectList(true); }, 112));
            }
            else
            {
                var group = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                var label = TaskMonitorUi.Text("常驻关注", 12); label.Margin = new Thickness(0, 0, 8, 0); group.Children.Add(label);
                if (focus.Parent is Panel oldGroup) oldGroup.Children.Remove(focus);
                focus.Margin = new Thickness(0); group.Children.Add(focus); toolbar.Children.Add(group);
            }
        }
        finally { rebuilding = false; }
    }
    private void Render()
    {
        rebuilding = true;
        try
        {
            var monitor = Monitor; var counts = monitor.O("summary");
            heading.Text = selected.Length > 0 ? "任务详情" : notificationMessages is not null ? "本次通知消息" : "任务监控";
            summary.Text = $"监控中 {counts.I("active")} · 需处理 {counts.I("attention")} · 未读 {counts.I("unread")}　·　本轮结束不等于整个需求完成";
            if (headingActions.Children.Count == 0 || (headingActions.Children.Count == 3) != (selected.Length > 0))
            {
                headingActions.Children.Clear();
                if (selected.Length > 0) headingActions.Children.Add(TaskMonitorUi.Button("返回列表", () => SelectList(messages), 86));
                headingActions.Children.Add(TaskMonitorUi.Button("选择任务", ShowPicker, 94));
                headingActions.Children.Add(TaskMonitorUi.Button("提醒设置", async () => await OpenSettings(), 86));
                foreach (FrameworkElement action in headingActions.Children) action.Margin = new Thickness(0, 0, 8, 0);
            }
            navigation.Visibility = selected.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
            watchingTab.IsChecked = !messages; messagesTab.IsChecked = messages;
            messagesTab.Content = counts.I("unread") > 0 ? "消息记录 · " + (counts.I("unread") > 99 ? "99+" : counts.I("unread").ToString()) : "消息记录";
            UpdateSource();
            string error = monitor.S("error"); feedback.Text = error.Length > 0 ? error : latestFeedback;
            feedback.Visibility = feedback.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            var options = new List<TaskMonitorUi.Choice> { new("", "全部已选任务") };
            options.AddRange(monitor.A("watches").Rows().Select(w => new TaskMonitorUi.Choice(w.S("id"), TaskMonitorUi.Title(w))));
            string signature = string.Join("\n", options.Select(x => x.Id + ":" + x.Label));
            if (!focus.IsDropDownOpen)
            {
                if (focusOptionsSignature != signature) { focusOptionsSignature = signature; focus.ItemsSource = options; }
                focus.SelectedItem = focus.Items.Cast<TaskMonitorUi.Choice>().FirstOrDefault(x => x.Id == monitor.O("settings").S("focusID")) ?? focus.Items[0];
            }
            RenderItems();
        }
        finally { rebuilding = false; }
    }
    private bool Matches(JsonObject row) => search.Text.Trim().Length == 0 || (TaskMonitorUi.Title(row) + " " + row.S("project")).Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase);
    private void RenderItems()
    {
        double offset = scroller.VerticalOffset; list.Children.Clear(); elapsedLabels.Clear();
        if (Monitor.O("recovery").B("required"))
        {
            var recovery = new StackPanel(); recovery.Children.Add(TaskMonitorUi.Text("监控记录无法读取。原文件保留，恢复前不会把它当成空记录。", 13, true));
            recovery.Children.Add(TaskMonitorUi.Text(Monitor.S("error"), 12));
            recovery.Children.Add(TaskMonitorUi.Button("备份并恢复监控记录…", async () =>
            {
                if (MessageBox.Show(Window.GetWindow(this), "原文件会先备份，然后恢复可读取的监控记录。Codex 日志和任务不受影响。是否继续？", "恢复监控记录", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    await Command("recover", J.Obj(("confirm", true)));
            }, 170));
            list.Children.Add(TaskMonitorUi.Card(recovery)); return;
        }
        if (selected.Length > 0) RenderDetail();
        else if (messages) RenderMessages();
        else RenderWatches();
        scroller.ScrollToVerticalOffset(offset);
    }
    private void RenderWatches()
    {
        var all = Monitor.A("watches").Rows().ToList();
        string status = (filter.SelectedItem as TaskMonitorUi.Choice)?.Id ?? "all";
        var active = all.Where(x => x.B("active") && Matches(x) && (status == "all" || x.S("status") == status)).OrderBy(x => TaskMonitorUi.Priority(x.S("status"))).ToList();
        if (active.Count == 0)
        {
            var empty = new StackPanel(); empty.Children.Add(TaskMonitorUi.Text(all.Any(x => x.B("active")) ? "没有符合当前筛选的任务" : "选择正在执行的任务，结束后提醒你", 16, true));
            empty.Children.Add(TaskMonitorUi.Text("关闭主面板、收起浮窗都继续监控。任务用量筛选不会改变这里的选择。", 12));
            empty.Children.Add(TaskMonitorUi.Button("选择任务", ShowPicker, 94)); list.Children.Add(TaskMonitorUi.Card(empty));
        }
        foreach (var watch in active) list.Children.Add(WatchCard(watch));
        var ended = all.Where(x => !x.B("active")).ToList();
        if (ended.Count > 0)
        {
            var header = new WrapPanel { Margin = new Thickness(0, 10, 0, 5) };
            header.Children.Add(TaskMonitorUi.Text($"已结束结果 {ended.Count} · 常驻符号保留本次结果", 12));
            var clear = TaskMonitorUi.Button("清除已结束结果", async () => await Command("clear-ended"), 124); clear.Margin = new Thickness(10, 0, 0, 0); header.Children.Add(clear); list.Children.Add(header);
            foreach (var watch in ended.Where(Matches)) list.Children.Add(WatchCard(watch));
        }
        var note = TaskMonitorUi.Text("常驻关注只改变圆环与侧签的任务状态符号；其他任务仍照常监控和提醒。已读不会清除仍需处理或已结束状态。", 11);
        note.Margin = new Thickness(0, 8, 0, 0); list.Children.Add(note);
    }
    private Border WatchCard(JsonObject watch)
    {
        var content = new Grid(); content.ColumnDefinitions.Add(new ColumnDefinition()); content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var info = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        info.Children.Add(TaskMonitorUi.Text(TaskMonitorUi.Title(watch), 14, true));
        info.Children.Add(TaskMonitorUi.Text(TaskMonitorUi.Project(watch), 11));
        info.Children.Add(watch.B("active") ? TimedStatus(watch) : TaskMonitorUi.Status(watch.S("status"), "本次监控已结束"));
        info.Children.Add(TaskMonitorUi.Text(watch.S("mode") == "each" ? "每轮结束都提醒" : "仅所选本轮提醒", 11)); content.Children.Add(info);
        var actions = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(TaskMonitorUi.Button("查看", () => Select(watch.S("id")), 70));
        var more = TaskMonitorUi.Button("更多", () => { }, 70); more.Margin = new Thickness(0, 6, 0, 0);
        more.Click += (_, _) => ShowTaskMenu(more, watch); actions.Children.Add(more); Grid.SetColumn(actions, 1); content.Children.Add(actions);
        return TaskMonitorUi.Card(content);
    }
    private void ShowTaskMenu(Button anchor, JsonObject watch)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        void Item(string title, Func<Task> action)
        {
            var item = new MenuItem { Header = title }; item.Click += async (_, _) => await action(); menu.Items.Add(item);
        }
        Item("常驻关注此任务", () => Command("focus", J.Obj(("id", watch.S("id")))));
        if (watch.B("active"))
        {
            string mode = watch.S("mode") == "each" ? "once" : "each";
            if (!(watch.S("status") == "idle" && mode == "once")) Item(mode == "each" ? "改为每轮结束都提醒" : "改为仅当前轮提醒", () => Command("mode", J.Obj(("id", watch.S("id")), ("mode", mode))));
            menu.Items.Add(new Separator());
            Item(watch.S("mode") == "each" ? "停止每轮提醒" : "取消本轮提醒", () => Command("stop", J.Obj(("id", watch.S("id")))));
        }
        menu.Items.Add(new Separator()); Item("复制任务标识", () => { Copy(watch.S("id")); return Task.CompletedTask; });
        menu.Opened += (_, _) => menuOpen = true; menu.Closed += (_, _) => { menuOpen = false; Render(); }; menu.IsOpen = true;
    }
    private void RenderMessages()
    {
        string mode = (filter.SelectedItem as TaskMonitorUi.Choice)?.Id ?? "all";
        var scoped = Monitor.A("messages").Rows().Where(x => notificationMessages is null || notificationMessages.Contains(x.S("id"))).ToArray();
        var items = scoped.Where(Matches).Where(x => mode != "unread" || !x.B("read")).Where(x => mode != "attention" || IsPending(x)).OrderByDescending(x => TaskMonitorUi.Timestamp(x["createdAt"])).ToList();
        var allIDs = scoped.Select(x => x.S("id")).ToHashSet(); selectedMessages.IntersectWith(allIDs);
        markSelected.IsEnabled = selectedMessages.Count > 0 && !busy; markAll.IsEnabled = scoped.Any(x => !x.B("read")) && !busy;
        clearHistory.IsEnabled = Monitor.A("messages").Rows().Any(x => x.B("read") && !IsPending(x)) && !busy;
        if (notificationMessages is not null)
            list.Children.Add(TaskMonitorUi.Text($"仅显示本通知关联的 {scoped.Length} 条消息；查看列表不会标为已读。" + (scoped.Length < notificationMessages.Count ? "部分关联记录已清理或不再可用。" : ""), 12));
        if (items.Count == 0) list.Children.Add(TaskMonitorUi.Card(TaskMonitorUi.Text("没有符合当前筛选的消息。监控列表和 Hover 不会自动确认已读。", 13)));
        foreach (var message in items)
        {
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var check = new CheckBox { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 12, 0), IsChecked = selectedMessages.Contains(message.S("id")) };
            AutomationProperties.SetName(check, "选择消息：" + TaskMonitorUi.Title(message));
            check.Click += (_, _) => { if (check.IsChecked == true) selectedMessages.Add(message.S("id")); else selectedMessages.Remove(message.S("id")); markSelected.IsEnabled = selectedMessages.Count > 0 && !busy; };
            row.Children.Add(check);
            var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0) }; text.Children.Add(TaskMonitorUi.Text(TaskMonitorUi.Title(message), 14, !message.B("read")));
            text.Children.Add(TaskMonitorUi.Status(message.S("status"), message.B("read") ? (IsPending(message) ? "已读 · 仍需处理" : "已读") : "未读"));
            if (message.S("status") == "waiting" && !IsPending(message)) text.Children.Add(TaskMonitorUi.Text("该处理请求已不再是当前状态", 11));
            text.Children.Add(TaskMonitorUi.Text(TaskMonitorUi.Project(message) + " · " + TaskMonitorUi.Time(message["createdAt"]) + (message.B("offline") ? " · 离线期间发生" : ""), 11));
            Grid.SetColumn(text, 1); row.Children.Add(text);
            var view = TaskMonitorUi.Button("查看", () => Select(message.S("taskID"), message.S("id")), 70); Grid.SetColumn(view, 2); row.Children.Add(view); list.Children.Add(TaskMonitorUi.Card(row));
        }
    }
    private bool IsPending(JsonObject message)
    {
        if (message.S("status") != "waiting") return false;
        return Monitor.A("tasks").Rows().Any(t => t.S("id") == message.S("taskID") && t.S("turnID") == message.S("turnID") && t.S("status") == "waiting");
    }
    private void RenderDetail()
    {
        var task = Monitor.A("tasks").Rows().FirstOrDefault(x => x.S("id") == selected);
        var watch = Monitor.A("watches").Rows().FirstOrDefault(x => x.S("id") == selected);
        var events = Monitor.A("messages").Rows().Where(x => x.S("taskID") == selected).OrderByDescending(x => TaskMonitorUi.Timestamp(x["createdAt"])).ToList();
        var identity = task ?? watch ?? events.FirstOrDefault();
        if (identity == null) { list.Children.Add(TaskMonitorUi.Card(TaskMonitorUi.Text("此任务记录暂不可用。可检查连接，或返回消息记录。", 14))); return; }
        var top = new StackPanel(); top.Children.Add(TaskMonitorUi.Text(TaskMonitorUi.Title(identity), 20, true)); top.Children.Add(TaskMonitorUi.Text(TaskMonitorUi.Project(identity), 12));
        top.Children.Add(task == null ? TaskMonitorUi.Status("unknown", "当前执行状态不可核实；以下保留历史结果") : TimedStatus(task, "当前任务状态 · "));
        if (watch != null)
        {
            top.Children.Add(TaskMonitorUi.Text((watch.S("mode") == "each" ? "每轮提醒" : "仅所选本轮提醒") + " · " + (watch.B("active") ? "已开启" : "本次监控已结束") + " · 本轮：" + TaskMonitorUi.StateLabel(watch.S("status")), 12));
            if (task != null && task.S("turnID") != watch.S("turnID") && watch.S("mode") != "each") top.Children.Add(TaskMonitorUi.Text("任务已进入其他轮次；本次一次性提醒仍绑定原轮次，不会自动跟随。", 12));
        }
        var actions = new WrapPanel();
        actions.Children.Add(TaskMonitorUi.Button("复制任务名称", () => Copy(TaskMonitorUi.Title(identity)), 112));
        actions.Children.Add(TaskMonitorUi.Button("复制任务标识", () => Copy(selected), 112));
        if (watch?.B("active") == true) actions.Children.Add(TaskMonitorUi.Button(watch.S("mode") == "each" ? "停止每轮提醒" : "取消本轮提醒", async () => await Command("stop", J.Obj(("id", selected))), 116));
        foreach (FrameworkElement element in actions.Children) element.Margin = new Thickness(0, 8, 8, 0); top.Children.Add(actions);
        top.Children.Add(TaskMonitorUi.Text("取消提醒仅停止本工具的监控，Codex 继续执行。", 11));
        list.Children.Add(TaskMonitorUi.Card(top));
        var ids = new TextBox { IsReadOnly = true, Text = "任务 " + selected + "\n当前轮次 " + (task?.S("turnID") ?? "未知") + "\n监控轮次 " + (watch?.S("turnID") ?? "无"), TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Margin = new Thickness(0, 3, 0, 8) };
        AutomationProperties.SetName(ids, "可复制的任务和轮次标识"); list.Children.Add(ids);
        list.Children.Add(TaskMonitorUi.Text("所选任务的消息记录", 15, true));
        if (events.Count == 0) list.Children.Add(TaskMonitorUi.Text("还没有消息。仅查看当前状态不会清除未来到达的未读消息。", 12));
        foreach (var message in events)
        {
            bool chosen = message.S("id") == selectedMessage;
            var content = new StackPanel(); content.Children.Add(TaskMonitorUi.Status(message.S("status"), (chosen ? "正在查看 · " : "") + (message.B("read") ? "已读" : "未读")));
            content.Children.Add(TaskMonitorUi.Text(TaskMonitorUi.Time(message["createdAt"]) + " · 轮次 " + message.S("turnID") + (message.B("offline") ? " · 离线期间发生" : ""), 11));
            content.Children.Add(TaskMonitorUi.Text(message.S("text", TaskMonitorUi.StateLabel(message.S("status"))), 13));
            if (message.S("status") == "waiting") content.Children.Add(TaskMonitorUi.Text(IsPending(message) ? "仍需在 Codex 中处理；标为已读不会代替审批或输入。" : "此请求已不是当前状态，请以顶部当前状态为准。", 12));
            string delivery = TaskMonitorUi.Delivery(message["delivery"]); if (delivery.Length > 0) content.Children.Add(TaskMonitorUi.Text("提醒记录：" + delivery, 11));
            var controls = new WrapPanel();
            if (!chosen) controls.Children.Add(TaskMonitorUi.Button("查看此条消息", () => Select(selected, message.S("id")), 116));
            if (!message.B("read")) controls.Children.Add(TaskMonitorUi.Button("标为已读", async () => await Command("read", J.Obj(("ids", Strings([message.S("id")])))), 92));
            foreach (FrameworkElement element in controls.Children) element.Margin = new Thickness(0, 5, 8, 0); content.Children.Add(controls);
            var card = TaskMonitorUi.Card(content); if (chosen) card.BorderBrush = new SolidColorBrush(Color.FromRgb(33, 143, 124)); list.Children.Add(card);
        }
        list.Children.Add(TaskMonitorUi.Text(TaskMonitorUi.Capabilities(Monitor.O("capabilities")), 11));
    }
    private async Task OpenSettings()
    {
        if (state.B("demo")) { new TaskMonitorSettingsWindow(Window.GetWindow(this), () => state.Copy(), send).Show(); return; }
        await Run(async () => { ForegroundTransfer.GrantHost(state); await send(J.Obj(("action", "monitor-settings-dialog"))); });
    }
    private void ShowPicker()
    {
        var picker = new TaskMonitorPickerWindow(Window.GetWindow(this), () => state.Copy(), send);
        picker.ViewMessage = Select;
        picker.Changed += (_, _) => Render(); picker.ShowDialog();
    }
    internal Task Check() => Command("check");
    private Task Command(string operation, JsonObject? payload = null) => Run(async () =>
    {
        var result = await send(J.Obj(("action", "monitor"), ("operation", operation), ("payload", payload ?? new JsonObject())));
        TaskMonitorUi.EnsureSuccess(result); Update(result);
        latestFeedback = result.O("monitorResult").S("message", operation == "stop" ? "提醒已取消，Codex 继续执行。" : "已更新"); report(latestFeedback);
    });
    private async Task Run(Func<Task> action)
    {
        if (busy) return; busy = true; IsEnabled = false;
        try { await action(); }
        catch (Exception error) { latestFeedback = "操作未完成：" + error.Message; report(latestFeedback); }
        finally { busy = false; IsEnabled = true; Render(); }
    }
    private void Copy(string value)
    {
        try { Clipboard.SetText(value); latestFeedback = "已复制"; }
        catch (Exception error) { latestFeedback = "复制失败：" + error.Message; }
        feedback.Text = latestFeedback; feedback.Visibility = Visibility.Visible;
    }
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());

    internal JsonObject Inspect() => J.Obj(("detail", IsViewingDetail), ("taskID", selected), ("messageID", selectedMessage), ("messages", messages), ("notificationIDs", notificationMessages?.ToArray()), ("filter", (filter.SelectedItem as TaskMonitorUi.Choice)?.Id ?? "all"), ("search", search.Text), ("feedback", feedback.Text), ("width", ActualWidth), ("height", ActualHeight));
}

internal static class TaskMonitorUi
{
    internal sealed record Choice(string Id, string Label) { public override string ToString() => Label; }
    internal static TextBlock Text(string text, double size = 13, bool bold = false)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4), VerticalAlignment = VerticalAlignment.Center };
        block.SetResourceReference(TextBlock.ForegroundProperty, bold ? "ForegroundBrush" : "SecondaryBrush"); return block;
    }
    internal static Button Button(string label, Action action, double width = 84)
    {
        var button = new Button { Content = label, MinWidth = width, MinHeight = 34, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(13, 6, 13, 6) };
        // String Content is also the native ButtonAutomationPeer name. Keeping a
        // second, fixed Name would make selection counts and renamed actions stale.
        button.Click += (_, _) => action(); return button;
    }
    internal static Button Button(string label, Func<Task> action, double width = 84)
    {
        var button = Button(label, () => { }, width); button.Click += async (_, _) => await action(); return button;
    }
    internal static Border Card(UIElement content)
    {
        var card = new Border { Child = content, Padding = new Thickness(14), CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(38, 128, 128, 128)), Margin = new Thickness(0, 0, 0, 9) };
        card.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); return card;
    }
    internal static FrameworkElement Status(string status, string suffix = "")
    {
        var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = new TaskMonitorStatusIcon(status) { Width = 14, Height = 14, Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Center }; row.Children.Add(icon);
        var label = Text(StateLabel(status) + (suffix.Length > 0 ? " · " + suffix : ""), 12, true); Grid.SetColumn(label, 1); row.Children.Add(label);
        AutomationProperties.SetName(row, StateLabel(status) + (suffix.Length > 0 ? "，" + suffix : "")); return row;
    }
    internal static string Title(JsonObject task) => task.S("title", task.S("label", "未命名任务"));
    internal static string Project(JsonObject task) => task.S("project").Length > 0 ? task.S("project") : "本地任务 · 项目未记录";
    internal static string StateLabel(string status) => TaskMonitorGlyph.Label(TaskMonitorGlyph.States.Contains(status) ? status : "unknown");
    internal static string Symbol(string status) => status switch { "running" => "▶", "waiting" => "!", "completed" => "✓", "failed" => "×", "interrupted" => "■", "idle" => "◷", _ => "?" };
    internal static int Priority(string status) => status switch { "waiting" => 0, "failed" => 1, "unknown" => 2, "running" => 3, "idle" => 4, _ => 5 };
    internal static double Timestamp(JsonNode? value)
    {
        if (value == null) return 0;
        if (double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) return number > 100000000000 ? number / 1000 : number;
        return DateTimeOffset.TryParse(value.ToString(), out var date) ? date.ToUnixTimeSeconds() : 0;
    }
    internal static string Time(JsonNode? value) => Time(Timestamp(value));
    internal static string Time(double seconds)
    {
        try { return seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)seconds).ToLocalTime().ToString("MM-dd HH:mm:ss") : "时间未记录"; } catch (ArgumentOutOfRangeException) { return "时间未记录"; }
    }
    internal static string Elapsed(JsonObject task)
    {
        double start = Timestamp(task["startedAt"]); if (start <= 0) return "开始时间未记录";
        if (task.S("status") is not ("running" or "waiting")) return "更新于 " + Time(task["updatedAt"]);
        var duration = TimeSpan.FromSeconds(Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - start));
        return duration.TotalMinutes < 1 ? "本轮开始不足 1 分钟" : "本轮开始 " + (duration.TotalHours < 1 ? (int)duration.TotalMinutes + " 分钟" : (int)duration.TotalHours + " 小时 " + duration.Minutes + " 分钟");
    }
    internal static string Capabilities(JsonObject caps)
    {
        var available = new List<string>(); var missing = new List<string>();
        foreach (var (id, title) in new[] { ("completed", "明确本轮结束"), ("waiting", "等待审批或输入"), ("failure", "执行失败"), ("interrupted", "明确中断") }) (caps.B(id) ? available : missing).Add(title);
        return "当前来源支持：" + (available.Count > 0 ? string.Join("、", available) : "尚待核实") + (missing.Count > 0 ? "。暂不支持：" + string.Join("、", missing) + "；不从日志安静或文本猜测这些状态。" : "。");
    }
    internal static string Delivery(JsonNode? value)
    {
        string status = value is JsonObject data ? data.S("status", data.S("reason")) : value?.ToString() ?? "";
        return status switch { "sent" or "submitted" => "已提交系统；不代表用户已看见", "markers" => "仅应用内标记", "paused" => "暂停通知期间保留", "offline" => "离线补记，不补弹", "viewing" => "用户正在查看，未另弹横幅", "quiet" or "suppressed" => "当前策略限制，应用内保留", "failed" => "系统通知未发出，应用内保留", "" => "", _ => status };
    }
    internal static void EnsureSuccess(JsonObject state)
    {
        var result = state.O("monitorResult");
        if (result.ContainsKey("ok") && !result.B("ok")) throw new InvalidOperationException(result.S("message", result.S("error", "操作未完成，请检查监控连接后重试。")));
        if (state.ContainsKey("ok") && !state.B("ok") && state.S("error").Length > 0) throw new InvalidOperationException(state.S("error"));
    }
    internal static void ChoiceTemplate(ComboBox box, double maxWidth)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Label")); text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); text.SetValue(FrameworkElement.MaxWidthProperty, maxWidth);
        box.ItemTemplate = new DataTemplate { VisualTree = text };
    }
    internal static void SearchStyle(TextBox search)
    {
        search.SetResourceReference(FrameworkElement.StyleProperty, "UsageSearchBox");
        void SetHint() { search.ApplyTemplate(); if (search.Template?.FindName("Placeholder", search) is TextBlock hint) hint.Text = "搜索任务名称或项目"; }
        SetHint(); search.Loaded += (_, _) => SetHint();
    }
}

internal sealed class TaskMonitorStatusIcon : Control
{
    private readonly string status;
    internal TaskMonitorStatusIcon(string status)
    {
        this.status = status; Focusable = false; IsHitTestVisible = false; SetResourceReference(ForegroundProperty, "ForegroundBrush");
    }
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        bool light = Foreground is not SolidColorBrush brush || brush.Color.R + brush.Color.G + brush.Color.B < 420;
        TaskMonitorGlyph.Draw(drawingContext, new Rect(RenderSize), status, light);
    }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e) { base.OnPropertyChanged(e); if (e.Property == ForegroundProperty) InvalidateVisual(); }
}
