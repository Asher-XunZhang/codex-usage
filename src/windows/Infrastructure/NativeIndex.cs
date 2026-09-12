using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace CodexUsage;

// Windows supplies winsqlite3.dll. All connections are read-only, bounded and short-lived.
internal sealed class NativeDb : IDisposable
{
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string name, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_errmsg(IntPtr db);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_text(IntPtr stmt, int i, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int bytes, IntPtr destructor);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_double(IntPtr stmt, int i, double value);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_count(IntPtr stmt);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_name(IntPtr stmt, int i);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_type(IntPtr stmt, int i);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern long sqlite3_column_int64(IntPtr stmt, int i);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern double sqlite3_column_double(IntPtr stmt, int i);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr stmt, int i);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_bytes(IntPtr stmt, int i);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Progress(IntPtr data);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void sqlite3_progress_handler(IntPtr db, int instructions, Progress? callback, IntPtr data);
    private IntPtr db;
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private readonly Progress progress;
    public NativeDb(string path)
    {
        progress = _ => watch.ElapsedMilliseconds > 5000 ? 1 : 0;
        if (!File.Exists(path)) throw new FileNotFoundException("等待首次索引", path);
        if (sqlite3_open_v2(path, out db, 1 | 0x8000, IntPtr.Zero) != 0) { var message = Error; Dispose(); throw new IOException(message); }
        try
        {
            sqlite3_busy_timeout(db, 1500); sqlite3_progress_handler(db, 10000, progress, IntPtr.Zero);
            Query("PRAGMA query_only=ON"); Query("PRAGMA cache_size=-128"); Query("PRAGMA mmap_size=0"); Query("PRAGMA temp_store=FILE"); Query("BEGIN");
        }
        catch { Dispose(); throw; }
    }
    private string Error => Marshal.PtrToStringUTF8(sqlite3_errmsg(db)) ?? "SQLite 查询失败";
    public List<JsonObject> Query(string sql, params object[] values)
    {
        IntPtr stmt = IntPtr.Zero;
        try
        {
            if (sqlite3_prepare_v2(db, sql, -1, out stmt, IntPtr.Zero) != 0) throw new IOException(Error);
            for (int i = 0; i < values.Length; i++)
            {
                int code = values[i] is string str ? sqlite3_bind_text(stmt, i + 1, str, -1, new IntPtr(-1)) : sqlite3_bind_double(stmt, i + 1, Convert.ToDouble(values[i]));
                if (code != 0) throw new IOException(Error);
            }
            var result = new List<JsonObject>(); int step;
            while ((step = sqlite3_step(stmt)) == 100)
            {
                if (result.Count >= 20000) throw new IOException("索引结果超出上限");
                var row = new JsonObject();
                for (int i = 0; i < sqlite3_column_count(stmt); i++)
                {
                    string name = Marshal.PtrToStringUTF8(sqlite3_column_name(stmt, i))!;
                    switch (sqlite3_column_type(stmt, i))
                    {
                        case 1: row[name] = sqlite3_column_int64(stmt, i); break;
                        case 2: row[name] = sqlite3_column_double(stmt, i); break;
                        case 3:
                            if (sqlite3_column_bytes(stmt, i) > 8_388_608) throw new IOException("索引文本超出上限");
                            row[name] = Marshal.PtrToStringUTF8(sqlite3_column_text(stmt, i)); break;
                        default: row[name] = null; break;
                    }
                }
                result.Add(row);
            }
            if (step != 101) throw new IOException(Error);
            return result;
        }
        finally { if (stmt != IntPtr.Zero) sqlite3_finalize(stmt); }
    }
    public void Dispose() { if (db != IntPtr.Zero) { sqlite3_progress_handler(db, 0, null, IntPtr.Zero); sqlite3_close(db); db = IntPtr.Zero; } }
}

