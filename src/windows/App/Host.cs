using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CodexUsage;

internal sealed class Host : IDisposable
{
    private readonly Settings settings = new();
    private readonly BudgetStore budgets;
    private readonly TaskMonitorService monitor;
    private readonly TaskNotifications taskNotifications;
    private readonly ChildJob job = new();
    private readonly Tray tray = new();
    private readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource stop = new();
    private readonly bool demo;
    private readonly bool noQuota;
    private CapsuleWindow? capsule;
    private SettingsWindow? preferences;
    private TaskMonitorSettingsWindow? monitorPreferences;
    private Process? main;
    private FileSystemWatcher? watcher;
    private JsonObject today = new(), filtered = new(), quota = new(), lastBudget = new(), demoBudgets = new();
    private readonly SemaphoreSlim mainGate = new(1, 1);
    // Polling, user commands and delivery share one order. A queued stop/read/settings
    // command cannot race a notification built from an earlier snapshot.
    private readonly SemaphoreSlim monitorGate = new(1, 1);
    private TaskCompletionSource? scanCompletion;
    private Task? quotaTask;
    private bool dirty = true, scanning, reading, quotaReading, queued, disposed;
    private long generation;
    private double lastScan, lastRead, lastReconcile, lastQuota, refreshStarted;
    private string refreshID = "", status = "正在准备本机统计…", viewedBudget = "";
    private string publishedState = "";
    private string localError = "", settingsBackupPath = "";
    private int viewedPID;
    private bool viewing;
    private bool monitorReading, deliveringTasks, monitorViewing;
    private string viewedMonitor = "", viewedMessage = "";
    private int monitorViewedPID;
    private string[]? notifying;
    private double notificationAttempt;
    private string[] lastNotifiedRules = [];
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    public Host(bool demo = false, bool noQuota = false)
    {
        this.demo = demo; this.noQuota = noQuota; Directory.CreateDirectory(Paths.Base);
        budgets = new BudgetStore(Path.Combine(Paths.Base, "budgets.json"));
        monitor = new TaskMonitorService(Path.Combine(Paths.Base, demo ? "demo-task-monitor.json" : "task-monitor.json"), settings.Home);
        taskNotifications = new TaskNotifications(request => Application.Current.Dispatcher.BeginInvoke(() => Guard(async () => { await Handle(request); })),
            !demo && Environment.GetEnvironmentVariable("CODEX_USAGE_TEST_BACKGROUND") != "1");
        try { quota = Quota.Describe(J.Read(Path.Combine(Paths.Base, "quota.json"), 65536)); } catch (Exception) { quota = Quota.Describe(new()); }
        if (demo) { var s = DemoData.State(); today = s.O("today"); filtered = s.O("filtered"); quota = s.O("quota"); demoBudgets = s.O("budgets"); status = "演示数据 · 未读取账号或本机日志"; dirty = false; }
        tray.OpenMain = () => Guard(() => OpenMain()); tray.Menu = BuildMenu;
        tray.RefreshData = () => Guard(() => Refresh(true)); tray.OpenSettings = () => ShowSettings("appearance");
        tray.OpenMonitor = id => Guard(() => OpenMain("monitor", monitorID: id));
        tray.OpenMonitorSettings = ShowMonitorSettings;
        tray.MonitorRequest = request => Handle(request);
        tray.BalloonShown = () =>
        {
            if (notifying is null) return;
            try { budgets.Apply("acknowledge", J.Obj(("ids", notifying)), settings.Home); NotifyState(); } catch (Exception e) { status = e.Message; }
        };
        tray.BalloonFinished = () => notifying = null;
        tray.BalloonClicked = () => Guard(() => OpenMain("budget", lastNotifiedRules.FirstOrDefault()));
        Watch(); SetMode(settings.Data.S("mode", "both"), false);
        tick.Tick += (_, _) => Tick(); tick.Start();
        _ = Ipc.Listen(Paths.Pipe, req => Application.Current.Dispatcher.InvokeAsync(() => Handle(req)).Task.Unwrap(), stop.Token,
            req => { if (req.S("action") == "quit") Application.Current.Dispatcher.BeginInvoke(() => Guard(Quit)); });
        Guard(async () => { await ReadIndex(); await Refresh(true); });
        if (!demo) Guard(() => PollMonitor(true));
        if (!demo && !noQuota) Guard(RefreshQuota);
    }
    private void Guard(Func<Task> work) { async void Run() { try { await work(); } catch (Exception e) { if (!disposed) { status = e.Message; NotifyState(); } } } Run(); }
    public JsonObject State(bool includeDemoUsage = false)
    {
        var value = J.Obj(("settings", settings.Data), ("home", settings.Home), ("cache", Paths.Cache(settings.Home)), ("quota", Quota.Describe(quota)), ("today", today), ("filtered", filtered),
            ("budgets", J.Obj(("rules", budgets.Rules), ("summaries", budgets.Summaries), ("events", budgets.Events), ("error", budgets.PersistenceError), ("recovery", budgets.Recovery))),
            ("busy", refreshID.Length > 0 || scanning || quotaReading), ("status", settings.Error.Length > 0 ? settings.Error : localError.Length > 0 ? "本地更新失败：" + localError : status),
            ("settingsError", settings.Error), ("settingsBackupPath", settingsBackupPath), ("updates", UpdateStates()), ("hostPID", Environment.ProcessId), ("mainPID", MainAlive ? main!.Id : 0), ("demo", demo));
        var monitored = monitor.Snapshot(); monitored["notification"] = taskNotifications.Status(); value["monitor"] = monitored;
        if (demo) { value["budgets"] = demoBudgets.DeepClone(); if (includeDemoUsage) value["usage"] = DemoData.Usage(); }
        return value;
    }
    private bool MainAlive => main is not null && !main.HasExited;
    private JsonObject UpdateStates()
    {
        bool localBusy = refreshID.Length > 0 || scanning;
        string quotaError = quota.S("error");
        return J.Obj(("local", J.Obj(("busy", localBusy), ("error", localError), ("updatedAt", lastRead > 0 ? lastRead : null),
                ("status", localBusy ? "本地日志更新中" : localError.Length > 0 ? "本地更新失败，可重试" : settings.Refresh == 0 ? "本地自动更新已暂停" : status))),
            ("quota", J.Obj(("busy", quotaReading), ("enabled", !noQuota), ("error", quotaError), ("updatedAt", quota.N("updated_at")),
                ("status", noQuota ? "账号额度读取未启用" : quotaReading ? "账号额度更新中" : quotaError.Length > 0 ? "账号额度更新失败，可重试" : Quota.Describe(quota).B("stale") ? "账号额度为上次记录" : "账号额度已更新"))));
    }
    private void NotifyState()
    {
        if (disposed) return;
        var state = State(); string signature = J.Text(state) + Theme.PreferenceSignature(settings.Data, "main") + Theme.PreferenceSignature(settings.Data, "floating") + Theme.PreferenceSignature(settings.Data, "tray");
        if (signature == publishedState) return;
        publishedState = signature; Theme.Apply(Theme.Resolve(settings.Data, "main")); capsule?.Update(state);
        string tip = "今日 " + J.Compact(today.O("summary").N("total_tokens")) + " · " + state.O("quota").S("compact");
        if (state.O("monitor").A("watches").Count > 0 || state.O("monitor").O("summary").I("unread") > 0)
            tip += " · " + TaskMonitorVisual.SummaryText(state);
        tray.Update(tip, state);
    }
    private void Watch()
    {
        watcher?.Dispose(); watcher = null; if (demo || !Directory.Exists(settings.Home)) return;
        watcher = new FileSystemWatcher(settings.Home) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size, InternalBufferSize = 8192 };
        void Changed(object? _, FileSystemEventArgs e) { string relative = Path.GetRelativePath(settings.Home, e.FullPath).Replace('\\', '/'); if (relative.StartsWith("sessions/", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("archived_sessions/", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("state_5.sqlite", StringComparison.OrdinalIgnoreCase) || relative == "session_index.jsonl" || relative is "sessions" or "archived_sessions") Application.Current.Dispatcher.BeginInvoke(() => dirty = true); }
        watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed; watcher.Renamed += Changed;
        watcher.Error += (_, _) => Application.Current.Dispatcher.BeginInvoke(() => dirty = true); watcher.EnableRaisingEvents = true;
    }
    private void Tick()
    {
        if (disposed) return; double now = J.Now;
        if (!demo && !noQuota && now - lastQuota >= 60 && !quotaReading) Guard(RefreshQuota);
        if (now - lastReconcile >= 600) { lastReconcile = now; dirty = true; Watch(); }
        if (main is not null && !MainAlive && mainGate.CurrentCount > 0) { main.Dispose(); main = null; dirty = true; viewing = false; }
        if (refreshID.Length > 0 && now - refreshStarted > 120) { refreshID = ""; localError = "本地刷新超时，可重新尝试"; NotifyState(); }
        if (!demo && settings.Refresh > 0 && now - lastScan >= settings.Refresh && !scanning)
        {
            if (!MainAlive && dirty) Guard(() => Refresh(false));
            // The main worker reports changed snapshots; do not reread SQLite on idle ticks.
        }
        if (!demo)
        {
            if (!monitorReading) Guard(() => PollMonitor());
            bool boundary = budgets.Summaries.Rows().Any(x => x.N("end") is double e && e <= now && x.S("status") != "ended" || x.S("status") == "scheduled" && x.N("start") is double s && s <= now || x.B("paused") && x.N("pausedUntil") is double p && p <= now);
            if (boundary) { budgets.Evaluate(null, Quota.Describe(quota), settings.Home); Guard(() => ReadIndex()); }
            DeliverAlerts();
        }
        NotifyState();
    }
    private async Task PollMonitor(bool force = false)
    {
        if (monitorReading || disposed || demo) return;
        monitorReading = true; string home = settings.Home;
        try { await monitorGate.WaitAsync(); try { await Task.Run(() => monitor.Poll(home, force)); } finally { monitorGate.Release(); } }
        finally { monitorReading = false; if (!disposed) { NotifyState(); await DeliverTaskAlerts(); } }
    }
    private async Task<JsonObject> MonitorOperation(string operation, JsonObject payload)
    {
        await monitorGate.WaitAsync();
        try { return await ApplyMonitorOperation(operation, payload); }
        finally { monitorGate.Release(); }
    }
    private async Task<JsonObject> ApplyMonitorOperation(string operation, JsonObject payload)
    {
        string home = settings.Home;
        var result = await Task.Run(() => monitor.Apply(operation, payload, home));
        taskNotifications.Reconcile(monitor.Snapshot().A("messages"));
        NotifyState(); var state = State(); state["monitorResult"] = result; return state;
    }
    private async Task DeliverTaskAlerts()
    {
        if (deliveringTasks || disposed || demo) return;
        deliveringTasks = true;
        await monitorGate.WaitAsync();
        try
        {
            var snapshot = monitor.Snapshot(); var preferences = snapshot.O("settings");
            // Delivery must never acknowledge an event whose local record could not be saved.
            if (snapshot.S("error").Length > 0) return;
            taskNotifications.Reconcile(snapshot.A("messages"));
            var pending = snapshot.A("messages").Rows().Where(x => x.S("delivery") == "pending" && !x.B("read")).ToArray();
            if (pending.Length == 0) return;
            // Suppression is consumed at event time, so leaving a game never replays a queue of old toasts.
            string suppression = NotificationPolicy.Suppression(preferences);
            GetWindowThreadProcessId(GetForegroundWindow(), out uint foreground);
            bool mainViewed = monitorViewing && MainAlive && monitorViewedPID == main!.Id && foreground == monitorViewedPID;
            var inline = pending.Where(x => suppression.Length > 0 || mainViewed && x.S("taskID") == viewedMonitor || tray.IsViewingMonitor(x.S("taskID")) ||
                capsule?.IsVisible == true && capsule.IsMonitorTaskVisible(x.S("taskID")) || J.Now - (x.N("createdAt") ?? J.Now) > 120).ToArray();
            if (inline.Length > 0)
                await ApplyMonitorOperation("delivery", J.Obj(("ids", inline.Select(x => x.S("id")).ToArray()), ("status", "suppressed"),
                    ("reason", suppression.Length > 0 ? suppression : "已在应用内显示，或该消息已超过即时提醒时效")));
            var ready = pending.Except(inline).Where(x => J.Now - (x.N("createdAt") ?? J.Now) >= 3 || x.S("status") is "waiting" or "failed").Take(32).ToArray();
            if (ready.Length == 0) return;
            if (monitor.Snapshot().S("error").Length > 0) return;
            var delivery = taskNotifications.Send(ready, preferences);
            delivery["ids"] = System.Text.Json.JsonSerializer.SerializeToNode(ready.Select(x => x.S("id")).ToArray());
            await ApplyMonitorOperation("delivery", delivery);
        }
        finally { monitorGate.Release(); deliveringTasks = false; }
    }
    private async Task ReadIndex(bool choices = false)
    {
        if (demo) { NotifyState(); return; }
        if (reading) { queued = true; return; }
        reading = true;
        long ticket = generation; string home = settings.Home; var filter = settings.Floating.Copy(); var requests = budgets.Requests(home);
        try
        {
            var read = await Task.Run(() => NativeIndex.Read(Paths.Cache(home), filter, requests, choices, includeEmptyBudgetMetadata: false));
            if (disposed || ticket != generation || J.Text(filter) != J.Text(settings.Floating)) return;
            today = read.O("today"); filtered = read.O("filtered"); lastBudget = read.O("budgetResult");
            if (read.B("filter_reset")) { var next = settings.Floating.Copy(); next["model"] = "all"; next["task"] = "all"; settings.Update(J.Obj(("floating", next))); }
            budgets.Evaluate(lastBudget, Quota.Describe(quota), home);
            lastRead = DateTimeOffset.TryParse(today.O("meta").S("generated_at"), out var generated) ? generated.ToUnixTimeMilliseconds() / 1000d : 0;
            status = lastRead > 0 ? "本地快照 · " + J.Date(lastRead, "HH:mm:ss") + " · 截至已落盘用量" : "等待本地用量记录";
        }
        catch (Exception e) { if (ticket == generation) localError = e.Message; }
        finally { reading = false; NotifyState(); if (queued && !disposed) { queued = false; Guard(() => ReadIndex()); } }
    }
    public Task Refresh(bool force) => RefreshOperations.Run(force, !noQuota, () => RefreshLocal(force), RefreshQuota);
    private async Task RefreshLocal(bool force)
    {
        if (demo) { NotifyState(); return; }
        if (scanning) { queued = true; return; }
        if (MainAlive)
        {
            if (refreshID.Length > 0) return; localError = ""; refreshID = Guid.NewGuid().ToString("N"); refreshStarted = J.Now; NotifyState();
            try { await Ipc.Send(J.Obj(("action", "refresh"), ("refreshID", refreshID)), Paths.Pipe + "-main"); }
            catch (Exception e) { refreshID = ""; localError = e.Message; NotifyState(); }
            return;
        }
        localError = ""; scanning = true; scanCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously); dirty = false; lastScan = J.Now; long ticket = generation; string home = settings.Home; NotifyState();
        try
        {
            string output = Path.Combine(Paths.Base, "compact-" + Environment.ProcessId + ".json");
            await Processes.Run(job, Paths.Python, ["-E", "-s", "-B", Path.Combine(Paths.Backend, "compact_snapshot.py"), "--codex-home", home, "--cache-path", Paths.Cache(home), "--output", output, "--parent-pid", Environment.ProcessId.ToString()], 180000);
            if (ticket == generation) await ReadIndex();
        }
        catch (Exception e) { if (ticket == generation) { localError = e.Message; dirty = true; } }
        finally { scanning = false; scanCompletion?.TrySetResult(); scanCompletion = null; NotifyState(); }
    }
    private Task RefreshQuota() => noQuota || disposed ? Task.CompletedTask : quotaTask is { IsCompleted: false } ? quotaTask : quotaTask = ReadQuota();
    private async Task ReadQuota()
    {
        if (quotaReading || disposed) return; quotaReading = true; lastQuota = J.Now;
        NotifyState();
        try { var result = await Quota.Read(job, quota, demo); if (disposed) return; quota = result; if (!demo) J.Write(Path.Combine(Paths.Base, "quota.json"), quota); budgets.Evaluate(null, Quota.Describe(quota), settings.Home); }
        catch (Exception e) { if (!disposed) { quota = quota.Copy(); quota["error"] = "账号额度更新失败：" + e.Message; quota["attempted_at"] = J.Now; } }
        finally { quotaReading = false; if (!disposed) NotifyState(); }
    }
    private void DeliverAlerts()
    {
        if (notifying is not null) return;
        var pending = budgets.Evaluate(null, Quota.Describe(quota), settings.Home).Rows().ToArray();
        if (pending.Length == 0) { notifying = null; return; }
        GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
        bool viewed = viewing && MainAlive && viewedPID == main!.Id && pid == viewedPID;
        var inline = pending.Where(x => viewed && x.S("ruleID") == viewedBudget).Select(x => x.S("id")).ToArray();
        if (inline.Length > 0) { try { budgets.Apply("acknowledge", J.Obj(("ids", inline)), settings.Home); } catch (Exception e) { status = "提醒状态未能保存：" + e.Message; return; } }
        var active = pending.Where(x => !inline.Contains(x.S("id"))).ToArray();
        if (active.Length == 0) return;
        if (J.Now - notificationAttempt < 30) return;
        string body = string.Join("\n", active.Take(3).Select(x => x.S("name") + " · " + x.S("message"))) + (active.Length > 3 ? $"\n另有 {active.Length - 3} 项预算触发提醒" : "");
        notifying = active.Select(x => x.S("id")).ToArray(); lastNotifiedRules = active.Select(x => x.S("ruleID")).Distinct().ToArray(); notificationAttempt = J.Now;
        if (!tray.Notify("Codex 用量 · 预算提醒", body)) notifying = null;
    }
    public async Task OpenMain(string? page = null, string? budgetID = null, string? monitorID = null, string? messageID = null, string? monitorList = null)
    {
        await mainGate.WaitAsync();
        try
        {
            if (disposed) return;
            if (scanCompletion is not null && page != "monitor") await scanCompletion.Task;
            if (disposed) return;
            if (MainAlive)
            {
                try
                {
                    var focus = J.Obj(("action", "focus"), ("page", page), ("budgetID", budgetID), ("monitorID", monitorID), ("messageID", messageID), ("monitorList", monitorList));
                    // Choose the unread/history list from the same current snapshot as
                    // the floating header, even if the main panel has not polled yet.
                    if (monitorList == "messages") focus["state"] = State();
                    await Ipc.Send(focus, Paths.Pipe + "-main"); return;
                }
                catch (IOException) { if (MainAlive) throw; }
            }
            var args = new System.Collections.Generic.List<string> { "--main", "--parent-pid", Environment.ProcessId.ToString() };
            if (demo) args.Add("--demo"); if (page is not null) { args.Add("--page"); args.Add(page); }
            if (budgetID is not null) { args.Add("--budget-id"); args.Add(budgetID); }
            if (monitorID is not null) { args.Add("--monitor-id"); args.Add(monitorID); }
            if (messageID is not null) { args.Add("--message-id"); args.Add(messageID); }
            if (monitorList == "messages") { args.Add("--monitor-list"); args.Add(monitorList); }
            main?.Dispose(); main = job.Start(Environment.ProcessPath!, args.ToArray()); main.StandardInput.Close(); _ = Processes.ReadBounded(main.StandardError, 65536, stop.Token); _ = Processes.ReadBounded(main.StandardOutput, 65536, stop.Token);
            // The helper asks for its initial state over the host pipe when ready.
        }
        finally { mainGate.Release(); }
    }
    private async Task CloseMain()
    {
        await mainGate.WaitAsync();
        try
        {
            if (!MainAlive) return;
            try { await Ipc.Send(J.Obj(("action", "close")), Paths.Pipe + "-main", 12000); }
            catch (Exception e) { if (MainAlive) throw new InvalidOperationException("主面板尚未关闭，草稿和设置可能仍待保存。请在主面板重试：" + e.Message, e); }
            if (MainAlive)
            {
                try { using var deadline = new CancellationTokenSource(5000); await main!.WaitForExitAsync(deadline.Token); }
                catch (OperationCanceledException) { throw new InvalidOperationException("主面板仍在保存或结束任务，本次操作已暂停，请稍后重试。"); }
            }
        }
        finally { mainGate.Release(); }
    }
    private void SetMode(string mode, bool save = true)
    {
        if (mode is not ("tray" or "float" or "both")) throw new ArgumentException("显示模式无效");
        if (save) settings.Update(J.Obj(("mode", mode)));
        tray.SetVisible(mode != "float");
        if (mode != "tray") ShowFloating(); else if (capsule is not null) { capsule.Hide(); }
        NotifyState();
    }
    private void ShowFloating()
    {
        if (capsule is null) { capsule = new CapsuleWindow(CapsuleAction); capsule.Restore(settings.Floating); capsule.Update(State()); }
        if (Environment.GetEnvironmentVariable("CODEX_USAGE_TEST_BACKGROUND") == "1") capsule.Opacity = 0;
        capsule.Show();
    }
    private void ApplyFloating(JsonObject patch)
    {
        var next = settings.Floating.Copy(); foreach (var (key, value) in patch) next[key] = value?.DeepClone();
        // A merged patch cannot remove old keys by omission. Drop legacy fixed
        // expansion direction from the actual object that reaches persistence.
        next.Remove("panelOffsetX"); next.Remove("panelOffsetY");
        if (patch.S("content") == "monitor") next["quotaContent"] = settings.Floating.S("content") == "budget" ? "budget" : settings.Floating.S("quotaContent", "usage");
        else if (patch.S("content") is "usage" or "budget") next["quotaContent"] = patch.S("content");
        bool scopeChanged = new[] { "days", "model", "task" }.Any(key => next.S(key) != settings.Floating.S(key));
        settings.Update(J.Obj(("floating", next)));
        if (scopeChanged) { generation++; filtered = new(); localError = ""; Guard(() => ReadIndex()); }
        NotifyState();
    }
    private void ViewBudget(string id)
    {
        var rules = demo ? demoBudgets.A("rules") : budgets.Rules;
        if (!rules.Rows().Any(x => x.S("id") == id)) throw new ArgumentException("预算不存在，请重新选择。");
        ApplyFloating(J.Obj(("content", "budget"), ("budgetID", id)));
        SetMode(settings.Data.S("mode", "both") == "tray" ? "both" : settings.Data.S("mode", "both"));
    }
    private async Task CapsuleAction(string action, string? value)
    {
        try
        {
            var f = settings.Floating.Copy();
            switch (action)
            {
                case "main": await OpenMain(f.S("content") is "budget" or "monitor" ? f.S("content") : null, f.S("budgetID")); return;
                case "monitorManage": await OpenMain("monitor"); return;
                case "monitorMessages": await OpenMain("monitor", monitorList: "messages"); return;
                case "monitorDetail": await OpenMain("monitor", monitorID: value); return;
                case "monitorStop":
                    var stopped = await MonitorOperation("stop", J.Obj(("id", value)));
                    if (!stopped.O("monitorResult").B("ok")) capsule?.Surface.ShowError(stopped.O("monitorResult").S("error", "取消监控失败，请重试"));
                    return;
                case "monitorSettings": ShowMonitorSettings(); return;
                case "monitorCheck":
                    string checkedHome = settings.Home;
                    try { var checkedState = await MonitorOperation("check", new()); capsule?.Surface.CompleteMonitorCheck(checkedState, checkedHome); }
                    catch (Exception e) { capsule?.Surface.FailMonitorCheck(e.Message, checkedHome); }
                    return;
                case "monitorClearEnded":
                    var cleared = await MonitorOperation("clear-ended", new());
                    if (!cleared.O("monitorResult").B("ok")) capsule?.Surface.ShowError(cleared.O("monitorResult").S("error", "清除失败，请到任务监控页面重试"));
                    return;
                case "budgetManage": await OpenMain("budget", f.S("budgetID")); return;
                case "budgetEdit": await OpenMain("budget", f.S("budgetID")); await Ipc.Send(J.Obj(("action", "focus"), ("page", "budget"), ("budgetID", f.S("budgetID")), ("edit", true)), Paths.Pipe + "-main"); return;
                case "refresh": await Refresh(true); return;
                case "updateStatus": ShowUpdateStatus(); return;
                case "settings-dialog": ShowSettings(value ?? "appearance"); return;
                case "quit": await Quit(); return;
                case "only": SetMode("float"); return;
                case "menu": SetMode("tray"); return;
                case "mode": SetMode(value ?? "both"); return;
                case "hide-floating": case "close": SetMode("tray"); return;
                case "view-budget": ViewBudget(value ?? f.S("budgetID")); return;
                case "budgetPause": case "budgetResume":
                    budgets.Apply(action == "budgetPause" ? "pause" : "resume", J.Obj(("id", f.S("budgetID")), ("mode", value == "cycle" ? "cycle" : "duration")), settings.Home); await ReadIndex(); return;
                case "keepExpanded": ApplyFloating(J.Obj(("keepExpanded", value == "true"))); return;
                case "filters-reset": ApplyFloating(J.Obj(("model", "all"), ("task", "all"))); return;
                case "pin": f["pinned"] = !f.B("pinned", true); break;
                case "themeDark": f["theme"] = "dark"; Theme.Apply(true); break;
                case "themeLight": f["theme"] = "light"; Theme.Apply(false); break;
                case "period": f["days"] = value; break;
                case "model": case "task": case "content": f[action] = value; break;
                case "budget": f["budgetID"] = value; break;
                case "edgeAutoHide": f["edgeAutoHide"] = !f.B("edgeAutoHide", true); break;
                case "edgeMetric": f["edgeMetric"] = value == "used" ? "used" : "remaining"; break;
                case "position":
                    var position = J.Parse(value!);
                    foreach (string key in new[] { "left", "top", "pixelLeft", "pixelTop", "monitorX", "monitorY" }) if (position.N(key) is double coordinate && double.IsFinite(coordinate)) f[key] = coordinate;
                    foreach (string key in new[] { "dockEdge", "monitor" }) if (position[key] is JsonValue) f[key] = position.S(key);
                    break;
                case "interval":
                    int seconds; if (value == "custom") { int? answer = Dialogs.Interval(settings.Refresh); if (answer is null) return; seconds = answer.Value; } else seconds = int.Parse(value!);
                    await Handle(J.Obj(("action", "settings"), ("patch", J.Obj(("refresh", seconds))))); return;
                default: return;
            }
            ApplyFloating(f);
        }
        catch (Exception e) { status = e.Message; NotifyState(); }
    }
    public async Task<JsonObject> Handle(JsonObject request)
    {
        switch (request.S("action"))
        {
            case "state": return State(true);
            case "choices":
            case "budget-choices":
                var state = State(); if (demo) state["choices"] = DemoData.Usage().O("filters").DeepClone();
                else { var filters = request.S("action") == "budget-choices" ? J.Obj(("days", "all"), ("model", "all"), ("task", "all")) : settings.Floating.Copy(); string cache = Paths.Cache(settings.Home); state["choices"] = (await Task.Run(() => NativeIndex.Read(cache, filters, new(), true, includeEmptyBudgetMetadata: false))).O("choices").DeepClone(); }
                return state;
            case "main": await OpenMain(request.S("page"), request.S("budgetID"), request.S("monitorID"), request.S("messageID"), request.S("monitorList")); break;
            case "monitor":
                if (request.S("operation") == "test-notification")
                {
                    var result = taskNotifications.Send([J.Obj(("id", "notification-test"), ("title", "通知测试"), ("status", "completed"))], monitor.Snapshot().O("settings"), true);
                    var testState = State(); testState["monitorResult"] = J.Obj(("ok", result.S("status") == "sent"), ("message", result.S("reason")), ("delivery", result)); return testState;
                }
                return await MonitorOperation(request.S("operation"), request.O("payload"));
            case "monitor-settings-dialog": ShowMonitorSettings(); return State();
            case "monitor-test":
                return await Handle(J.Obj(("action", "monitor"), ("operation", "test-notification")));
            case "monitor-notification-settings":
                Process.Start(new ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true }); return State();
            case "monitor-viewing": monitorViewedPID = request.I("pid"); monitorViewing = request.B("active"); viewedMonitor = request.S("taskID"); viewedMessage = request.S("messageID"); return State();
            case "monitor-notification":
                var ids = request.A("ids").OfType<System.Text.Json.Nodes.JsonValue>().Select(x => x.TryGetValue<string>(out var id) ? id : "").ToHashSet(StringComparer.Ordinal);
                var messages = monitor.Snapshot().A("messages").Rows().Where(x => ids.Contains(x.S("id"))).ToArray();
                if (request.S("operation") == "read") return await MonitorOperation("read", J.Obj(("ids", messages.Select(x => x.S("id")).ToArray())));
                if (messages.Length == 1) await OpenMain("monitor", monitorID: messages[0].S("taskID"), messageID: messages[0].S("id"));
                else await OpenMain("monitor");
                return State();
            case "refresh": Guard(() => Refresh(true)); break;
            case "refresh-local": Guard(() => RefreshLocal(true)); break;
            case "refresh-quota": Guard(RefreshQuota); break;
            case "update-status": _ = Application.Current.Dispatcher.BeginInvoke(ShowUpdateStatus); return State();
            case "settings-dialog": ShowSettings(request.S("page", "appearance")); return State();
            case "view-budget": ViewBudget(request.S("budgetID")); break;
            case "floating-settings": ApplyFloating(request.O("patch")); break;
            case "recover-settings":
                settingsBackupPath = settings.Recover(); generation++; dirty = true; localError = ""; Watch();
                Theme.Apply(Theme.Resolve(settings.Data, "main")); SetMode(settings.Data.S("mode", "both"), false);
                status = settingsBackupPath.Length > 0 ? "原设置已备份，已恢复默认设置" : "已重新载入修复后的设置";
                Guard(() => ReadIndex()); break;
            case "data-changed":
                bool expected = request.S("refreshID").Length == 0 || request.S("refreshID") == refreshID;
                if (request.S("refreshID").Length > 0 && request.S("refreshID") == refreshID) refreshID = "";
                if (expected) localError = request.B("success", true) ? "" : request.S("error", "本地刷新失败");
                if (expected && request.B("success", true)) Guard(() => ReadIndex()); break;
            case "settings":
                {
                    var patch = request.O("patch");
                    if (patch.ContainsKey("refresh") && (patch.N("refresh") is not double r || r < 0 || r > 3600 || r != Math.Truncate(r))) throw new ArgumentException("刷新间隔需要0至3600秒");
                    if (patch["appearance"] is JsonObject appearance) Theme.ValidateAppearance(appearance);
                    var old = settings.Data.Copy(); settings.Update(patch);
                    if (old.I("refresh", 5) != settings.Refresh && MainAlive) Guard(async () => { await Ipc.Send(J.Obj(("action", "settings"), ("settings", settings.Data)), Paths.Pipe + "-main"); });
                    if (patch.ContainsKey("floating")) { generation++; filtered = new(); Guard(() => ReadIndex()); }
                    if (patch.ContainsKey("mode")) SetMode(settings.Data.S("mode", "both"), false);
                    break;
                }
            case "home":
                {
                    string home = Path.GetFullPath(request.S("path")); if (!Directory.Exists(home)) throw new DirectoryNotFoundException("数据目录不存在");
                    await CloseMain(); generation++; settings.Update(J.Obj(("home", home))); today = new(); filtered = new(); lastBudget = new(); dirty = true; Watch(); budgets.Evaluate(null, Quota.Describe(quota), home); Guard(() => Refresh(true)); Guard(() => OpenMain()); break;
                }
            case "mode": SetMode(request.S("value")); break;
            case "show-float": SetMode(settings.Data.S("mode", "both") == "tray" ? "both" : settings.Data.S("mode", "both")); break;
            case "hide-float": SetMode("tray"); break;
            case "budget": budgets.Apply(request.S("operation"), request.O("payload"), settings.Home); await ReadIndex(); break;
            case "viewing": viewedBudget = request.S("budgetID"); viewedPID = request.I("pid"); viewing = request.B("active") && !request.B("editing"); break;
            case "quit": if (preferences != null) await preferences.PrepareClose(); return J.Obj(("stopping", true));
            default: throw new ArgumentException("未知操作");
        }
        NotifyState(); return State();
    }
    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu(); Theme.ApplyTo(menu.Resources, Theme.Resolve(settings.Data, "tray"));
        void Add(string label, Func<Task> run, bool enabled = true) { var item = new MenuItem { Header = label, IsEnabled = enabled }; item.Click += (_, _) => Guard(run); menu.Items.Add(item); }
        Add("打开主面板", () => OpenMain()); Add("预算与提醒", () => OpenMain("budget"));
        Add("任务监控…", () => OpenMain("monitor"));
        menu.Items.Add(new Separator());
        Add("刷新本地用量与账号额度", () => Refresh(true), !scanning && refreshID.Length == 0 && !quotaReading);
        Add("数据与更新…", () => { ShowUpdateStatus(); return Task.CompletedTask; });
        if (lastNotifiedRules.Length > 0)
        {
            async Task Pause(string mode) { foreach (string id in lastNotifiedRules.Where(id => budgets.Rules.Rows().Any(x => x.S("id") == id))) budgets.Apply("pause", J.Obj(("id", id), ("mode", mode)), settings.Home); await ReadIndex(); }
            Add("暂停最近提醒 30 分钟", () => Pause("duration")); Add("最近提醒本周期不再弹出", () => Pause("cycle"));
        }
        menu.Items.Add(new Separator());
        var taskPreferences = monitor.Snapshot().O("settings");
        var taskMenu = new MenuItem { Header = "任务提醒" }; menu.Items.Add(taskMenu);
        foreach (var (id, label) in new[] { ("system", "系统通知＋应用内标记"), ("markers", "仅应用内标记") })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = taskPreferences.S("delivery", "system") == id };
            item.Click += (_, _) => Guard(async () => { await MonitorOperation("settings", J.Obj(("patch", J.Obj(("delivery", id))))); }); taskMenu.Items.Add(item);
        }
        taskMenu.Items.Add(new Separator());
        double paused = taskPreferences.N("pausedUntil") ?? 0;
        if (paused < 0 || paused > J.Now)
        {
            var resume = new MenuItem { Header = "恢复任务通知" }; resume.Click += (_, _) => Guard(async () => { await MonitorOperation("settings", J.Obj(("patch", J.Obj(("pausedUntil", 0))))); }); taskMenu.Items.Add(resume);
        }
        else foreach (var (seconds, label) in new[] { (1800, "暂停任务通知 30 分钟"), (3600, "暂停任务通知 1 小时"), (-1, "暂停任务通知，直到手动恢复") })
        {
            var pause = new MenuItem { Header = label }; pause.Click += (_, _) => Guard(async () => { await MonitorOperation("settings", J.Obj(("patch", J.Obj(("pausedUntil", seconds < 0 ? -1 : J.Now + seconds))))); }); taskMenu.Items.Add(pause);
        }
        Add("任务提醒设置…", () => { ShowMonitorSettings(); return Task.CompletedTask; });
        menu.Items.Add(new Separator());
        var modes = new MenuItem { Header = "常驻显示方式" }; menu.Items.Add(modes);
        foreach (var (id, label) in new[] { ("tray", "仅系统托盘"), ("float", "仅悬浮窗"), ("both", "托盘与悬浮窗") })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = settings.Data.S("mode", "both") == id };
            item.Click += (_, _) => Guard(() => { SetMode(id); return Task.CompletedTask; }); modes.Items.Add(item);
        }
        Add("外观与设置…", () => { ShowSettings("appearance"); return Task.CompletedTask; });
        Add("显示与悬浮窗设置…", () => { ShowSettings("display"); return Task.CompletedTask; });
        menu.Items.Add(new Separator()); Add("退出 Codex 用量", Quit); ((MenuItem)menu.Items[^1]).ToolTip = "退出后停止本工具的统计与监控，Codex 任务继续执行"; return menu;
    }
    private async Task Quit() { if (preferences != null) await preferences.PrepareClose(); await CloseMain(); Dispose(); Application.Current.Shutdown(); }
    private void ShowUpdateStatus() => ShowSettings("updates");
    private void ShowMonitorSettings()
    {
        if (monitorPreferences == null) { monitorPreferences = new TaskMonitorSettingsWindow(null, () => State(), Handle); monitorPreferences.Closed += (_, _) => monitorPreferences = null; }
        if (Environment.GetEnvironmentVariable("CODEX_USAGE_TEST_BACKGROUND") == "1") return;
        if (!monitorPreferences.IsVisible) monitorPreferences.Show();
        if (monitorPreferences.WindowState == WindowState.Minimized) monitorPreferences.WindowState = WindowState.Normal;
        monitorPreferences.Activate();
    }
    private void ShowSettings(string page)
    {
        if (preferences == null) { preferences = new SettingsWindow(() => State(), Handle); preferences.Closed += (_, _) => preferences = null; }
        preferences.SelectPage(page);
        if (Environment.GetEnvironmentVariable("CODEX_USAGE_TEST_BACKGROUND") == "1") return;
        if (!preferences.IsVisible) preferences.Show();
        if (preferences.WindowState == WindowState.Minimized) preferences.WindowState = WindowState.Normal;
        preferences.Activate();
    }
    public void Dispose() { if (disposed) return; disposed = true; stop.Cancel(); tick.Stop(); watcher?.Dispose(); capsule?.Close(); preferences?.Close(); monitorPreferences?.Close(); taskNotifications.Dispose(); monitor.Dispose(); tray.Dispose(); job.Dispose(); main?.Dispose(); stop.Dispose(); }
}

internal static class Dialogs
{
    public static int? Interval(int current)
    {
        var window = new Window { Title = "自动刷新间隔", Width = 360, Height = 200, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true };
        var stack = new StackPanel { Margin = new Thickness(20) }; stack.Children.Add(new TextBlock { Text = "输入 1–3600 秒；0 表示关闭自动刷新", Margin = new Thickness(0, 0, 0, 12) });
        var input = new TextBox { Text = current.ToString() }; stack.Children.Add(input); var error = new TextBlock { Foreground = System.Windows.Media.Brushes.IndianRed }; stack.Children.Add(error);
        var save = new Button { Content = "确定", Margin = new Thickness(0, 12, 0, 0), IsDefault = true }; stack.Children.Add(save); int? result = null;
        save.Click += (_, _) => { if (int.TryParse(input.Text, out int value) && value >= 0 && value <= 3600) { result = value; window.DialogResult = true; } else error.Text = "请输入0至3600的整数"; }; window.Content = stack; window.ShowDialog(); return result;
    }
}
