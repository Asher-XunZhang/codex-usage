using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;

namespace CodexUsage;

/// <summary>Native budget list, details and draft-preserving editor; all writes go to the host.</summary>
internal sealed class BudgetView : UserControl
{
    private readonly Func<JsonObject, Task<JsonObject>> send;
    private readonly Action<string> status;
    private JsonObject state = new();
    private JsonObject editingRule = new();
    private readonly Dictionary<string, Control> fields = new();
    private readonly Dictionary<string, FrameworkElement> sections = new();
    private readonly List<Dictionary<string, TextBox>> prices = new();
    private StackPanel pricePanel = new();
    private TextBlock error = new();
    private TextBlock amountLabel = new();
    private TextBlock quotaStatus = new();
    private TextBlock changeTiming = new();
    private Button? quotaRefresh;
    private readonly DispatcherTimer draftTimer;
    private readonly SemaphoreSlim draftGate = new(1, 1);
    private readonly BudgetDrafts drafts = new();
    private readonly HashSet<string> expandedPending = new(StringComparer.Ordinal);
    private long persistedDraftRevision;
    private (string id, bool edit)? pendingSelection;
    private bool recoveryArmed;
    private string selected = "", deleteArmed = "";
    private string editorReturnSelection = "";
    private bool editing, building, busy, initialized;
    private ScrollViewer? listScroller, detailScroller;
    private bool resetDetailScroll;
    public string SelectedId => selected;
    public bool IsEditing => editing;
    public event Action<string, bool>? ViewingChanged;
    public async Task FlushDraft() { draftTimer.Stop(); CaptureDraft(); await PersistDraftBook(true); }
    private JsonArray Rules => state.O("budgets").A("rules");
    private JsonArray Summaries => state.O("budgets").A("summaries");
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(35, 173, 143));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(135, 146, 153));
    private static readonly Brush Danger = new SolidColorBrush(Color.FromRgb(231, 101, 99));
    private static bool AllowFocus => Environment.GetEnvironmentVariable("CODEX_USAGE_TEST_BACKGROUND") != "1";

    public BudgetView(Func<JsonObject, Task<JsonObject>> send, Action<string> status)
    {
        this.send = send; this.status = status;
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        draftTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        draftTimer.Tick += async (_, _) => { draftTimer.Stop(); await PersistDraft(); };
        Unloaded += (_, _) => { if (editing) { draftTimer.Stop(); _ = PersistDraft(); } };
        RenderList();
    }

    public void Update(JsonObject next)
    {
        var nextChoices = next["choices"] as JsonObject ?? state.O("choices");
        if (StateMatches(next, nextChoices)) return;
        bool choicesChanged = !JsonNode.DeepEquals(state["choices"], nextChoices);
        bool presentationChanged = !JsonNode.DeepEquals(state["budgets"], next["budgets"]) || choicesChanged
            || state.O("settings").I("refresh", 5) != next.O("settings").I("refresh", 5);
        string previousError = state.O("budgets").S("error");
        state = CopyWithChoices(next);
        if (!initialized)
        {
            initialized = true;
            drafts.Load(state.O("settings")["budgetDraft"] as JsonObject);
            var draft = drafts.Find(drafts.ActiveId);
            if (draft?["rule"] is JsonObject rule && draft["fields"] is JsonObject && !state.O("budgets").O("recovery").B("required"))
            {
                BeginEditing(rule, draft); status("已恢复本机未保存的预算草稿。"); return;
            }
        }
        if (!editing) { if (presentationChanged) RenderList(); }
        else
        {
            if (choicesChanged) RefreshEditorChoices();
            RefreshQuotaChoices();
            RefreshQuotaStatus();
            string nextError = state.O("budgets").S("error");
            if (nextError != previousError && (nextError.Length > 0 || error.Text == previousError)) error.Text = nextError;
        }
    }

    internal bool StateMatches(JsonObject next, JsonObject choices)
    {
        // Cache only inputs this view uses. Host polling timestamps, quota snapshots, main filters,
        // and busy flags are unrelated to its controls; local editing/selection remain independent.
        return initialized && JsonNode.DeepEquals(state["budgets"], next["budgets"])
            && JsonNode.DeepEquals(state["choices"], choices)
            && state.O("settings").I("refresh", 5) == next.O("settings").I("refresh", 5)
            && JsonNode.DeepEquals(state.O("settings")["floating"], next.O("settings")["floating"])
            && BudgetStore.AvailableQuotaWindows(state.O("quota")).SequenceEqual(BudgetStore.AvailableQuotaWindows(next.O("quota")))
            && state.O("updates").O("quota").B("busy") == next.O("updates").O("quota").B("busy")
            && JsonNode.DeepEquals(state.O("settings")["budgetDraft"], next.O("settings")["budgetDraft"]);
    }

    private JsonObject CopyWithChoices(JsonObject next)
    {
        var copy = next.Copy();
        // Regular command replies omit optional choice lists. Preserve the asynchronously loaded
        // lists so a save/pin reply cannot erase them now that idle polling skips redundant updates.
        if (!next.ContainsKey("choices")) copy["choices"] = state.O("choices").DeepClone();
        return copy;
    }

    public void Select(string id, bool edit = false)
    {
        if (busy) { pendingSelection = (id, edit); return; }
        if (editing) { draftTimer.Stop(); _ = PersistDraft(); }
        resetDetailScroll = selected != id || editing; selected = id; deleteArmed = "";
        var rule = Rules.Rows().FirstOrDefault(x => x.S("id") == id);
        if (edit && rule != null) BeginEditing(rule);
        else { editing = false; RenderList(); ViewingChanged?.Invoke(selected, false); }
    }

    private static TextBlock Text(string value, double size = 13, Brush? brush = null, bool bold = false)
    {
        var text = new TextBlock
        {
            Text = value,
            FontSize = size,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        if (brush == null) text.SetResourceReference(TextBlock.ForegroundProperty, "ForegroundBrush");
        else if (ReferenceEquals(brush, Muted)) text.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryBrush");
        else if (ReferenceEquals(brush, Accent)) text.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        else text.Foreground = brush;
        return text;
    }
    private static StackPanel Stack() => new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        CanContentScroll = false,
        Padding = new Thickness(0, 0, 10, 14)
    };
    private static Border Card(UIElement content, double padding = 20)
    {
        var border = new Border
        {
            Child = content,
            Padding = new Thickness(padding),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(55, 135, 146, 153)),
            Margin = new Thickness(0, 0, 0, 16)
        };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); return border;
    }
    private Button Button(string label, Action action)
    {
        var button = new Button { Content = label, Padding = new Thickness(13, 7, 13, 7), Margin = new Thickness(0, 0, 8, 8), MinHeight = 34 };
        AutomationProperties.SetName(button, label); button.Click += (_, _) => { if (!busy) action(); }; return button;
    }
    private Button AsyncButton(string label, Func<Task> action) => Button(label, () => _ = Execute(action));
    private async Task Execute(Func<Task> action)
    {
        if (busy) return; busy = true; IsEnabled = false;
        try { await action(); }
        catch (Exception e) { if (editing) error.Text = e.Message; status(e.Message); }
        finally
        {
            busy = false; IsEnabled = true;
            if (pendingSelection is { } selection) { pendingSelection = null; Select(selection.id, selection.edit); }
        }
    }
    private Grid Page(string title, string subtitle, UIElement body, params Button[] buttons)
    {
        var grid = new Grid(); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new());
        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 16) };
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; foreach (var b in buttons) actions.Children.Add(b);
        DockPanel.SetDock(actions, Dock.Right); header.Children.Add(actions);
        var heading = Stack(); heading.Children.Add(Text(title, 24, bold: true)); heading.Children.Add(Text(subtitle, 12, Muted)); header.Children.Add(heading);
        grid.Children.Add(header); Grid.SetRow(body, 1); grid.Children.Add(body); return grid;
    }
    private static string Kind(string kind) => kind == "money" ? "估算金额" : kind == "quota" ? "官方余量下限" : "Token 数量";
    private static string Metric(string metric) => metric == "noncached" ? "非缓存输入 + 输出" : metric == "output" ? "仅输出 Token（含推理）" : "总 Token · 输入 + 输出";
    private static string PeriodName(string type) => type switch { "week" => "每周", "month" => "每月", "once" => "自定义时段", "interval" => "固定时长", _ => "每日" };
    private static string Value(double? value, string kind, string currency)
    {
        if (value is not double n) return "未知";
        return kind == "money" ? "≈ " + (currency == "CNY" ? "¥ " : "$ ") + n.ToString("N2", CultureInfo.InvariantCulture) :
            kind == "quota" ? n.ToString("0.####", CultureInfo.InvariantCulture) + "%" : J.Compact(n) + " Token";
    }
    private static string Date(double? time, string zone)
    {
        if (time is not double n) return "等待本期区间";
        try { return TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds((long)(n * 1000)), BudgetStore.Zone(zone)).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); }
        catch (ArgumentException) { return "时间超出可显示范围"; }
    }
    private string ChoiceLabel(string key, string id)
    {
        if (id == "all") return key == "models" ? "全部模型" : "全部任务";
        return state.O("choices").A(key).Rows().FirstOrDefault(x => x.S("id") == id)?.S("label", id) ?? id;
    }

    private void RenderList()
    {
        if (state.O("budgets").O("recovery").B("required")) { RenderRecovery(); return; }
        double listOffset = listScroller?.VerticalOffset ?? 0, detailOffset = resetDetailScroll ? 0 : detailScroller?.VerticalOffset ?? 0;
        resetDetailScroll = false;
        var columns = new Grid(); columns.ColumnDefinitions.Add(new() { Width = new GridLength(.3, GridUnitType.Star), MinWidth = 208, MaxWidth = 262 }); columns.ColumnDefinitions.Add(new() { Width = new GridLength(16) }); columns.ColumnDefinitions.Add(new());
        var listColumn = new DockPanel(); var listHeading = Stack(); listHeading.Children.Add(Text("我的预算", 12, Muted));
        var create = Button("＋ 新建预算", () => BeginEditing(null)); create.HorizontalContentAlignment = HorizontalAlignment.Left; listHeading.Children.Add(create);
        DockPanel.SetDock(listHeading, Dock.Top); listColumn.Children.Add(listHeading);
        var list = Stack();
        foreach (var draft in drafts.Items)
        {
            string draftId = draft.O("rule").S("id"), name = draft.O("fields").S("name").Trim();
            if (name.Length == 0) name = "未命名预算";
            var resume = Button("继续草稿 · " + name, () => ResumeDraft(draftId));
            resume.Content = new TextBlock { Text = "继续草稿 · " + name, TextTrimming = TextTrimming.CharacterEllipsis };
            resume.HorizontalContentAlignment = HorizontalAlignment.Left; resume.ToolTip = "继续草稿 · " + name + "（尚未应用）";
            list.Children.Add(resume);
        }
        foreach (var rule in Rules.Rows())
        {
            string id = rule.S("id"); var summary = Summaries.Rows().FirstOrDefault(x => x.S("id") == id) ?? new();
            string kind = summary.S("kind", rule.S("kind")), metric = summary.S("tokenMetric", rule.S("tokenMetric"));
            var body = Stack();
            var name = Text(rule.S("name"), 15, bold: true); name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; name.ToolTip = rule.S("name"); body.Children.Add(name);
            body.Children.Add(Text((kind == "token" ? Metric(metric) : Kind(kind)) + " · " + PeriodName(summary.O("period").S("type", rule.O("period").S("type"))), 11, Muted));
            string label = !rule.B("enabled", true) ? "已停用" : summary.B("paused") ? "提醒暂停" : kind == "quota" ? "官方余量" : "预算剩余";
            body.Children.Add(Text(label + "    " + (summary.N("remainingPercent") is double remaining ? remaining.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "未知"), 14,
                summary.S("status") is "exhausted" or "exceeded" ? Danger : Accent, true));
            var button = Button("", () => Select(id)); button.Content = body; button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.Padding = new Thickness(16, 12, 16, 6); button.MinHeight = 116; button.Margin = new Thickness(0, 0, 0, 10);
            if (id == selected) { button.BorderBrush = Accent; button.BorderThickness = new Thickness(2); }
            AutomationProperties.SetName(button, rule.S("name")); list.Children.Add(button);
        }
        list.Children.Add(Text("每项预算独立统计。\n切换用量筛选不会修改预算。", 11, Muted));
        var listScroll = Scroll(list); listScroller = listScroll; listColumn.Children.Add(listScroll); columns.Children.Add(listColumn);
        var detail = Scroll(Detail()); detailScroller = detail; Grid.SetColumn(detail, 2); columns.Children.Add(detail);
        listScroll.Loaded += (_, _) => listScroll.ScrollToVerticalOffset(listOffset);
        detail.Loaded += (_, _) => detail.ScrollToVerticalOffset(detailOffset);
        string subtitle = Rules.Rows().Count(x => x.B("enabled", true)) + " 项已启用 · 仅视觉提醒";
        if (state.O("budgets").S("error").Length > 0) subtitle = state.O("budgets").S("error");
        Content = Page("预算与提醒", subtitle, columns,
            AsyncButton("刷新", async () => { Update(await send(J.Obj(("action", "refresh")))); status("已请求更新，完成后显示最新预算数据。"); }));
    }
    private void ResumeDraft(string id)
    {
        var draft = drafts.Find(id); if (draft == null) return;
        BeginEditing(draft.O("rule"), draft);
    }
    private void RenderRecovery()
    {
        var recovery = state.O("budgets").O("recovery"); var body = Stack();
        body.Children.Add(Text("预算配置需要恢复", 20, bold: true));
        body.Children.Add(Text(state.O("budgets").S("error"), 13, Danger));
        body.Children.Add(Text("预算监测已暂停，原文件没有被覆盖。恢复会先完整备份原配置，再建立空预算配置；本机用量记录与编辑草稿保留。", 13));
        body.Children.Add(Text("原文件：" + recovery.S("path"), 12, Muted));
        body.Children.Add(AsyncButton(recoveryArmed ? "确认备份并重新开始" : "备份原配置并重新开始…", async () =>
        {
            if (!recoveryArmed) { recoveryArmed = true; RenderRecovery(); return; }
            var next = await send(J.Obj(("action", "budget"), ("operation", "recover"), ("payload", J.Obj(("confirm", true)))));
            recoveryArmed = false; Update(next); RenderList();
        }));
        if (recoveryArmed)
        {
            body.Children.Add(Text("备份成功后，原预算规则与提醒历史仅保留在备份文件中，不会自动监测。再次点击确认执行。", 12, Muted));
            body.Children.Add(Button("取消恢复", () => { recoveryArmed = false; RenderRecovery(); }));
        }
        Content = Page("预算与提醒", "修复配置后可重新创建预算", Scroll(Card(body)));
    }
    private UIElement Detail()
    {
        var rule = Rules.Rows().FirstOrDefault(x => x.S("id") == selected);
        if (rule == null)
        {
            var empty = Stack(); empty.Children.Add(Text(selected.Length == 0 ? "为你的使用节奏设置预算" : "此预算已删除或暂不可用", 20, bold: true));
            if (state.O("budgets").O("recovery").S("backupPath") is string backup && backup.Length > 0) empty.Children.Add(Text("原预算配置已完整备份至：" + backup, 12, Muted));
            empty.Children.Add(Text("自定义时间、模型和任务范围。接近额度时通过无声视觉提醒呈现，不会中断任务。", 13, Muted));
            empty.Children.Add(Button("创建预算…", () => BeginEditing(null))); return Card(empty);
        }
        string id = rule.S("id"); var summary = Summaries.Rows().FirstOrDefault(x => x.S("id") == id) ?? new();
        var current = rule.Copy(); foreach (string key in new[] { "kind", "currency", "period", "model", "task", "tokenMetric", "windowMinutes", "amount" }) if (summary.ContainsKey(key)) current[key] = summary[key]?.DeepClone();
        string kind = current.S("kind"), currency = current.S("currency"), dataStatus = summary.S("status", "unknown");
        var body = Stack(); body.Children.Add(Text(rule.S("name"), 20, bold: true));
        var header = new WrapPanel { Margin = new Thickness(0, 2, 0, 6) };
        header.Children.Add(Button("编辑", () => BeginEditing(rule)));
        header.Children.Add(AsyncButton("在浮窗查看", () => Pin(id)));
        header.Children.Add(MenuButton("更多", MoreMenu(rule))); body.Children.Add(header);
        if (deleteArmed == id)
        {
            var confirmation = Stack(); confirmation.Children.Add(Text("确认删除“" + rule.S("name") + "”？预算规则和提醒历史会被删除，本机用量记录不受影响。", 12, Danger));
            var confirmActions = new WrapPanel(); confirmActions.Children.Add(AsyncButton("确认删除此预算", async () => { await Operation("delete", J.Obj(("id", id), ("expectedRevision", rule.I("revision")))); deleteArmed = ""; }));
            confirmActions.Children.Add(Button("保留预算", () => { deleteArmed = ""; RenderList(); })); confirmation.Children.Add(confirmActions); body.Children.Add(Card(confirmation, 12));
        }
        body.Children.Add(Text(kind == "token" ? Metric(current.S("tokenMetric")) : Kind(kind), 12, Muted));
        if (summary.B("scheduledChange"))
        {
            body.Children.Add(Text("修改已保存，将于下期生效；以下仍为本期有效范围。", 12, Accent));
            body.Children.Add(PendingChanges(rule, summary));
        }
        body.Children.Add(Text(summary.S("message", "等待预算数据更新"), 13, dataStatus is "exceeded" or "exhausted" ? Danger : Muted));
        if (summary.S("issueCode") == "window_invalid") body.Children.Add(Button("修正官方额度窗口…", () => BeginRepair(rule, summary, false)));
        var issues = summary.A("issues").Rows().ToArray();
        foreach (var issue in issues.Take(12)) body.Children.Add(Text((issue.S("model").Length > 0 ? issue.S("model") + " · " : "") + issue.S("message"), 12, Muted));
        if (issues.Length > 12) body.Children.Add(Text("另有 " + (issues.Length - 12) + " 项待补全；编辑中会列出所有缺价模型。", 12, Muted));
        if (issues.Any(x => x.S("type") == "price")) body.Children.Add(Button("补齐单价并重算本期…", () => BeginRepair(rule, summary, true)));
        double overage = summary.N("overage") ?? 0;
        bool lower = summary.S("coverage") == "partial" && dataStatus is "exhausted" or "exceeded";
        bool known = summary.N("remaining") != null && dataStatus is not ("unknown" or "partial" or "source_invalid" or "scope_invalid");
        body.Children.Add(Text(kind == "quota" ? "官方剩余额度" : lower ? overage > 0 ? "已知至少超出" : "已知用量已达到预算" : overage > 0 ? "已超出预算" : "剩余预算", 12, Muted));
        string balance = Value(known ? kind != "quota" && overage > 0 ? overage : summary.N("remaining") : null, kind, currency);
        if (lower && overage > 0) balance = "≥ " + balance.Replace("≈ ", "");
        body.Children.Add(Text(balance, 38, dataStatus is "exceeded" or "exhausted" ? Danger : Accent, true));
        var meter = new ProgressBar { Minimum = 0, Maximum = 1, Value = known ? Math.Clamp(summary.N("remainingFraction") ?? 0, 0, 1) : 0, Height = 7, Foreground = Accent, Margin = new Thickness(0, 0, 0, 14) };
        meter.ToolTip = known ? "剩余 " + (100 * meter.Value).ToString("0.#", CultureInfo.InvariantCulture) + "%" : "剩余未知"; body.Children.Add(meter);
        string used = Value(summary.N("used"), kind, currency), limit = Value(kind == "quota" ? summary.N("quotaFloor") ?? rule.N("amount") : current.N("amount"), kind, currency);
        body.Children.Add(Text(kind == "quota" ? "官方余量 ≤ " + limit + " 时提醒" : "已用 " + (lower ? "≥ " + used.Replace("≈ ", "") : used) + "    额度 " + limit, 12, Muted));
        string zone = current.O("period").S("timezone", BudgetStore.LocalIanaZone);
        DetailRow(body, "当前周期", Date(summary.N("periodStart"), zone) + " — " + Date(summary.N("periodEnd"), zone) + "\n" + zone + " · " + PeriodName(current.O("period").S("type")));
        DetailRow(body, "统计范围", kind == "quota" ? "账号级 · " + current.I("windowMinutes", 10080) + " 分钟额度窗口" : ChoiceLabel("models", current.S("model", "all")) + " · " + ChoiceLabel("tasks", current.S("task", "all")));
        if (kind == "quota" && summary.N("resetsAt") is double resetAt) DetailRow(body, "官方重置", Date(resetAt, zone) + " · 与上方监测计划独立");
        DetailRow(body, "数据依据", kind == "money" ? "本机记录 × 自定义模型单价 · 估算金额，不代表订阅账单" : kind == "quota" ? "官方账号额度快照 · 不按模型或任务拆分" : "本机已落盘记录 · 缓存与推理不重复计算");
        int refresh = state.O("settings").I("refresh", 5);
        DetailRow(body, "更新节奏", kind == "quota" ? "官方额度约 60 秒更新" : refresh == 0 ? "Token / 金额自动更新暂停；手动刷新后判断" : "Token / 金额每 " + refresh + " 秒更新");
        if (summary.N("updatedAt") != null) DetailRow(body, "更新于", Date(summary.N("updatedAt"), zone));
        if (summary.S("coverage") != "complete") DetailRow(body, "数据覆盖", lower ? "以上为已知消费下界；缺失类别仍未计入，实际消费可能更高。" : summary.S("coverage") == "partial" ? "部分数据缺失，剩余额度暂无法确认" : "尚无完整数据");
        var reminder = Stack(); reminder.Margin = new Thickness(0, 18, 0, 0); reminder.Children.Add(Text("提醒", 15, bold: true));
        string thresholds = string.Join(" / ", rule.A("thresholds").Select(x => J.Number(x)?.ToString("0.####", CultureInfo.InvariantCulture) + "%"));
        reminder.Children.Add(Text(kind == "quota" ? "官方余量 ≤ " + limit + " 时提醒；监测周期或官方窗口重置后，可再次提醒。" : "剩余 " + thresholds + " · 每个监测周期每级一次", 12));
        reminder.Children.Add(Text(!rule.B("enabled", true) ? "预算已停用，可在“更多”中启用。" : summary.B("paused") ? "提醒暂停至 " + Date(summary.N("pausedUntil"), zone) + "；统计继续更新。" : "无声视觉提醒；暂停提醒时，统计仍继续更新。", 12, Muted));
        if (rule.B("enabled", true)) reminder.Children.Add(summary.B("paused") ? AsyncButton("恢复提醒", () => Operation("resume", J.Obj(("id", id)))) : MenuButton("暂停提醒", PauseMenu(id)));
        body.Children.Add(reminder);
        var events = state.O("budgets").A("events").Rows().Where(x => x.S("ruleID") == id).Take(5).ToArray();
        if (events.Length > 0)
        {
            body.Children.Add(Text("最近提醒", 15, bold: true));
            foreach (var item in events) body.Children.Add(Text(Date(item.N("createdAt"), zone) + "   " + item.S("message", "预算视觉提醒"), 12, Muted));
        }
        return Card(body);
    }
    private UIElement PendingChanges(JsonObject rule, JsonObject summary)
    {
        var pending = summary.O("pendingChange");
        var current = pending["current"] as JsonObject ?? summary.Copy(); var next = pending["next"] as JsonObject ?? rule;
        if (!pending.ContainsKey("current") && current.S("kind") == "quota") current["amount"] = current["quotaFloor"]?.DeepClone();
        var changes = new List<(string label, string before, string after)>();
        string DisplayField(string key, JsonObject data) => key switch
        {
            "kind" => Kind(data.S(key)), "tokenMetric" => Metric(data.S(key)),
            "model" => ChoiceLabel("models", data.S(key)), "task" => ChoiceLabel("tasks", data.S(key)),
            "currency" => data.S(key) == "CNY" ? "CNY · 人民币" : "USD · 美元",
            "fx" => "1 USD = " + data.N(key)?.ToString("G", CultureInfo.InvariantCulture) + " CNY",
            "period" => PeriodDescription(data.O(key)), "windowMinutes" => BudgetStore.QuotaWindowName(data.I(key)),
            "amount" => Value(data.N(key), data.S("kind"), data.S("currency")), _ => data.S(key)
        };
        foreach (var (key, label) in new[] { ("kind", "预算口径"), ("amount", "额度"), ("tokenMetric", "Token 口径"), ("model", "模型"), ("task", "任务"),
            ("period", "周期"), ("windowMinutes", "官方额度窗口"), ("currency", "币种"), ("fx", "固定汇率") })
            if (current.ContainsKey(key) && !JsonNode.DeepEquals(current[key], next[key])) changes.Add((label, DisplayField(key, current), DisplayField(key, next)));
        if (current["prices"] is JsonArray currentPrices)
        {
            var before = currentPrices.Rows().ToDictionary(x => x.S("model"), StringComparer.Ordinal);
            var after = next.A("prices").Rows().ToDictionary(x => x.S("model"), StringComparer.Ordinal);
            foreach (string model in before.Keys.Union(after.Keys).Order(StringComparer.Ordinal))
            {
                var oldPrice = before.GetValueOrDefault(model) ?? new(); var newPrice = after.GetValueOrDefault(model) ?? new();
                var changed = new[] { ("input", "输入"), ("cachedInput", "缓存输入"), ("output", "输出") }.Where(x => !JsonNode.DeepEquals(oldPrice[x.Item1], newPrice[x.Item1])).ToArray();
                if (changed.Length == 0)
                {
                    if (before.ContainsKey(model) != after.ContainsKey(model)) changes.Add(("模型单价 · " + model, before.ContainsKey(model) ? "单价尚未设置" : "未配置模型", after.ContainsKey(model) ? "单价尚未设置" : "已移除模型"));
                    continue;
                }
                string Prices(JsonObject price) => string.Join("；", changed.Select(x => x.Item2 + " " + (price.N(x.Item1)?.ToString("G", CultureInfo.InvariantCulture) ?? "未设置")));
                changes.Add(("模型单价 · " + model + "（USD / 百万 Token）", Prices(oldPrice), Prices(newPrice)));
            }
        }
        var content = Stack(); content.Margin = new Thickness(0, 8, 0, 0);
        content.Children.Add(Text("生效于 " + Date(pending.N("effectiveAt") ?? summary.N("periodEnd"), current.O("period").S("timezone", BudgetStore.LocalIanaZone)), 11, Muted));
        foreach (var change in changes)
        {
            content.Children.Add(Text(change.label, 12, bold: true));
            content.Children.Add(Text("本期：" + change.before, 12, Muted)); content.Children.Add(Text("下期：" + change.after, 12));
        }
        if (changes.Count == 0) content.Children.Add(Text(pending.ContainsKey("current") ? "本期与下期的显示值相同；已保存的设置会于下期应用。" : "下期设置已保存，等待本期对照数据更新。", 12, Muted));
        string id = rule.S("id");
        var expander = new Expander { Name = "BudgetPendingChanges", Header = Text("下期设置 · " + changes.Count + " 项变更", 12, bold: true), Content = content, Margin = new Thickness(0, 0, 0, 12), HorizontalContentAlignment = HorizontalAlignment.Stretch, IsExpanded = expandedPending.Contains(id) };
        expander.Expanded += (_, _) => expandedPending.Add(id); expander.Collapsed += (_, _) => expandedPending.Remove(id);
        expander.SetResourceReference(ForegroundProperty, "ForegroundBrush"); AutomationProperties.SetName(expander, "下期设置"); return expander;
    }
    private static string PeriodDescription(JsonObject period)
    {
        string type = period.S("type"), zone = period.S("timezone", BudgetStore.LocalIanaZone);
        string clock = period.I("hour").ToString("D2") + ":" + period.I("minute").ToString("D2");
        string details = type switch
        {
            "week" => new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" }[Math.Clamp(period.I("weekday", 2), 1, 7) - 1] + " " + clock,
            "month" => period.I("day", 1) + " 日 " + clock,
            "once" => Date(period.N("start"), zone) + " — " + Date(period.N("end"), zone),
            "interval" => "从 " + Date(period.N("start"), zone) + " 起，每 " + ((period.N("seconds") ?? 86400) / 3600).ToString("0.####", CultureInfo.InvariantCulture) + " 小时",
            _ => clock
        };
        return PeriodName(type) + " · " + details + " · " + zone;
    }
    private static void DetailRow(Panel body, string label, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 8, 0, 0) }; row.ColumnDefinitions.Add(new() { Width = new GridLength(82) }); row.ColumnDefinitions.Add(new());
        row.Children.Add(Text(label, 12, Muted)); var text = Text(value, 12); Grid.SetColumn(text, 1); row.Children.Add(text); body.Children.Add(row);
    }
    private async Task Operation(string operation, JsonObject payload)
    {
        Update(await send(J.Obj(("action", "budget"), ("operation", operation), ("payload", payload)))); status("预算设置已保存。");
    }
    private async Task Pin(string id)
    {
        Update(await send(J.Obj(("action", "view-budget"), ("budgetID", id)))); status("已在浮窗显示此预算。");
    }
    private Button MenuButton(string label, ContextMenu menu)
    {
        var button = Button(label + " ▾", () => { }); AutomationProperties.SetName(button, label);
        button.ContextMenu = menu; button.Click += (_, _) => { if (busy) return; menu.PlacementTarget = button; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true; };
        menu.Closed += (_, _) => { if (AllowFocus && button.IsVisible && IsKeyboardFocusWithin) button.Focus(); };
        return button;
    }
    private MenuItem MenuAction(string label, Func<Task> action)
    {
        var item = new MenuItem { Header = label }; item.Click += (_, _) => _ = Execute(action); return item;
    }
    private ContextMenu MoreMenu(JsonObject rule)
    {
        string id = rule.S("id"); var menu = new ContextMenu();
        menu.Items.Add(MenuAction(rule.B("enabled", true) ? "停用预算" : "启用预算", () => Operation(rule.B("enabled", true) ? "disable" : "enable", J.Obj(("id", id), ("expectedRevision", rule.I("revision"))))));
        menu.Items.Add(MenuAction("复制预算…", () => { var copy = rule.Copy(); copy["id"] = Guid.NewGuid().ToString(); copy["revision"] = 0; copy["name"] = rule.S("name")[..Math.Min(rule.S("name").Length, 96)] + " 副本"; BeginEditing(copy, returnSelection: id); return Task.CompletedTask; }));
        menu.Items.Add(new Separator()); var delete = MenuAction("删除预算…", () => { deleteArmed = id; RenderList(); return Task.CompletedTask; }); delete.Foreground = Danger; menu.Items.Add(delete);
        return menu;
    }
    private ContextMenu PauseMenu(string id)
    {
        var menu = new ContextMenu(); menu.Items.Add(MenuAction("暂停 30 分钟", () => Operation("pause", J.Obj(("id", id), ("durationSeconds", 1800)))));
        menu.Items.Add(MenuAction("本监测周期不再提醒", () => Operation("pause", J.Obj(("id", id), ("mode", "cycle"))))); return menu;
    }

    private void BeginEditing(JsonObject? original, JsonObject? draft = null, string? returnSelection = null)
    {
        if (editing) { CaptureDraft(); _ = PersistDraftBook(); }
        draft ??= original != null ? drafts.Find(original.S("id")) : null;
        // A restored draft keeps the revision it was edited against. Attaching old fields to a
        // newer rule would silently bypass the host's conflict protection.
        if (draft?["rule"] is JsonObject draftRule) original = draftRule;
        editorReturnSelection = returnSelection ?? (draft != null ? draft.S("returnSelection") : original?.S("id") ?? selected);
        editing = true; building = true; deleteArmed = ""; fields.Clear(); sections.Clear(); prices.Clear(); draftTimer.Stop();
        editingRule = original?.Copy() ?? J.Obj(("id", Guid.NewGuid().ToString()), ("revision", 0), ("name", ""), ("kind", "token"), ("amount", 20000000),
            ("model", "all"), ("task", "all"), ("tokenMetric", "total"), ("currency", "USD"), ("fx", 1), ("prices", new JsonArray()),
            ("period", J.Obj(("type", "day"), ("timezone", BudgetStore.LocalIanaZone), ("hour", 0), ("minute", 0))), ("thresholds", new[] { 20, 10, 0 }),
            ("enabled", true), ("windowMinutes", 10080), ("quotaCondition", "floor"));
        selected = editingRule.S("id"); var period = editingRule.O("period"); string kind = editingRule.S("kind");
        var form = Stack(); var basics = Stack(); basics.Children.Add(Text("名称与额度", 16, bold: true));
        basics.Children.Add(Field("预算名称", Input("name", editingRule.S("name"), "例如：日常开发")));
        basics.Children.Add(Field("预算口径", Combo("kind", new[] { ("token", "Token 数量"), ("money", "估算金额"), ("quota", "官方余量下限") }, kind)));
        var quota = Stack(); quota.Children.Add(Text("官方余量下限按账号快照判断。期间消耗上限暂不可用：缺少稳定账号与窗口身份，无法可靠区分重置及跨设备变化。", 12, Muted)); AddSection(basics, "quota", quota);
        double amount = editingRule.N("amount") ?? 20000000;
        var amountRow = Field("每期额度", Input("amount", (kind == "token" ? amount / 1000000 : amount).ToString("G", CultureInfo.InvariantCulture)), out amountLabel); basics.Children.Add(amountRow);
        AddSection(basics, "tokenUnit", Field("Token 单位", Combo("tokenUnit", new[] { ("M", "百万 Token · M"), ("K", "千 Token · K"), ("raw", "Token") }, "M")));
        AddSection(basics, "tokenMetric", Field("Token 口径", Combo("tokenMetric", new[] { ("total", "总 Token（缓存与推理不重复计入）"), ("noncached", "非缓存输入 + 输出"), ("output", "仅输出（含推理）") }, editingRule.S("tokenMetric", "total"))));
        var windowSection = Stack();
        int selectedWindow = editingRule.I("windowMinutes", 10080); var actualWindows = BudgetStore.AvailableQuotaWindows(state.O("quota"));
        if (original == null && actualWindows.Length > 0 && !actualWindows.Contains(selectedWindow)) selectedWindow = actualWindows[0];
        windowSection.Children.Add(Field("官方额度窗口", Combo("windowMinutes", QuotaChoices(selectedWindow), selectedWindow.ToString(CultureInfo.InvariantCulture))));
        windowSection.Children.Add(Text("选择当前账号实际提供的窗口。窗口未出现时先刷新官方额度；这里不会改变官方重置时间。", 11, Muted));
        quotaStatus = Text("", 11, Muted); windowSection.Children.Add(quotaStatus);
        quotaRefresh = AsyncButton("刷新官方额度", async () => { Update(await send(J.Obj(("action", "refresh-quota")))); }); windowSection.Children.Add(quotaRefresh);
        RefreshQuotaStatus();
        AddSection(basics, "window", windowSection);
        var currency = Stack(); currency.Children.Add(Field("币种", Combo("currency", new[] { ("USD", "USD · 美元"), ("CNY", "CNY · 人民币") }, editingRule.S("currency", "USD"))));
        currency.Children.Add(Field("固定汇率 USD → CNY", Input("fx", (editingRule.N("fx") ?? 1).ToString("G", CultureInfo.InvariantCulture)))); AddSection(basics, "currency", currency);
        var money = Stack(); money.Children.Add(Text("模型单价", 16, bold: true));
        money.Children.Add(Text("使用你填写的模型单价，不代表订阅扣费。价格与汇率固定于当前预算周期；留空表示未知。", 11, Muted));
        money.Children.Add(Text("每百万 Token 的 USD 单价", 13, bold: true));
        pricePanel = Stack(); money.Children.Add(pricePanel);
        if (draft?["prices"] is JsonArray draftPrices) foreach (var price in draftPrices.Rows()) AddPrice(price, true);
        else foreach (var price in editingRule.A("prices").Rows()) AddPrice(price);
        var add = Button("＋ 添加模型单价", () => { AddPrice(new()); Changed(); }); add.HorizontalAlignment = HorizontalAlignment.Left; money.Children.Add(add);
        form.Children.Add(Card(basics));

        var dates = Stack(); dates.Children.Add(Text("范围与周期", 16, bold: true));
        var scope = Stack();
        scope.Children.Add(Field("模型", Combo("model", Choices("models"), editingRule.S("model", "all")))); scope.Children.Add(Field("任务", Combo("task", Choices("tasks"), editingRule.S("task", "all"))));
        scope.Children.Add(Text("范围独立保存，不跟随用量页筛选。官方额度为账号级，不支持拆分模型与任务。", 11, Muted)); AddSection(dates, "scope", scope);
        dates.Children.Add(Field("周期", Combo("period", new[] { ("day", "每日重置"), ("week", "每周重置"), ("month", "每月重置"), ("once", "自定义起止时间"), ("interval", "固定时长重复") }, period.S("type", "day"))));
        dates.Children.Add(Field("时区", TimeZoneInput(period.S("timezone", BudgetStore.LocalIanaZone))));
        AddSection(dates, "reset", Field("重置时间", ClockInput(period.I("hour"), period.I("minute"))));
        AddSection(dates, "week", Field("每周重置日", Combo("weekday", new[] { ("1", "周日"), ("2", "周一"), ("3", "周二"), ("4", "周三"), ("5", "周四"), ("6", "周五"), ("7", "周六") }, period.I("weekday", 2).ToString(CultureInfo.InvariantCulture))));
        AddSection(dates, "month", Field("每月日期", Combo("day", Enumerable.Range(1, 31).Select(x => (x.ToString(CultureInfo.InvariantCulture), x + " 日")), period.I("day", 1).ToString(CultureInfo.InvariantCulture))));
        string zone = period.S("timezone", BudgetStore.LocalIanaZone);
        AddSection(dates, "start", Field("开始 · 所选时区", DateTimeInput("start", Date(period.N("start") ?? J.Now, zone))));
        AddSection(dates, "end", Field("结束 · 所选时区", DateTimeInput("end", Date(period.N("end") ?? J.Now + 86400, zone))));
        AddSection(dates, "interval", Field("每期小时数", Input("hours", ((period.N("seconds") ?? 86400) / 3600).ToString("G", CultureInfo.InvariantCulture))));
        dates.Children.Add(Text("包含开始时刻、不包含结束时刻。短月份取月末；夏令时由当地日历处理。本期已经发生的记录会计入预算。官方实际重置时间由账号决定。", 11, Muted)); form.Children.Add(Card(dates));

        AddSection(form, "money", Card(money));
        var notifications = Stack(); notifications.Children.Add(Text("提醒", 16, bold: true));
        AddSection(notifications, "thresholds", Field("剩余比例 · %", Input("thresholds", string.Join(", ", editingRule.A("thresholds").Select(x => J.Number(x)?.ToString("G", CultureInfo.InvariantCulture))), "20, 10, 0")));
        notifications.Children.Add(Text("逗号分隔；每周期每级一次，跨过多个节点时只提示最严重一级。无声音、不强制展开、不打断任务。", 11, Muted));
        notifications.Children.Add(Check("enabled", "启用预算监测", editingRule.B("enabled", true))); form.Children.Add(Card(notifications));
        var footer = Stack(); footer.Name = "BudgetEditorFooter";
        var preferences = new WrapPanel();
        if (editingRule.I("revision") > 0)
        {
            var immediate = Check("recalculateCurrent", "立即应用并重算本期", false); immediate.Margin = new Thickness(0, 0, 20, 8); preferences.Children.Add(immediate);
            changeTiming = Text("", 11, Muted); changeTiming.Name = "BudgetChangeTiming"; footer.Children.Add(changeTiming);
            var stored = Rules.Rows().FirstOrDefault(x => x.S("id") == editingRule.S("id"));
            if (stored == null || stored.I("revision") != editingRule.I("revision"))
            {
                form.Children.Add(Text(stored == null ? "原预算已删除或配置已重建。草稿仍保留，可另存为新预算。" : "原预算已被更新。草稿仍保留；可另存为新预算，或取消草稿后重新编辑最新规则。", 12, Muted));
                form.Children.Add(Button("将草稿另存为新预算…", CopyDraftAsNew));
            }
        }
        else footer.Children.Add(Text("保存后开始监测；本期已经发生的记录也会计入预算。", 11, Muted));
        var show = Check("pin", "保存后在浮窗查看", false); show.Margin = new Thickness(0, 0, 0, 8); preferences.Children.Add(show); footer.Children.Add(preferences);
        error = Text("", 12, Danger); error.Name = "BudgetEditorError";
        var errorScroll = Scroll(error); errorScroll.MaxHeight = 60; errorScroll.Padding = new Thickness(0); footer.Children.Add(errorScroll);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(AsyncButton("取消", async () => { await ClearDraft(); editing = false; selected = editorReturnSelection; RenderList(); ViewingChanged?.Invoke(selected, false); status("已取消编辑，原有监测保持不变。"); }));
        actions.Children.Add(AsyncButton("保存预算", Save)); footer.Children.Add(actions);
        if (draft?["fields"] is JsonObject savedFields) foreach (var pair in fields)
        {
            if (!savedFields.ContainsKey(pair.Key)) continue;
            if (pair.Value is TextBox text) text.Text = savedFields.S(pair.Key);
            else if (pair.Value is BudgetDateTimeInput date) { date.Value = savedFields.S(pair.Key); date.RestoreClock(savedFields, pair.Key); }
            else if (pair.Value is ComboBox combo) SelectChoice(combo, savedFields.S(pair.Key));
            else if (pair.Value is CheckBox check) check.IsChecked = savedFields.B(pair.Key);
        }
        var editor = new Grid(); editor.RowDefinitions.Add(new()); editor.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var formScroll = Scroll(form); formScroll.Name = "BudgetEditorScroll"; editor.Children.Add(formScroll);
        var fixedFooter = new Border { Child = footer, Padding = new Thickness(0, 12, 0, 0), BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = Muted }; Grid.SetRow(fixedFooter, 1); editor.Children.Add(fixedFooter);
        Content = Page(editingRule.I("revision") == 0 ? "新建预算" : "编辑预算", "草稿按预算分别保存在本机，保存后应用", editor);
        building = false; VisibilityForEditor(); Changed(); ViewingChanged?.Invoke(selected, true);
    }
    private IEnumerable<(string id, string label)> Choices(string key)
    {
        yield return ("all", key == "models" ? "全部模型" : "全部任务");
        foreach (var node in state.O("choices").A(key))
        {
            string id = node is JsonObject row ? row.S("id") : node?.GetValue<string>() ?? "";
            string label = node is JsonObject item ? item.S("label", id) : id;
            if (id.Length > 0 && id != "all") yield return (id, label);
        }
    }
    private IEnumerable<(string id, string label)> QuotaChoices(int selectedWindow)
    {
        var available = BudgetStore.AvailableQuotaWindows(state.O("quota"));
        foreach (int window in available) yield return (window.ToString(CultureInfo.InvariantCulture), BudgetStore.QuotaWindowName(window));
        if (!available.Contains(selectedWindow)) yield return (selectedWindow.ToString(CultureInfo.InvariantCulture), BudgetStore.QuotaWindowName(selectedWindow) + (available.Length == 0 ? "（尚未取得窗口）" : "（当前账号未提供）"));
    }
    private void RefreshQuotaChoices()
    {
        if (fields.GetValueOrDefault("windowMinutes") is not ComboBox combo) return;
        int selectedWindow = int.TryParse(combo.SelectedValue as string, out int value) ? value : 10080;
        var options = QuotaChoices(selectedWindow).Select(x => new Choice(x.id, x.label)).ToArray();
        if (options.SequenceEqual(combo.Items.OfType<Choice>())) return;
        bool previous = building; building = true;
        try { combo.Items.Clear(); foreach (var option in options) combo.Items.Add(option); combo.SelectedValue = selectedWindow.ToString(CultureInfo.InvariantCulture); }
        finally { building = previous; }
    }
    private void RefreshQuotaStatus()
    {
        bool reading = state.O("updates").O("quota").B("busy");
        quotaStatus.Text = reading ? "正在读取官方额度，完成后更新可选窗口…" : BudgetStore.AvailableQuotaWindows(state.O("quota")).Length == 0 ? "尚未取得当前账号的窗口，刷新后可选择。草稿会保留。" : "可选窗口来自最近一次账号额度快照。";
        if (quotaRefresh != null) quotaRefresh.IsEnabled = !reading;
    }
    private void BeginRepair(JsonObject rule, JsonObject summary, bool pricesMissing)
    {
        BeginEditing(rule);
        if (fields.GetValueOrDefault("recalculateCurrent") is CheckBox recalculate) recalculate.IsChecked = true;
        if (pricesMissing)
        {
            foreach (string model in summary.A("issues").Rows().Where(x => x.S("type") == "price").Select(x => x.S("model")).Where(x => x.Length > 0).Distinct())
                if (!prices.Any(x => x["model"].Text.Trim() == model)) AddPrice(J.Obj(("model", model)));
            error.Text = "已列出缺价模型。请补齐需要的单价；保存后将按编辑中的规则重算本期，可能立即触发提醒。";
            if (AllowFocus && sections.TryGetValue("money", out var money)) money.Loaded += (_, _) => money.BringIntoView();
        }
        else
        {
            error.Text = "请选择当前账号提供的额度窗口；保存后立即应用于本期。";
            if (AllowFocus) fields["windowMinutes"].Focus();
        }
        Changed();
    }
    private void CopyDraftAsNew()
    {
        CaptureDraft(); var draft = Draft(); var rule = editingRule.Copy();
        rule["id"] = Guid.NewGuid().ToString(); rule["revision"] = 0; rule.Remove("source"); rule.Remove("createdAt"); rule.Remove("modifiedAt");
        draft["rule"] = rule; BeginEditing(rule, draft);
    }
    private void RefreshEditorChoices()
    {
        if (state.O("choices").Count == 0) return;
        bool previousBuilding = building; building = true;
        try
        {
            foreach (var pair in new[] { ("model", "models"), ("task", "tasks") })
            {
                if (!fields.TryGetValue(pair.Item1, out var control) || control is not ComboBox combo) continue;
                string selectedId = Get(pair.Item1);
                var options = Choices(pair.Item2).GroupBy(x => x.id).Select(x => x.First()).Select(x => new Choice(x.id, x.label)).ToList();
                if (!options.Any(x => x.Id == selectedId)) options.Add(new Choice(selectedId, selectedId + "（已保存）"));
                if (options.SequenceEqual(combo.Items.OfType<Choice>())) continue;
                combo.Items.Clear(); foreach (var option in options) combo.Items.Add(option); combo.SelectedValue = selectedId;
                combo.ToolTip = (combo.SelectedItem as Choice)?.Label;
            }
        }
        finally { building = previousBuilding; }
    }
    private void AddSection(Panel parent, string key, FrameworkElement element) { sections[key] = element; parent.Children.Add(element); }
    private static FrameworkElement Field(string label, Control control) => Field(label, control, out _);
    private static FrameworkElement Field(string label, Control control, out TextBlock title)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 14), HorizontalAlignment = HorizontalAlignment.Left };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(166) }); row.ColumnDefinitions.Add(new() { Width = new GridLength(360) });
        title = Text(label, 12, Muted); title.VerticalAlignment = VerticalAlignment.Center; title.Margin = new Thickness(0, 0, 10, 0); row.Children.Add(title);
        control.Width = 360; control.HorizontalAlignment = HorizontalAlignment.Left; Grid.SetColumn(control, 1); row.Children.Add(control); AutomationProperties.SetName(control, label); return row;
    }
    private TextBox Input(string key, string text, string hint = "")
    {
        var field = new TextBox { Text = text, MinHeight = 30, Padding = new Thickness(8, 5, 8, 5), MaxLength = key == "name" ? 100 : key == "thresholds" ? 256 : 512, ToolTip = hint, VerticalContentAlignment = VerticalAlignment.Center };
        fields[key] = field; field.TextChanged += (_, _) => Changed(); return field;
    }
    private Control ClockInput(int hour, int minute)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var hours = Combo("hour", Enumerable.Range(0, 24).Select(x => (x.ToString(CultureInfo.InvariantCulture), x.ToString("D2"))), hour.ToString(CultureInfo.InvariantCulture));
        var minutes = Combo("minute", Enumerable.Range(0, 60).Select(x => (x.ToString(CultureInfo.InvariantCulture), x.ToString("D2"))), minute.ToString(CultureInfo.InvariantCulture));
        hours.Width = minutes.Width = 92; AutomationProperties.SetName(hours, "重置小时"); AutomationProperties.SetName(minutes, "重置分钟");
        panel.Children.Add(hours); var colon = Text(":", 16); colon.Margin = new Thickness(10, 3, 10, 0); panel.Children.Add(colon); panel.Children.Add(minutes);
        return new UserControl { Content = panel };
    }
    private static readonly Lazy<(string id, string label)[]> TimeZones = new(() => TimeZoneInfo.GetSystemTimeZones()
        .Select(zone => (zone, id: zone.HasIanaId ? zone.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var converted) ? converted : ""))
        .Where(x => x.id.Length > 0).Select(x => (id: x.id, label: x.id + " · " + x.zone.DisplayName))
        .Append((id: "UTC", label: "UTC · 协调世界时")).DistinctBy(x => x.id).OrderBy(x => x.id, StringComparer.OrdinalIgnoreCase).ToArray());
    private ComboBox TimeZoneInput(string value)
    {
        var combo = Combo("timezone", TimeZones.Value, value); combo.IsEditable = true; combo.IsTextSearchEnabled = false; combo.StaysOpenOnEdit = true;
        combo.ToolTip = "搜索城市或时区，也可直接输入 IANA 名称，例如 Asia/Shanghai";
        bool filtering = false;
        combo.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) =>
        {
            if (building || filtering || !combo.IsKeyboardFocusWithin) return;
            if (combo.SelectedItem is Choice selectedZone && combo.Text == selectedZone.Label) return;
            string query = combo.Text; filtering = true;
            try
            {
                var options = TimeZones.Value.Where(x => x.id.Contains(query, StringComparison.OrdinalIgnoreCase) || x.label.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
                combo.Items.Clear(); foreach (var option in options) combo.Items.Add(new Choice(option.id, option.label));
                combo.Text = query; if (combo.IsLoaded) combo.IsDropDownOpen = options.Length > 0;
            }
            finally { filtering = false; }
            Changed();
        }));
        return combo;
    }
    private Control DateTimeInput(string key, string value)
    {
        var input = new BudgetDateTimeInput(value, key == "start" ? "开始" : "结束");
        input.ValueChanged += () =>
        {
            if (input.ValidationMessage.Length == 0 && error.Text.StartsWith(input.ErrorPrefix, StringComparison.Ordinal)) { error.Text = ""; status("日期已修正，请保存预算以应用。"); }
            Changed();
        };
        fields[key] = input; return input;
    }
    private sealed class BudgetDateTimeInput : UserControl
    {
        private static readonly Lazy<Style> CalendarTheme = new(() =>
        {
            var resources = (ResourceDictionary)XamlReader.Parse("""
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Style x:Key="CalendarNavigation" TargetType="Button">
    <Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/><Setter Property="Background" Value="Transparent"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/><Setter Property="MinHeight" Value="30"/><Setter Property="Padding" Value="5"/>
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button"><Border x:Name="Body" CornerRadius="5" Background="{TemplateBinding Background}" BorderBrush="Transparent" BorderThickness="1" Padding="{TemplateBinding Padding}"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" TextBlock.Foreground="{TemplateBinding Foreground}"/></Border><ControlTemplate.Triggers>
      <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Body" Property="Background" Value="{DynamicResource HoverBrush}"/></Trigger>
      <Trigger Property="IsPressed" Value="True"><Setter TargetName="Body" Property="Background" Value="{DynamicResource SelectionBrush}"/></Trigger>
      <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Body" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger>
      <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
    </ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style x:Key="CalendarDay" TargetType="CalendarDayButton">
    <Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/><Setter Property="Background" Value="Transparent"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/><Setter Property="MinHeight" Value="30"/><Setter Property="MinWidth" Value="32"/><Setter Property="FontSize" Value="13"/>
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="CalendarDayButton"><Grid Margin="1">
      <Border x:Name="Body" CornerRadius="5" Background="{TemplateBinding Background}" BorderBrush="Transparent" BorderThickness="1"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"><ContentPresenter.ContentTemplate><DataTemplate><TextBlock Text="{Binding}" Foreground="{Binding Foreground,RelativeSource={RelativeSource AncestorType=CalendarDayButton}}"/></DataTemplate></ContentPresenter.ContentTemplate></ContentPresenter></Border>
      <Border x:Name="Focus" Margin="2" CornerRadius="4" BorderBrush="{DynamicResource AccentBrush}" BorderThickness="2" Visibility="Collapsed" IsHitTestVisible="False"/>
      <Path x:Name="Blackout" Data="M 0,0 L 14,14" Width="14" Height="14" Stroke="{DynamicResource SecondaryBrush}" StrokeThickness="1.1" Visibility="Collapsed" IsHitTestVisible="False"/>
    </Grid><ControlTemplate.Triggers>
      <Trigger Property="IsInactive" Value="True"><Setter Property="Foreground" Value="{DynamicResource SecondaryBrush}"/></Trigger>
      <Trigger Property="IsToday" Value="True"><Setter TargetName="Body" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger>
      <MultiTrigger><MultiTrigger.Conditions><Condition Property="IsMouseOver" Value="True"/><Condition Property="IsSelected" Value="False"/></MultiTrigger.Conditions><Setter TargetName="Body" Property="Background" Value="{DynamicResource HoverBrush}"/></MultiTrigger>
      <Trigger Property="IsSelected" Value="True"><Setter TargetName="Body" Property="Background" Value="{DynamicResource AccentBrush}"/><Setter Property="Foreground" Value="{DynamicResource BackgroundBrush}"/></Trigger>
      <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Focus" Property="Visibility" Value="Visible"/></Trigger>
      <MultiTrigger><MultiTrigger.Conditions><Condition Property="IsKeyboardFocused" Value="True"/><Condition Property="IsSelected" Value="True"/></MultiTrigger.Conditions><Setter TargetName="Focus" Property="BorderBrush" Value="{DynamicResource BackgroundBrush}"/></MultiTrigger>
      <Trigger Property="IsBlackedOut" Value="True"><Setter TargetName="Blackout" Property="Visibility" Value="Visible"/><Setter Property="Foreground" Value="{DynamicResource SecondaryBrush}"/></Trigger>
      <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
    </ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style x:Key="CalendarRange" TargetType="CalendarButton">
    <Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/><Setter Property="Background" Value="Transparent"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/><Setter Property="MinHeight" Value="48"/><Setter Property="FontSize" Value="13"/>
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="CalendarButton"><Grid Margin="2"><Border x:Name="Body" CornerRadius="5" Background="{TemplateBinding Background}" BorderBrush="Transparent" BorderThickness="1"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"><ContentPresenter.ContentTemplate><DataTemplate><TextBlock Text="{Binding}" Foreground="{Binding Foreground,RelativeSource={RelativeSource AncestorType=CalendarButton}}"/></DataTemplate></ContentPresenter.ContentTemplate></ContentPresenter></Border><Border x:Name="Focus" Margin="2" CornerRadius="4" BorderBrush="{DynamicResource AccentBrush}" BorderThickness="2" Visibility="Collapsed" IsHitTestVisible="False"/></Grid><ControlTemplate.Triggers>
      <Trigger Property="IsInactive" Value="True"><Setter Property="Foreground" Value="{DynamicResource SecondaryBrush}"/></Trigger>
      <MultiTrigger><MultiTrigger.Conditions><Condition Property="IsMouseOver" Value="True"/><Condition Property="HasSelectedDays" Value="False"/></MultiTrigger.Conditions><Setter TargetName="Body" Property="Background" Value="{DynamicResource HoverBrush}"/></MultiTrigger>
      <Trigger Property="HasSelectedDays" Value="True"><Setter TargetName="Body" Property="Background" Value="{DynamicResource AccentBrush}"/><Setter Property="Foreground" Value="{DynamicResource BackgroundBrush}"/></Trigger>
      <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Focus" Property="Visibility" Value="Visible"/></Trigger>
      <MultiTrigger><MultiTrigger.Conditions><Condition Property="IsKeyboardFocused" Value="True"/><Condition Property="HasSelectedDays" Value="True"/></MultiTrigger.Conditions><Setter TargetName="Focus" Property="BorderBrush" Value="{DynamicResource BackgroundBrush}"/></MultiTrigger>
      <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
    </ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style x:Key="CalendarPanel" TargetType="CalendarItem">
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="CalendarItem">
      <ControlTemplate.Resources><DataTemplate x:Key="{x:Static CalendarItem.DayTitleTemplateResourceKey}"><TextBlock Text="{Binding}" Foreground="{DynamicResource SecondaryBrush}" FontSize="11" HorizontalAlignment="Center" VerticalAlignment="Center"/></DataTemplate></ControlTemplate.Resources>
      <Border x:Name="PART_Root" Width="282" CornerRadius="9" Background="{DynamicResource PopupBrush}" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" Padding="8">
        <Grid><Grid.RowDefinitions><RowDefinition Height="32"/><RowDefinition Height="Auto"/></Grid.RowDefinitions><Grid.ColumnDefinitions><ColumnDefinition Width="32"/><ColumnDefinition Width="*"/><ColumnDefinition Width="32"/></Grid.ColumnDefinitions>
          <Button x:Name="PART_PreviousButton" Style="{StaticResource CalendarNavigation}" AutomationProperties.Name="上一个月或年份"><Path Width="5" Height="9" Data="M 4,0 L 0,4 L 4,8" Stroke="{DynamicResource ForegroundBrush}" StrokeThickness="1.4"/></Button>
          <Button x:Name="PART_HeaderButton" Grid.Column="1" Style="{StaticResource CalendarNavigation}" FontWeight="SemiBold"/>
          <Button x:Name="PART_NextButton" Grid.Column="2" Style="{StaticResource CalendarNavigation}" AutomationProperties.Name="下一个月或年份"><Path Width="5" Height="9" Data="M 0,0 L 4,4 L 0,8" Stroke="{DynamicResource ForegroundBrush}" StrokeThickness="1.4"/></Button>
          <Grid x:Name="PART_MonthView" Grid.Row="1" Grid.ColumnSpan="3" Margin="0,6,0,0"><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition/><ColumnDefinition/><ColumnDefinition/><ColumnDefinition/><ColumnDefinition/><ColumnDefinition/></Grid.ColumnDefinitions><Grid.RowDefinitions><RowDefinition Height="24"/><RowDefinition Height="32"/><RowDefinition Height="32"/><RowDefinition Height="32"/><RowDefinition Height="32"/><RowDefinition Height="32"/><RowDefinition Height="32"/></Grid.RowDefinitions></Grid>
          <Grid x:Name="PART_YearView" Grid.Row="1" Grid.ColumnSpan="3" Margin="0,6,0,0" Height="216" Visibility="Collapsed"><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition/><ColumnDefinition/><ColumnDefinition/></Grid.ColumnDefinitions><Grid.RowDefinitions><RowDefinition/><RowDefinition/><RowDefinition/></Grid.RowDefinitions></Grid>
          <Border x:Name="PART_DisabledVisual" Grid.RowSpan="2" Grid.ColumnSpan="3" Visibility="Collapsed" IsHitTestVisible="False"/>
        </Grid>
      </Border><ControlTemplate.Triggers>
        <DataTrigger Binding="{Binding DisplayMode,RelativeSource={RelativeSource AncestorType=Calendar}}" Value="Year"><Setter TargetName="PART_MonthView" Property="Visibility" Value="Collapsed"/><Setter TargetName="PART_YearView" Property="Visibility" Value="Visible"/></DataTrigger>
        <DataTrigger Binding="{Binding DisplayMode,RelativeSource={RelativeSource AncestorType=Calendar}}" Value="Decade"><Setter TargetName="PART_MonthView" Property="Visibility" Value="Collapsed"/><Setter TargetName="PART_YearView" Property="Visibility" Value="Visible"/></DataTrigger>
      </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style x:Key="BudgetCalendar" TargetType="Calendar">
    <Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/><Setter Property="Background" Value="{DynamicResource PopupBrush}"/><Setter Property="BorderBrush" Value="{DynamicResource BorderBrush}"/><Setter Property="CalendarItemStyle" Value="{StaticResource CalendarPanel}"/><Setter Property="CalendarDayButtonStyle" Value="{StaticResource CalendarDay}"/><Setter Property="CalendarButtonStyle" Value="{StaticResource CalendarRange}"/>
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Calendar"><StackPanel x:Name="PART_Root"><CalendarItem x:Name="PART_CalendarItem" Style="{TemplateBinding CalendarItemStyle}"/></StackPanel></ControlTemplate></Setter.Value></Setter>
  </Style>
</ResourceDictionary>
""");
            return (Style)resources["BudgetCalendar"];
        });
        private readonly DatePicker date = new() { SelectedDateFormat = DatePickerFormat.Short, MinWidth = 156, MinHeight = 30 };
        private readonly ComboBox hour = new() { MinHeight = 30 }, minute = new() { MinHeight = 30 };
        private readonly TextBlock validation = Text("", 11, Danger);
        private readonly string label;
        private string? invalidValue;
        private int seconds;
        private bool applyingValue, rejectingReset;
        private TextBox DateText => (TextBox)date.Template.FindName("PART_TextBox", date);
        public string ErrorPrefix => label + "日期";
        public string ValidationMessage => validation.Text;
        public event Action? ValueChanged;
        public BudgetDateTimeInput(string value, string label)
        {
            this.label = label;
            date.Language = XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.Name.Length > 0 ? CultureInfo.CurrentCulture.Name : "en-US");
            date.CalendarStyle = CalendarTheme.Value;
            date.SetResourceReference(ForegroundProperty, "ForegroundBrush"); date.SetResourceReference(BackgroundProperty, "PanelBrush"); date.SetResourceReference(BorderBrushProperty, "BorderBrush");
            date.Template = (ControlTemplate)XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="DatePicker">
  <Grid x:Name="PART_Root">
    <Border x:Name="DateChrome" CornerRadius="7" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1">
      <DockPanel>
        <Button x:Name="PART_Button" DockPanel.Dock="Right" Width="28" MinHeight="26" Margin="2" Padding="5" Focusable="False">
          <Path Width="14" Height="14" Stretch="Uniform" Data="M 2,3 L 12,3 Q 13,3 13,4 L 13,12 Q 13,13 12,13 L 2,13 Q 1,13 1,12 L 1,4 Q 1,3 2,3 M 1,6 L 13,6 M 4,1 L 4,4 M 10,1 L 10,4" Stroke="{DynamicResource SecondaryBrush}" StrokeThickness="1.3" StrokeStartLineCap="Round" StrokeEndLineCap="Round"/>
        </Button>
        <DatePickerTextBox x:Name="PART_TextBox" MinHeight="28" Padding="7,4" BorderThickness="0" Background="Transparent" Foreground="{TemplateBinding Foreground}" VerticalContentAlignment="Center">
          <DatePickerTextBox.Template><ControlTemplate TargetType="DatePickerTextBox"><Border Background="Transparent" Padding="{TemplateBinding Padding}"><ScrollViewer x:Name="PART_ContentHost" VerticalAlignment="Center" HorizontalScrollBarVisibility="Hidden" VerticalScrollBarVisibility="Hidden"/></Border></ControlTemplate></DatePickerTextBox.Template>
        </DatePickerTextBox>
      </DockPanel>
    </Border>
    <Popup x:Name="PART_Popup" Placement="Bottom" PlacementTarget="{Binding RelativeSource={RelativeSource TemplatedParent}}" StaysOpen="False" AllowsTransparency="True" Focusable="False" VerticalOffset="4"/>
  </Grid>
  <ControlTemplate.Triggers>
    <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="DateChrome" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger>
    <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.42"/></Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>
""");
            var row = new Grid(); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = new GridLength(82) }); row.ColumnDefinitions.Add(new() { Width = new GridLength(82) });
            date.Margin = new Thickness(0, 0, 8, 0); hour.Margin = new Thickness(0, 0, 8, 0); row.Children.Add(date); Grid.SetColumn(hour, 1); row.Children.Add(hour); Grid.SetColumn(minute, 2); row.Children.Add(minute);
            hour.ItemsSource = Enumerable.Range(0, 24).Select(x => x.ToString("D2")).ToArray(); minute.ItemsSource = Enumerable.Range(0, 60).Select(x => x.ToString("D2")).ToArray();
            AutomationProperties.SetName(date, label + "日期"); AutomationProperties.SetName(hour, label + "小时"); AutomationProperties.SetName(minute, label + "分钟");
            date.ToolTip = "选择日期，按表单所选时区解释"; hour.ToolTip = "小时"; minute.ToolTip = "分钟";
            validation.Name = "BudgetDateError"; validation.Margin = new Thickness(0, 5, 0, 0);
            var content = Stack(); content.Children.Add(row); content.Children.Add(validation); Content = content;
            date.ApplyTemplate();
            if (date.Template?.FindName("PART_Button", date) is Button calendar)
            { calendar.ToolTip = "选择日期"; AutomationProperties.SetName(calendar, label + "日历"); }
            if (date.Template?.FindName("PART_Popup", date) is Popup { Child: System.Windows.Controls.Calendar picker })
            {
                // Selecting the already-selected day does not raise SelectedDateChanged.
                // An explicit day acceptance must still replace previously rejected text.
                picker.AddHandler(UIElement.PreviewMouseUpEvent, new MouseButtonEventHandler((_, args) => { if (args.ChangedButton == MouseButton.Left) AcceptCalendarSource(args.OriginalSource as DependencyObject); }), true);
                picker.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler((_, args) => { if (args.Key is Key.Enter or Key.Space) AcceptCalendarSource(args.OriginalSource as DependencyObject); }), true);
            }
            AutomationProperties.SetName(DateText, label + "日期输入");
            date.DateValidationError += (_, args) =>
            {
                args.ThrowException = false; invalidValue = args.Text; rejectingReset = true; ShowValidation(args.Text); ValueChanged?.Invoke();
                // DatePicker otherwise replaces invalid text with SelectedDate during LostFocus.
                // Keep the raw value authoritative while it completes, then restore it before render.
                string rejected = args.Text;
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                {
                    try { if (invalidValue == rejected) { applyingValue = true; DateText.Text = rejected; } }
                    finally { applyingValue = false; rejectingReset = false; }
                }));
            };
            date.SelectedDateChanged += (_, _) =>
            {
                if (applyingValue) return;
                rejectingReset = false; invalidValue = null; seconds = 0; ShowValidation(date.SelectedDate.HasValue ? DateText.Text : ""); ValueChanged?.Invoke();
            };
            void ClockChanged() { if (applyingValue) return; seconds = 0; ValueChanged?.Invoke(); }
            hour.SelectionChanged += (_, _) => ClockChanged(); minute.SelectionChanged += (_, _) => ClockChanged();
            DateText.TextChanged += (_, _) =>
            {
                if (applyingValue || rejectingReset) return;
                string raw = DateText.Text; invalidValue = DateTime.TryParse(raw, date.Language.GetSpecificCulture(), DateTimeStyles.None, out _) ? null : raw;
                ShowValidation(raw); ValueChanged?.Invoke();
            };
            Value = value;
        }
        private void AcceptCalendarSource(DependencyObject? source)
        {
            while (source != null && source is not CalendarDayButton) source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
            if (source is not CalendarDayButton { IsEnabled: true, IsBlackedOut: false, DataContext: DateTime accepted }) return;
            applyingValue = true;
            try { rejectingReset = false; invalidValue = null; date.SelectedDate = accepted.Date; DateText.Text = accepted.ToString(date.Language.GetSpecificCulture().DateTimeFormat.ShortDatePattern, date.Language.GetSpecificCulture()); seconds = 0; ShowValidation(DateText.Text); }
            finally { applyingValue = false; }
            ValueChanged?.Invoke();
        }
        private void ShowValidation(string raw)
        {
            validation.Text = raw.Trim().Length == 0 ? ErrorPrefix + "不能为空。" : DateTime.TryParse(raw, date.Language.GetSpecificCulture(), DateTimeStyles.None, out _) ? "" : ErrorPrefix + "无效，请输入有效日期或使用日历选择。";
            validation.Visibility = validation.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        public void SaveClock(JsonObject values, string key) { values[key + "Hour"] = hour.SelectedIndex; values[key + "Minute"] = minute.SelectedIndex; }
        public void RestoreClock(JsonObject values, string key)
        {
            applyingValue = true;
            try { hour.SelectedIndex = Math.Clamp(values.I(key + "Hour", hour.SelectedIndex), 0, 23); minute.SelectedIndex = Math.Clamp(values.I(key + "Minute", minute.SelectedIndex), 0, 59); }
            finally { applyingValue = false; }
        }
        public string Value
        {
            get
            {
                if (invalidValue != null) return invalidValue;
                string raw = DateText.Text;
                if (!DateTime.TryParse(raw, date.Language.GetSpecificCulture(), DateTimeStyles.None, out var day)) return raw;
                return day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " " + (hour.SelectedItem as string ?? "00") + ":" + (minute.SelectedItem as string ?? "00") + (seconds > 0 ? ":" + seconds.ToString("D2") : "");
            }
            set
            {
                applyingValue = true;
                try
                {
                    if (DateTime.TryParseExact(value.Trim(), new[] { "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                    { invalidValue = null; date.SelectedDate = parsed.Date; hour.SelectedIndex = parsed.Hour; minute.SelectedIndex = parsed.Minute; seconds = parsed.Second; }
                    else { invalidValue = value; date.SelectedDate = null; DateText.Text = value; hour.SelectedIndex = minute.SelectedIndex = 0; }
                    ShowValidation(DateText.Text);
                }
                finally { applyingValue = false; }
            }
        }
    }
    private sealed record Choice(string Id, string Label) { public override string ToString() => Label; }
    private ComboBox Combo(string key, IEnumerable<(string id, string label)> options, string value)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding("Label")); text.SetBinding(ToolTipProperty, new Binding("Label"));
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap); text.SetValue(MaxWidthProperty, 324d);
        var combo = new ComboBox { MinHeight = 30, MaxDropDownHeight = 320, IsTextSearchEnabled = true, SelectedValuePath = "Id", ItemTemplate = new DataTemplate { VisualTree = text } };
        TextSearch.SetTextPath(combo, "Label");
        foreach (var option in options.GroupBy(x => x.id).Select(x => x.First())) combo.Items.Add(new Choice(option.id, option.label));
        SelectChoice(combo, value); fields[key] = combo;
        combo.SelectionChanged += (_, _) => { combo.ToolTip = (combo.SelectedItem as Choice)?.Label; if (!building) VisibilityForEditor(); Changed(); };
        combo.ToolTip = (combo.SelectedItem as Choice)?.Label; return combo;
    }
    private static void SelectChoice(ComboBox combo, string id)
    {
        if (!combo.Items.OfType<Choice>().Any(x => x.Id == id)) combo.Items.Add(new Choice(id, id + "（已保存）"));
        combo.SelectedValue = id;
    }
    private CheckBox Check(string key, string title, bool value)
    {
        var field = new CheckBox { Content = title, IsChecked = value, Margin = new Thickness(0, 3, 0, 12) }; fields[key] = field;
        field.Checked += (_, _) => Changed(); field.Unchecked += (_, _) => Changed(); return field;
    }
    private string Get(string key) => fields.TryGetValue(key, out var field) ? field is TextBox text ? text.Text : field is BudgetDateTimeInput date ? date.Value : field is ComboBox combo ? combo.SelectedValue as string ?? (combo.IsEditable ? combo.Text : "") : "" : "";
    private bool Checked(string key) => fields.TryGetValue(key, out var field) && field is CheckBox check && check.IsChecked == true;
    private void VisibilityForEditor()
    {
        if (!editing || fields.Count == 0) return; string kind = Get("kind"), period = Get("period");
        void Show(string key, bool visible) { if (sections.TryGetValue(key, out var section)) section.Visibility = visible ? Visibility.Visible : Visibility.Collapsed; }
        Show("tokenUnit", kind == "token"); Show("tokenMetric", kind == "token"); Show("money", kind == "money"); Show("currency", kind == "money"); Show("quota", kind == "quota"); Show("window", kind == "quota");
        Show("scope", kind != "quota"); Show("thresholds", kind != "quota"); Show("reset", period is "day" or "week" or "month");
        Show("week", period == "week"); Show("month", period == "month"); Show("start", period is "once" or "interval"); Show("end", period == "once"); Show("interval", period == "interval");
        amountLabel.Text = kind == "quota" ? "官方余量下限 · %" : "每期额度";
    }
    private void AddPrice(JsonObject price, bool draft = false)
    {
        if (prices.Count >= 100) { status("单条预算最多设置 100 个模型价格。"); return; }
        var map = new Dictionary<string, TextBox>(); var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        foreach (double width in new[] { 220d, 96, 96, 96, 72 }) grid.ColumnDefinitions.Add(new() { Width = new GridLength(width) });
        string[] keys = { "model", "input", "cachedInput", "output" }; string[] labels = { "模型", "输入", "缓存输入", "输出" };
        for (int i = 0; i < keys.Length; i++)
        {
            string key = keys[i]; var stack = Stack(); stack.Children.Add(Text(labels[i], 11, Muted));
            var field = new TextBox { Text = key == "model" || draft ? price.S(key) : price.N(key)?.ToString("G", CultureInfo.InvariantCulture) ?? "", MinHeight = 30, Padding = new Thickness(5), MaxLength = key == "model" ? 512 : 32, Margin = new Thickness(0, 0, 8, 0), ToolTip = key == "model" ? "填写精确模型 ID；从缺价提示进入时会自动填好" : "每百万 Token 的 USD 单价；空白表示未知" };
            AutomationProperties.SetName(field, labels[i]); field.TextChanged += (_, _) => Changed(); map[key] = field; stack.Children.Add(field); Grid.SetColumn(stack, i); grid.Children.Add(stack);
        }
        var remove = Button("移除", () => { prices.Remove(map); pricePanel.Children.Remove(grid); Changed(); }); remove.VerticalAlignment = VerticalAlignment.Bottom; remove.Margin = new Thickness(0); Grid.SetColumn(remove, 4); grid.Children.Add(remove);
        prices.Add(map); pricePanel.Children.Add(grid);
    }
    private void Changed()
    {
        if (building || !editing) return;
        if (editingRule.I("revision") > 0)
            changeTiming.Text = Checked("recalculateCurrent")
                ? "本次修改立即应用，并按新设置重算本期；可能触发提醒。"
                : "立即生效：名称、启停状态和提醒节点。下期生效：范围、周期、统计口径、官方窗口、币种、汇率和单价。\n额度通常立即生效；改变预算口径或币种时，额度也于下期生效。当前监测周期已结束时，所有修改直接应用。";
        draftTimer.Stop(); draftTimer.Start();
    }
    private JsonObject Draft()
    {
        var values = new JsonObject(); foreach (var pair in fields) values[pair.Key] = pair.Value is CheckBox ? JsonValue.Create(Checked(pair.Key)) : JsonValue.Create(Get(pair.Key));
        foreach (var pair in fields) if (pair.Value is BudgetDateTimeInput date) date.SaveClock(values, pair.Key);
        var rows = new JsonArray(); foreach (var row in prices) { var value = new JsonObject(); foreach (var pair in row) value[pair.Key] = pair.Value.Text; rows.Add(value); }
        return J.Obj(("rule", editingRule), ("fields", values), ("prices", rows), ("returnSelection", editorReturnSelection));
    }
    private async Task PersistDraft()
    {
        CaptureDraft(); await PersistDraftBook();
    }
    private void CaptureDraft() { if (editing) drafts.Capture(Draft()); }
    private async Task PersistDraftBook(bool propagateFailure = false)
    {
        await draftGate.WaitAsync();
        try
        {
            if (persistedDraftRevision == drafts.Revision) return;
            long revision = drafts.Revision;
            await send(J.Obj(("action", "settings"), ("patch", J.Obj(("budgetDraft", drafts.Snapshot())))));
            persistedDraftRevision = revision;
        }
        catch (Exception e) { status("预算草稿未能保存：" + e.Message); if (propagateFailure) throw; }
        finally { draftGate.Release(); }
    }
    private async Task ClearDraft()
    {
        draftTimer.Stop(); await draftGate.WaitAsync();
        try { drafts.Remove(editingRule.S("id")); long revision = drafts.Revision; state = CopyWithChoices(await send(J.Obj(("action", "settings"), ("patch", J.Obj(("budgetDraft", drafts.Snapshot())))))); persistedDraftRevision = revision; }
        catch { CaptureDraft(); Changed(); throw; }
        finally { draftGate.Release(); }
    }
    private double Number(string key, double minimum = 0, double maximum = 1e18, bool integer = false)
    {
        if (!double.TryParse(Get(key).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value) || value < minimum || value > maximum || integer && value != Math.Truncate(value))
            throw new InvalidOperationException("请检查“" + (fields.TryGetValue(key, out var control) ? AutomationProperties.GetName(control) : key) + "”：需要 " + minimum.ToString("G", CultureInfo.InvariantCulture) + "–" + maximum.ToString("G", CultureInfo.InvariantCulture) + (integer ? " 的整数。" : " 的数字。"));
        return value;
    }
    private JsonObject BuildRule()
    {
        var rule = editingRule.Copy(); string kind = Get("kind"); rule["name"] = Get("name").Trim(); rule["kind"] = kind;
        double multiplier = kind == "token" ? Get("tokenUnit") == "M" ? 1000000 : Get("tokenUnit") == "K" ? 1000 : 1 : 1;
        rule["amount"] = Number("amount", kind == "quota" ? 0 : double.Epsilon, kind == "quota" ? 100 : 1e18 / multiplier) * multiplier;
        rule["enabled"] = Checked("enabled"); rule["model"] = kind == "quota" ? "all" : Get("model"); rule["task"] = kind == "quota" ? "all" : Get("task");
        rule["tokenMetric"] = Get("tokenMetric");
        if (kind == "money")
        {
            rule["currency"] = Get("currency"); rule["fx"] = Number("fx", double.Epsilon, 1e6); var priceRows = new JsonArray();
            foreach (var map in prices)
            {
                var price = J.Obj(("model", map["model"].Text.Trim()));
                foreach (string key in new[] { "input", "cachedInput", "output" })
                {
                    string text = map[key].Text.Trim(); if (text.Length == 0) continue;
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value) || value < 0 || value > 1e9) throw new InvalidOperationException("模型单价需为非负的每百万 Token 美元价格；留空表示未知。");
                    price[key] = value;
                }
                priceRows.Add(price);
            }
            rule["prices"] = priceRows;
        }
        if (kind == "quota")
        {
            int window = (int)Number("windowMinutes", 1, 525600, true);
            var available = BudgetStore.AvailableQuotaWindows(state.O("quota"));
            if (!available.Contains(window)) throw new InvalidOperationException(available.Length == 0 ? "尚未取得当前账号的额度窗口，请先刷新官方额度后选择。草稿已保留。" : "当前账号未提供所选额度窗口，请从列表选择可用窗口。");
            rule["quotaCondition"] = "floor"; rule["windowMinutes"] = window;
        }
        else
        {
            var levels = new JsonArray(); foreach (string value in Get("thresholds").Replace('，', ',').Split(',', StringSplitOptions.TrimEntries))
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || !double.IsFinite(n)) throw new InvalidOperationException("提醒节点需为逗号分隔的 0–100% 数字。"); levels.Add(n);
            }
            rule["thresholds"] = levels;
        }
        string type = Get("period"), zone = Get("timezone").Trim(); var tz = BudgetStore.Zone(zone);
        var period = J.Obj(("type", type), ("timezone", zone));
        if (type is "day" or "week" or "month") { period["hour"] = Number("hour", 0, 23, true); period["minute"] = Number("minute", 0, 59, true); }
        if (type == "week") period["weekday"] = Number("weekday", 1, 7, true);
        if (type == "month") period["day"] = Number("day", 1, 31, true);
        double ParseDate(string key)
        {
            if (fields.GetValueOrDefault(key) is BudgetDateTimeInput input && input.ValidationMessage.Length > 0) throw new InvalidOperationException(input.ValidationMessage + "草稿已保留。");
            if (!DateTime.TryParseExact(Get(key).Trim(), new[] { "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
                throw new InvalidOperationException("开始与结束时间请使用 yyyy-MM-dd HH:mm 格式，按所选 IANA 时区解释。");
            return BudgetStore.WallTime(local, tz);
        }
        if (type is "once" or "interval") period["start"] = ParseDate("start");
        if (type == "once") period["end"] = ParseDate("end");
        if (type == "interval") period["seconds"] = Number("hours", 1d / 60, 87660) * 3600;
        rule["period"] = period; return rule;
    }
    private async Task Save()
    {
        error.Text = ""; var rule = BuildRule(); bool pin = Checked("pin"); draftTimer.Stop(); CaptureDraft();
        await draftGate.WaitAsync();
        try
        {
            var next = await send(J.Obj(("action", "budget"), ("operation", "save"), ("payload", J.Obj(("rule", rule), ("expectedRevision", editingRule.I("revision")), ("recalculateCurrent", Checked("recalculateCurrent"))))));
            state = CopyWithChoices(next); drafts.Remove(rule.S("id")); var patch = J.Obj(("budgetDraft", drafts.Snapshot()));
            // Rule persistence succeeded. A later preference failure must not pretend it did not save.
            editing = false; selected = rule.S("id");
            long draftRevision = drafts.Revision;
            var warnings = new List<string>();
            try { state = CopyWithChoices(await send(J.Obj(("action", "settings"), ("patch", patch)))); persistedDraftRevision = draftRevision; }
            catch (Exception e) { warnings.Add("草稿偏好未能更新：" + e.Message); }
            if (pin)
            {
                try { state = CopyWithChoices(await send(J.Obj(("action", "view-budget"), ("budgetID", rule.S("id"))))); }
                catch (Exception e) { warnings.Add("浮窗未能打开：" + e.Message); }
            }
            status("预算已保存" + (warnings.Count > 0 ? "，但" + string.Join("；", warnings) : pin ? "，已在浮窗显示。" : "，将按所选设置监测。"));
            RenderList(); ViewingChanged?.Invoke(selected, false);
        }
        finally { draftGate.Release(); if (editing) Changed(); }
    }
}
