using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace CodexUsage;

internal static class TaskMonitorTests
{
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException("Task monitor test: " + message); }
    private static JsonObject Event(string kind, string turn, double at) => J.Obj(("timestamp", at), ("type", "event_msg"),
        ("payload", J.Obj(("type", kind), ("turn_id", turn), (kind == "task_started" ? "started_at" : "completed_at", at), ("reason", "interrupted"))));
    private static void Append(string path, JsonObject value) => File.AppendAllText(path, J.Text(value) + "\n", new UTF8Encoding(false));
    private static string CreateLog(string home, string id, string turn, double at, string parent = "")
    {
        string directory = Path.Combine(home, "sessions", "2026", "09", "12"); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "rollout-" + id + ".jsonl");
        Append(path, J.Obj(("type", "session_meta"), ("timestamp", at - 1), ("payload", J.Obj(("id", id), ("cwd", "D:\\Example Project"), ("parent_thread_id", parent)))));
        Append(path, Event("task_started", turn, at)); return path;
    }
    private static JsonObject Add(TaskMonitorService service, string home, string id, string turn, string mode = "once") =>
        service.Apply("add", J.Obj(("mode", mode), ("selections", new JsonArray(J.Obj(("id", id), ("turnID", turn))))), home);
    private static JsonObject Watch(TaskMonitorService service, string id) => service.Snapshot().A("watches").Rows().Single(x => x.S("id") == id);
    private static void Fill(string path, int megabytes, bool oneLine = false)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        byte[] text = Encoding.UTF8.GetBytes(new string('x', 128 * 1024));
        if (oneLine) stream.Write(Encoding.UTF8.GetBytes("{\"type\":\"response_item\",\"payload\":{\"text\":\""));
        for (int i = 0; i < megabytes * 8; i++)
        {
            if (!oneLine) stream.Write(Encoding.UTF8.GetBytes("{\"type\":\"response_item\",\"payload\":{\"text\":\""));
            stream.Write(text);
            if (!oneLine) stream.Write(Encoding.UTF8.GetBytes("\"}}\n"));
        }
        if (oneLine) stream.Write(Encoding.UTF8.GetBytes("\"}}\n"));
    }
    private static void Settle(TaskMonitorService service, string home, int polls = 100)
    {
        for (int i = 0; i < polls; i++)
        {
            double before = service.Snapshot().O("diagnostics").N("bytesRead") ?? 0;
            service.Poll(home, true);
            Check((service.Snapshot().O("diagnostics").N("bytesRead") ?? 0) - before <= 11 * 1024 * 1024, "one poll has a shared forward/backfill byte budget");
            if (service.Snapshot().O("diagnostics").I("backfillPending") == 0) return;
        }
        throw new InvalidOperationException("Backfill did not terminate within bounded fixture work");
    }

    private static void FirstTurnTimestampPrecisionTests(string root)
    {
        long at = new DateTimeOffset(2026, 9, 13, 9, 32, 52, TimeSpan.Zero).ToUnixTimeSeconds();
        string CreatePrecisionLog(string home, string id, string turn, string fraction)
        {
            string directory = Path.Combine(home, "sessions"); Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, "rollout-" + id + ".jsonl");
            string timestamp = "2026-09-13T09:32:52." + fraction + "Z";
            Append(file, J.Obj(("type", "session_meta"), ("timestamp", timestamp),
                ("payload", J.Obj(("id", id), ("cwd", "D:\\Example Project")))));
            Append(file, J.Obj(("type", "event_msg"), ("timestamp", timestamp),
                ("payload", J.Obj(("type", "task_started"), ("turn_id", turn), ("started_at", at)))));
            return file;
        }

        foreach (bool historical in new[] { false, true })
        {
            string suffix = historical ? "backfill" : "forward", id = "precision-" + suffix, turn = id + "-turn";
            string home = Path.Combine(root, id + "-home"), store = Path.Combine(root, id + ".json");
            string file = CreatePrecisionLog(home, id, turn, historical ? "949" : "390");
            if (historical) Fill(file, 5);
            using (var service = new TaskMonitorService(store, home))
            {
                service.Poll(home, true);
                if (historical)
                {
                    Check(service.Snapshot().O("diagnostics").I("backfillPending") > 0,
                        "precision fixture requires recovery of the first start beyond the initial tail");
                    Settle(service, home);
                }
                var task = service.Snapshot().A("tasks").Rows().Single();
                Check(task.B("selectable") && task.S("status") == "running" && task.S("turnID") == turn && task.N("startedAt") == at,
                    suffix + " first start remains selectable when its whole-second time precedes metadata milliseconds");
                Check(service.Snapshot().A("messages").Count == 0 && Add(service, home, id, turn).B("ok") && Watch(service, id).B("active"),
                    suffix + " previously running first turn can actually be monitored without inventing a past result");
                Append(file, Event("task_complete", turn, at + 1)); service.Poll(home, true);
                Check(!Watch(service, id).B("active") && Watch(service, id).S("status") == "completed" &&
                    service.Snapshot().A("messages").Rows().Single().S("turnID") == turn,
                    suffix + " precision-safe first turn records its real end once");
                Append(file, Event("task_complete", turn, at + 1)); service.Poll(home, true);
                Check(service.Snapshot().A("messages").Count == 1,
                    suffix + " duplicate completion does not duplicate the recovered first-turn result");
            }
            using (var restored = new TaskMonitorService(store, home))
            {
                restored.Poll(home, true);
                Check(restored.Snapshot().A("messages").Count == 1 && !Watch(restored, id).B("active"),
                    suffix + " completed first-turn result remains deduplicated after restart");
            }
        }

        string legacyHome = Path.Combine(root, "precision-legacy-home"), legacyStore = Path.Combine(root, "precision-legacy.json");
        string legacyFile = CreatePrecisionLog(legacyHome, "precision-legacy", "precision-legacy-turn", "949");
        using (var seed = new TaskMonitorService(legacyStore, legacyHome)) seed.Poll(legacyHome, true);
        var saved = J.Read(legacyStore, 32 * 1024 * 1024); var cursor = saved.A("cursors").Rows().Single();
        Check(cursor.A("turns").Rows().Any(x => x.S("turnID") == "precision-legacy-turn" && x.S("status") == "running"),
            "legacy fixture retains the already parsed explicit running turn");
        cursor.O("task")["turnID"] = ""; cursor.O("task")["status"] = "unknown"; cursor.O("task")["updatedAt"] = at + .949;
        cursor["offset"] = new FileInfo(legacyFile).Length; cursor["backfillDone"] = true;
        J.Write(legacyStore, saved);
        using (var restored = new TaskMonitorService(legacyStore, legacyHome))
        {
            Settle(restored, legacyHome); var task = restored.Snapshot().A("tasks").Rows().Single();
            Check(task.B("selectable") && task.S("status") == "running" && task.S("turnID") == "precision-legacy-turn" &&
                restored.Snapshot().A("messages").Count == 0,
                "restart repairs an exhausted legacy cursor whose parsed first turn was hidden by metadata precision");
            Check(Add(restored, legacyHome, "precision-legacy", "precision-legacy-turn").B("ok"),
                "repaired legacy running task accepts a real subscription without another start event");
        }

        string absentHome = Path.Combine(root, "precision-absent-home"), absentStore = Path.Combine(root, "precision-absent.json");
        string absentFile = CreatePrecisionLog(absentHome, "precision-absent", "unwritten-turn", "390");
        File.WriteAllText(absentFile, File.ReadLines(absentFile).First() + "\n", new UTF8Encoding(false));
        using (var service = new TaskMonitorService(absentStore, absentHome)) Settle(service, absentHome);
        using (var restored = new TaskMonitorService(absentStore, absentHome))
        {
            Settle(restored, absentHome); var task = restored.Snapshot().A("tasks").Rows().Single();
            Check(!task.B("selectable") && task.S("status") == "unknown" && task.S("turnID").Length == 0 &&
                !Add(restored, absentHome, "precision-absent", "unwritten-turn").B("ok") && restored.Snapshot().A("messages").Count == 0,
                "metadata alone remains unselectable after restart and cannot invent a running turn");
        }
    }

    private static void BackfillTests(string root)
    {
        string home = Path.Combine(root, "long-home"), store = Path.Combine(root, "long-state.json");
        double at = J.Now - 100; string log = CreateLog(home, "long-task", "long-turn", at); Fill(log, 21);
        using (var service = new TaskMonitorService(store, home))
        {
            service.Poll(home, true);
            var task = service.Snapshot().A("tasks").Rows().Single();
            Check(!task.B("selectable") && task.S("status") == "unknown" && task.S("sourceError").Contains("回溯", StringComparison.Ordinal), "long task exposes verification state before the start is found");
            Check(service.Snapshot().O("sourceStatus").S("status") == "partial", "readable source does not hide incomplete lifecycle discovery");
            Settle(service, home);
            task = service.Snapshot().A("tasks").Rows().Single();
            Check(task.B("selectable") && task.S("turnID") == "long-turn" && service.Snapshot().A("messages").Count == 0, "arbitrarily distant explicit start becomes selectable without invented completion");
            Check(Add(service, home, "long-task", "long-turn").B("ok"), "long running task can actually be monitored");
        }
        // Emulate a v1.4.0 cursor at EOF with no recovered lifecycle, then restart in the middle of a split reverse line.
        var saved = J.Read(store, 32 * 1024 * 1024); saved["watches"] = new JsonArray();
        foreach (var cursor in saved.A("cursors").Rows())
        { cursor.O("task")["status"] = "unknown"; cursor.O("task")["turnID"] = ""; cursor["turns"] = new JsonArray(); cursor.Remove("backfillDone"); cursor.Remove("backfillPosition"); }
        J.Write(store, saved);
        using (var service = new TaskMonitorService(store, home))
        { service.Poll(home, true); Check(service.Snapshot().O("diagnostics").I("backfillPending") > 0, "legacy unknown cursor starts backfill"); }
        using (var service = new TaskMonitorService(store, home))
        { Settle(service, home); Check(service.Snapshot().A("tasks")[0].B("selectable"), "reverse checkpoint resumes correctly across restart"); }

        string endedHome = Path.Combine(root, "ended-home"), endedLog = CreateLog(endedHome, "ended-task", "ended-turn", at);
        Append(endedLog, Event("task_complete", "ended-turn", at + 1)); Fill(endedLog, 5);
        using (var service = new TaskMonitorService(Path.Combine(root, "ended-state.json"), endedHome))
        {
            Settle(service, endedHome);
            Check(service.Snapshot().A("tasks")[0].S("status") == "completed" && !service.Snapshot().A("tasks")[0].B("selectable") && service.Snapshot().A("messages").Count == 0,
                "backfill uses the most recent explicit end, not an older start, and creates no historical notification");
        }

        string quietHome = Path.Combine(root, "quiet-home"), quietLog = CreateLog(quietHome, "quiet-task", "unused", at);
        string header = File.ReadLines(quietLog).First() + "\n"; File.WriteAllText(quietLog, header, new UTF8Encoding(false)); Fill(quietLog, 6);
        string quietStore = Path.Combine(root, "quiet-state.json");
        using (var service = new TaskMonitorService(quietStore, quietHome))
        {
            Settle(service, quietHome); long read = (long)(service.Snapshot().O("diagnostics").N("bytesRead") ?? 0);
            for (int i = 0; i < 3; i++) service.Poll(quietHome, true);
            Check((service.Snapshot().O("diagnostics").N("bytesRead") ?? 0) == read && !service.Snapshot().A("tasks")[0].B("selectable"), "files with no lifecycle stop scanning at the head");
        }
        using (var service = new TaskMonitorService(quietStore, quietHome))
        {
            service.Poll(quietHome, true);
            Check(service.Snapshot().O("diagnostics").N("backfillBytes") == 0, "exhausted discovery is durable across restart");
            string partial = J.Text(Event("task_started", "quiet-new", J.Now)); File.AppendAllText(quietLog, partial, new UTF8Encoding(false)); service.Poll(quietHome, true);
            Check(!service.Snapshot().A("tasks")[0].B("selectable"), "unterminated tail is not treated as a complete lifecycle event");
            File.AppendAllText(quietLog, "\n"); service.Poll(quietHome, true);
            Check(service.Snapshot().A("tasks")[0].B("selectable"), "new complete event wakes previously exhausted discovery");
        }

        string changingHome = Path.Combine(root, "changing-home"), changingLog = CreateLog(changingHome, "changing-task", "old-turn", at); Fill(changingLog, 9);
        using (var service = new TaskMonitorService(Path.Combine(root, "changing-state.json"), changingHome))
        {
            service.Poll(changingHome, true); Append(changingLog, Event("task_started", "new-turn", J.Now)); service.Poll(changingHome, true);
            Check(service.Snapshot().A("tasks")[0].S("turnID") == "new-turn" && service.Snapshot().O("diagnostics").I("backfillPending") == 0, "new forward lifecycle cancels older backfill without rewinding execution");
        }
        string movedHome = Path.Combine(root, "moved-home"), movedLog = CreateLog(movedHome, "moved-task", "moved-turn", at); Fill(movedLog, 9);
        using (var service = new TaskMonitorService(Path.Combine(root, "moved-state.json"), movedHome))
        {
            service.Poll(movedHome, true); string archive = Path.Combine(movedHome, "archived_sessions"); Directory.CreateDirectory(archive);
            File.Move(movedLog, Path.Combine(archive, Path.GetFileName(movedLog))); Settle(service, movedHome);
            Check(service.Snapshot().A("tasks").Count == 1 && service.Snapshot().A("tasks")[0].B("selectable"), "archive move preserves an in-progress reverse cursor");
        }
        string truncatedHome = Path.Combine(root, "truncated-home"), truncatedLog = CreateLog(truncatedHome, "truncated-task", "removed-turn", at); Fill(truncatedLog, 9);
        using (var service = new TaskMonitorService(Path.Combine(root, "truncated-state.json"), truncatedHome))
        {
            service.Poll(truncatedHome, true); string meta = File.ReadLines(truncatedLog).First() + "\n";
            File.WriteAllText(truncatedLog, meta, new UTF8Encoding(false)); Append(truncatedLog, Event("task_started", "replacement-turn", J.Now)); service.Poll(truncatedHome, true);
            Check(service.Snapshot().A("tasks")[0].S("turnID") == "replacement-turn" && service.Snapshot().A("tasks")[0].B("selectable"), "truncation invalidates the old backfill and finds the replacement lifecycle");
        }
        string largeLineHome = Path.Combine(root, "large-line-home"), largeLineLog = CreateLog(largeLineHome, "large-line-task", "large-line-turn", at); Fill(largeLineLog, 18, true);
        using (var service = new TaskMonitorService(Path.Combine(root, "large-line-state.json"), largeLineHome))
        { Settle(service, largeLineHome); Check(service.Snapshot().A("tasks")[0].B("selectable"), "a huge ordinary response line does not hide an earlier lifecycle"); }
    }
    private static void SourceRecoveryTests(string root)
    {
        string home = Path.Combine(root, "scan-error-home"), healthy = Path.Combine(root, "scan-healthy-home");
        Directory.CreateDirectory(Path.Combine(home, "sessions")); Directory.CreateDirectory(Path.Combine(healthy, "sessions"));
        bool failScan = true;
        System.Collections.Generic.IEnumerable<string> Enumerate(string folder)
        {
            if (failScan && string.Equals(folder, Path.Combine(home, "sessions"), StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("合成目录枚举失败");
            return Directory.EnumerateFiles(folder, "*.jsonl", SearchOption.AllDirectories);
        }
        using (var service = new TaskMonitorService(Path.Combine(root, "scan-errors.json"), home, enumerateFiles: Enumerate))
        {
            var reply = service.Apply("check", new(), home);
            Check(reply.B("ok") && service.Snapshot().O("sourceStatus").S("status") == "partial" &&
                service.Snapshot().O("sourceStatus").S("text").Contains("合成目录枚举失败", StringComparison.Ordinal),
                "a completed check reports partial source health after directory discovery fails");
            int scans = service.Snapshot().O("diagnostics").I("directoryScans");
            System.Threading.Thread.Sleep(550); service.Poll(home);
            Check(service.Snapshot().O("diagnostics").I("directoryScans") == scans && service.Snapshot().O("sourceStatus").S("status") == "partial",
                "ordinary polling cannot erase an unverified directory scan failure");
            failScan = false;
            System.Threading.Thread.Sleep(550); service.Poll(home);
            Check(service.Snapshot().O("diagnostics").I("directoryScans") == scans && service.Snapshot().O("sourceStatus").S("status") == "partial",
                "restored permissions remain unconfirmed until directory discovery actually reruns");
            service.Apply("check", new(), home);
            Check(service.Snapshot().O("diagnostics").I("directoryScans") == scans + 1 && service.Snapshot().O("sourceStatus").S("status") == "available",
                "a successful complete scan clears its own previous error");
            failScan = true; service.Apply("check", new(), home);
            Check(service.Snapshot().O("sourceStatus").S("status") == "partial", "a new scan failure is reported again after recovery");
            service.Poll(healthy, true);
            Check(service.Snapshot().O("sourceStatus").S("status") == "available" && service.Snapshot().O("sourceStatus").S("home") == healthy,
                "switching data directories does not carry the old directory scan error into the new source");
        }

        string watcherHome = Path.Combine(root, "watcher-error-home"); Directory.CreateDirectory(Path.Combine(watcherHome, "sessions"));
        bool failWatcher = true; FileSystemWatcher? currentWatcher = null;
        FileSystemWatcher StartWatcher(string folder)
        {
            if (failWatcher) throw new IOException("合成文件监听启动失败");
            return currentWatcher = new FileSystemWatcher(folder, "*.jsonl");
        }
        using (var service = new TaskMonitorService(Path.Combine(root, "watcher-errors.json"), watcherHome, createWatcher: StartWatcher))
        {
            service.Apply("check", new(), watcherHome);
            Check(service.Snapshot().O("sourceStatus").S("status") == "partial" &&
                service.Snapshot().O("sourceStatus").S("text").Contains("合成文件监听启动失败", StringComparison.Ordinal),
                "successful directory discovery does not hide a failed file-event listener");
            failWatcher = false; service.Apply("check", new(), watcherHome);
            Check(service.Snapshot().O("sourceStatus").S("status") == "available" && currentWatcher?.EnableRaisingEvents == true,
                "a successfully restarted file-event listener clears its own error");
            // Raise the native wrapper's error event without changing system permissions or overflowing user watchers.
            typeof(FileSystemWatcher).GetMethod("OnError", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(currentWatcher, [new ErrorEventArgs(new InternalBufferOverflowException("合成事件缓冲区溢出"))]);
            Check(service.Snapshot().O("sourceStatus").S("status") == "partial", "file-event interruption is visible before recovery starts");
            failWatcher = true; service.Apply("check", new(), watcherHome);
            Check(service.Snapshot().O("sourceStatus").S("status") == "partial", "a failed watcher reconnect is not hidden by the recovery scan");
            failWatcher = false; service.Apply("check", new(), watcherHome);
            Check(service.Snapshot().O("sourceStatus").S("status") == "available", "watcher interruption recovers after reconnect and rescan succeed");
        }

        string missingHome = Path.Combine(root, "missing-source-home");
        using (var service = new TaskMonitorService(Path.Combine(root, "missing-source.json"), missingHome))
        {
            service.Apply("check", new(), missingHome);
            Check(service.Snapshot().O("sourceStatus").S("status") == "unavailable", "missing sessions directory remains unavailable after a check completes");
            Directory.CreateDirectory(Path.Combine(missingHome, "sessions")); service.Apply("check", new(), missingHome);
            Check(service.Snapshot().O("sourceStatus").S("status") == "available", "a restored sessions directory is confirmed by actual listener setup and discovery");
        }
    }
    private static void ClearEndedTests(string root)
    {
        string home = Path.Combine(root, "cleanup-home"), store = Path.Combine(root, "cleanup.json"); double at = J.Now;
        string ended = CreateLog(home, "cleanup-once", "once-turn", at);
        string continuous = CreateLog(home, "cleanup-each", "each-turn", at);
        CreateLog(home, "cleanup-running", "running-turn", at);
        using (var service = new TaskMonitorService(store, home))
        {
            service.Poll(home, true);
            Check(Add(service, home, "cleanup-once", "once-turn").B("ok") && Add(service, home, "cleanup-each", "each-turn", "each").B("ok") &&
                Add(service, home, "cleanup-running", "running-turn").B("ok"), "cleanup fixtures subscribe to one-shot, continuous and running tasks");
            Append(ended, Event("task_complete", "once-turn", at + 1));
            Append(continuous, Event("task_complete", "each-turn", at + 1)); service.Poll(home, true);
            service.Apply("focus", J.Obj(("id", "cleanup-once")), home);
            string history = J.Text(service.Snapshot().A("messages"));
            Check(service.Apply("clear-ended", new(), home).B("ok"), "completed-result cleanup saves successfully");
            var state = service.Snapshot();
            Check(state.A("watches").Count == 2 && state.A("watches").Rows().All(x => x.B("active")) &&
                Watch(service, "cleanup-each").S("status") == "idle" && Watch(service, "cleanup-running").S("status") == "running",
                "cleanup preserves running and each-round subscriptions");
            Check(J.Text(state.A("messages")) == history && state.O("summary").I("unread") == 2 && state.O("settings").S("focusID") == "",
                "cleanup preserves unread messages and clears only removed focus");
        }
        using (var restored = new TaskMonitorService(store, home))
        {
            restored.Poll(home, true); var state = restored.Snapshot();
            Check(state.A("watches").Count == 2 && state.A("watches").Rows().All(x => x.B("active")) && state.A("messages").Count == 2 &&
                state.O("summary").I("unread") == 2 && state.A("watches").Rows().All(x => x.S("id") != "cleanup-once"),
                "cleanup survives restart without deleting unread history or active monitoring");
        }
    }
    private static void CancelMonitorTests(string root)
    {
        string home = Path.Combine(root, "cancel-home"), store = Path.Combine(root, "cancel.json"); double at = J.Now;
        string cancelLog = CreateLog(home, "cancel-each", "cancel-turn", at);
        string endedLog = CreateLog(home, "cancel-ended", "ended-turn", at);
        string runningLog = CreateLog(home, "cancel-other", "other-turn", at);
        using (var service = new TaskMonitorService(store, home))
        {
            service.Poll(home, true);
            Check(Add(service, home, "cancel-each", "cancel-turn", "each").B("ok") && Add(service, home, "cancel-ended", "ended-turn").B("ok") &&
                Add(service, home, "cancel-other", "other-turn").B("ok"), "cancel fixtures subscribe independently");
            Append(cancelLog, Event("task_complete", "cancel-turn", at + 1)); Append(endedLog, Event("task_complete", "ended-turn", at + 1)); service.Poll(home, true);
            service.Apply("focus", J.Obj(("id", "cancel-each")), home);
            string[] originalLogs = new[] { cancelLog, endedLog, runningLog }.Select(File.ReadAllText).ToArray();
            Check(service.Apply("stop", J.Obj(("id", "cancel-each")), home).B("ok"), "cancel shares the persisted observer-stop operation");
            var state = service.Snapshot();
            Check(state.A("watches").Count == 2 && state.A("watches").Rows().All(x => x.S("id") != "cancel-each") && Watch(service, "cancel-other").B("active"),
                "cancel removes only the identified subscription");
            Check(state.A("messages").Count == 2 && state.O("summary").I("unread") == 2 &&
                state.A("messages").Rows().Single(x => x.S("taskID") == "cancel-each").S("delivery") == "suppressed" && state.O("settings").S("focusID") == "",
                "cancel preserves unread messages while suppressing its pending notification and resetting removed focus");
            Check(new[] { cancelLog, endedLog, runningLog }.Select(File.ReadAllText).SequenceEqual(originalLogs), "cancel never writes Codex task logs");
            Check(!service.Apply("stop", J.Obj(("id", "cancel-each")), home).B("ok") && service.Snapshot().A("watches").Count == 2,
                "duplicate cancel cannot remove a different task");
            Check(service.Apply("clear-ended", new(), home).B("ok") && service.Snapshot().A("watches").Count == 1 && Watch(service, "cancel-other").B("active"),
                "cleanup after cancel preserves unrelated active monitoring");
        }
        using (var restored = new TaskMonitorService(store, home))
        {
            restored.Poll(home, true); var state = restored.Snapshot();
            Check(state.A("watches").Count == 1 && Watch(restored, "cancel-other").B("active") && state.A("messages").Count == 2 && state.O("summary").I("unread") == 2,
                "cancel and cleanup survive restart while unread history remains");
        }
    }
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "CodexUsageMonitorTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            FirstTurnTimestampPrecisionTests(root);
            string home = Path.Combine(root, "home"), storePath = Path.Combine(root, "monitor.json"); double now = J.Now + .01;
            string first = CreateLog(home, "task-a", "turn-a", now);
            string second = CreateLog(home, "task-b", "turn-b", now);
            string child = CreateLog(home, "subtask", "child-turn", now, "task-a");
            Append(Path.Combine(home, "session_index.jsonl"), J.Obj(("id", "task-a"), ("thread_name", "任务名称 · 不读取提示词")));
            using (var service = new TaskMonitorService(storePath, home))
            {
                service.Poll(home, true);
                var state = service.Snapshot();
                Check(state.A("tasks").Count == 2 && state.A("tasks").Rows().All(x => x.B("selectable")), "zero-token root tasks discovered independently of usage");
                Check(state.A("tasks").Rows().Single(x => x.S("id") == "task-a").S("title") == "任务名称 · 不读取提示词", "session-index task title");
                Check(!state.O("capabilities").B("waiting") && !state.O("capabilities").B("failure"), "unsupported lifecycle states are not advertised");
                Check(Add(service, home, "task-a", "turn-a").B("ok"), "one-shot subscription");
                Check(Add(service, home, "task-b", "turn-b", "each").B("ok"), "continuous subscription");
                state = service.Snapshot(); Check(state.O("summary").S("status") == "running" && state.O("summary").I("active") == 2, "persistent running symbol is independent of unread");
                Check(!Add(service, home, "task-a", "turn-a").B("ok"), "duplicate subscription rejected");
                Check(!Add(service, home, "subtask", "child-turn").B("ok"), "subagent cannot masquerade as root task");
                Append(child, Event("task_complete", "child-turn", now + 1)); service.Poll(home, true);
                Check(service.Snapshot().A("messages").Count == 0 && Watch(service, "task-a").S("status") == "running", "subagent completion does not complete parent");

                // Only complete newline-terminated records are consumed, even across a restartable append cursor.
                string completion = J.Text(Event("task_complete", "turn-a", now + 2));
                File.AppendAllText(first, completion[..20], new UTF8Encoding(false)); service.Poll(home, true);
                Check(Watch(service, "task-a").S("status") == "running", "partial write cannot produce a terminal event");
                File.AppendAllText(first, completion[20..] + "\n", new UTF8Encoding(false)); service.Poll(home, true);
                state = service.Snapshot(); var message = state.A("messages").Rows().Single();
                Check(message.S("delivery") == "pending" && !message.B("offline") && message.S("status") == "completed", "live explicit completion queues one notification");
                Check(!Watch(service, "task-a").B("active") && Watch(service, "task-a").S("status") == "completed", "ended once watch preserves status snapshot");
                Check(state.O("summary").S("status") == "running", "a completed task cannot hide another running task");
                service.Apply("delivery", J.Obj(("ids", new[] { message.S("id") }), ("status", "sent"), ("notificationTag", "tag-1")), home);
                service.Apply("read", J.Obj(("all", true)), home);
                Check(Watch(service, "task-a").S("status") == "completed" && service.Snapshot().O("summary").I("unread") == 0, "read state does not clear execution snapshot");
                Check(service.Snapshot().A("messages")[0].S("notificationTag") == "tag-1", "notification tag retained for removal");
                service.Apply("focus", J.Obj(("id", "task-a")), home);
                Check(service.Snapshot().O("summary").S("status") == "completed", "focus isolates persistent status from other tasks");
                service.Apply("clear-ended", new JsonObject(), home);
                Check(service.Snapshot().O("settings").S("focusID") == "" && service.Snapshot().O("summary").S("status") == "running", "clearing completed focused result returns focus to all");

                Append(second, Event("task_complete", "turn-b", now + 3)); Append(second, Event("task_started", "turn-b2", now + 4)); service.Poll(home, true);
                Check(Watch(service, "task-b").S("turnID") == "turn-b2" && Watch(service, "task-b").S("status") == "running", "continuous subscription follows next round and keeps prior message");
                service.Apply("read", J.Obj(("all", true)), home); service.Apply("clear-history", new JsonObject(), home);
                Append(second, Event("task_complete", "turn-b", now + 3)); service.Poll(home, true);
                Check(service.Snapshot().A("messages").Count == 0 && Watch(service, "task-b").S("turnID") == "turn-b2", "dedup survives clearing read history and ignores out-of-order old end");
                service.Apply("settings", J.Obj(("patch", J.Obj(("pausedUntil", -1)))), home);
                Append(second, Event("turn_aborted", "turn-b2", now + 5)); service.Poll(home, true);
                message = service.Snapshot().A("messages").Rows().Single();
                Check(message.S("status") == "interrupted" && message.S("delivery") == "suppressed" && service.Snapshot().O("capabilities").B("interrupted"), "verified abort is distinct and paused notifications retain messages");
                Check(Watch(service, "task-b").S("status") == "idle", "each subscription waits for next round");
                Check(!service.Apply("mode", J.Obj(("id", "task-b"), ("mode", "once")), home).B("ok"), "cannot monitor an idle past round as current");
                service.Apply("settings", J.Obj(("patch", J.Obj(("pausedUntil", 0)))), home);
                Check(service.Snapshot().A("messages")[0].S("delivery") == "suppressed", "resume does not replay paused history");
                Append(second, Event("task_started", "turn-b3", now + 6)); service.Poll(home, true);
                service.Apply("stop", J.Obj(("id", "task-b")), home);
                Append(second, Event("task_complete", "turn-b3", now + 7)); service.Poll(home, true);
                Check(service.Snapshot().A("messages").Count == 1 && service.Snapshot().O("summary").S("status") == "none", "stop affects only observer and preserves old messages");
                Check(File.ReadAllText(second).Contains("turn-b3", StringComparison.Ordinal), "observer never modifies Codex logs");

                // The picker sees turn-c, but both its end and another start land before Save.
                string race = CreateLog(home, "task-c", "turn-c", now + 8); service.Poll(home, true);
                Append(race, Event("task_complete", "turn-c", now + 9)); Append(race, Event("task_started", "turn-c2", now + 10));
                var result = Add(service, home, "task-c", "turn-c", "each");
                Check(result.B("ok") && result.A("items")[0].B("ended") && Watch(service, "task-c").S("turnID") == "turn-c" && !Watch(service, "task-c").B("active"), "save race records selected end without switching to new round");
                Check(service.Snapshot().A("messages").Rows().Single(x => x.S("taskID") == "task-c").S("delivery") == "suppressed", "late picker save cannot issue delayed toast");
                Check(Add(service, home, "task-c", "turn-c2").B("ok"), "explicit new-round selection works");
                long before = (long)(service.Snapshot().O("diagnostics").N("bytesRead") ?? 0); service.Poll(home, true);
                Check((service.Snapshot().O("diagnostics").N("bytesRead") ?? 0) == before, "unchanged logs use byte cursor instead of rereading history");
                Parallel.For(0, 10, _ => Check(service.Snapshot().O("summary").I("active") == 1, "published snapshot is thread safe"));
            }

            // A process restart must reconcile an offline end without replaying submitted or pending notifications.
            string third = Path.Combine(home, "sessions", "2026", "09", "12", "rollout-task-c.jsonl");
            Append(third, Event("task_complete", "turn-c2", now + 11));
            using (var restored = new TaskMonitorService(storePath, home))
            {
                restored.Poll(home, true); var message = restored.Snapshot().A("messages").Rows().Single(x => x.S("turnID") == "turn-c2");
                Check(message.B("offline") && message.S("delivery") == "suppressed" && !Watch(restored, "task-c").B("active"), "offline end is recorded once without toast");
                int count = restored.Snapshot().A("messages").Count; Append(third, Event("task_complete", "turn-c2", now + 11)); restored.Poll(home, true);
                Check(restored.Snapshot().A("messages").Count == count, "restart and duplicate append do not duplicate message");
            }

            string recoverHome = Path.Combine(root, "recovery-home"); string recoveryLog = CreateLog(recoverHome, "recover-task", "recover-turn", J.Now);
            using (var service = new TaskMonitorService(Path.Combine(root, "recover-state.json"), recoverHome))
            {
                service.Poll(recoverHome, true); Check(Add(service, recoverHome, "recover-task", "recover-turn").B("ok"), "recovery fixture subscribed");
                Append(recoveryLog, J.Obj(("type", "event_msg"), ("payload", J.Obj(("type", "task_complete"))))); service.Poll(recoverHome, true);
                Check(Watch(service, "recover-task").S("status") == "unknown" && service.Snapshot().A("messages").Count == 0, "malformed terminal event downgrades instead of completing");
                service.Poll(recoverHome, true); Check(Watch(service, "recover-task").S("status") == "unknown", "malformed state remains unknown across healthy idle reads");
                Append(recoveryLog, Event("task_started", "recover-turn", J.Now + 1)); service.Poll(recoverHome, true);
                Check(Watch(service, "recover-task").S("status") == "running", "explicit lifecycle restores confirmed state");
                string moved = recoveryLog + ".moved"; File.Move(recoveryLog, moved); service.Poll(recoverHome, true);
                Check(Watch(service, "recover-task").S("status") == "unknown" && service.Snapshot().A("messages").Count == 0, "missing log is unknown, never completed");
                File.Move(moved, recoveryLog); service.Poll(recoverHome, true);
                Check(Watch(service, "recover-task").S("status") == "running", "same-source recovery preserves subscription");
                string alternative = Path.Combine(root, "other-home"); Directory.CreateDirectory(Path.Combine(alternative, "sessions")); service.Poll(alternative, true);
                Check(Watch(service, "recover-task").S("status") == "unknown", "home switch cannot silently rebind identity");
                service.Poll(recoverHome, true); Check(Watch(service, "recover-task").S("status") == "running", "switching back recovers original watch");
            }

            string damaged = Path.Combine(root, "damaged.json"); const string broken = "{broken-original"; File.WriteAllText(damaged, broken);
            using (var service = new TaskMonitorService(damaged, home))
            {
                Check(service.Snapshot().O("recovery").B("required"), "corrupt store requests explicit recovery");
                Check(!service.Apply("settings", J.Obj(("patch", J.Obj(("sound", true)))), home).B("ok") && File.ReadAllText(damaged) == broken, "ordinary actions never overwrite corrupt store");
                var result = service.Apply("recover", new JsonObject(), home);
                Check(result.B("ok") && File.ReadAllText(result.S("backupPath")) == broken && !service.Snapshot().O("recovery").B("required"), "recovery preserves exact damaged bytes");
            }
            File.WriteAllText(damaged, broken);
            using (var service = new TaskMonitorService(damaged, home))
            {
                File.Copy(storePath, damaged, true);
                var result = service.Apply("recover", new JsonObject(), home);
                Check(result.B("ok") && service.Snapshot().A("messages").Count > 0 && result.S("backupPath").Length == 0, "external repair is adopted instead of being reset by a stale recovery action");
            }
            ClearEndedTests(root);
            CancelMonitorTests(root);
            SourceRecoveryTests(root);
            BackfillTests(root);
            Console.WriteLine("Task monitor lifecycle, persistence, notification, discovery and recovery tests passed.");
        }
        finally { Directory.Delete(root, true); }
    }
}