internal static class NativeIndex
{
    private static readonly string[] Fields = ["input_tokens", "output_tokens", "total_tokens", "cached_input_tokens", "reasoning_output_tokens", "cache_write_input_tokens"];
    public static JsonObject Read(string cache, JsonObject filters, JsonArray requests, bool choices = false, double? now = null, bool includeEmptyBudgetMetadata = true)
    {
        ValidateRequests(requests);
        using var db = new NativeDb(cache);
        string raw = db.Query("SELECT value FROM kv WHERE key='snapshot'").FirstOrDefault()?.S("value") ?? throw new IOException("等待首次索引");
        var snapshot = J.Parse(raw);
        var tasks = snapshot.A("tasks").Rows().ToDictionary(x => x.S("id"));
        var models = snapshot.A("models").Select(x => x?.GetValue<string>() ?? "").ToHashSet();
        string days = filters.S("days", "1"), model = filters.S("model", "all"), task = filters.S("task", "all");
        if (!new[] { "1", "7", "30", "90", "all" }.Contains(days)) throw new ArgumentException("统计时间范围无效");
        bool reset = model != "all" && !models.Contains(model) || task != "all" && !tasks.ContainsKey(task);
        if (reset) { model = "all"; task = "all"; }
        DateTime today = DateTimeOffset.FromUnixTimeMilliseconds((long)((now ?? J.Now) * 1000)).ToOffset(TimeSpan.FromHours(8)).Date;
        JsonObject Summary(string range, string m, string t)
        {
            string start = range == "all" ? "" : today.AddDays(1 - int.Parse(range)).ToString("yyyy-MM-dd");
            string sums = string.Join(",", Fields.Select(f => $"CASE WHEN COUNT({f})=COUNT(*) THEN COALESCE(SUM({f}),0) END AS {f}"));
            var row = db.Query($"SELECT {sums},COALESCE(SUM(requests),0) requests,COUNT(DISTINCT task) active_tasks FROM daily WHERE date>=?1 AND date<=?2 AND (?3='all' OR model=?3) AND (?4='all' OR task=?4)", start, today.ToString("yyyy-MM-dd"), m, t).Single();
            row["noncached_input_tokens"] = row["input_tokens"] is JsonValue inputValue && inputValue.TryGetValue<long>(out long input) && row["cached_input_tokens"] is JsonValue cachedValue && cachedValue.TryGetValue<long>(out long cached) ? input - cached : null;
            row["cache_hit_rate"] = row.N("input_tokens") is double denominator && denominator != 0 && row.N("cached_input_tokens") is double numerator ? numerator / denominator * 100 : null;
            if (!snapshot.B("has_rows")) foreach (string key in row.Select(x => x.Key).ToArray()) row[key] = null;
            return J.Obj(("summary", row), ("meta", J.Obj(("generated_at", snapshot.S("generated_at")), ("time_zone", "UTC+08:00"), ("loading", false), ("refresh_seconds", filters.I("refresh_seconds")), ("refresh_error", null))), ("filters", J.Obj(("selected_task", tasks.GetValueOrDefault(t)))));
        }
        var result = J.Obj(("today", Summary("1", "all", "all")), ("filtered", Summary(days, model, task)), ("filter_reset", reset));
        if (choices)
        {
            string start = days == "all" ? "" : today.AddDays(1 - int.Parse(days)).ToString("yyyy-MM-dd");
            var eligible = db.Query("SELECT task,MAX(timestamp) last_used FROM daily WHERE date>=?1 AND date<=?2 AND (?3='all' OR model=?3) GROUP BY task ORDER BY last_used DESC,task DESC", start, today.ToString("yyyy-MM-dd"), model);
            var selected = eligible.Where(x => tasks.ContainsKey(x.S("task"))).Select(x => { var row = tasks[x.S("task")].Copy(); row["last_used"] = x["last_used"]?.DeepClone(); return row; }).ToList();
            if (task != "all" && tasks.ContainsKey(task) && selected.All(x => x.S("id") != task)) selected.Add(tasks[task]);
            result["choices"] = J.Obj(("models", snapshot.A("models")), ("tasks", J.Array(selected)));
        }
        // The resident UI needs no raw-usage scan when no token or money budget is active.
        if (requests.Count > 0 || includeEmptyBudgetMetadata)
            result["budgetResult"] = Budgets(db, snapshot, requests, models, tasks.Keys.ToHashSet());
        return result;
    }

