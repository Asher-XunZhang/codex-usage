using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexUsage;

internal sealed class SettingsWindow : Window
{
    private sealed record Option(string Id, string Label) { public override string ToString() => Label; }
    private readonly Func<JsonObject> read;
    private readonly Func<JsonObject, Task<JsonObject>> send;
    private readonly StackPanel body = new();
    private readonly TextBlock feedback = Text("");
    private readonly Button retry = new() { Content = "重试保存", Visibility = Visibility.Collapsed };
    private readonly Button discard = new() { Content = "取消未保存修改", Visibility = Visibility.Collapsed };
    private readonly Dictionary<string, JsonObject> pending = new();
    private readonly List<Action<JsonObject>> sync = new();
    private readonly Dictionary<string, System.Windows.Controls.Primitives.ToggleButton> tabs = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool saving, applying, closed;
    private string page = "appearance", error = "";
    private TextBox? localStatus, quotaStatus;
    private Button? localRetry, quotaRetry;
    internal SettingsWindow(Func<JsonObject> read, Func<JsonObject, Task<JsonObject>> send)
    {
        this.read = () =>
        {
            var effective = read().Copy(); var settings = effective.O("settings").Copy();
            foreach (var request in pending.Values)
            {
                if (request.S("action") == "settings") foreach (var (key, value) in request.O("patch")) settings[key] = value?.DeepClone();
                else if (request.S("action") == "mode") settings["mode"] = request.S("value");
                else if (request.S("action") == "floating-settings") { var floating = settings.O("floating").Copy(); foreach (var (key, value) in request.O("patch")) floating[key] = value?.DeepClone(); settings["floating"] = floating; }
            }
            effective["settings"] = settings; return effective;
        };
        this.send = send;
        Title = "Codex 用量 · 设置"; Width = 650; Height = 650; MinWidth = 470; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "BackgroundBrush"); SetResourceReference(ForegroundProperty, "ForegroundBrush");
        var root = new DockPanel { Margin = new Thickness(20) }; root.SetResourceReference(Panel.BackgroundProperty, "BackgroundBrush");
        var navigation = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (id, label) in new[] { ("appearance", "外观"), ("display", "显示与提醒"), ("updates", "数据与更新"), ("help", "帮助与恢复") })
        {
            var tab = new System.Windows.Controls.Primitives.ToggleButton { Content = label, Padding = new Thickness(11, 6, 11, 6) };
            tabs[id] = tab; navigation.Children.Add(tab); tab.Click += (_, _) => SelectPage(id);
        }
        var segmented = new SegmentedGroup(navigation) { HorizontalAlignment = HorizontalAlignment.Left }; System.Windows.Automation.AutomationProperties.SetName(segmented, "设置分类"); segmented.Margin = new Thickness(0, 0, 0, 18);
        DockPanel.SetDock(segmented, Dock.Top); root.Children.Add(segmented);
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var close = new Button { Content = "完成", MinWidth = 74 }; close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right); footer.Children.Add(close);
        var status = new StackPanel(); feedback.TextWrapping = TextWrapping.Wrap; status.Children.Add(feedback);
        var recovery = new WrapPanel(); retry.Margin = new Thickness(0, 6, 8, 0); discard.Margin = new Thickness(0, 6, 8, 0);
        recovery.Children.Add(retry); recovery.Children.Add(discard); status.Children.Add(recovery); footer.Children.Add(status);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = root; retry.Click += async (_, _) => await Flush();
        discard.Click += (_, _) => { if (saving) return; pending.Clear(); error = ""; SelectPage(page); Refresh(); };
        Closing += (_, e) => { if (saving || pending.Count > 0) { e.Cancel = true; error = "设置尚未保存。请重试，或取消未保存修改后关闭。"; Refresh(); } };
        Closed += (_, _) => { closed = true; timer.Stop(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        timer.Tick += (_, _) => Refresh(); timer.Start(); SelectPage("appearance");
    }
    internal void SelectPage(string requested)
    {
        page = tabs.ContainsKey(requested) ? requested : "appearance";
        foreach (var (id, tab) in tabs) tab.IsChecked = id == page;
        body.Children.Clear(); sync.Clear(); localStatus = quotaStatus = null; localRetry = quotaRetry = null;
        applying = true;
        try { switch (page) { case "appearance": BuildAppearance(); break; case "display": BuildDisplay(); break; case "updates": BuildUpdates(); break; default: BuildHelp(); break; } }
        finally { applying = false; }
        Refresh();
    }
    internal async Task PrepareClose()
    {
        if (closed) return;
        await Flush();
        if (saving || pending.Count > 0) throw new IOException("设置尚未保存，请在设置窗口重试或取消未保存修改。");
        Close();
    }
    private static TextBlock Text(string value, double size = 12, bool bold = false) => new() { Text = value, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
    private void Heading(string title, string? note = null)
    {
        body.Children.Add(Text(title, 16, true)); if (note != null) { var text = Text(note); text.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryBrush"); body.Children.Add(text); }
    }
    private void Divider() => body.Children.Add(new Separator { Margin = new Thickness(0, 15, 0, 15) });
    private ComboBox Picker(string label, Option[] choices, Func<JsonObject, string> value, Action<string> changed, Panel? parent = null)
    {
        parent ??= body; var row = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        row.Children.Add(Text(label, 13)); var picker = new ComboBox { ItemsSource = choices, MinHeight = 34, MaxDropDownHeight = 260 };
        System.Windows.Automation.AutomationProperties.SetName(picker, label);
        Grid.SetColumn(picker, 1); row.Children.Add(picker); parent.Children.Add(row);
        void Update(JsonObject state)
        {
            if (picker.IsDropDownOpen || picker.IsKeyboardFocusWithin) return; string id = value(state);
            var selected = picker.Items.Cast<Option>().FirstOrDefault(x => x.Id == id);
            if (selected == null && label == "本地自动更新" && int.TryParse(id, out int seconds) && seconds is >= 1 and <= 3600)
            {
                selected = new Option(id, "每 " + seconds + " 秒"); picker.ItemsSource = picker.Items.Cast<Option>().Append(selected).ToArray();
            }
            picker.SelectedItem = selected ?? choices[0];
        }
        Update(read()); sync.Add(Update);
        picker.SelectionChanged += (_, _) => { if (!applying && picker.SelectedItem is Option option) changed(option.Id); };
        return picker;
    }
    private void Toggle(string label, string key, bool fallback)
    {
        var check = new CheckBox { Content = label, Margin = new Thickness(0, 9, 0, 9), IsChecked = read().O("settings").O("floating").B(key, fallback) }; body.Children.Add(check);
        sync.Add(state => { if (!check.IsKeyboardFocusWithin) check.IsChecked = state.O("settings").O("floating").B(key, fallback); });
        check.Click += (_, _) => Queue("float-" + key, J.Obj(("action", "floating-settings"), ("patch", J.Obj((key, check.IsChecked == true)))));
    }
    private static readonly Option[] Appearances = [new("system", "跟随系统"), new("light", "浅色"), new("dark", "深色")];
    private void BuildAppearance()
    {
        Heading("外观", "主面板、浮窗和托盘详情默认共用全局外观。独立外观会覆盖全局选择。");
        Picker("全局外观", Appearances, state => Theme.Appearance(state.O("settings")).S("theme", "system"), value => SaveAppearance(null, value));
        var inner = new StackPanel(); var expander = new Expander { Header = "独立外观", Content = inner, Margin = new Thickness(0, 14, 0, 0), IsExpanded = Theme.Appearance(read().O("settings")).O("overrides").Any(x => x.Value?.GetValue<string>() != "inherit") };
        body.Children.Add(expander);
        foreach (var (id, name) in new[] { ("main", "主面板"), ("floating", "悬浮窗"), ("tray", "托盘详情") })
            Picker(name, [new("inherit", "继承全局外观"), .. Appearances], state => Theme.Appearance(state.O("settings")).O("overrides").S(id, "inherit"), value => SaveAppearance(id, value), inner);
        var unify = new Button { Content = "三个界面都使用全局外观", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        unify.Click += (_, _) => { var next = Theme.Appearance(read().O("settings")); next["overrides"] = new JsonObject(); Queue("appearance", J.Obj(("action", "settings"), ("patch", J.Obj(("appearance", next))))); }; inner.Children.Add(unify);
    }
    private void SaveAppearance(string? surface, string preference)
    {
        var next = pending.TryGetValue("appearance", out var queued) ? queued.O("patch").O("appearance").Copy() : Theme.Appearance(read().O("settings"));
        if (surface == null) next["theme"] = preference; else { var overrides = next.O("overrides").Copy(); overrides[surface] = preference; next["overrides"] = overrides; }
        Queue("appearance", J.Obj(("action", "settings"), ("patch", J.Obj(("appearance", next)))));
    }
    private void BuildDisplay()
    {
        Heading("常驻显示方式", "只改变常驻入口，已打开的主面板继续保留。隐藏浮窗后仍可从托盘找回。");
        Picker("显示方式", [new("tray", "仅系统托盘"), new("float", "仅悬浮窗"), new("both", "托盘与悬浮窗")], state => state.O("settings").S("mode", "both"), value => Queue("mode", J.Obj(("action", "mode"), ("value", value))));
        Divider(); Heading("悬浮窗行为");
        body.Children.Add(Command("弧线配色…", () => send(J.Obj(("action", "arcColors")))));
        Toggle("保持展开：鼠标离开后保留详情", "keepExpanded", false);
        Toggle("始终置顶：显示在其他窗口上方", "pinned", true);
        Toggle("贴边自动隐藏", "edgeAutoHide", true);
        Picker("贴边指示条", [new("remaining", "显示剩余额度"), new("used", "显示已用额度")], state => state.O("settings").O("floating").S("edgeMetric", "remaining"), value => Queue("edgeMetric", J.Obj(("action", "floating-settings"), ("patch", J.Obj(("edgeMetric", value))))));
        body.Children.Add(Text("主动收起会变回圆环；鼠标离开再进入后才会再次自动展开。收起与隐藏均不会退出应用。"));
        Divider(); Heading("任务提醒", "收起浮窗、隐藏至托盘或关闭主面板都继续监控。提醒方式、声音和暂停通知在统一入口管理。 ");
        body.Children.Add(Command("任务提醒设置…", () => send(J.Obj(("action", "monitor-settings-dialog")))));
    }
    private static TextBox Readout() => new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0), Margin = new Thickness(0, 8, 0, 8), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 160 };
    private void BuildUpdates()
    {
        Heading("本地用量", "读取本机 Codex 日志。下面的自动更新间隔只控制本地日志。");
        localStatus = Readout(); body.Children.Add(localStatus);
        var path = Readout(); path.Text = read().S("home"); body.Children.Add(path);
        var localActions = new WrapPanel(); localRetry = Command("重新读取日志", () => send(J.Obj(("action", "refresh-local")))) ; localActions.Children.Add(localRetry);
        var directory = Command("更换数据目录…", async () =>
        {
            using var chooser = new System.Windows.Forms.FolderBrowserDialog { Description = "选择 Codex 数据目录", SelectedPath = read().S("home"), UseDescriptionForTitle = true };
            if (chooser.ShowDialog() == System.Windows.Forms.DialogResult.OK) { await send(J.Obj(("action", "home"), ("path", chooser.SelectedPath))); path.Text = read().S("home"); }
        }); localActions.Children.Add(directory); body.Children.Add(localActions);
        var options = new List<Option> { new("0", "暂停自动更新"), new("5", "每 5 秒"), new("15", "每 15 秒"), new("30", "每 30 秒"), new("60", "每 60 秒"), new("300", "每 5 分钟") };
        string current = read().O("settings").I("refresh", 5).ToString(); if (!options.Any(x => x.Id == current)) options.Add(new(current, "每 " + current + " 秒"));
        options.Add(new("custom", "自定义间隔…"));
        Picker("本地自动更新", options.ToArray(), state => state.O("settings").I("refresh", 5).ToString(), value =>
        {
            int? seconds = value == "custom" ? Dialogs.Interval(read().O("settings").I("refresh", 5)) : int.Parse(value);
            if (seconds != null) Queue("refresh", J.Obj(("action", "settings"), ("patch", J.Obj(("refresh", seconds.Value)))));
        });
        Divider(); Heading("账号额度", "通过本机已登录的 Codex 读取，约每 60 秒更新，与本地自动更新开关独立。");
        var quotaSwitch = new CheckBox { Content = "读取账号额度", Margin = new Thickness(0, 9, 0, 9) };
        void UpdateQuotaSwitch(JsonObject state)
        {
            bool locked = state.O("updates").O("quota").B("locked");
            quotaSwitch.IsChecked = !locked && state.O("settings").B("quotaEnabled", true);
            quotaSwitch.IsEnabled = !locked;
            quotaSwitch.ToolTip = locked ? "本次启动使用了 --no-quota；重新正常启动后可修改。" : "关闭后停止后续查询，保留上次账号快照；本地统计继续更新。";
        }
        UpdateQuotaSwitch(read()); sync.Add(UpdateQuotaSwitch); body.Children.Add(quotaSwitch);
        quotaSwitch.Click += (_, _) => Queue("quotaEnabled", J.Obj(("action", "settings"), ("patch", J.Obj(("quotaEnabled", quotaSwitch.IsChecked == true)))));
        quotaStatus = Readout(); body.Children.Add(quotaStatus); quotaRetry = Command("重试账号额度", () => send(J.Obj(("action", "refresh-quota")))); body.Children.Add(quotaRetry);
        body.Children.Add(Text("若读取失败，请确认本机 Codex 已安装并已登录，再重试。失败时保留上次成功记录并标明时间。"));
    }
    private void BuildHelp()
    {
        var state = read(); Heading("数据覆盖与统计口径");
        body.Children.Add(Text("本地统计只包含本机已记录日志。输入包含缓存输入，输出包含推理；总 Token 为输入与输出之和，不重复加上缓存。统计日界线为 UTC+08:00。账号额度来自账号快照，不随本地模型与任务筛选变化。"));
        body.Children.Add(Text(state.O("today").O("meta").S("coverage_note", "完整任务、模型和覆盖提示可在主面板查看。")));
        Divider(); Heading("帮助与恢复", "恢复损坏配置前会备份原文件。用量日志不受影响。");
        var diagnostics = Readout(); diagnostics.Text = "版本 " + (typeof(Host).Assembly.GetName().Version?.ToString(3) ?? "未知") + "\n应用数据：" + Paths.Base; body.Children.Add(diagnostics);
        body.Children.Add(Command("打开应用数据文件夹", () => { Process.Start(new ProcessStartInfo(Paths.Base) { UseShellExecute = true }); return Task.CompletedTask; }));
        if (state.S("settingsError").Length > 0)
        {
            body.Children.Add(Text(state.S("settingsError")));
            body.Children.Add(Command("备份并恢复设置…", async () => { if (MessageBox.Show(this, "备份损坏的设置文件并恢复默认设置？用量日志和预算规则保留。", "恢复设置", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) { await send(J.Obj(("action", "recover-settings"))); SelectPage("help"); } }));
        }
        if (state.O("budgets").O("recovery").B("required"))
        {
            body.Children.Add(Text(state.O("budgets").S("error", "预算配置无法读取，监测已暂停。")));
            body.Children.Add(Command("备份并新建预算配置…", async () => { if (MessageBox.Show(this, "备份原预算配置并新建空配置？原文件会保留，用量日志不变。", "恢复预算配置", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) { await send(J.Obj(("action", "budget"), ("operation", "recover"), ("payload", J.Obj(("confirm", true))))); SelectPage("help"); } }));
        }
        if (state.S("settingsError").Length == 0 && !state.O("budgets").O("recovery").B("required")) body.Children.Add(Text("设置和预算配置可正常读取，无需恢复。"));
    }
    private Button Command(string label, Func<Task> action)
    {
        var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 8, 8) };
        button.Click += async (_, _) => { button.IsEnabled = false; try { await action(); error = ""; } catch (Exception e) { error = e.Message; } finally { button.IsEnabled = true; Refresh(); } };
        return button;
    }
    private async void Queue(string key, JsonObject request) { if (applying || closed) return; pending[key] = request; await Flush(); }
    private async Task Flush()
    {
        if (saving || closed) return; saving = true; error = ""; Refresh();
        try
        {
            while (pending.Count > 0)
            {
                var item = pending.First(); await send(item.Value);
                if (pending.TryGetValue(item.Key, out var latest) && ReferenceEquals(latest, item.Value)) pending.Remove(item.Key);
            }
        }
        catch (Exception e) { error = "尚未保存：" + e.Message; }
        finally { saving = false; Refresh(); }
    }
    internal void Refresh()
    {
        if (closed) return; var state = read(); Theme.ApplyTo(Resources, Theme.Resolve(state.O("settings"), "main"));
        if (!saving && pending.Count == 0) { applying = true; try { foreach (var update in sync) update(state); } finally { applying = false; } }
        string Describe(JsonObject source) => source.S("status", "等待更新") + "\n上次成功：" + J.Date(source.N("updatedAt"), "yyyy-MM-dd HH:mm:ss") + (source.S("error").Length > 0 ? "\n" + source.S("error") : "");
        if (localStatus != null) { string text = Describe(state.O("updates").O("local")); if (localStatus.Text != text) localStatus.Text = text; }
        if (quotaStatus != null) { string text = Describe(state.O("updates").O("quota")); if (quotaStatus.Text != text) quotaStatus.Text = text; }
        if (localRetry != null) localRetry.IsEnabled = !state.O("updates").O("local").B("busy");
        if (quotaRetry != null) quotaRetry.IsEnabled = !state.O("updates").O("quota").B("busy") && state.O("updates").O("quota").B("enabled", true);
        feedback.Text = error.Length > 0 ? error : saving ? "正在保存…" : pending.Count > 0 ? "设置尚未保存" : "更改自动保存";
        retry.Visibility = discard.Visibility = pending.Count > 0 && !saving ? Visibility.Visible : Visibility.Collapsed;
    }
}
