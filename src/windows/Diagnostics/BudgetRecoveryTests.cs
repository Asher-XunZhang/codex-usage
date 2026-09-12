using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexUsage;

/// <summary>Dispatcher-only controls with a synthetic host; no HWNDs, accounts or user files.</summary>
internal static class BudgetRecoveryTests
{
    private static IEnumerable<T> Controls<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var item in Controls<T>(child)) yield return item;
    }
    private static IEnumerable<T> VisualControls<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in VisualControls<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static TextBox Name(BudgetView view) => Controls<TextBox>(view).Single(x => AutomationProperties.GetName(x) == "预算名称");
    private static void Click(BudgetView view, string text) => Controls<Button>(view).Single(x => x.Content as string == text || AutomationProperties.GetName(x) == text).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static JsonObject Rule(string id, string kind = "token") => J.Obj(("id", id), ("revision", 1), ("name", id), ("kind", kind), ("amount", kind == "token" ? 20_000_000 : 20),
        ("tokenMetric", "total"), ("model", "all"), ("task", "all"), ("currency", "USD"), ("fx", 1), ("prices", new JsonArray()),
        ("period", J.Obj(("type", "day"), ("timezone", "UTC"))), ("thresholds", new[] { 20, 10, 0 }), ("enabled", true), ("windowMinutes", 10080));
    private static JsonObject PendingFixture(JsonObject next)
    {
        string model = "model-" + string.Concat(Enumerable.Repeat("long-identifier-", 12));
        var current = next.Copy(); current["model"] = "all"; current["fx"] = 1;
        current["prices"] = new JsonArray(J.Obj(("model", model), ("input", 2), ("cachedInput", .5), ("output", 10)));
        next["model"] = "next-period-model"; next["fx"] = 7;
        next["prices"] = new JsonArray(J.Obj(("model", model), ("input", 3), ("cachedInput", .5), ("output", 10)));
        var summary = current.Copy(); summary["scheduledChange"] = true; summary["periodEnd"] = 1790967600;
        summary["pendingChange"] = J.Obj(("effectiveAt", 1790967600), ("current", current), ("next", next)); return summary;
    }
    private sealed class Host
    {
        public JsonObject State = J.Obj(("settings", new JsonObject()), ("choices", J.Obj(("models", new[] { "new-model" }), ("tasks", new JsonArray()))),
            ("quota", J.Obj(("windows", new JsonArray(J.Obj(("duration_minutes", 300)), J.Obj(("duration_minutes", 10080)))))),
            ("budgets", J.Obj(("rules", new JsonArray(Rule("a"), Rule("b"))), ("summaries", new JsonArray()), ("events", new JsonArray()))));
        public BudgetView? View;
        public TaskCompletionSource? HoldNextDraft, HoldNextSave;
        public JsonObject? LastSave;
        public readonly List<JsonObject> Requests = new();
        public readonly List<string> Statuses = new();
        public bool FailDraft;
        public async Task<JsonObject> Send(JsonObject request)
        {
            var captured = request.Copy();
            Requests.Add(captured.Copy());
            if (captured.S("action") == "settings")
            {
                if (HoldNextDraft is { } hold) { HoldNextDraft = null; await hold.Task; }
                if (FailDraft) throw new InvalidOperationException("fixture disk unavailable");
                foreach (var item in captured.O("patch")) State.O("settings")[item.Key] = item.Value?.DeepClone();
            }
            else if (captured.S("action") == "budget" && captured.S("operation") == "save")
            {
                if (HoldNextSave is { } hold) { HoldNextSave = null; await hold.Task; }
                LastSave = captured.O("payload").Copy(); var rule = LastSave.O("rule").Copy(); rule["revision"] = rule.I("revision") + 1;
                var rules = State.O("budgets").A("rules"); var existing = rules.Rows().FirstOrDefault(x => x.S("id") == rule.S("id"));
                if (existing != null) rules[rules.IndexOf(existing)] = rule; else rules.Add(rule);
            }
            var result = State.Copy(); View?.Update(result); return result;
        }
        public BudgetView Create() { View = new BudgetView(Send, Statuses.Add); View.Update(State.Copy()); return View; }
        public BudgetDrafts Book() { var book = new BudgetDrafts(); book.Load(State.O("settings")["budgetDraft"] as JsonObject); return book; }
    }
    public static async Task<JsonObject> RunAsync()
    {
        int checks = 0; void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Budget recovery: " + message); checks++; }
        var legacy = J.Obj(("rule", Rule("a")), ("fields", J.Obj(("name", "旧格式草稿"))), ("prices", new JsonArray()));
        var book = new BudgetDrafts(); book.Load(legacy); Check(book.Find("a")?.O("fields").S("name") == "旧格式草稿", "legacy draft migration");
        var second = legacy.Copy(); second.O("rule")["id"] = "b"; second.O("fields")["name"] = "第二份";
        book.Capture(second, 2); var snapshot = book.Snapshot(); book.Remove("b");
        Check(book.Find("a") != null && snapshot!.A("drafts").Count == 2, "draft removal and snapshots are independent");

        var host = new Host(); var view = host.Create(); view.Select("a", true); Name(view).Text = "A 的未保存编辑";
        await view.FlushDraft(); view.Select("b"); view.Select("a", true);
        Check(Name(view).Text == "A 的未保存编辑", "notification navigation restores matching draft");
        view.Select("b"); Click(view, "＋ 新建预算"); string firstId = view.SelectedId; Name(view).Text = "新草稿一";
        view.Select("b"); Click(view, "＋ 新建预算"); string secondId = view.SelectedId; Name(view).Text = "新草稿二";
        await view.FlushDraft();
        Check(firstId != secondId && host.Book().Find(firstId)?.O("fields").S("name") == "新草稿一" && host.Book().Find(secondId)?.O("fields").S("name") == "新草稿二", "new drafts coexist without replacing previous work");
        view.Select("b"); Click(view, "继续草稿 · 新草稿一");
        Check(view.SelectedId == firstId && Name(view).Text == "新草稿一", "draft has a visible resume entry");
        Click(view, "取消"); await view.FlushDraft();
        Check(host.Book().Find(firstId) == null && host.Book().Find(secondId) != null && host.Book().Find("a") != null, "explicit cancel removes only the current draft");

        view.Select("a", true); Name(view).Text = "请求发出时的 A";
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); host.HoldNextDraft = blocked;
        Task firstWrite = view.FlushDraft(); view.Select("b", true); Name(view).Text = "请求等待时的 B"; Task lastWrite = view.FlushDraft(); blocked.SetResult();
        await firstWrite; await lastWrite;
        Check(host.Book().Find("a")?.O("fields").S("name") == "请求发出时的 A" && host.Book().Find("b")?.O("fields").S("name") == "请求等待时的 B", "queued persistence captures the correct editor before navigation");
        host.FailDraft = true; Name(view).Text = "磁盘失败时仍保留";
        bool flushFailed = false; try { await view.FlushDraft(); } catch (InvalidOperationException) { flushFailed = true; }
        Check(flushFailed, "close-time flush reports persistence failure instead of claiming success"); view.Select("a"); view.Select("b", true);
        Check(Name(view).Text == "磁盘失败时仍保留", "failed draft write retains in-memory work"); host.FailDraft = false; await view.FlushDraft();
        var heldSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); host.HoldNextSave = heldSave;
        Click(view, "保存预算"); view.Select("a", true); heldSave.SetResult();
        for (int attempt = 0; attempt < 100 && (!view.IsEnabled || view.SelectedId != "a"); attempt++) await Task.Delay(10);
        Check(view.IsEnabled && view.SelectedId == "a" && view.IsEditing && Name(view).Text == "请求发出时的 A", "navigation during save is replayed after the save finishes");
        Check(host.Book().Find("b") == null && host.Book().Find(secondId) != null, "save clears only the successfully saved draft");
        view.Select("a"); await view.FlushDraft();
        host.State.O("budgets").A("rules").Rows().First(x => x.S("id") == "a")["revision"] = 2;
        view.Update(host.State.Copy()); view.Select("a", true);
        Check(Controls<Button>(view).Any(x => x.Content as string == "将草稿另存为新预算…") && Name(view).Text == "请求发出时的 A", "restored fields keep their original revision instead of silently overwriting newer rules");
        view.Select("a"); await view.FlushDraft();

        var priceHost = new Host(); priceHost.State.O("budgets")["rules"] = new JsonArray(Rule("money", "money"));
        priceHost.State.O("budgets")["summaries"] = new JsonArray(J.Obj(("id", "money"), ("kind", "money"), ("status", "partial"), ("issues", new JsonArray(J.Obj(("type", "price"), ("model", "new-model"), ("category", "model"), ("message", "尚未设置模型单价"))))));
        var pricesView = priceHost.Create(); pricesView.Select("money"); Click(pricesView, "补齐单价并重算本期…");
        Check(Controls<TextBox>(pricesView).Any(x => x.Text == "new-model") && Controls<CheckBox>(pricesView).Single(x => x.Content as string == "立即应用并重算本期").IsChecked == true, "price repair prefills model and explicitly recalculates current period");
        pricesView.Select("money"); await pricesView.FlushDraft();

        var quotaHost = new Host(); var quotaRule = Rule("quota", "quota"); quotaRule["windowMinutes"] = 60;
        quotaHost.State.O("budgets")["rules"] = new JsonArray(quotaRule);
        quotaHost.State.O("budgets")["summaries"] = new JsonArray(J.Obj(("id", "quota"), ("kind", "quota"), ("status", "unknown"), ("issueCode", "window_invalid")));
        var quotaView = quotaHost.Create(); quotaView.Select("quota"); Click(quotaView, "修正官方额度窗口…");
        var picker = Controls<ComboBox>(quotaView).Single(x => AutomationProperties.GetName(x) == "官方额度窗口");
        Check(picker.Items.Count == 3 && picker.SelectedValue as string == "60", "invalid stored quota window stays explicit beside actual choices");
        Click(quotaView, "保存预算");
        Check(quotaHost.LastSave == null && Controls<TextBlock>(quotaView).Any(x => x.Text.Contains("当前账号未提供所选")), "unsupported window cannot silently save");
        picker.SelectedValue = "300"; Click(quotaView, "保存预算");
        Check(quotaHost.LastSave?.O("rule").I("windowMinutes") == 300 && quotaHost.LastSave.B("recalculateCurrent"), "actual account window correction applies to the current period");
        await quotaView.FlushDraft();

        var corruptHost = new Host(); corruptHost.State.O("budgets")["recovery"] = J.Obj(("required", true), ("path", "fixture/budgets.json"));
        corruptHost.State.O("budgets")["error"] = "fixture corrupt"; corruptHost.State.O("settings")["budgetDraft"] = legacy;
        var corruptView = corruptHost.Create();
        Check(!corruptView.IsEditing && !Controls<Button>(corruptView).Any(x => x.Content as string == "＋ 新建预算") && Controls<Button>(corruptView).Any(x => x.Content as string == "备份原配置并重新开始…"), "corrupt store presents recovery instead of misleading create or restored editor");
        Click(corruptView, "备份原配置并重新开始…");
        Check(Controls<Button>(corruptView).Any(x => x.Content as string == "确认备份并重新开始"), "recovery requires explicit second confirmation");
        Click(corruptView, "取消恢复"); await corruptView.FlushDraft();
        Check(corruptHost.Book().Find("a") != null, "configuration recovery does not discard drafts");
        corruptHost.State.O("budgets")["recovery"] = J.Obj(("required", false)); corruptHost.State.O("budgets")["rules"] = new JsonArray();
        corruptView.Update(corruptHost.State.Copy()); Click(corruptView, "继续草稿 · 旧格式草稿"); Click(corruptView, "将草稿另存为新预算…");
        string recoveredId = corruptView.SelectedId; Click(corruptView, "保存预算");
        Check(recoveredId != "a" && corruptHost.LastSave?.O("rule").S("name") == "旧格式草稿" && corruptHost.LastSave.O("rule").I("revision") == 0 && corruptHost.Book().Find("a") != null, "draft of deleted or rebuilt configuration can explicitly save a new rule without losing the original draft");
        await corruptView.FlushDraft();
        var actionsHost = new Host(); var actionsView = actionsHost.Create(); actionsView.Select("a"); Click(actionsView, "在浮窗查看");
        Check(actionsHost.Requests.Any(x => x.S("action") == "view-budget" && x.S("budgetID") == "a"), "detail requests actual floating budget visibility");
        var more = Controls<Button>(actionsView).Single(x => AutomationProperties.GetName(x) == "更多").ContextMenu;
        Check(more.Items.Count == 4 && more.Items[2] is Separator && more.Items[3] is MenuItem { Header: "删除预算…" }, "deletion is separated from enable and copy in the more menu");
        ((MenuItem)more.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(actionsView.SelectedId != "a" && actionsView.IsEditing && Name(actionsView).Text == "a 副本", "copy opens an independent unsaved rule");
        string cancelledCopy = actionsView.SelectedId; await actionsView.FlushDraft(); actionsView.Select("b"); Click(actionsView, "继续草稿 · a 副本"); Click(actionsView, "取消");
        Check(actionsView.SelectedId == "a" && !actionsView.IsEditing && actionsHost.Book().Find(cancelledCopy) == null
            && !Controls<TextBlock>(actionsView).Any(x => x.Text == "此预算已删除或暂不可用"), "cancelled copy returns to its source detail even after draft navigation and restore");
        more = Controls<Button>(actionsView).Single(x => AutomationProperties.GetName(x) == "更多").ContextMenu;
        ((MenuItem)more.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Controls<CheckBox>(actionsView).Single(x => x.Content as string == "保存后在浮窗查看").IsChecked = true; Click(actionsView, "保存预算");
        Check(actionsHost.Requests.Count(x => x.S("action") == "view-budget") == 2 && actionsHost.LastSave!.O("rule").I("revision") == 0, "save-and-view uses the host display action after saving the new rule");
        actionsView.Select("a"); var deleteMenu = Controls<Button>(actionsView).Single(x => AutomationProperties.GetName(x) == "更多").ContextMenu;
        ((MenuItem)deleteMenu.Items[3]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(!actionsHost.Requests.Any(x => x.S("operation") == "delete") && Controls<Button>(actionsView).Any(x => x.Content as string == "确认删除此预算"), "choosing delete only opens explicit confirmation");
        Click(actionsView, "保留预算");
        Check(Controls<Button>(actionsView).Any(x => AutomationProperties.GetName(x) == "暂停提醒") && !Controls<Button>(actionsView).Any(x => x.Content as string == "恢复提醒"), "active reminder exposes pause instead of simultaneous opposing commands");
        actionsHost.State.O("budgets")["summaries"] = new JsonArray(J.Obj(("id", "a"), ("paused", true))); actionsView.Update(actionsHost.State.Copy());
        Check(Controls<Button>(actionsView).Any(x => x.Content as string == "恢复提醒") && !Controls<Button>(actionsView).Any(x => AutomationProperties.GetName(x) == "暂停提醒"), "paused reminder exposes restore beside its status");
        actionsView.Select("a", true); var zone = Controls<ComboBox>(actionsView).Single(x => AutomationProperties.GetName(x) == "时区");
        var timing = Controls<TextBlock>(actionsView).Single(x => x.Name == "BudgetChangeTiming");
        Check(timing.Text.Contains("名称、启停状态和提醒节点") && timing.Text.Contains("改变预算口径或币种时，额度也于下期生效") && timing.Text.Contains("当前监测周期已结束"), "default timing explains immediate reminder changes, deferred unit changes and an ended period");
        var immediate = Controls<CheckBox>(actionsView).Single(x => x.Content as string == "立即应用并重算本期"); immediate.IsChecked = true;
        Check(timing.Text.Contains("本次修改立即应用") && !timing.Text.Contains("下期生效"), "immediate recalculation replaces rather than contradicts default timing");
        immediate.IsChecked = false;
        Check(zone.IsEditable && zone.Items.Count > 50, "timezone supports searchable actual system zones and direct IANA input");
        var repeat = Controls<ComboBox>(actionsView).Single(x => AutomationProperties.GetName(x) == "周期"); repeat.SelectedValue = "once";
        var dates = Controls<DatePicker>(actionsView).ToArray(); dates[0].SelectedDate = new DateTime(2026, 10, 2); dates[1].SelectedDate = new DateTime(2026, 10, 3);
        Controls<ComboBox>(actionsView).Single(x => AutomationProperties.GetName(x) == "开始小时").SelectedIndex = 9;
        await actionsView.FlushDraft(); actionsView.Select("b"); actionsView.Select("a", true);
        Check(actionsHost.Book().Find("a")!.O("fields").S("start").StartsWith("2026-10-02 09:") && Controls<DatePicker>(actionsView).First().SelectedDate == new DateTime(2026, 10, 2), "calendar and clock values preserve the existing draft string format across navigation");
        actionsView.Select("a"); await actionsView.FlushDraft();
        var pendingHost = new Host(); var pendingRule = Rule("pending", "money"); var pendingSummary = PendingFixture(pendingRule);
        pendingHost.State.O("budgets")["rules"] = new JsonArray(pendingRule); pendingHost.State.O("budgets")["summaries"] = new JsonArray(pendingSummary);
        var pendingView = pendingHost.Create(); pendingView.Select("pending"); var pendingExpander = Controls<Expander>(pendingView).Single(x => x.Name == "BudgetPendingChanges");
        string pendingText = string.Join("\n", Controls<TextBlock>(pendingExpander).Select(x => x.Text));
        Check(!pendingExpander.IsExpanded && pendingText.Contains("3 项变更") && pendingText.Contains("本期：全部模型") && pendingText.Contains("下期：next-period-model")
            && pendingText.Contains("本期：输入 2") && pendingText.Contains("下期：输入 3") && !pendingText.Contains("缓存输入") && !pendingText.Contains("输出 10"), "pending changes compare actual values and omit unchanged price categories behind a collapsed section");
        pendingExpander.IsExpanded = true; pendingHost.State.O("budgets").A("summaries")[0]!["updatedAt"] = J.Now; pendingView.Update(pendingHost.State.Copy());
        Check(Controls<Expander>(pendingView).Single(x => x.Name == "BudgetPendingChanges").IsExpanded, "background budget refresh preserves the open pending comparison");
        var dateHost = new Host(); var dateView = dateHost.Create(); dateView.Select("a", true);
        Controls<ComboBox>(dateView).Single(x => AutomationProperties.GetName(x) == "周期").SelectedValue = "once";
        DatePicker StartDate() => Controls<DatePicker>(dateView).Single(x => AutomationProperties.GetName(x) == "开始日期");
        TextBox RawDate() => (TextBox)StartDate().Template.FindName("PART_TextBox", StartDate());
        Controls<ComboBox>(dateView).Single(x => AutomationProperties.GetName(x) == "开始小时").SelectedIndex = 9;
        Controls<ComboBox>(dateView).Single(x => AutomationProperties.GetName(x) == "开始分钟").SelectedIndex = 0;
        RawDate().Text = "2026-02-31"; RawDate().RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
        Click(dateView, "保存预算");
        Check(dateHost.LastSave == null && Controls<TextBlock>(dateView).Any(x => x.Text.StartsWith("开始日期无效")), "invalid typed date cannot save the previous valid SelectedDate even before its deferred text restoration");
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Check(RawDate().Text == "2026-02-31", "invalid date survives the same LostFocus validation path used by Tab");
        await dateView.FlushDraft(); dateView.Select("b"); dateView.Select("a", true);
        Check(RawDate().Text == "2026-02-31" && Controls<ComboBox>(dateView).Single(x => AutomationProperties.GetName(x) == "开始小时").SelectedIndex == 9
            && Controls<TextBlock>(dateView).Any(x => x.Name == "BudgetDateError" && x.Text.StartsWith("开始日期无效")), "invalid date and separate clock survive draft restore with visible validation");
        StartDate().SelectedDate = new DateTime(2026, 9, 12); RawDate().Text = "2026-02-31"; RawDate().RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent)); await Dispatcher.Yield(DispatcherPriority.ContextIdle); Click(dateView, "保存预算");
        var sameDayCalendar = (System.Windows.Controls.Calendar)((Popup)StartDate().Template.FindName("PART_Popup", StartDate())).Child;
        sameDayCalendar.DisplayDate = new DateTime(2026, 9, 12); sameDayCalendar.Measure(new Size(320, 400)); sameDayCalendar.Arrange(new Rect(0, 0, 320, 400)); sameDayCalendar.UpdateLayout();
        var sameDay = VisualControls<CalendarDayButton>(sameDayCalendar).Single(x => x.DataContext is DateTime day && day.Date == new DateTime(2026, 9, 12));
        sameDay.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseUpEvent });
        Check(StartDate().SelectedDate == new DateTime(2026, 9, 12) && RawDate().Text != "2026-02-31" && Controls<TextBlock>(dateView).Where(x => x.Name is "BudgetDateError" or "BudgetEditorError").All(x => x.Text.Length == 0)
            && dateHost.Statuses.Last() == "日期已修正，请保存预算以应用。", "accepting the same selected calendar day clears raw validation and the shared status without requiring a date change: " + RawDate().Text + " / " + string.Join(" | ", Controls<TextBlock>(dateView).Where(x => x.Name is "BudgetDateError" or "BudgetEditorError").Select(x => x.Text)) + " / " + dateHost.Statuses.Last());
        StartDate().SelectedDate = new DateTime(2026, 10, 2);
        Check(Controls<TextBlock>(dateView).Where(x => x.Name == "BudgetDateError").All(x => x.Text.Length == 0), "valid calendar selection clears invalid text feedback");
        RawDate().Clear(); RawDate().RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent)); await Dispatcher.Yield(DispatcherPriority.ContextIdle); Click(dateView, "保存预算");
        Check(dateHost.LastSave == null && RawDate().Text.Length == 0 && Controls<TextBlock>(dateView).Any(x => x.Text.StartsWith("开始日期不能为空")), "empty date stays empty and blocks save with a required-date message");
        RawDate().Text = "2026-10-02"; RawDate().RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
        Controls<DatePicker>(dateView).Single(x => AutomationProperties.GetName(x) == "结束日期").SelectedDate = new DateTime(2026, 10, 3); Click(dateView, "保存预算");
        Check(dateHost.LastSave?.O("rule").O("period").N("start") == DateTimeOffset.Parse("2026-10-02T09:00:00Z").ToUnixTimeSeconds(), "valid text repair saves the intended date and preserved hour");
        await dateView.FlushDraft();
        return J.Obj(("success", true), ("checks", checks), ("scope", "synthetic budget recovery and concurrent draft controls; no native windows"));
    }
    public static JsonObject RenderLayouts(string? directory = null)
    {
        if (directory != null) Directory.CreateDirectory(directory);
        bool originalTheme = Theme.Dark; int checks = 0; var cases = new JsonArray();
        void Check(bool condition, string text) { if (!condition) throw new InvalidOperationException("Budget layout: " + text); checks++; }
        try
        {
            foreach (bool dark in new[] { false, true }) foreach (int width in new[] { 700, 1000 }) foreach (string mode in new[] { "detail", "pending", "token", "money", "quota", "once", "date-error", "error", "corrupt" })
            {
                Theme.Apply(dark); var host = new Host(); var rule = Rule("layout", mode == "money" ? "money" : mode == "quota" ? "quota" : "token");
                rule["name"] = string.Concat(Enumerable.Repeat("跨模型日常开发预算", 8));
                if (mode == "money") rule["prices"] = new JsonArray(J.Obj(("model", "model-with-a-long-explicit-identifier"), ("input", 2), ("cachedInput", .5), ("output", 10)));
                if (mode is "once" or "date-error") rule["period"] = J.Obj(("type", "once"), ("timezone", "Asia/Shanghai"), ("start", 1790881200), ("end", 1790967600));
                host.State.O("budgets")["rules"] = new JsonArray(rule);
                host.State.O("budgets")["summaries"] = new JsonArray(J.Obj(("id", "layout"), ("kind", rule.S("kind")), ("remaining", 18), ("remainingFraction", .9), ("remainingPercent", 90), ("used", 2), ("status", "healthy"), ("coverage", "complete")));
                if (mode == "pending") host.State.O("budgets")["summaries"] = new JsonArray(PendingFixture(rule));
                if (mode == "corrupt") { host.State.O("budgets")["recovery"] = J.Obj(("required", true), ("path", "fixture/budgets.json")); host.State.O("budgets")["error"] = "无法读取预算文件，原内容已保留。"; }
                var view = host.Create(); if (mode != "corrupt") view.Select("layout", mode is not ("detail" or "pending"));
                var root = new Border { Width = width, Height = 520, Background = Theme.Background, Child = view };
                void Layout() { root.Measure(new Size(width, 520)); root.Arrange(new Rect(0, 0, width, 520)); root.UpdateLayout(); }
                Layout();
                if (view.IsEditing)
                {
                    var save = Controls<Button>(view).Single(x => x.Content as string == "保存预算"); var cancel = Controls<Button>(view).Single(x => x.Content as string == "取消");
                    var scroll = Controls<ScrollViewer>(view).Single(x => x.Name == "BudgetEditorScroll");
                    if (mode == "error") { Controls<TextBlock>(view).Single(x => x.Name == "BudgetEditorError").Text = string.Concat(Enumerable.Repeat("保存失败：请检查所选时区和预算范围，草稿已保留。", 10)); Layout(); }
                    if (mode == "date-error") { var date = Controls<DatePicker>(view).First(); ((TextBox)date.Template.FindName("PART_TextBox", date)).Text = "2026-02-31"; Click(view, "保存预算"); Layout(); }
                    Point before = save.TranslatePoint(new Point(), root); scroll.ScrollToEnd(); Layout(); Point after = save.TranslatePoint(new Point(), root);
                    Check(Math.Abs(before.Y - after.Y) < .01, "save remains fixed while scrolling " + mode + "/" + width);
                    foreach (var button in new[] { save, cancel })
                    {
                        var p = button.TranslatePoint(new Point(), root);
                        Check(p.X >= 0 && p.Y >= 0 && p.X + button.ActualWidth <= width + .1 && p.Y + button.ActualHeight <= 520.1, "footer command stays completely inside viewport");
                    }
                    Check(scroll.ViewportHeight >= 160, "editor retains usable scrollable content at minimum window size");
                    Check(((FrameworkElement)scroll.Content).DesiredSize.Width <= scroll.ViewportWidth + .5, "editor fields do not require clipped horizontal content");
                    var timing = Controls<TextBlock>(view).Single(x => x.Name == "BudgetChangeTiming"); var timingAt = timing.TranslatePoint(new Point(), root);
                    Check(timingAt.X >= 0 && timingAt.X + timing.ActualWidth <= width + .1 && timingAt.Y >= 0 && timingAt.Y + timing.ActualHeight <= after.Y + .1
                        && timing.ActualHeight >= timing.DesiredSize.Height - timing.Margin.Top - timing.Margin.Bottom - .1, "complete timing notice fits above the fixed save controls, including long errors " + mode + "/" + width);
                    if (mode is "once" or "date-error")
                    {
                        var date = Controls<DatePicker>(view).First(); double y = date.TranslatePoint(new Point(), (UIElement)scroll.Content).Y;
                        scroll.ScrollToVerticalOffset(Math.Max(0, y - 100)); Layout();
                        Check(date.ActualWidth >= 150, "calendar field remains usable beside the time choices");
                        var chrome = (Border)date.Template.FindName("DateChrome", date); var input = (TextBox)date.Template.FindName("PART_TextBox", date); var calendar = (Button)date.Template.FindName("PART_Button", date);
                        Check(chrome.CornerRadius.TopLeft == 7 && input.BorderThickness == new Thickness(0) && calendar.Content is System.Windows.Shapes.Path
                            && Equals(((SolidColorBrush)input.Foreground).Color, ((SolidColorBrush)Theme.Foreground).Color), "date input and calendar button use readable themed rounded chrome without native white inset");
                    }
                }
                else if (mode is "detail" or "pending")
                {
                    foreach (string label in new[] { "＋ 新建预算", "编辑", "在浮窗查看", "更多" })
                        Check(Controls<Button>(view).Any(x => x.Content as string == label || AutomationProperties.GetName(x) == label), "list and selected budget commands remain reachable");
                    if (mode == "pending")
                    {
                        var changes = Controls<Expander>(view).Single(x => x.Name == "BudgetPendingChanges"); changes.IsExpanded = true; Layout();
                        var scroll = Controls<ScrollViewer>(view).Single(x => Controls<Expander>((DependencyObject)x.Content).Contains(changes));
                        scroll.ScrollToVerticalOffset(changes.TranslatePoint(new Point(), (UIElement)scroll.Content).Y); Layout();
                        Check(Controls<TextBlock>(changes).All(text => { var p = text.TranslatePoint(new Point(), root); return p.X >= 0 && p.X + text.ActualWidth <= width + .1 && text.ActualHeight >= text.DesiredSize.Height - text.Margin.Top - text.Margin.Bottom - .1; }), "expanded long pending values wrap completely inside narrow detail column");
                    }
                }
                string name = "budget-" + mode + "-" + width + "-" + (dark ? "dark" : "light");
                if (directory != null)
                {
                    var bitmap = new RenderTargetBitmap(width, 520, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(output);
                }
                cases.Add(name); if (view.IsEditing) view.Select("layout");
            }
        }
        finally { Theme.Apply(originalTheme); }
        return J.Obj(("success", true), ("checks", checks), ("cases", cases), ("calendars", RenderCalendars(directory)));
    }
    public static JsonObject RenderCalendars(string? directory = null)
    {
        bool originalTheme = Theme.Dark; int checks = 0; var cases = new JsonArray();
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Budget calendar: " + message); checks++; }
        static Color ColorOf(Brush brush) => ((SolidColorBrush)brush).Color;
        static double Contrast(Color a, Color b)
        {
            static double L(Color c) { double Channel(byte x) { double n = x / 255d; return n <= .04045 ? n / 12.92 : Math.Pow((n + .055) / 1.055, 2.4); } return .2126 * Channel(c.R) + .7152 * Channel(c.G) + .0722 * Channel(c.B); }
            double x = L(a), y = L(b); return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
        }
        try
        {
            foreach (bool dark in new[] { false, true })
            {
                Theme.Apply(dark); var host = new Host(); var view = host.Create(); view.Select("a", true);
                var date = Controls<DatePicker>(view).First(); date.SelectedDate = new DateTime(2026, 9, 12);
                var calendar = (System.Windows.Controls.Calendar)((Popup)date.Template.FindName("PART_Popup", date)).Child;
                calendar.DisplayDate = new DateTime(2026, 9, 12); calendar.DisplayDateStart = new DateTime(2026, 9, 3); calendar.DisplayDateEnd = new DateTime(2026, 10, 25); calendar.BlackoutDates.Add(new CalendarDateRange(new DateTime(2026, 9, 11)));
                void Layout() { calendar.Measure(new Size(320, 400)); calendar.Arrange(new Rect(new Point(), calendar.DesiredSize)); calendar.UpdateLayout(); }
                Layout(); var item = VisualControls<CalendarItem>(calendar).Single(); var days = VisualControls<CalendarDayButton>(calendar).ToArray();
                var selected = days.Single(x => x.IsSelected); var current = days.First(x => x.IsEnabled && !x.IsSelected && !x.IsInactive && !x.IsBlackedOut); var inactive = days.First(x => x.IsEnabled && x.IsInactive); var blocked = days.Single(x => x.IsBlackedOut); var disabled = days.First(x => !x.IsEnabled);
                Brush surface = ((Border)item.Template.FindName("PART_Root", item)).Background;
                Check(days.Length == 42 && Contrast(ColorOf(current.Foreground), ColorOf(surface)) >= 4.5 && Contrast(ColorOf(inactive.Foreground), ColorOf(surface)) >= 4.5, "current and non-current dates are readable on themed surface");
                Check(ColorOf(current.Foreground) != ColorOf(inactive.Foreground) && Contrast(ColorOf(selected.Foreground), ColorOf(((Border)selected.Template.FindName("Body", selected)).Background)) >= 4.5, "selection and neighboring month remain visually distinct");
                Check(new[] { current, selected, inactive }.All(day => VisualControls<TextBlock>(day).All(text => ColorOf(text.Foreground) == ColorOf(day.Foreground))), "actual date glyphs use state colors instead of global TextBlock foreground inheritance");
                Check(((System.Windows.Shapes.Path)blocked.Template.FindName("Blackout", blocked)).Visibility == Visibility.Visible && disabled.Opacity < 1, "blackout and unavailable dates have distinct visible feedback");
                bool HasCondition(ControlTemplate template, DependencyProperty property) => template.Triggers.OfType<Trigger>().Any(x => x.Property == property) || template.Triggers.OfType<MultiTrigger>().Any(x => x.Conditions.Cast<System.Windows.Condition>().Any(c => c.Property == property));
                var header = (Button)item.Template.FindName("PART_HeaderButton", item); var previous = (Button)item.Template.FindName("PART_PreviousButton", item); var next = (Button)item.Template.FindName("PART_NextButton", item);
                Check(new[] { header, previous, next }.All(x => Contrast(ColorOf(x.Foreground), ColorOf(surface)) >= 4.5 && HasCondition(x.Template, UIElement.IsKeyboardFocusedProperty))
                    && HasCondition(current.Template, UIElement.IsKeyboardFocusedProperty) && HasCondition(current.Template, UIElement.IsMouseOverProperty), "header and navigation are readable with focus and hover feedback");
                foreach (string mode in new[] { "month", "year", "decade", "disabled", "unrestricted" })
                {
                    if (mode == "year") { calendar.DisplayDateStart = null; calendar.DisplayDateEnd = null; calendar.BlackoutDates.Clear(); }
                    calendar.DisplayMode = mode == "year" ? CalendarMode.Year : mode == "decade" ? CalendarMode.Decade : CalendarMode.Month; calendar.IsEnabled = mode != "disabled"; Layout();
                    string name = "budget-calendar-" + mode + "-" + (dark ? "dark" : "light"); cases.Add(name);
                    if (mode is "year" or "decade") Check(VisualControls<CalendarButton>(calendar).Count(x => x.Visibility == Visibility.Visible) == 12
                        && ((Grid)item.Template.FindName("PART_YearView", item)).Visibility == Visibility.Visible && ((Grid)item.Template.FindName("PART_MonthView", item)).Visibility == Visibility.Collapsed, "native year and decade navigation grids remain available");
                    if (mode == "unrestricted") Check(VisualControls<CalendarDayButton>(calendar).Count(x => x.Visibility == Visibility.Visible && x.IsEnabled && VisualControls<TextBlock>(x).Any(text => text.Text.Length > 0)) == 42, "unrestricted actual picker shows every day including both neighboring months");
                    if (directory != null)
                    {
                        Directory.CreateDirectory(directory); var bitmap = new RenderTargetBitmap((int)Math.Ceiling(calendar.ActualWidth), (int)Math.Ceiling(calendar.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(calendar); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(output);
                    }
                }
                calendar.IsEnabled = true; calendar.DisplayMode = CalendarMode.Month; Layout(); next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(calendar.DisplayDate.Month == 10, "native next-month button retains its behavior");
                view.Select("a");
            }
        }
        finally { Theme.Apply(originalTheme); }
        return J.Obj(("success", true), ("checks", checks), ("cases", cases), ("scope", "offscreen native Calendar controls; popup never opened"));
    }
}
