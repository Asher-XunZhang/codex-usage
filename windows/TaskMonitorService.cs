using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace CodexUsage;

/// <summary>Read-only local lifecycle observer. All public entry points share one lock; the host calls them off the UI thread.</summary>
internal sealed class TaskMonitorService : IDisposable
{
    private const int TailBytes = 2 * 1024 * 1024, ReadBudget = 8 * 1024 * 1024, LineLimit = 16 * 1024 * 1024;
    private const int BackfillBudget = 2 * 1024 * 1024, BackfillChunk = 256 * 1024;
    private readonly object gate = new();
    private readonly string path;
    private readonly Func<string, IEnumerable<string>> enumerateFiles;
    private readonly Func<string, FileSystemWatcher> createWatcher;
    private readonly double launchedAt = J.Now;
    private readonly Dictionary<string, LogCursor> logs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> titles = new(StringComparer.Ordinal);
    private readonly HashSet<string> changed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> seenTerminals = new(StringComparer.Ordinal);
    private JsonObject published = new();
    private JsonArray watches = new(), messages = new();
    private JsonObject settings = Defaults();
    private FileSystemWatcher? watcher;
    private string home = "", directoryError = "", discoveryError = "", watcherError = "", persistenceError = "", recoveryBackup = "";
    private string SourceError => directoryError.Length > 0 ? directoryError : discoveryError.Length > 0 ? discoveryError : watcherError;
    private bool corrupt, disposed, needsScan = true, watcherNeedsRestart, dirty, sawComplete, sawInterrupted;
    private double scannedAt, checkedAt, polledAt, savedAt;
    private long indexOffset, bytesRead;
    private int scanCount;
    private int backfillRound, forwardRound;
    private long backfillBytes;
    private string indexPath = "";

    private sealed class LogCursor
    {
        public string Path = "", Home = "", ID = "", Parent = "", Error = "", IdentityHash = "", ParseError = "";
        public long Offset, Position;
        public long BackfillPosition = -1, BackfillCheckpoint = -1;
        public double WrittenAt;
        public bool Offline, TailSkipped, Oversize;
        public bool BackfillDone, BackfillOversize, CatchingUp;
        public readonly MemoryStream Partial = new();
        public readonly MemoryStream ReverseLine = new();
        public readonly Queue<byte> ReversePrefix = new(256);
        public JsonObject Task = new();
        public readonly Dictionary<string, JsonObject> Turns = new(StringComparer.Ordinal);
    }