    private static void ValidateRequests(JsonArray requests)
    {
        if (requests.Count > 50 || Encoding.UTF8.GetByteCount(J.Text(J.Obj(("requests", requests)))) > 65536) throw new ArgumentException("预算请求超出上限");
        var ids = new HashSet<string>();
        foreach (var node in requests)
        {
            if (node is not JsonObject r || r.Count != 7 || !new[] { "id", "revision", "periodID", "start", "end", "model", "task" }.All(r.ContainsKey)) throw new ArgumentException("预算请求字段无效");
            foreach (var p in new[] { ("id", 128), ("periodID", 256), ("model", 512), ("task", 512) }) { string value = r.S(p.Item1); if (value.Length == 0 || value.Contains('\0') || Encoding.UTF8.GetByteCount(value) > p.Item2) throw new ArgumentException("预算请求标识无效"); }
            if (!ids.Add(r.S("id")) || r.N("revision") is not double revision || revision < 0 || revision != Math.Truncate(revision) || revision >= 9223372036854775808d || r.N("start") is not double start || r.N("end") is not double end || start < -62135596800 || end > 253402300799 || end <= start) throw new ArgumentException("预算请求范围或版本无效");
        }
    }

    private static JsonObject Budgets(NativeDb db, JsonObject snapshot, JsonArray requests, HashSet<string> models, HashSet<string> tasks)
    {
        if (requests.Count > 50) throw new ArgumentException("最多查询50项预算");
        var coverage = snapshot.O("coverage");
        double invalidTime = db.Query("SELECT COUNT(*) n FROM usage WHERE julianday(timestamp) IS NULL")[0].N("n") ?? 0;
        bool complete = snapshot.B("has_rows") && snapshot.A("issues").Count == 0 && snapshot.I("excluded_legacy_threads") == 0 && coverage.I("partial_legacy_threads") == 0 && coverage.A("cumulative_gaps").Count == 0 && invalidTime == 0;
        double generated = DateTimeOffset.TryParse(snapshot.S("generated_at"), out var time) ? time.ToUnixTimeMilliseconds() / 1000d : 0;
        var results = new JsonArray();
        string sums = string.Join(",", Fields.Select(f => $"CASE WHEN COUNT({f})=COUNT(*) THEN SUM({f}) END AS {f},COUNT({f}) known_{f},COALESCE(SUM({f}),0) lower_bound_{f}"));
        int count = 0;
        foreach (var request in requests.Rows())
        {
            string model = request.S("model"), task = request.S("task");
            bool valid = (model == "all" || models.Contains(model)) && (task == "all" || tasks.Contains(task));
            var row = request.Copy(); row["scope_valid"] = valid; row["coverage_complete"] = valid && complete;
            row["issues"] = valid && complete ? new JsonArray() : new JsonArray(valid ? "索引存在覆盖缺失，本区间仅能确认已记录用量" : "所选范围不存在");
            var rows = valid ? db.Query($"SELECT model,COUNT(*) requests,{sums} FROM usage WHERE julianday(timestamp)>=julianday(?1,'unixepoch') AND julianday(timestamp)<julianday(?2,'unixepoch') AND (?3='all' OR model=?3) AND (?4='all' OR task=?4) GROUP BY model ORDER BY model LIMIT 1025", request.N("start") ?? 0, request.N("end") ?? 0, model, task) : new();
            count += rows.Count; if (count > 1024) throw new IOException("预算模型结果超出上限");
            row["rows"] = J.Array(rows); results.Add(row);
        }
        var issues = snapshot.A("issues");
        var details = J.Obj(("issue_count", issues.Count), ("excluded_legacy_threads", snapshot.I("excluded_legacy_threads")), ("partial_legacy_threads", coverage.I("partial_legacy_threads")), ("cumulative_gap_count", coverage.A("cumulative_gaps").Count), ("invalid_timestamp_rows", invalidTime), ("issues_truncated", issues.Count > 32));
        var boundedIssues = new JsonArray(issues.Take(32).Select(x => JsonValue.Create((x?.GetValue<string>() ?? "")[..Math.Min(512, (x?.GetValue<string>() ?? "").Length)]) as JsonNode).ToArray());
        return J.Obj(("generated_at", generated), ("has_rows", snapshot.B("has_rows")), ("coverage_complete", complete), ("coverage", details), ("issues", boundedIssues), ("results", results));
    }
}
