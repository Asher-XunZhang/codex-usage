using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace CodexUsage;

/// <summary>Host-owned budget rules, calendar queries and durable notification ledger.</summary>
internal sealed class BudgetStore
{
    public JsonArray Rules { get; private set; } = new();
    public JsonArray Summaries { get; private set; } = new();
    public string? PersistenceError { get; private set; }
    public JsonObject Recovery => J.Obj(("required", unreadable), ("path", path), ("backupPath", recoveryBackup));
    public JsonArray PendingAlerts => J.Array(ledger.Rows().Where(x => !x.B("acknowledged")));
    public JsonArray Events => J.Array(ledger.Rows().Where(x => !x.B("suppressed")).OrderByDescending(x => x.N("createdAt") ?? 0).Take(20));
    private JsonObject runtime = new();
    private JsonArray ledger = new();
    private readonly string path;
    private bool unreadable;
    private string? recoveryBackup;
    private static readonly string[] Deferred = { "kind", "tokenMetric", "model", "task", "currency", "fx", "prices", "period", "windowMinutes", "quotaCondition" };
    private static InvalidDataException Invalid(string text) => new(text);
    private static bool Equal(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(a, b);
    private static int? Integer(JsonNode? value) => J.Number(value) is double d && d == Math.Truncate(d) && d > int.MinValue && d < int.MaxValue ? (int)d : null;
    private static bool Reached(double remaining, double threshold) => remaining <= threshold + 8 * 2.2204460492503131e-16 * Math.Max(100, Math.Abs(threshold));
    private static string Display(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
    private static string Stable(double value) => value.ToString("0.0################", CultureInfo.InvariantCulture);
    private static void Put(JsonObject target, string key, JsonNode? value) => target[key] = value?.DeepClone();
    private JsonObject State(string id) { if (runtime[id] is not JsonObject) runtime[id] = new JsonObject(); return runtime.O(id); }

    public BudgetStore(string path)
    {
        this.path = Path.GetFullPath(path);
        if (!File.Exists(path)) return;
        try
        {
            var saved = J.Read(path);
            if (saved.I("version") != 1 || saved["rules"] is not JsonArray savedRules || savedRules.Count > 50 ||
                saved["runtime"] is not JsonObject savedRuntime || saved["ledger"] is not JsonArray savedLedger || savedLedger.Count > 1000 ||
                saved["summaries"] is not JsonArray savedSummaries || savedSummaries.Count > 50 ||
                savedRules.Any(x => x is not JsonObject) || savedLedger.Any(x => x is not JsonObject) || savedSummaries.Any(x => x is not JsonObject))
                throw Invalid("预算文件格式不正确");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var canonicalRules = new JsonArray();
            foreach (var row in savedRules.Rows())
            {
                string id = row.S("id"), source = row.S("source");
                if (id.Length == 0 || !ids.Add(id) || source.Length == 0 || row.I("revision") <= 0) throw Invalid("预算文件包含重复或无效规则");
                var canonical = Normalize(row, source); canonical["revision"] = row.I("revision");
                foreach (string key in new[] { "createdAt", "modifiedAt" }) if (row.N(key) is double n) canonical[key] = n;
                canonicalRules.Add(canonical);
            }
            var checkedRuntime = new JsonObject();
            foreach (var row in canonicalRules.Rows())
            {
                string id = row.S("id"); if (savedRuntime[id] is not JsonObject state) continue;
                var clean = new JsonObject();
                if (state["deferred"] is JsonObject fields && state.N("deferredUntil") is double until)
                {
                    var merged = row.Copy();
                    foreach (string key in Deferred.Append("amount")) if (fields.ContainsKey(key)) Put(merged, key, fields[key]);
                    var normalized = Normalize(merged, row.S("source")); var validated = new JsonObject();
                    foreach (string key in Deferred.Append("amount")) if (fields.ContainsKey(key)) Put(validated, key, normalized[key]);
                    clean["deferred"] = validated; clean["deferredUntil"] = until;
                }
                if (state.N("pausedUntil") is double pausedUntil && state["pausePeriod"] is JsonValue && state.S("pausePeriod").Length <= 256)
                {
                    clean["pausedUntil"] = pausedUntil; clean["pausePeriod"] = state.S("pausePeriod");
                }
                checkedRuntime[id] = clean;
            }
            Rules = canonicalRules; runtime = checkedRuntime; ledger = (JsonArray)savedLedger.DeepClone();
            Summaries = J.Array(savedSummaries.Rows().Where(row => ids.Contains(row.S("id")) && Integer(row["revision"]) != null &&
                row["periodID"] is JsonValue && row["kind"] is JsonValue && row["name"] is JsonValue &&
                row.ContainsKey("used") && row.ContainsKey("remaining") && row.ContainsKey("amount")));
        }
        catch (Exception e) { unreadable = true; PersistenceError = "预算配置无法读取；为保护原文件，未覆盖：" + e.Message; }
    }

    public void Apply(string action, JsonObject payload, string source, double? now = null)
    {
        if (action == "recover") { Recover(payload); return; }
        if (unreadable) throw Invalid(PersistenceError ?? "预算文件无法读取");
        double time = now ?? J.Now; var before = Snapshot();
        try
        {
            switch (action)
            {
                case "save":
                case "upsert":
                    {
                        var input = payload["rule"] as JsonObject ?? payload;
                        string id = input.S("id", Guid.NewGuid().ToString());
                        var old = Rules.Rows().FirstOrDefault(x => x.S("id") == id);
                        if (old != null)
                        {
                            CheckRevision(payload, input, old);
                            if (old.S("source") != source) throw Invalid("预算绑定的数据目录不同；请在原目录编辑或另建规则");
                        }
                        else
                        {
                            if (Rules.Count >= 50) throw Invalid("最多保存 50 条预算");
                            if (Integer(payload["expectedRevision"] ?? input["revision"]) is int revision && revision != 0) throw Conflict();
                        }
                        var normalized = Normalize(input, source); normalized["id"] = id;
                        normalized["revision"] = (old?.I("revision") ?? 0) + 1;
                        normalized["createdAt"] = old?.N("createdAt") ?? time; normalized["modifiedAt"] = time;
                        if (old != null)
                        {
                            var effectiveOld = Effective(old, time);
                            bool changed = Deferred.Any(key => !Equal(normalized[key], effectiveOld[key]));
                            var period = Period(effectiveOld, time);
                            if (changed && !payload.B("recalculateCurrent") && period != null && time < period.End)
                            {
                                var fields = new JsonObject(); foreach (string key in Deferred) Put(fields, key, effectiveOld[key]);
                                if (!Equal(normalized["kind"], effectiveOld["kind"]) || !Equal(normalized["currency"], effectiveOld["currency"])) Put(fields, "amount", effectiveOld["amount"]);
                                var state = State(id); state["deferred"] = fields; state["deferredUntil"] = period.End;
                            }
                            else { State(id).Remove("deferred"); State(id).Remove("deferredUntil"); }
                            Rules[Rules.IndexOf(old)] = normalized;
                        }
                        else Rules.Add(normalized);
                        Summaries = J.Array(Summaries.Rows().Where(x => x.S("id") != id));
                        break;
                    }
                case "delete":
                    {
                        var row = Locate(payload); CheckRevision(payload, payload, row); string id = row.S("id");
                        Rules.Remove(row); runtime.Remove(id); Summaries = J.Array(Summaries.Rows().Where(x => x.S("id") != id));
                        ledger = J.Array(ledger.Rows().Where(x => x.S("ruleID") != id)); break;
                    }
                case "enable":
                case "disable":
                case "toggle":
                    {
                        var row = Locate(payload); CheckRevision(payload, payload, row);
                        row["enabled"] = action == "toggle" ? !row.B("enabled", true) : action == "enable";
                        row["revision"] = row.I("revision") + 1;
                        Summaries = J.Array(Summaries.Rows().Where(x => x.S("id") != row.S("id"))); break;
                    }
                case "pause":
                    {
                        var row = Locate(payload); var period = Period(Effective(row, time), time) ?? throw Invalid("预算周期无效");
                        double seconds = payload.N("durationSeconds") ?? payload.N("seconds") ?? 1800;
                        if (!(seconds > 0 && seconds <= 86400)) throw Invalid("暂停时间必须在 1 秒到 24 小时之间");
                        var state = State(row.S("id")); state["pausePeriod"] = period.Id;
                        state["pausedUntil"] = payload.S("mode") == "cycle" || payload.B("period") ? period.End : Math.Min(time + seconds, period.End); break;
                    }
                case "resume": { var state = State(Locate(payload).S("id")); state.Remove("pausedUntil"); state.Remove("pausePeriod"); break; }
                case "acknowledge":
                    {
                        var ids = payload.A("ids").Select(x => x?.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
                        foreach (var row in ledger.Rows()) if (ids.Contains(row.S("id"))) row["acknowledged"] = true;
                        break;
                    }
                default: throw Invalid("未知预算操作");
            }
            TrimLedger(); Persist();
        }
        catch (Exception e) { Restore(before); if (e is not InvalidDataException) PersistenceError = e.Message; throw; }
    }

    private void Recover(JsonObject payload)
    {
        if (!unreadable || !payload.B("confirm")) throw Invalid("请确认备份无法读取的预算配置后重新开始；正常配置不能通过此操作清空");
        if (!File.Exists(path)) throw Invalid("原预算文件已变化，请重新启动后检查；未创建或覆盖文件");
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
        string backup = path + ".unreadable-" + stamp + ".bak", prepared = path + ".recover-" + stamp + ".tmp";
        try
        {
            J.Write(prepared, J.Obj(("version", 1), ("rules", new JsonArray()), ("runtime", new JsonObject()), ("ledger", new JsonArray()), ("summaries", new JsonArray())));
            // One filesystem replacement preserves the exact file being replaced, including a
            // concurrent external correction. Never clear the store after a failed backup.
            File.Replace(prepared, path, backup);
            Rules = new(); runtime = new(); ledger = new(); Summaries = new();
            unreadable = false; PersistenceError = null; recoveryBackup = backup;
        }
        finally { if (File.Exists(prepared)) File.Delete(prepared); }
    }

    public JsonArray Requests(string source, double? now = null)
    {
        double time = now ?? J.Now; var result = new JsonArray();
        foreach (var original in Rules.Rows())
        {
            var rule = Effective(original, time); var period = Period(rule, time);
            if (!rule.B("enabled", true) || rule.S("source") != source || rule.S("kind") == "quota" || period == null || time < period.Start) continue;
            result.Add(J.Obj(("id", rule.S("id")), ("revision", rule.I("revision")), ("periodID", period.Id),
                ("start", period.Start), ("end", period.End), ("model", rule.S("model")), ("task", rule.S("task"))));
        }
        return result;
    }

    public JsonArray Evaluate(JsonObject? result, JsonObject? quota, string source, double? now = null)
    {
        if (unreadable) return new();
        double time = now ?? J.Now; var before = Snapshot(); var next = new JsonArray();
        double? generated = result?.N("generated_at"); var results = result.A("results");
        foreach (var original in Rules.Rows())
        {
            var rule = Effective(original, time); var interval = Period(rule, time); if (interval == null) continue;
            string id = rule.S("id"); var state = runtime.O(id);
            var old = Summaries.Rows().FirstOrDefault(x => x.S("id") == id && x.S("periodID") == interval.Id && x.I("revision") == rule.I("revision"));
            var summary = old?.Copy() ?? BaseSummary(rule, interval);
            summary["scheduledChange"] = (state.N("deferredUntil") ?? 0) > time;
            if (summary.B("scheduledChange"))
            {
                var currentFields = new JsonObject(); var nextFields = new JsonObject();
                foreach (string key in Deferred.Append("amount")) { Put(currentFields, key, rule[key]); Put(nextFields, key, original[key]); }
                summary["pendingChange"] = J.Obj(("effectiveAt", state.N("deferredUntil")), ("current", currentFields), ("next", nextFields));
            }
            else summary.Remove("pendingChange");
            double pausedUntil = state.N("pausedUntil") ?? 0;
            bool paused = state.S("pausePeriod") == interval.Id && time < pausedUntil;
            summary["paused"] = paused; summary["pausedUntil"] = paused ? JsonValue.Create(pausedUntil) : null;
            bool enabled = rule.B("enabled", true), sourceMatches = rule.S("source") == source;
            if (!sourceMatches) Invalidate(summary, "source_invalid", "数据目录已切换；预算仍绑定原目录");
            else if (!enabled) { summary["status"] = "disabled"; summary["message"] = "预算已停用"; }
            else if (time < interval.Start) Invalidate(summary, "scheduled", "尚未开始");
            else if (rule.S("kind") == "quota")
            {
                if (quota != null) EvaluateQuota(rule, quota, summary, time);
                else if (summary.N("updatedAt") is double updated && time - updated > 180) Invalidate(summary, "unknown", "官方额度已过期，等待更新");
            }
            else if (generated is double g && g <= time + 5 && g >= interval.Start && g >= (summary.N("updatedAt") ?? double.MinValue) &&
                results.Rows().FirstOrDefault(x => Matches(x, rule, interval)) is JsonObject row) EvaluateUsage(rule, result!, row, g, summary);
            if (time >= interval.End) { summary["status"] = "ended"; summary["message"] = "预算已结束"; }
            if (enabled && sourceMatches && time >= interval.Start && time < interval.End && !paused) CreateAlerts(rule, interval, summary, time);
            next.Add(summary);
        }
        Summaries = next;
        var eligible = Summaries.Rows().Where(x => new[] { "warning", "exhausted", "exceeded" }.Contains(x.S("status")) && !x.B("paused"))
            .Select(x => x.S("id") + "|" + x.S("periodID")).ToHashSet(StringComparer.Ordinal);
        TrimLedger();
        try
        {
            if (!Equal(before, Snapshot())) Persist();
            return J.Array(PendingAlerts.Rows().Where(alert =>
            {
                if (!eligible.Contains(alert.S("ruleID") + "|" + alert.S("periodID"))) return false;
                var summary = Summaries.Rows().FirstOrDefault(x => x.S("id") == alert.S("ruleID"));
                return summary != null && summary.N("remainingPercent") is double remaining && alert.N("threshold") is double threshold && Reached(remaining, threshold) &&
                    alert.S("alertPeriodID", alert.S("periodID")) == summary.S("alertPeriodID", summary.S("periodID"));
            }));
        }
        catch (Exception e) { Restore(before); PersistenceError = "预算状态未能保存，暂未发送提醒：" + e.Message; return new(); }
    }

    internal sealed record Interval(double Start, double End)
    {
        public string Id => Start.ToString("F3", CultureInfo.InvariantCulture) + ":" + End.ToString("F3", CultureInfo.InvariantCulture);
    }
    internal static string LocalIanaZone => TimeZoneInfo.Local.HasIanaId ? TimeZoneInfo.Local.Id :
        TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var id) ? id : "UTC";
    internal static TimeZoneInfo Zone(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw Invalid("请选择有效的周期和 IANA 时区");
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(name);
            if (name != "UTC" && !zone.HasIanaId && !TimeZoneInfo.TryConvertIanaIdToWindowsId(name, out _)) throw Invalid("请选择有效的周期和 IANA 时区");
            return zone;
        }
        catch (TimeZoneNotFoundException) { throw Invalid("请选择有效的周期和 IANA 时区"); }
        catch (InvalidTimeZoneException) { throw Invalid("请选择有效的周期和 IANA 时区"); }
    }
    // Foundation's nextTime/first policy: a missing wall time advances to the first valid
    // minute; a repeated wall time chooses the larger offset (the first instant).
    internal static double WallTime(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        int limit = 0; while (zone.IsInvalidTime(local) && limit++ < 2880) local = local.AddMinutes(1);
        if (zone.IsInvalidTime(local)) throw Invalid("无法解析所选时区的周期边界");
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUnixTimeMilliseconds() / 1000d;
    }
    private static Interval? Period(JsonObject rule, double now)
    {
        try
        {
            var spec = rule.O("period"); string type = spec.S("type"); var zone = Zone(spec.S("timezone"));
            if (type == "once") return new(spec.N("start")!.Value, spec.N("end")!.Value);
            if (type == "interval")
            {
                double anchor = spec.N("start")!.Value, seconds = spec.N("seconds")!.Value;
                double start = anchor + Math.Max(0, Math.Floor((now - anchor) / seconds)) * seconds; return new(start, start + seconds);
            }
            var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds((long)(now * 1000)), zone).DateTime;
            double Boundary(DateTime date) => WallTime(date.Date.AddHours(spec.I("hour")).AddMinutes(spec.I("minute")), zone);
            if (type is "day" or "week")
            {
                int days = type == "day" ? 1 : 7;
                int offset = type == "day" ? 0 : ((int)local.DayOfWeek + 1 - spec.I("weekday", 2) + 7) % 7;
                var anchor = local.Date.AddDays(-offset); double start = Boundary(anchor);
                if (start > now) { anchor = anchor.AddDays(-days); start = Boundary(anchor); }
                return new(start, Boundary(anchor.AddDays(days)));
            }
            if (type == "month")
            {
                var month = new DateTime(local.Year, local.Month, 1);
                double MonthBoundary(DateTime first) => Boundary(first.AddDays(Math.Min(spec.I("day", 1), DateTime.DaysInMonth(first.Year, first.Month)) - 1));
                double start = MonthBoundary(month); if (start > now) { month = month.AddMonths(-1); start = MonthBoundary(month); }
                return new(start, MonthBoundary(month.AddMonths(1)));
            }
            return null;
        }
        catch (ArgumentException) { return null; }
        catch (InvalidDataException) { return null; }
    }
    private JsonObject Effective(JsonObject rule, double now)
    {
        var state = runtime.O(rule.S("id"));
        if ((state.N("deferredUntil") ?? 0) <= now || state["deferred"] is not JsonObject fields) return rule;
        var result = rule.Copy(); foreach (var field in fields) Put(result, field.Key, field.Value); return result;
    }