    internal TaskMonitorService(string storePath, string home, Func<string, IEnumerable<string>>? enumerateFiles = null,
        Func<string, FileSystemWatcher>? createWatcher = null)
    {
        path = Path.GetFullPath(storePath);
        this.enumerateFiles = enumerateFiles ?? (root => Directory.EnumerateFiles(root, "*.jsonl",
            new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint }));
        this.createWatcher = createWatcher ?? (root => new FileSystemWatcher(root, "*.jsonl"));
        Load();
        SetHome(home);
        // A submitted/pending notification from a previous process is never replayed on startup.
        foreach (var row in messages.Rows().Where(x => x.S("delivery") == "pending"))
        { row["delivery"] = "suppressed"; row["reason"] = "应用重启后仅保留消息"; row["offline"] = true; dirty = true; }
        foreach (var row in watches.Rows().Where(x => x.B("active")))
        { row["lastConfirmedStatus"] = row.S("status"); row["status"] = "unknown"; row["sourceError"] = "正在重新核对已保存的任务状态"; }
        Publish();
    }

    private static JsonObject Defaults() => J.Obj(("delivery", "system"), ("sound", false), ("notifyCompleted", true),
        ("notifyAttention", true), ("notifyFailures", true), ("hideNames", false), ("pausedUntil", 0),
        ("defaultMode", "once"), ("retentionDays", 30), ("focusID", ""));
    private static string Safe(string value, int max = 256) => new(value.Where(c => !char.IsControl(c)).Take(max).ToArray());
    private static bool ID(string value) => value.Length is > 0 and <= 128 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static bool Terminal(string status) => status is "completed" or "interrupted" or "failed";
    private static double Timestamp(JsonNode? node, string key, double fallback)
    {
        if (node.N(key) is double number && number > 0) return number > 100_000_000_000 ? number / 1000 : number;
        return DateTimeOffset.TryParse(node.S(key), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToUnixTimeMilliseconds() / 1000d : fallback;
    }
    private static string NormalizeHome(string value) => Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    private static bool SameHome(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string MessageID(string task, string turn) => "turn-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(task + "\n" + turn))).ToLowerInvariant()[..32];

    private void Load()
    {
        if (!File.Exists(path)) return;
        try
        {
            var saved = J.Read(path, 32 * 1024 * 1024);
            if (saved.I("version") != 1 || saved["watches"] is not JsonArray ws || saved["messages"] is not JsonArray ms ||
                saved["settings"] is not JsonObject ss || saved["cursors"] is not JsonArray cs || ws.Count > 200 || ms.Count > 20000 || cs.Count > 500)
                throw new InvalidDataException("任务监控文件格式不正确");
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in ws)
            {
                if (node is not JsonObject row || !ID(row.S("id")) || !ID(row.S("turnID")) || !unique.Add(row.S("id")) ||
                    row.S("mode") is not ("once" or "each") || row["active"] is not JsonValue || row.S("home").Length == 0)
                    throw new InvalidDataException("任务监控记录无效或重复");
            }
            unique.Clear();
            foreach (var node in ms)
                if (node is not JsonObject row || row.S("id").Length == 0 || !unique.Add(row.S("id")) || !ID(row.S("taskID")) || !ID(row.S("turnID")))
                    throw new InvalidDataException("任务消息记录无效或重复");
            watches = (JsonArray)ws.DeepClone(); messages = (JsonArray)ms.DeepClone(); settings = ValidateSettings(ss, Defaults());
            foreach (var value in saved.A("seenTerminals")) if (value is JsonValue v && v.TryGetValue<string>(out string? id) && id.Length <= 128) seenTerminals.Add(id);
            foreach (var message in messages.Rows()) seenTerminals.Add(message.S("id"));
            foreach (var row in cs.Rows())
            {
                string logPath = row.S("path"), sourceHome = row.S("home"); var task = row.O("task");
                if (!ID(task.S("id")) || sourceHome.Length == 0 || !IsSessionPath(logPath, sourceHome)) continue;
                long offset = (long)(row.N("offset") ?? 0); if (offset < 0) continue;
                var log = new LogCursor { Path = logPath, Home = sourceHome, ID = task.S("id"), Parent = row.S("parent"),
                    Offset = offset, Position = offset, Task = task.Copy(), Offline = true, IdentityHash = row.S("identityHash"), ParseError = row.S("parseError") };
                log.BackfillDone = row.B("backfillDone", ID(task.S("turnID")) && task.S("status") != "unknown");
                log.BackfillPosition = log.BackfillCheckpoint = Math.Clamp((long)(row.N("backfillPosition") ?? offset), 0, offset);
                foreach (var turn in row.A("turns").Rows().TakeLast(64)) if (ID(turn.S("turnID"))) log.Turns[turn.S("turnID")] = turn.Copy();
                logs[logPath] = log;
            }
            sawComplete = saved.B("sawComplete"); sawInterrupted = saved.B("sawInterrupted");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or InvalidDataException)
        { corrupt = true; persistenceError = "任务监控文件无法读取，原文件已保留：" + e.Message; }
    }

    private static bool IsSessionPath(string file, string root)
    {
        try
        {
            string full = Path.GetFullPath(file), basePath = NormalizeHome(root) + Path.DirectorySeparatorChar;
            return full.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) && full.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) &&
                (full.StartsWith(basePath + "sessions" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                 full.StartsWith(basePath + "archived_sessions" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception) { return false; }
    }

    private void SetHome(string next)
    {
        next = NormalizeHome(next); if (SameHome(home, next)) return;
        watcher?.Dispose(); watcher = null; home = next; changed.Clear(); needsScan = true; scannedAt = 0;
        directoryError = discoveryError = watcherError = ""; watcherNeedsRestart = false; checkedAt = polledAt = 0;
        indexPath = Path.Combine(home, "session_index.jsonl"); indexOffset = 0; titles.Clear();
        foreach (var log in logs.Values.Where(x => !SameHome(x.Home, home))) log.Error = "监控绑定其他日志目录，请切回原目录";
        StartWatcher();
    }

    private void StartWatcher()
    {
        watcher?.Dispose(); watcher = null; watcherNeedsRestart = false;
        try
        {
            if (Directory.Exists(home))
            {
                watcher = createWatcher(home); watcher.IncludeSubdirectories = true;
                watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size;
                watcher.Changed += OnChanged; watcher.Created += OnChanged; watcher.Deleted += OnChanged;
                watcher.Renamed += (_, e) => { lock (gate) { if (!disposed) { changed.Add(e.FullPath); changed.Add(e.OldFullPath); needsScan = true; } } };
                watcher.Error += OnWatcherError;
                watcher.EnableRaisingEvents = true;
                watcherError = ""; needsScan = true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        { watcher?.Dispose(); watcher = null; watcherError = "文件事件监听不可用，将定期核对日志：" + e.Message; }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        lock (gate)
        {
            if (disposed || !ReferenceEquals(sender, watcher)) return;
            watcherError = "文件事件监听中断，正在重新连接并核对日志：" + e.GetException().Message;
            watcherNeedsRestart = true; needsScan = true; Publish();
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        lock (gate)
        {
            if (disposed) return;
            if (changed.Count < 4096) changed.Add(e.FullPath); else needsScan = true;
        }
    }

    public void Poll(string home, bool force = false)
    {
        lock (gate)
        {
            if (disposed) return;
            SetHome(home); double now = J.Now;
            if (!force && now - polledAt < .5) return;
            bool resumed = polledAt > 0 && now - polledAt > 20; polledAt = now;
            if (!Directory.Exists(Path.Combine(this.home, "sessions")))
            {
                directoryError = "日志目录不可用，请检查 Codex 数据目录"; needsScan = true;
                foreach (var log in logs.Values.Where(x => SameHome(x.Home, this.home))) log.Error = directoryError;
                foreach (var log in logs.Values) UpdateWatch(log);
                checkedAt = now; Persist(); Publish(); return;
            }
            directoryError = "";
            if (watcher == null || watcherNeedsRestart) StartWatcher();
            var pending = new HashSet<string>(changed, StringComparer.OrdinalIgnoreCase); changed.Clear();
            if (force || needsScan || now - scannedAt >= 60)
            {
                Discover(pending); needsScan = false; scannedAt = now;
            }
            // Active watches are always checked, even if a file event was lost. Other idle files are never reread every second.
            foreach (var log in logs.Values)
                if (SameHome(log.Home, this.home) && (log.Offline || watches.Rows().Any(x => x.B("active") && x.S("id") == log.ID))) pending.Add(log.Path);
            ReadTitles();
            int forwardBudget = ReadBudget, newFiles = 0;
            var forwardFiles = pending.OrderByDescending(file => logs.TryGetValue(file, out var known) && watches.Rows().Any(w => w.B("active") && w.S("id") == known.ID)).ToArray();
            int forwardStart = forwardFiles.Length == 0 ? 0 : forwardRound++ % forwardFiles.Length;
            for (int fileIndex = 0; fileIndex < forwardFiles.Length; fileIndex++)
            {
                string file = forwardFiles[(forwardStart + fileIndex) % forwardFiles.Length];
                if (!IsSessionPath(file, this.home)) continue;
                if (forwardBudget <= 0) { changed.Add(file); continue; }
                if (!logs.TryGetValue(file, out var log))
                {
                    if (newFiles++ >= 8) { changed.Add(file); continue; }
                    log = OpenLog(file);
                }
                if (log == null) continue;
                if (resumed) log.Offline = true;
                forwardBudget -= ReadLog(log, Math.Min(TailBytes, forwardBudget));
            }
            Backfill();
            foreach (var log in logs.Values)
            {
                if (titles.TryGetValue(log.ID, out string? title) && log.Task.S("title") != title) { log.Task["title"] = title; dirty = true; }
                UpdateWatch(log);
            }
            checkedAt = now; Cleanup(); Persist(); Publish();
        }
    }

    private void Discover(HashSet<string> pending)
    {
        scanCount++;
        try
        {
            var files = new List<FileInfo>();
            foreach (string folder in new[] { "sessions", "archived_sessions" })
            {
                string root = Path.Combine(home, folder); if (!Directory.Exists(root)) continue;
                foreach (string file in enumerateFiles(root))
                    files.Add(new FileInfo(file));
            }
            // Startup discovery reads the most recently written files; later scans only add changed/new logs.
            foreach (var file in files.OrderByDescending(x => x.LastWriteTimeUtc).Take(scannedAt == 0 ? 256 : 1024))
                if (!logs.TryGetValue(file.FullName, out var log) || file.LastWriteTimeUtc.Ticks != log.WrittenAt || file.Length != log.Position) pending.Add(file.FullName);
            foreach (var log in logs.Values.Where(x => SameHome(x.Home, home))) if (!File.Exists(log.Path)) { log.Error = "任务日志已移动或无法读取"; }
            // An idle file check cannot establish that a previously failed directory scan recovered.
            discoveryError = "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { discoveryError = "无法完整发现任务：" + e.Message; }
    }

    private LogCursor? OpenLog(string file)
    {
        if (!File.Exists(file)) return null;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var head = new MemoryStream(); int ch;
            while (head.Length < LineLimit && (ch = stream.ReadByte()) >= 0) { if (ch == '\n') break; head.WriteByte((byte)ch); }
            bytesRead += head.Length;
            var meta = J.Parse(Encoding.UTF8.GetString(head.ToArray()).TrimStart('\uFEFF')); var p = meta.O("payload");
            if (meta.S("type") != "session_meta" || !ID(p.S("id"))) return null;
            string id = p.S("id"), parent = p.S("parent_thread_id", p.O("source").O("subagent").O("thread_spawn").S("parent_thread_id"));
            var relocated = logs.Values.FirstOrDefault(x => x.ID == id && SameHome(x.Home, home) && !File.Exists(x.Path));
            if (relocated != null && relocated.IdentityHash.Length > 0 && HeaderIdentity(stream) == relocated.IdentityHash && stream.Length >= relocated.Offset)
            {
                logs.Remove(relocated.Path); relocated.Path = file; relocated.Offline = true; relocated.Error = ""; logs[file] = relocated;
                foreach (var watch in watches.Rows().Where(x => x.S("id") == id && SameHome(x.S("home"), home))) watch["path"] = file;
                dirty = true; return relocated;
            }
            var log = new LogCursor { Path = file, Home = home, ID = id, Parent = parent, Offset = stream.Position, Position = stream.Position, Offline = true,
                Task = J.Obj(("id", id), ("turnID", ""), ("title", titles.GetValueOrDefault(id, "未命名任务 · " + id[..Math.Min(8, id.Length)])),
                    ("project", Safe(p.S("cwd"), 1024)), ("status", "unknown"), ("updatedAt", Timestamp(meta, "timestamp", 0)), ("parentID", parent)) };
            log.IdentityHash = HeaderIdentity(stream);
            if (stream.Length - stream.Position > TailBytes)
            {
                stream.Seek(-TailBytes, SeekOrigin.End); while ((ch = stream.ReadByte()) >= 0 && ch != '\n') { }
                log.Offset = log.Position = stream.Position; log.TailSkipped = true;
            }
            log.BackfillPosition = log.BackfillCheckpoint = log.Offset;
            logs[file] = log; return log;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or InvalidDataException)
        { return null; }
    }

    private int ReadLog(LogCursor log, int budget)
    {
        int read = 0;
        try
        {
            using var stream = new FileStream(log.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // Subagent histories may contain inherited parent events. They are not selectable roots and are never replayed into root watches.
            if (log.Parent.Length > 0)
            { log.Offset = log.Position = stream.Length; log.WrittenAt = File.GetLastWriteTimeUtc(log.Path).Ticks; log.Offline = false; return 0; }
            string identity = HeaderIdentity(stream);
            if (log.IdentityHash.Length > 0 && identity != log.IdentityHash)
            { log.Error = "日志身份或元数据已变化，无法继续原任务监控"; log.Task["status"] = "unknown"; log.Offline = true; return 0; }
            log.IdentityHash = identity;
            if (stream.Length < log.Position)
            {
                log.Offset = log.Position = 0; log.Partial.SetLength(0); log.Oversize = false; log.Offline = true;
                log.Task["status"] = "unknown"; log.Turns.Clear(); log.Error = "任务日志已重写，正在重新核对";
                log.Task["turnID"] = ""; log.BackfillDone = false; log.BackfillPosition = log.BackfillCheckpoint = 0;
                log.Task["updatedAt"] = 0; log.ParseError = "";
                log.ReverseLine.SetLength(0); log.ReversePrefix.Clear(); log.BackfillOversize = false;
            }
            stream.Seek(log.Position, SeekOrigin.Begin); byte[] buffer = new byte[65536]; int remaining = budget, count;
            string oldError = log.Error; long previousOffset = log.Offset; log.Error = log.ParseError;
            while (remaining > 0 && (count = stream.Read(buffer, 0, Math.Min(buffer.Length, remaining))) > 0)
            {
                remaining -= count; bytesRead += count; read += count;
                for (int i = 0; i < count; i++)
                {
                    byte b = buffer[i]; log.Position++;
                    if (b == '\n')
                    {
                        if (!log.Oversize) ParseLine(log, Encoding.UTF8.GetString(log.Partial.GetBuffer(), 0, (int)log.Partial.Length));
                        else { log.ParseError = log.Error = "部分日志记录超出读取限制，状态需要核对"; log.Task["status"] = "unknown"; }
                        log.Partial.SetLength(0); log.Oversize = false; log.Offset = log.Position;
                    }
                    else if (!log.Oversize)
                    {
                        if (log.Partial.Length < LineLimit) log.Partial.WriteByte(b);
                        else { log.Oversize = true; log.Partial.SetLength(0); }
                    }
                }
            }
            log.WrittenAt = File.GetLastWriteTimeUtc(log.Path).Ticks;
            log.CatchingUp = log.Position < stream.Length;
            if (log.Position >= stream.Length && log.Partial.Length == 0) log.Offline = false;
            else changed.Add(log.Path);
            if (oldError != log.Error) dirty = true;
            if (previousOffset != log.Offset && watches.Rows().Any(x => x.B("active") && x.S("id") == log.ID)) dirty = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { log.Error = "无法读取任务日志：" + e.Message; log.Offline = true; }
        return read;
    }

    private static bool NeedsBackfill(LogCursor log) => log.Parent.Length == 0 && !log.BackfillDone && !log.CatchingUp;
    private static string StateError(LogCursor log)
    {
        if (log.Error.Length > 0) return log.Error;
        if (log.CatchingUp) return "正在核对尚未读取的日志";
        if (NeedsBackfill(log)) return "正在回溯核对本轮执行状态";
        if (log.BackfillDone && !ID(log.Task.S("turnID"))) return "该日志尚无可识别的轮次事件；出现新事件后会自动核对";
        return "";
    }

    private void Backfill()
    {
        var candidates = logs.Values.Where(x => SameHome(x.Home, home) && NeedsBackfill(x) && x.Error.Length == 0).ToArray();
        if (candidates.Length == 0) return;
        int budget = BackfillBudget, start = backfillRound++ % candidates.Length;
        for (int n = 0; n < candidates.Length * (BackfillBudget / BackfillChunk) && budget > 0; n++)
        {
            var log = candidates[(start + n) % candidates.Length];
            if (log.BackfillDone) continue;
            try
            {
                using var stream = new FileStream(log.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (HeaderIdentity(stream) != log.IdentityHash || stream.Length < log.Position)
                { changed.Add(log.Path); continue; } // Forward validation owns identity changes and truncation.
                if (log.BackfillPosition < 0) log.BackfillPosition = log.BackfillCheckpoint = log.Offset;
                int count = (int)Math.Min(Math.Min(BackfillChunk, budget), log.BackfillPosition);
                byte[] block = new byte[count]; stream.Position = log.BackfillPosition - count;
                stream.ReadExactly(block); budget -= count; bytesRead += count; backfillBytes += count;
                for (int i = count - 1; i >= 0 && !log.BackfillDone; i--)
                {
                    log.BackfillPosition--;
                    if (block[i] == '\n')
                    {
                        InspectPreviousLine(log);
                        log.BackfillCheckpoint = log.BackfillPosition;
                    }
                    else
                    {
                        if (log.ReversePrefix.Count == 256) log.ReversePrefix.Dequeue();
                        log.ReversePrefix.Enqueue(block[i]);
                        if (!log.BackfillOversize)
                        {
                            if (log.ReverseLine.Length < LineLimit) log.ReverseLine.WriteByte(block[i]);
                            else { log.BackfillOversize = true; log.ReverseLine.SetLength(0); }
                        }
                    }
                }
                if (log.BackfillPosition == 0 && !log.BackfillDone)
                { InspectPreviousLine(log); log.BackfillDone = true; log.BackfillCheckpoint = 0; }
                dirty = true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { log.Error = "回溯任务日志失败：" + e.Message; log.Offline = true; }
        }
    }

    private void InspectPreviousLine(LogCursor log)
    {
        if (!log.BackfillOversize && log.ReverseLine.Length > 0)
        {
            byte[] bytes = log.ReverseLine.ToArray(); Array.Reverse(bytes);
            string line = Encoding.UTF8.GetString(bytes);
            if (line.AsSpan(0, Math.Min(line.Length, 256)).Contains("event_msg", StringComparison.Ordinal))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line); var root = doc.RootElement;
                    if (root.TryGetProperty("type", out var kind) && kind.GetString() == "event_msg" &&
                        root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object &&
                        payload.TryGetProperty("type", out var eventKind) && eventKind.GetString() is "task_started" or "task_complete" or "turn_aborted")
                    {
                        // The newest explicit lifecycle owns the state. Never replay all older rounds or create live notifications from discovery.
                        bool offline = log.Offline; log.Offline = true;
                        ParseLine(log, line); log.Offline = offline; log.BackfillDone = true;
                    }
                }
                catch (Exception e) when (e is JsonException or InvalidOperationException)
                { log.ParseError = log.Error = "最近的事件记录无法解析，状态待确认"; log.BackfillDone = true; }
            }
        }
        else if (log.BackfillOversize)
        {
            byte[] prefix = log.ReversePrefix.ToArray(); Array.Reverse(prefix);
            if (Encoding.UTF8.GetString(prefix).Contains("event_msg", StringComparison.Ordinal))
            {
                // An unbounded lifecycle event could itself be terminal; an oversized response body is simply skipped.
                log.ParseError = log.Error = "回溯遇到超大事件记录，无法可靠核对轮次"; log.BackfillDone = true;
            }
        }
        log.ReverseLine.SetLength(0); log.ReversePrefix.Clear(); log.BackfillOversize = false;
    }

    private static string HeaderIdentity(FileStream stream)
    {
        long previous = stream.Position; stream.Position = 0;
        // Fingerprint only the metadata prefix, never an expanding short file's following lifecycle data.
        using var prefix = new MemoryStream(); int b;
        while (prefix.Length < 256 && (b = stream.ReadByte()) >= 0 && b != '\n') prefix.WriteByte((byte)b);
        stream.Position = previous;
        return Convert.ToHexString(SHA256.HashData(prefix.ToArray()));
    }

    private void ParseLine(LogCursor log, string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        // Only metadata/lifecycle records are inspected. Prompt and response bodies are never retained.
        if (!line.AsSpan(0, Math.Min(line.Length, 256)).Contains("event_msg", StringComparison.Ordinal) &&
            !line.AsSpan(0, Math.Min(line.Length, 256)).Contains("session_meta", StringComparison.Ordinal)) return;
        try
        {
            using var doc = JsonDocument.Parse(line); var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type)) return;
            if (!root.TryGetProperty("payload", out var data) || data.ValueKind != JsonValueKind.Object) return;
            string kind = type.GetString() ?? "";
            if (kind == "session_meta")
            {
                if (data.TryGetProperty("id", out var identity) && identity.GetString() != log.ID) { log.ParseError = log.Error = "日志身份已变化，无法继续原任务监控"; log.Task["status"] = "unknown"; }
                return;
            }
            if (kind != "event_msg" || !data.TryGetProperty("type", out var eventType)) return;
            string eventKind = eventType.GetString() ?? "";
            if (eventKind is not ("task_started" or "task_complete" or "turn_aborted")) return;
            if (!data.TryGetProperty("turn_id", out var turnNode) || turnNode.ValueKind != JsonValueKind.String || !ID(turnNode.GetString() ?? ""))
            { log.ParseError = log.Error = "生命周期记录缺少轮次标识，不能确认状态"; log.Task["status"] = "unknown"; return; }
            string turn = turnNode.GetString()!;
            string status = eventKind switch { "task_started" => "running", "task_complete" => "completed", _ => "interrupted" };
            if (eventKind == "turn_aborted" && (!data.TryGetProperty("reason", out var reason) || reason.GetString() != "interrupted"))
            { log.ParseError = log.Error = "发现尚不支持的中止原因，状态待确认"; log.Task["status"] = "unknown"; return; }
            if (eventKind == "task_complete") sawComplete = true;
            if (eventKind == "turn_aborted") sawInterrupted = true;
            var payload = J.Parse(data.GetRawText()); double at = Timestamp(J.Parse(root.GetRawText()), "timestamp", J.Now);
            at = Timestamp(payload, status == "running" ? "started_at" : "completed_at", at);
            var result = J.Obj(("turnID", turn), ("status", status), ("startedAt", Timestamp(payload, "started_at", log.Turns.GetValueOrDefault(turn)?.N("startedAt") ?? at)),
                ("updatedAt", at));
            // Replayed starts cannot overwrite a known terminal result for the same turn.
            if (log.Turns.TryGetValue(turn, out var old) && Terminal(old.S("status")) && status == "running") return;
            if (Terminal(status) && payload.N("duration_ms") is double duration && duration >= 0) result["durationMs"] = duration;
            log.Turns[turn] = result;
            log.BackfillDone = true;
            log.ReverseLine.SetLength(0); log.ReversePrefix.Clear(); log.BackfillOversize = false;
            if (log.Turns.Count > 64) foreach (string key in log.Turns.OrderBy(x => x.Value.N("updatedAt") ?? 0).Take(log.Turns.Count - 64).Select(x => x.Key).ToArray()) log.Turns.Remove(key);
            // An old turn's terminal event arriving after a newer start does not replace current execution.
            if (log.Task.S("turnID") == turn || at >= (log.Task.N("updatedAt") ?? 0))
            {
                foreach (var item in result) log.Task[item.Key] = item.Value?.DeepClone();
                log.ParseError = log.Error = "";
            }
            var watch = watches.Rows().FirstOrDefault(x => x.B("active") && x.S("id") == log.ID && SameHome(x.S("home"), log.Home));
            if (watch != null && (watch.S("turnID") == turn || watch.S("mode") == "each" && at >= (watch.N("subscribedAt") ?? 0)))
            {
                if (status == "running")
                {
                    if (watch.S("mode") == "each" || watch.S("turnID") == turn) CopyResult(watch, log, result);
                }
                else Finish(watch, log, result, log.Offline || at < launchedAt);
            }
            dirty = true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or InvalidDataException)
        { log.ParseError = log.Error = "部分生命周期记录无法解析，状态待确认"; log.Task["status"] = "unknown"; }
    }

    private void ReadTitles()
    {
        if (!File.Exists(indexPath)) return;
        try
        {
            using var stream = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < indexOffset) indexOffset = 0;
            if (indexOffset == 0 && stream.Length > TailBytes) { stream.Seek(-TailBytes, SeekOrigin.End); int ch; while ((ch = stream.ReadByte()) >= 0 && ch != '\n') { } indexOffset = stream.Position; }
            stream.Seek(indexOffset, SeekOrigin.Begin); using var data = new MemoryStream(); int remaining = TailBytes, next;
            while (remaining-- > 0 && (next = stream.ReadByte()) >= 0)
            {
                if (next == '\n')
                {
                    try { var row = J.Parse(Encoding.UTF8.GetString(data.ToArray())); if (ID(row.S("id")) && row.S("thread_name").Length > 0) titles[row.S("id")] = Safe(row.S("thread_name")); }
                    catch (Exception e) when (e is JsonException or InvalidOperationException or InvalidDataException) { }
                    data.SetLength(0); indexOffset = stream.Position;
                }
                else if (data.Length < 65536) data.WriteByte((byte)next);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void CopyResult(JsonObject watch, LogCursor log, JsonObject result)
    {
        foreach (var item in result) watch[item.Key] = item.Value?.DeepClone();
        watch["title"] = log.Task.S("title"); watch["project"] = log.Task.S("project"); watch["sourceError"] = "";
    }

    private void Finish(JsonObject watch, LogCursor log, JsonObject result, bool offline)
    {
        string turn = result.S("turnID"), status = result.S("status");
        string id = MessageID(log.ID, turn);
        if (seenTerminals.Add(id))
        {
            string text = status == "completed" ? "所选任务的本轮已结束" : status == "interrupted" ? "所选任务的本轮已中断" : "所选任务的本轮执行失败";
            string reason = offline ? "离线期间发生，仅补记消息" : Suppression(status);
            messages.Add(J.Obj(("id", id), ("taskID", log.ID), ("turnID", turn), ("title", log.Task.S("title")), ("project", log.Task.S("project")),
                ("status", status), ("text", text), ("createdAt", result.N("updatedAt") ?? J.Now), ("receivedAt", J.Now), ("read", false),
                ("offline", offline), ("delivery", reason.Length == 0 ? "pending" : "suppressed"), ("reason", reason), ("durationMs", result.N("durationMs"))));
        }
        // A duplicate older terminal result must not rewind a continuous subscription's new round.
        if (watch.S("turnID") == turn || (result.N("updatedAt") ?? 0) >= (watch.N("updatedAt") ?? 0))
        {
            CopyResult(watch, log, result); watch["lastResultStatus"] = status;
            if (watch.S("mode") == "once") watch["active"] = false;
            else watch["status"] = "idle";
        }
        dirty = true;
    }

    private string Suppression(string status)
    {
        if (settings.S("delivery") == "markers") return "仅应用内标记";
        double paused = settings.N("pausedUntil") ?? 0;
        if (paused < 0 || paused > J.Now) return "用户已暂停任务通知";
        if (status == "completed" && !settings.B("notifyCompleted")) return "本轮结束通知已关闭";
        if (status is "failed" or "interrupted" && !settings.B("notifyFailures")) return "失败与中断通知已关闭";
        if (status == "waiting" && !settings.B("notifyAttention")) return "需要处理通知已关闭";
        return "";
    }

    private void UpdateWatch(LogCursor log)
    {
        foreach (var watch in watches.Rows().Where(x => x.S("id") == log.ID && SameHome(x.S("home"), log.Home)))
        {
            if (watch.S("title") != log.Task.S("title")) { watch["title"] = log.Task.S("title"); dirty = true; }
            if (!watch.B("active")) continue;
            string error = !SameHome(log.Home, home) ? "监控绑定其他日志目录，请切回原目录" : StateError(log);
            if (error.Length > 0)
            {
                if (watch.S("status") != "unknown") watch["lastConfirmedStatus"] = watch.S("status");
                watch["status"] = "unknown"; watch["sourceError"] = error; dirty = true;
            }
            else if (log.Turns.TryGetValue(watch.S("turnID"), out var turn))
            {
                if (Terminal(turn.S("status"))) Finish(watch, log, turn, log.Offline || (turn.N("updatedAt") ?? 0) < launchedAt);
                else if (watch.S("status") != turn.S("status") || watch.S("sourceError").Length > 0) { CopyResult(watch, log, turn); dirty = true; }
            }
        }
    }

    public JsonObject Snapshot() => Volatile.Read(ref published).Copy();
    private void Publish() => Volatile.Write(ref published, BuildSnapshot());
    private JsonObject BuildSnapshot()
    {
        lock (gate)
        {
            string sourceError = SourceError;
            var tasks = logs.Values.Where(x => SameHome(x.Home, home) && x.Parent.Length == 0).GroupBy(x => x.ID).Select(g => g.OrderByDescending(x => x.Task.N("updatedAt") ?? 0).First())
                .OrderByDescending(x => x.Task.N("updatedAt") ?? 0).Take(256).Select(log =>
                {
                    var row = log.Task.Copy(); string error = StateError(log);
                    if (sourceError.Length > 0 && !Directory.Exists(Path.Combine(home, "sessions"))) error = sourceError;
                    if (error.Length > 0) { row["lastConfirmedStatus"] = row.S("status"); row["status"] = "unknown"; }
                    row["sourceError"] = error; row["selectable"] = error.Length == 0 && row.S("status") == "running" && ID(row.S("turnID"));
                    row["monitored"] = watches.Rows().Any(x => x.S("id") == log.ID && x.B("active"));
                    return row;
                });
            var viewed = watches.Rows().Where(x => settings.S("focusID").Length == 0 || x.S("id") == settings.S("focusID")).ToArray();
            string[] priority = ["waiting", "failed", "unknown", "running", "idle", "interrupted", "completed"];
            string status = viewed.Length == 0 ? "none" : priority.FirstOrDefault(p => viewed.Any(x => x.S("status") == p)) ?? "unknown";
            var summary = J.Obj(("active", watches.Rows().Count(x => x.B("active"))), ("running", watches.Rows().Count(x => x.B("active") && x.S("status") == "running")),
                ("attention", watches.Rows().Count(x => x.B("active") && x.S("status") == "waiting")), ("unread", messages.Rows().Count(x => !x.B("read"))),
                ("completed", watches.Rows().Count(x => x.S("status") == "completed")), ("idle", watches.Rows().Count(x => x.B("active") && x.S("status") == "idle")),
                ("unknown", watches.Rows().Count(x => x.B("active") && x.S("status") == "unknown")), ("status", status), ("focusID", settings.S("focusID")));
            bool localError = logs.Values.Any(x => SameHome(x.Home, home) && x.Parent.Length == 0 && StateError(x).Length > 0);
            string sourceStatus = !Directory.Exists(Path.Combine(home, "sessions")) ? "unavailable" : checkedAt == 0 || sourceError.Length > 0 || localError ? "partial" : "available";
            return J.Obj(("tasks", J.Array(tasks)), ("watches", watches), ("messages", J.Array(messages.Rows().OrderByDescending(x => x.N("createdAt") ?? 0))),
                ("settings", settings), ("summary", summary), ("capabilities", J.Obj(("completed", sawComplete), ("waiting", false), ("failure", false), ("interrupted", sawInterrupted))),
                ("sourceStatus", J.Obj(("status", sourceStatus), ("text", sourceError.Length > 0 ? sourceError : checkedAt == 0 ? "正在核对本机任务状态" : localError ? "部分任务状态待确认" : "本机生命周期日志 · 不依赖用量刷新"), ("home", home), ("checkedAt", checkedAt))),
                ("error", persistenceError), ("recovery", J.Obj(("required", corrupt), ("path", path), ("backupPath", recoveryBackup))),
                ("diagnostics", J.Obj(("discovered", logs.Count), ("bytesRead", bytesRead), ("backfillBytes", backfillBytes),
                    ("backfillPending", logs.Values.Count(x => SameHome(x.Home, home) && NeedsBackfill(x))),
                    ("discoveryPending", changed.Count(x => IsSessionPath(x, home) && !logs.ContainsKey(x))),
                    ("directoryScans", scanCount), ("selectionLimit", 100), ("discoveryLimit", 256))));
        }
    }

    public JsonObject Apply(string operation, JsonObject payload, string home)
    {
        lock (gate)
        {
            if (disposed) return J.Obj(("ok", false), ("error", "监控服务已关闭"));
            try
            {
                if (operation == "recover") { var recovered = Recover(); Publish(); return recovered; }
                if (corrupt) throw new InvalidDataException(persistenceError);
                SetHome(home);
                JsonObject response = J.Obj(("ok", true), ("message", "已更新"));
                switch (operation)
                {
                    case "discover": case "check": Poll(home, true); response["message"] = SourceError.Length > 0 ? SourceError : "已核对本机任务日志"; break;
                    case "add": response = Add(payload); break;
                    case "stop":
                        {
                            string id = payload.S("id"); var row = watches.Rows().FirstOrDefault(x => x.S("id") == id) ?? throw new InvalidDataException("该任务已不在监控列表中");
                            watches.Remove(row); if (settings.S("focusID") == id) settings["focusID"] = "";
                            foreach (var message in messages.Rows().Where(x => x.S("taskID") == id && x.S("delivery") == "pending")) { message["delivery"] = "suppressed"; message["reason"] = "已停止此任务的提醒"; }
                            response["message"] = "已停止提醒，Codex 继续执行"; dirty = true; break;
                        }
                    case "mode":
                        {
                            var row = watches.Rows().FirstOrDefault(x => x.S("id") == payload.S("id") && x.B("active")) ?? throw new InvalidDataException("只能调整活动监控的提醒策略");
                            string mode = payload.S("mode"); if (mode is not ("once" or "each")) throw new InvalidDataException("无效提醒策略");
                            if (mode == "once" && row.S("status") == "idle") throw new InvalidDataException("正在等待新一轮，请在新一轮开始后选择本轮提醒");
                            row["mode"] = mode; dirty = true; break;
                        }
                    case "read":
                        {
                            var ids = payload.A("ids").Select(x => x?.GetValue<string>() ?? "").ToHashSet(StringComparer.Ordinal);
                            foreach (var row in messages.Rows().Where(x => payload.B("all") || ids.Contains(x.S("id"))))
                            { row["read"] = true; if (row.S("delivery") == "pending") { row["delivery"] = "suppressed"; row["reason"] = "已在应用内阅读"; } }
                            dirty = true; break;
                        }
                    case "settings":
                        var adjusted = ValidateSettings(payload.O("patch"), settings);
                        if (adjusted.S("focusID").Length > 0 && !watches.Rows().Any(x => x.S("id") == adjusted.S("focusID"))) throw new InvalidDataException("关注对象不在监控列表中");
                        settings = adjusted; SuppressPending(); dirty = true; break;
                    case "focus":
                        {
                            string id = payload.S("id"); if (id.Length > 0 && !watches.Rows().Any(x => x.S("id") == id)) throw new InvalidDataException("关注对象不在监控列表中");
                            settings["focusID"] = id; dirty = true; break;
                        }
                    case "clear-ended":
                        foreach (var row in watches.Rows().Where(x => !x.B("active")).ToArray()) watches.Remove(row);
                        if (!watches.Rows().Any(x => x.S("id") == settings.S("focusID"))) settings["focusID"] = "";
                        dirty = true; break;
                    case "clear-history":
                        foreach (var row in messages.Rows().Where(x => x.B("read") && x.S("status") != "waiting").ToArray()) messages.Remove(row);
                        dirty = true; break;
                    case "delivery":
                        {
                            string status = payload.S("status"); if (status is not ("sent" or "suppressed" or "failed")) throw new InvalidDataException("无效通知投递状态");
                            var ids = payload.A("ids").Select(x => x?.GetValue<string>() ?? "").ToHashSet(StringComparer.Ordinal);
                            foreach (var row in messages.Rows().Where(x => ids.Contains(x.S("id")) && x.S("delivery") == "pending"))
                            { row["delivery"] = status; row["reason"] = Safe(payload.S("reason")); row["deliveredAt"] = J.Now;
                                if (payload.S("notificationTag").Length > 0) row["notificationTag"] = Safe(payload.S("notificationTag"), 64); }
                            dirty = true; break;
                        }
                    default: throw new InvalidDataException("未知任务监控操作");
                }
                Persist(true);
                Publish();
                if (persistenceError.Length > 0) return J.Obj(("ok", false), ("error", persistenceError), ("message", "状态尚未可靠保存，请检查存储后重试"));
                return response;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException or InvalidDataException)
            { Publish(); return J.Obj(("ok", false), ("error", e.Message), ("message", e.Message)); }
        }
    }

    private JsonObject Add(JsonObject payload)
    {
        string mode = payload.S("mode", settings.S("defaultMode", "once")); if (mode is not ("once" or "each")) throw new InvalidDataException("无效提醒策略");
        var selections = payload.A("selections").Rows().ToArray(); if (selections.Length is < 1 or > 100) throw new InvalidDataException("请选 1 至 100 个任务");
        Poll(home, true); var items = new JsonArray(); int success = 0;
        foreach (var selection in selections)
        {
            string id = selection.S("id"), turn = selection.S("turnID"); var item = J.Obj(("id", id), ("ok", false)); items.Add(item);
            var log = logs.Values.Where(x => x.ID == id && SameHome(x.Home, home) && x.Parent.Length == 0).OrderByDescending(x => x.Task.N("updatedAt") ?? 0).FirstOrDefault();
            if (!ID(id) || !ID(turn) || log == null) { item["error"] = "任务来源不可用，请重新发现任务"; continue; }
            if (StateError(log).Length > 0) { item["error"] = StateError(log); continue; }
            if (watches.Rows().Any(x => x.S("id") == id && x.B("active"))) { item["error"] = "该任务已开启提醒"; continue; }
            if (watches.Count >= 100 && !watches.Rows().Any(x => x.S("id") == id)) { item["error"] = "最多保留 100 项监控，请先清除已结束结果"; continue; }
            if (!log.Turns.TryGetValue(turn, out var result)) { item["error"] = "无法核实所选轮次，请重新选择"; continue; }
            bool ended = Terminal(result.S("status"));
            if (!ended && (log.Task.S("turnID") != turn || result.S("status") != "running")) { item["error"] = "所选轮次已变化，请重新选择当前轮次"; continue; }
            foreach (var old in watches.Rows().Where(x => x.S("id") == id).ToArray()) watches.Remove(old);
            var watch = J.Obj(("id", id), ("home", home), ("path", log.Path), ("mode", ended ? "once" : mode), ("active", true), ("subscribedAt", J.Now));
            CopyResult(watch, log, result); watches.Add(watch);
            if (ended)
            {
                Finish(watch, log, result, true); item["ended"] = true; item["messageID"] = MessageID(id, turn);
                item["message"] = "所选本轮已结束，已保存结果；未替换为新一轮";
            }
            item["ok"] = true; success++; dirty = true;
        }
        return J.Obj(("ok", success > 0), ("partial", success > 0 && success < selections.Length), ("items", items),
            ("message", success > 0 ? "已处理 " + success + " 项提醒" : "未能开启提醒，请查看各项原因"));
    }

    private static JsonObject ValidateSettings(JsonObject patch, JsonObject baseline)
    {
        var result = baseline.Copy();
        foreach (var entry in patch)
        {
            if (!result.ContainsKey(entry.Key)) continue;
            switch (entry.Key)
            {
                case "delivery": if (patch.S(entry.Key) is not ("system" or "markers")) throw new InvalidDataException("无效通知方式"); break;
                case "defaultMode": if (patch.S(entry.Key) is not ("once" or "each")) throw new InvalidDataException("无效默认提醒策略"); break;
                case "pausedUntil": if (patch.N(entry.Key) is not double pause || pause < -1) throw new InvalidDataException("无效暂停时间"); break;
                case "retentionDays": if (patch.I(entry.Key) is < 1 or > 365) throw new InvalidDataException("消息保留时间应为 1 至 365 天"); break;
                case "focusID": if (patch.S(entry.Key).Length > 0 && !ID(patch.S(entry.Key))) throw new InvalidDataException("无效关注对象"); break;
                default: if (entry.Value is not JsonValue value || !value.TryGetValue<bool>(out _)) throw new InvalidDataException("无效提醒设置"); break;
            }
            result[entry.Key] = entry.Value?.DeepClone();
        }
        return result;
    }

    private void SuppressPending()
    {
        foreach (var row in messages.Rows().Where(x => x.S("delivery") == "pending"))
        { string reason = Suppression(row.S("status")); if (reason.Length > 0) { row["delivery"] = "suppressed"; row["reason"] = reason; } }
    }
    private void Cleanup()
    {
        double cutoff = J.Now - settings.I("retentionDays", 30) * 86400;
        foreach (var row in messages.Rows().Where(x => x.B("read") && (x.N("createdAt") ?? 0) < cutoff && x.S("status") != "waiting").ToArray())
        { messages.Remove(row); dirty = true; }
        if (logs.Count > 512)
            foreach (var log in logs.Values.Where(x => !watches.Rows().Any(w => w.S("id") == x.ID)).OrderBy(x => x.Task.N("updatedAt") ?? 0).Take(logs.Count - 512).ToArray())
            { logs.Remove(log.Path); log.Partial.Dispose(); log.ReverseLine.Dispose(); }
    }

    private JsonObject Durable()
    {
        var cursors = logs.Values.Where(log => log.Parent.Length == 0)
            .OrderByDescending(log => watches.Rows().Any(w => w.S("id") == log.ID)).ThenByDescending(log => log.Task.N("updatedAt") ?? 0).Take(500).Select(log =>
            J.Obj(("path", log.Path), ("home", log.Home), ("offset", log.Offset), ("identityHash", log.IdentityHash), ("parseError", log.ParseError), ("parent", log.Parent),
                ("backfillDone", log.BackfillDone), ("backfillPosition", log.BackfillCheckpoint), ("task", log.Task), ("turns", J.Array(log.Turns.Values))));
        return J.Obj(("version", 1), ("settings", settings), ("watches", watches), ("messages", messages), ("cursors", J.Array(cursors)),
            ("seenTerminals", seenTerminals.ToArray()), ("sawComplete", sawComplete), ("sawInterrupted", sawInterrupted), ("savedAt", J.Now));
    }
    private void Persist(bool force = false)
    {
        if (!dirty || corrupt || !force && J.Now - savedAt < 1) return;
        try { J.Write(path, Durable()); dirty = false; persistenceError = ""; savedAt = J.Now; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { persistenceError = "任务监控状态无法保存：" + e.Message; }
    }
    private JsonObject Recover()
    {
        if (!corrupt) return J.Obj(("ok", true), ("message", "监控文件正常，无需恢复"));
        if (File.Exists(path))
        {
            corrupt = false; persistenceError = ""; Load();
            if (!corrupt)
            {
                foreach (var row in messages.Rows().Where(x => x.S("delivery") == "pending"))
                { row["delivery"] = "suppressed"; row["reason"] = "重新载入后仅保留消息"; row["offline"] = true; }
                dirty = true; Persist(true);
                return J.Obj(("ok", persistenceError.Length == 0), ("message", "已采用外部修复后的监控文件"), ("error", persistenceError));
            }
        }
        string backup = path + "." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "." + Guid.NewGuid().ToString("N") + ".backup";
        // Preserve the exact damaged bytes atomically before replacing with a fresh store.
        string replacement = path + "." + Guid.NewGuid().ToString("N") + ".recovery";
        try
        {
            J.Write(replacement, J.Obj(("version", 1), ("settings", Defaults()), ("watches", new JsonArray()), ("messages", new JsonArray()), ("cursors", new JsonArray())));
            if (File.Exists(path)) File.Replace(replacement, path, backup); else File.Move(replacement, path);
            watches = new(); messages = new(); seenTerminals.Clear(); settings = Defaults(); corrupt = false; persistenceError = ""; recoveryBackup = backup; dirty = false;
            return J.Obj(("ok", true), ("message", "原文件已备份，任务提醒需要重新选择"), ("backupPath", backup));
        }
        finally { if (File.Exists(replacement)) File.Delete(replacement); }
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; Persist(true); disposed = true; watcher?.Dispose(); watcher = null; foreach (var log in logs.Values) { log.Partial.Dispose(); log.ReverseLine.Dispose(); } }
    }
}