    private static JsonObject Normalize(JsonObject input, string source)
    {
        if (source.Length == 0 || source.Length > 4096) throw Invalid("预算需要绑定有效数据目录");
        if (input.ContainsKey("id") && (input.S("id").Length == 0 || input.S("id").Length > 128)) throw Invalid("预算标识无效");
        string kind = input.S("kind", "token"), name = input.S("name", "新预算").Trim();
        if (!new[] { "token", "money", "quota" }.Contains(kind)) throw Invalid("未知预算口径");
        if (name.Length == 0 || name.Length > 100) throw Invalid("预算名称需要 1–100 个字符");
        if (input.N("amount") is not double amount || (kind == "quota" ? amount < 0 : amount <= 0) || amount > 1e18) throw Invalid("预算额度必须为大于零的有限数字");
        string metric = input.S("tokenMetric", "total"), model = input.S("model", "all"), task = input.S("task", "all");
        if (!new[] { "total", "noncached", "output" }.Contains(metric)) throw Invalid("未知 Token 统计口径");
        if (model.Length == 0 || model.Length > 512 || task.Length == 0 || task.Length > 512) throw Invalid("模型或任务范围无效");
        string currency = input.S("currency", "USD"); double fx = input.N("fx") ?? 1;
        if (!new[] { "USD", "CNY" }.Contains(currency) || fx <= 0 || fx > 1e6) throw Invalid("币种或固定汇率无效");
        var prices = input.A("prices"); if (prices.Count > 100) throw Invalid("单条预算最多设置 100 个模型价格");
        var normalizedPrices = new JsonArray(); var models = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in prices)
        {
            if (entry is not JsonObject price) throw Invalid("价格需指定唯一的模型");
            string pm = price.S("model");
            if (pm.Length == 0 || pm == "all" || pm.Length > 512 || !models.Add(pm)) throw Invalid("价格需指定唯一的模型");
            var normalized = J.Obj(("model", pm));
            foreach (string key in new[] { "input", "cachedInput", "output" })
            {
                if (price[key] == null) continue;
                if (price.N(key) is not double value || value < 0 || value > 1e9) throw Invalid("已填写的价格需为非负的每百万 Token 美元单价；留空表示未知");
                normalized[key] = value;
            }
            normalizedPrices.Add(normalized);
        }
        string condition = input.S("quotaCondition", "floor"); int window = input.I("windowMinutes", 10080);
        if (kind == "quota")
        {
            if (condition != "floor") throw Invalid("期间消耗暂不可用：官方接口尚无稳定账号与窗口身份，无法可靠比较跨快照消耗");
            if (amount > 100 || window <= 0 || window > 525600 || model != "all" || task != "all") throw Invalid("官方提醒需选择有效窗口、0–100% 下限，且不能按模型或任务筛选");
        }
        var raw = input["thresholds"] as JsonArray ?? new JsonArray(20, 10, 0);
        var thresholds = raw.Select(J.Number).ToArray();
        if (thresholds.Length == 0 || thresholds.Length > 10 || thresholds.Any(x => x is not double n || n < 0 || n > 100) || thresholds.Distinct().Count() != thresholds.Length)
            throw Invalid("提醒节点需要 1–10 个不重复的 0–100% 数值");
        var originalSpec = input.O("period"); string type = originalSpec.S("type", "day"), tz = originalSpec.S("timezone", LocalIanaZone);
        if (!new[] { "day", "week", "month", "once", "interval" }.Contains(type)) throw Invalid("请选择有效的周期和 IANA 时区");
        Zone(tz);
        int hour = originalSpec.I("hour"), minute = originalSpec.I("minute"), weekday = originalSpec.I("weekday", 2), day = originalSpec.I("day", 1);
        if (hour < 0 || hour > 23 || minute < 0 || minute > 59 || weekday < 1 || weekday > 7 || day < 1 || day > 31) throw Invalid("周期重置日期或时刻无效");
        var spec = J.Obj(("type", type), ("timezone", tz), ("hour", hour), ("minute", minute), ("weekday", weekday), ("day", day));
        if (type is "once" or "interval")
        {
            if (originalSpec.N("start") is not double start || Math.Abs(start) >= 253402214400) throw Invalid("请输入有效开始时间");
            spec["start"] = start;
            if (type == "once")
            {
                if (originalSpec.N("end") is not double end || end <= start || Math.Abs(end) >= 253402214400) throw Invalid("结束时间必须晚于开始时间");
                spec["end"] = end;
            }
            else
            {
                if (originalSpec.N("seconds") is not double seconds || seconds < 60 || seconds > 315576000) throw Invalid("重复间隔需为 1 分钟到 10 年");
                spec["seconds"] = seconds;
            }
        }
        return J.Obj(("id", input.S("id", Guid.NewGuid().ToString())), ("revision", input.I("revision")), ("name", name),
            ("kind", kind), ("amount", amount), ("tokenMetric", metric), ("model", model), ("task", task), ("currency", currency),
            ("fx", fx), ("prices", normalizedPrices), ("period", spec), ("thresholds", thresholds.OrderByDescending(x => x).ToArray()),
            ("enabled", input.B("enabled", true)), ("source", source), ("windowMinutes", window), ("quotaCondition", condition));
    }

    private static JsonObject BaseSummary(JsonObject rule, Interval period)
    {
        var result = J.Obj(("amount", rule.S("kind") == "quota" ? 100 : rule.N("amount")), ("periodID", period.Id), ("start", period.Start), ("end", period.End),
            ("periodStart", period.Start), ("periodEnd", period.End), ("quotaFloor", rule.N("amount")), ("used", null), ("remaining", null),
            ("remainingPercent", null), ("remainingFraction", null), ("overage", null), ("updatedAt", null), ("paused", false), ("pausedUntil", null));
        foreach (string key in new[] { "id", "revision", "name", "kind", "currency", "model", "task", "tokenMetric", "period", "source", "windowMinutes" }) Put(result, key, rule[key]);
        Invalidate(result, "unknown", "等待本周期数据"); return result;
    }
    private static bool Matches(JsonObject row, JsonObject rule, Interval period) => row.S("id") == rule.S("id") && row.I("revision") == rule.I("revision") &&
        row.S("periodID") == period.Id && row.N("start") == period.Start && row.N("end") == period.End && row.S("model") == rule.S("model") && row.S("task") == rule.S("task");
    private static void EvaluateUsage(JsonObject rule, JsonObject root, JsonObject row, double generated, JsonObject summary)
    {
        if (!row.B("scope_valid")) { Invalidate(summary, "scope_invalid", "所选模型或任务不存在；未扩大统计范围"); return; }
        if (!root.B("has_rows")) { Invalidate(summary, "unknown", "尚无可确认的用量记录"); return; }
        if (row["rows"] is not JsonArray rows) { Invalidate(summary, "unknown", "用量结果不完整"); return; }
        bool complete = root.B("coverage_complete", true) && root.A("issues").Count == 0 && row.A("issues").Count == 0;
        var issues = new JsonArray(); var issueKeys = new HashSet<string>(StringComparer.Ordinal);
        void Issue(string type, string model, string category, string message)
        {
            if (issues.Count < 200 && issueKeys.Add(type + "|" + model + "|" + category))
                issues.Add(J.Obj(("type", type), ("model", model), ("category", category), ("message", message)));
        }
        if (!complete) Issue("coverage", "", "records", "本期部分记录未完整读取；请刷新用量并检查数据目录中的记录");
        summary["issues"] = issues; summary.Remove("issueCode");
        double used = 0; string metric = rule.S("tokenMetric", "total"), kind = rule.S("kind", "token");
        foreach (var entry in rows)
        {
            if (entry is not JsonObject usage || usage.N("requests") is not double requests || requests < 0) { complete = false; Issue("usage", "", "requests", "有记录缺少有效请求数量"); continue; }
            (double value, bool known) Amount(string key)
            {
                double? value = usage.N(key), known = usage.N("known_" + key);
                double lower = value ?? usage.N("lower_bound_" + key) ?? 0;
                bool available = value != null && (known == null || known == requests) && lower >= 0;
                if (!available) Issue("usage", usage.S("model"), key, "缺少完整的" + UsageCategory(key) + "记录");
                return (Math.Max(0, lower), available);
            }
            if (kind == "token")
            {
                if (metric is "total" or "output") { var value = Amount(metric == "total" ? "total_tokens" : "output_tokens"); used += value.value; complete &= value.known; }
                else
                {
                    var input = Amount("input_tokens"); var output = Amount("output_tokens"); var cached = Amount("cached_input_tokens");
                    used += output.value + (input.known && cached.known && cached.value <= input.value ? input.value - cached.value : 0);
                    complete &= input.known && output.known && cached.known && cached.value <= input.value;
                }
            }
            else
            {
                var price = rule.A("prices").Rows().FirstOrDefault(x => x.S("model") == usage.S("model"));
                if (price == null) { complete = false; Issue("price", usage.S("model"), "model", "尚未设置模型单价"); continue; }
                var input = Amount("input_tokens"); var output = Amount("output_tokens"); var cached = Amount("cached_input_tokens"); var write = Amount("cache_write_input_tokens");
                double? ip = price.N("input"), cp = price.N("cachedInput"), op = price.N("output");
                bool splitKnown = input.known && cached.known && write.known && cached.value <= input.value && write.value == 0;
                double noncached = Math.Max(0, input.value - cached.value);
                if (splitKnown && noncached > 0 && ip == null) Issue("price", usage.S("model"), "input", "缺少非缓存输入单价");
                if (splitKnown && cached.value > 0 && cp == null) Issue("price", usage.S("model"), "cachedInput", "缺少缓存输入单价");
                if (output.value > 0 && op == null) Issue("price", usage.S("model"), "output", "缺少输出单价");
                if (input.known && cached.known && cached.value > input.value) Issue("usage", usage.S("model"), "cached_input_tokens", "缓存输入大于总输入，暂无法确认分类");
                if (write.known && write.value > 0) Issue("usage", usage.S("model"), "cache_write_input_tokens", "存在缓存写入用量，当前价格口径无法完整估价");
                bool inputPriced = splitKnown && (noncached == 0 || ip != null) && (cached.value == 0 || cp != null);
                double inputCost = splitKnown ? noncached * (ip ?? 0) + cached.value * (cp ?? 0) : 0;
                used += (inputCost + output.value * (op ?? 0)) / 1_000_000;
                complete &= inputPriced && output.known && (output.value == 0 || op != null);
            }
        }
        if (kind == "money" && rule.S("currency") == "CNY") used *= rule.N("fx")!.Value;
        if (!double.IsFinite(used)) { Invalidate(summary, "unknown", "用量数值超出可计算范围"); return; }
        double amount = rule.N("amount")!.Value, remaining = Math.Max(0, amount - used), ratio = (amount - used) / amount * 100;
        summary["used"] = used; summary["updatedAt"] = generated; summary["overage"] = Math.Max(0, used - amount);
        summary["coverage"] = complete ? "complete" : "partial"; summary["dataStatus"] = complete ? "updated" : "partial"; summary["estimated"] = kind == "money";
        if (complete || used >= amount)
        {
            summary["remaining"] = remaining; summary["remainingPercent"] = Math.Max(0, ratio); summary["remainingFraction"] = Math.Max(0, ratio / 100);
            double threshold = rule.A("thresholds").Select(J.Number).Max() ?? 20;
            summary["status"] = used > amount ? "exceeded" : used == amount ? "exhausted" : Reached(ratio, threshold) ? "warning" : "healthy";
            summary["reason"] = complete ? "" : "仅已知用量已达到预算，实际消耗可能更高";
            summary["message"] = used > amount ? "预算已超出" : used == amount ? "预算已用尽" : "预算剩余 " + Display(Math.Max(0, ratio)) + "%";
        }
        else
        {
            summary["remaining"] = null; summary["remainingPercent"] = null; summary["remainingFraction"] = null; summary["status"] = "partial";
            summary["reason"] = kind == "money" ? "部分价格、缓存分类或用量缺失；剩余无法确认" : "部分用量或缓存分类缺失；剩余无法确认";
            Put(summary, "message", summary["reason"]);
        }
    }
    private static string UsageCategory(string key) => key switch
    {
        "input_tokens" => "输入", "output_tokens" => "输出", "cached_input_tokens" => "缓存输入", "cache_write_input_tokens" => "缓存写入", _ => "Token 用量"
    };
    internal static int[] AvailableQuotaWindows(JsonObject quota) => quota.A("windows").Rows().Select(x => x.I("duration_minutes")).Where(x => x > 0).Distinct().Order().ToArray();
    internal static string QuotaWindowName(int minutes) => minutes == 10080 ? "每周 · 10080 分钟" : minutes % 1440 == 0 ? (minutes / 1440) + " 天 · " + minutes + " 分钟" : minutes % 60 == 0 ? (minutes / 60) + " 小时 · " + minutes + " 分钟" : minutes + " 分钟";
    private static void EvaluateQuota(JsonObject rule, JsonObject quota, JsonObject summary, double now)
    {
        summary.Remove("issueCode"); summary.Remove("issues");
        summary["availableWindows"] = new JsonArray(AvailableQuotaWindows(quota).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
        var window = quota.A("windows").Rows().FirstOrDefault(x => x.I("duration_minutes") == rule.I("windowMinutes"));
        if (quota.N("updated_at") is not double updated || updated > now + 5 || now - updated > 180 || quota.B("stale") || quota["error"] != null)
        {
            Invalidate(summary, "unknown", "官方额度暂不可用或已过期，请刷新官方额度"); return;
        }
        if (window == null)
        {
            Invalidate(summary, "unknown", "当前账号未提供“" + QuotaWindowName(rule.I("windowMinutes")) + "”额度窗口，请修改窗口选择");
            summary["issueCode"] = "window_invalid"; return;
        }
        if (window.N("remaining") is not double remaining || remaining < 0 || remaining > 100) { Invalidate(summary, "unknown", "所选窗口未返回有效余量，请刷新官方额度"); return; }
        if (updated < (summary.N("updatedAt") ?? double.MinValue)) return;
        summary["remaining"] = remaining; summary["remainingPercent"] = remaining; summary["remainingFraction"] = remaining / 100;
        summary["used"] = 100 - remaining; summary["overage"] = 0; summary["updatedAt"] = updated; Put(summary, "resetsAt", window["resets_at"]);
        string monitoring = summary.S("periodID");
        summary["alertPeriodID"] = window.N("resets_at") is double reset ? monitoring + ":quota:" + rule.I("windowMinutes") + ":" + Stable(reset) : monitoring;
        summary["coverage"] = "complete"; summary["dataStatus"] = "updated"; summary["reason"] = "";
        summary["status"] = remaining == 0 ? "exhausted" : Reached(remaining, rule.N("amount")!.Value) ? "warning" : "healthy";
        summary["message"] = "官方额度剩余 " + Display(remaining) + "%";
    }
    private void CreateAlerts(JsonObject rule, Interval period, JsonObject summary, double now)
    {
        if (!new[] { "warning", "exhausted", "exceeded" }.Contains(summary.S("status")) || summary.N("remainingPercent") is not double remaining) return;
        string id = rule.S("id"), alertPeriod = summary.S("alertPeriodID", period.Id);
        var thresholds = rule.S("kind") == "quota" ? new[] { rule.N("amount")!.Value } : rule.A("thresholds").Select(x => J.Number(x)!.Value).ToArray();
        var crossed = thresholds.Where(t => Reached(remaining, t)).Order().ToArray(); if (crossed.Length == 0) return;
        double severity = crossed[0];
        foreach (double threshold in crossed)
        {
            string alertId = "budget:" + id + ":" + alertPeriod + ":" + Stable(threshold);
            var old = ledger.Rows().FirstOrDefault(x => x.S("id") == alertId);
            if (old != null)
            {
                if (!old.B("acknowledged") && threshold == severity) foreach (string key in new[] { "used", "remaining", "remainingPercent", "message", "reason" }) Put(old, key, summary[key]);
                continue;
            }
            if (threshold == severity) foreach (var entry in ledger.Rows().Where(x => x.S("ruleID") == id && x.S("alertPeriodID") == alertPeriod)) entry["acknowledged"] = true;
            ledger.Add(J.Obj(("id", alertId), ("ruleID", id), ("periodID", period.Id), ("alertPeriodID", alertPeriod), ("threshold", threshold),
                ("createdAt", now), ("acknowledged", threshold != severity), ("suppressed", threshold != severity), ("name", rule.S("name")),
                ("kind", rule.S("kind")), ("currency", rule.S("currency")), ("amount", summary.N("amount")), ("used", summary.N("used")),
                ("remaining", summary.N("remaining")), ("remainingPercent", remaining), ("periodEnd", period.End),
                ("message", summary.S("message", "预算提醒")), ("reason", summary.S("reason"))));
        }
    }
    private static void Invalidate(JsonObject summary, string status, string reason)
    {
        summary.Remove("issues"); summary.Remove("issueCode");
        summary["status"] = status; summary["reason"] = reason; summary["message"] = reason;
        summary["coverage"] = "unknown"; summary["dataStatus"] = "unknown";
        summary["remaining"] = null; summary["remainingPercent"] = null; summary["remainingFraction"] = null;
    }
    private JsonObject Locate(JsonObject payload) => Rules.Rows().FirstOrDefault(x => x.S("id") == payload.S("id")) ?? throw Invalid("预算不存在");
    private static InvalidDataException Conflict() => Invalid("预算已被另一界面修改，请刷新后重试");
    private static void CheckRevision(JsonObject payload, JsonObject input, JsonObject existing)
    {
        if (Integer(payload["expectedRevision"] ?? input["revision"]) is not int expected || expected != existing.I("revision")) throw Conflict();
    }
    private JsonObject Snapshot() => J.Obj(("version", 1), ("rules", Rules), ("runtime", runtime), ("ledger", ledger), ("summaries", Summaries));
    private void Restore(JsonObject state) { Rules = state.A("rules"); runtime = state.O("runtime"); ledger = state.A("ledger"); Summaries = state.A("summaries"); }
    private void Persist()
    {
        if (Encoding.UTF8.GetByteCount(J.Text(Rules)) > 65536) throw Invalid("预算配置过大，请减少规则或重复模型价格");
        var data = Snapshot(); if (Encoding.UTF8.GetByteCount(J.Text(data)) > 2097152) throw Invalid("预算配置超出存储上限");
        J.Write(path, data); PersistenceError = null;
    }
    private void TrimLedger()
    {
        var ids = Rules.Rows().Select(x => x.S("id")).ToHashSet(StringComparer.Ordinal);
        var periods = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        ledger = J.Array(ledger.Rows().Where(x => ids.Contains(x.S("ruleID"))).OrderByDescending(x => x.N("createdAt") ?? 0).Where(entry =>
        {
            string id = entry.S("ruleID"), period = entry.S("alertPeriodID", entry.S("periodID"));
            if (!periods.TryGetValue(id, out var values)) periods[id] = values = new(StringComparer.Ordinal);
            if (!values.Contains(period) && values.Count >= 2) return false;
            values.Add(period); return true;
        }));
    }
}
