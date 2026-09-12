using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace CodexUsage;

internal static class BudgetTests
{
    /// <summary>File-based adapter used by the unchanged macOS behavioral test suite.</summary>
    public static void Replay(string inputPath, string outputPath)
    {
        var input = J.Read(inputPath, 16_777_216);
        string storePath = input.S("path");
        if (storePath.Length == 0 || input["commands"] is not JsonArray commands || commands.Count > 2000)
            throw new InvalidDataException("预算测试输入需要 path 与 commands 数组。");
        var store = new BudgetStore(storePath); var output = new JsonArray();
        foreach (var command in commands.Rows())
        {
            var item = new JsonObject(); double now = command.N("now") ?? 0; string source = command.S("source", "/fixture");
            try
            {
                switch (command.S("op"))
                {
                    case "apply": store.Apply(command.S("action"), command.O("payload"), source, now); break;
                    case "evaluate": item["alerts"] = store.Evaluate(command["result"] as JsonObject, command["quota"] as JsonObject, source, now); break;
                    case "reload": store = new BudgetStore(storePath); break;
                }
            }
            catch (Exception e) { item["error"] = e.Message; }
            item["rules"] = store.Rules.DeepClone(); item["summaries"] = store.Summaries.DeepClone();
            item["requests"] = store.Requests(source, now); item["pending"] = store.PendingAlerts;
            item["persistenceError"] = store.PersistenceError; output.Add(item);
        }
        J.Write(Path.GetFullPath(outputPath), J.Obj(("version", 1), ("results", output)));
    }

    private const string Source = "/fixture";
    private static readonly double Now = Epoch("2026-09-11T12:00:00Z");
    private static double Epoch(string text) => DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds() / 1000d;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Budget test: " + message); }
    private static void Reject(Action action, string message) { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Budget accepted " + message); }
    private static JsonObject Rule(string kind = "token") => J.Obj(("id", "r1"), ("revision", 0), ("name", "日常开发"), ("kind", kind),
        ("amount", 100), ("tokenMetric", "total"), ("model", "all"), ("task", "all"), ("currency", "USD"), ("fx", 1),
        ("prices", new JsonArray()), ("period", J.Obj(("type", "day"), ("timezone", "UTC"))), ("thresholds", new[] { 20, 10, 0 }), ("enabled", true));
    private static JsonObject Usage(JsonObject request, double used, double? generated = null, bool complete = true, JsonArray? rows = null, bool hasRows = true)
    {
        var row = request.Copy(); row["scope_valid"] = true;
        row["rows"] = rows?.DeepClone() ?? new JsonArray(J.Obj(("model", "m"), ("requests", 1), ("input_tokens", used - 10), ("output_tokens", 10),
            ("total_tokens", used), ("cached_input_tokens", 0), ("cache_write_input_tokens", 0), ("reasoning_output_tokens", 0)));
        return J.Obj(("generated_at", generated ?? Now), ("has_rows", hasRows), ("coverage_complete", complete), ("results", new JsonArray(row)));
    }
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "CodexUsageBudgetTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        int count = 0;
        BudgetStore Create(JsonObject? rule = null, double? now = null)
        {
            var store = new BudgetStore(Path.Combine(directory, (++count) + ".json")); store.Apply("save", rule ?? Rule(), Source, now ?? Now); return store;
        }
        JsonObject Request(BudgetStore store, double? now = null) => (JsonObject)store.Requests(Source, now ?? Now)[0]!;
        JsonObject Summary(BudgetStore store) => (JsonObject)store.Summaries[0]!;
        JsonArray Evaluate(BudgetStore store, JsonObject? data = null, double? now = null, JsonObject? quota = null, string source = Source) => store.Evaluate(data, quota, source, now ?? Now);
        try
        {
            // Jumped levels are consumed, delivery survives restart, and corrected data cannot replay them.
            var store = Create(); var request = Request(store); var alerts = Evaluate(store, Usage(request, 95));
            Check(alerts.Count == 1 && alerts[0].N("threshold") == 10, "jump severity"); string alertId = alerts[0].S("id");
            store = new BudgetStore(Path.Combine(directory, count + ".json")); alerts = Evaluate(store);
            Check(alerts.Count == 1 && alerts[0].S("id") == alertId, "pending alert survives restart");
            store.Apply("acknowledge", J.Obj(("ids", new[] { alertId })), Source, Now);
            Check(Evaluate(store, Usage(request, 95)).Count == 0, "acknowledged alert deduplication");
            Evaluate(store, Usage(request, 60, Now + 1), Now + 1);
            Check(Evaluate(store, Usage(request, 85, Now + 2), Now + 2).Count == 0, "corrected data does not replay skipped threshold");
            alerts = Evaluate(store, Usage(request, 100, Now + 3), Now + 3);
            Check(alerts.Count == 1 && alerts[0].N("threshold") == 0 && Summary(store).S("status") == "exhausted", "exhaustion threshold");

            store = Create(); request = Request(store); store.Apply("pause", J.Obj(("id", "r1")), Source, Now);
            Check(Evaluate(store, Usage(request, 101)).Count == 0 && Summary(store).B("paused") && Summary(store).N("used") == 101, "pause continues evaluation");
            Check(Evaluate(store, Usage(request, 101, Now + 1801), Now + 1801).Count == 1, "pause does not consume threshold");
            store.Apply("disable", J.Obj(("id", "r1"), ("expectedRevision", 1)), Source, Now + 1802);
            Check(Evaluate(store).Count == 0 && Summary(store).S("status") == "disabled", "disabled rules stop alerts");
            store.Apply("enable", J.Obj(("id", "r1"), ("expectedRevision", 2)), Source, Now + 1803);
            Check(Request(store).I("revision") == 3, "enable retains revision");

            store = Create(); request = Request(store); var changed = Rule(); changed["revision"] = 1; changed["amount"] = 200;
            store.Apply("save", changed, Source, Now); Reject(() => store.Apply("save", changed, Source, Now), "stale revision");
            Evaluate(store, Usage(request, 200)); Check(Summary(store).N("remaining") == null, "stale query identity rejected");
            request = Request(store); Evaluate(store, Usage(request, 50)); Check(Summary(store).N("remaining") == 150, "amount edit immediately applies");
            Evaluate(store, now: Now + 60); Check(Summary(store).N("used") == 50, "nil preserves same cycle");
            Evaluate(store, source: "/another"); Check(store.Requests("/another", Now).Count == 0 && Summary(store).S("status") == "source_invalid", "source change invalidates");
            changed["revision"] = 2; Reject(() => store.Apply("save", changed, "/another", Now), "editing other source");
            Evaluate(store, Usage(request, 200, Now + 86400), Now + 86400);
            Check(Summary(store).S("status") == "unknown" && Request(store, Now + 86400).S("periodID") != request.S("periodID"), "rollover never carries balance");
            Evaluate(store, Usage(Request(store, Now + 86400), 0, Now, rows: new()), Now + 86400);
            Check(Summary(store).N("remaining") == null, "old index cannot fabricate fresh cycle");

            store = Create(); request = Request(store); Evaluate(store, Usage(request, 50, Now + 10), Now + 10); Evaluate(store, Usage(request, 99), Now + 11);
            Check(Summary(store).N("used") == 50, "old generated snapshot rejected");
            var lower = J.Obj(("model", "m"), ("requests", 2), ("total_tokens", null), ("known_total_tokens", 1), ("lower_bound_total_tokens", 50));
            Evaluate(store, Usage(request, 0, Now + 12, false, new(lower)), Now + 12);
            Check(Summary(store).N("used") == 50 && Summary(store).N("remaining") == null && Summary(store).S("status") == "partial", "partial lower bound");
            lower["lower_bound_total_tokens"] = 110;
            Check(Evaluate(store, Usage(request, 0, Now + 13, false, new(lower.DeepClone())), Now + 13).Count == 1 && Summary(store).S("status") == "exceeded", "known lower bound over budget");
            Evaluate(store, Usage(request, 0, Now + 14, hasRows: false), Now + 14); Check(Summary(store).S("status") == "unknown", "missing records not zero");

            var metricRows = new JsonArray(J.Obj(("model", "m"), ("requests", 1), ("input_tokens", 80), ("output_tokens", 20), ("total_tokens", 100),
                ("cached_input_tokens", 60), ("reasoning_output_tokens", 15), ("cache_write_input_tokens", 0)));
            foreach (var pair in new[] { ("total", 100), ("noncached", 40), ("output", 20) })
            {
                var r = Rule(); r["tokenMetric"] = pair.Item1; store = Create(r); Evaluate(store, Usage(Request(store), 0, rows: metricRows));
                Check(Summary(store).N("used") == pair.Item2, "cache/reasoning metric " + pair.Item1);
            }
            var money = Rule("money"); money["currency"] = "CNY"; money["fx"] = 7;
            money["prices"] = new JsonArray(J.Obj(("model", "m"), ("input", 2), ("cachedInput", .5), ("output", 10)));
            store = Create(money); request = Request(store);
            var moneyRow = J.Obj(("model", "m"), ("requests", 1), ("input_tokens", 1000000), ("output_tokens", 100000), ("total_tokens", 1100000), ("cached_input_tokens", 800000), ("cache_write_input_tokens", 0));
            Evaluate(store, Usage(request, 0, rows: new(moneyRow.DeepClone())));
            Check(Math.Abs(Summary(store).N("used")!.Value - 12.6) < 1e-9 && Summary(store).B("estimated"), "prices/cache/fx");
            moneyRow["cached_input_tokens"] = null; moneyRow["known_cached_input_tokens"] = 0;
            Evaluate(store, Usage(request, 0, rows: new(moneyRow.DeepClone())));
            Check(Summary(store).N("used") == 7 && Summary(store).S("status") == "partial", "missing cache split cannot price full input");
            moneyRow["cached_input_tokens"] = 800000; moneyRow.Remove("known_cached_input_tokens"); moneyRow["cache_write_input_tokens"] = null;
            Evaluate(store, Usage(request, 0, rows: new(moneyRow.DeepClone())));
            Check(Summary(store).N("used") == 7 && Summary(store).N("remaining") == null, "missing cache write classification");
            Check(Summary(store).A("issues").Rows().Any(x => x.S("type") == "usage" && x.S("model") == "m" && x.S("category") == "cache_write_input_tokens"), "missing usage category is actionable");
            moneyRow["cache_write_input_tokens"] = 0;
            var missingPriceRule = Rule("money"); missingPriceRule["prices"] = new JsonArray(J.Obj(("model", "m"), ("input", 2)));
            store = Create(missingPriceRule); request = Request(store); Evaluate(store, Usage(request, 0, rows: new(moneyRow.DeepClone())));
            Check(Summary(store).A("issues").Rows().Any(x => x.S("category") == "cachedInput") && Summary(store).A("issues").Rows().Any(x => x.S("category") == "output"), "missing prices identify model and exact category");
            moneyRow["model"] = "new-model"; Evaluate(store, Usage(request, 0, rows: new(moneyRow.DeepClone())));
            Check(Summary(store).A("issues").Rows().Single().S("model") == "new-model" && Summary(store).A("issues")[0].S("category") == "model" && Summary(store).N("remaining") == null, "unpriced model is not a free model");
            var repairedPrice = store.Rules[0]!.AsObject().Copy(); repairedPrice["prices"] = new JsonArray(J.Obj(("model", "new-model"), ("input", 2), ("cachedInput", .5), ("output", 10)));
            store.Apply("save", J.Obj(("rule", repairedPrice), ("recalculateCurrent", true)), Source, Now);
            Evaluate(store, Usage(Request(store), 0, rows: new(moneyRow.DeepClone())));
            Check(Summary(store).A("issues").Count == 0 && Summary(store).S("coverage") == "complete" && Summary(store).N("remaining") != null, "price repair immediately recovers current period");

            var official = Rule("quota"); official["amount"] = 20; official["windowMinutes"] = 10080; store = Create(official);
            var quota = J.Obj(("updated_at", Now), ("stale", false), ("windows", new JsonArray(J.Obj(("duration_minutes", 300), ("remaining", 3)), J.Obj(("duration_minutes", 10080), ("remaining", 21)))));
            Check(Evaluate(store, quota: quota).Count == 0 && Summary(store).N("remaining") == 21, "quota selected window");
            quota.A("windows")[1]!["remaining"] = 20; alerts = Evaluate(store, quota: quota);
            Check(alerts.Count == 1 && alerts[0].N("threshold") == 20 && Summary(store).N("amount") == 100, "quota floor boundary");
            Check(Evaluate(store, now: Now + 181, quota: quota).Count == 0 && Summary(store).S("status") == "unknown", "quota freshness");
            var unavailableRule = Rule("quota"); unavailableRule["amount"] = 20; unavailableRule["windowMinutes"] = 60; var unavailable = Create(unavailableRule);
            Evaluate(unavailable, quota: quota);
            Check(Summary(unavailable).S("issueCode") == "window_invalid" && Summary(unavailable).A("availableWindows").Count == 2 && Summary(unavailable).N("remaining") == null, "unsupported quota window is explicitly invalid");
            Evaluate(unavailable, now: Now + 181, quota: quota);
            Check(Summary(unavailable).S("issueCode") == "" && Summary(unavailable).S("message").Contains("刷新"), "stale quota does not falsely invalidate the saved window");
            official["id"] = "q2"; official["quotaCondition"] = "consumption"; Reject(() => store.Apply("save", official, Source, Now), "quota consumption mode");
            official = Rule("quota"); official["amount"] = 0; official["windowMinutes"] = 300; store = Create(official);
            quota = J.Obj(("updated_at", Now), ("windows", new JsonArray(J.Obj(("duration_minutes", 300), ("remaining", 0), ("resets_at", Now + 60)))));
            alerts = Evaluate(store, quota: quota); alertId = alerts[0].S("id"); string periodId = alerts[0].S("periodID");
            store.Apply("acknowledge", J.Obj(("ids", new[] { alertId })), Source, Now);
            quota["updated_at"] = Now + 100; quota.A("windows")[0]!["resets_at"] = Now + 18100; alerts = Evaluate(store, now: Now + 100, quota: quota);
            Check(alerts.Count == 1 && alerts[0].S("id") != alertId && alerts[0].S("periodID") == periodId, "official reset alert cycle");

            foreach (var test in new[] {
                ("day", "Asia/Shanghai", 1, 0, "2026-09-11T16:00:00Z", "2026-09-11T16:00:00Z", "2026-09-12T16:00:00Z"),
                ("week", "UTC", 1, 9, "2026-09-11T12:00:00Z", "2026-09-07T09:00:00Z", "2026-09-14T09:00:00Z"),
                ("month", "UTC", 31, 0, "2026-02-28T00:00:00Z", "2026-02-28T00:00:00Z", "2026-03-31T00:00:00Z"),
                ("month", "UTC", 31, 0, "2026-02-27T23:59:59Z", "2026-01-31T00:00:00Z", "2026-02-28T00:00:00Z") })
            {
                var r = Rule(); r["period"] = J.Obj(("type", test.Item1), ("timezone", test.Item2), ("day", test.Item3), ("hour", test.Item4), ("weekday", 2));
                store = Create(r, Epoch(test.Item5)); request = Request(store, Epoch(test.Item5));
                Check(request.N("start") == Epoch(test.Item6) && request.N("end") == Epoch(test.Item7), "calendar " + test.Item1);
            }
            foreach (var pair in new[] { ("2026-03-08T12:00:00Z", 23), ("2026-11-01T12:00:00Z", 25) })
            {
                var r = Rule(); r["period"] = J.Obj(("type", "day"), ("timezone", "America/New_York")); store = Create(r, Epoch(pair.Item1)); request = Request(store, Epoch(pair.Item1));
                Check(request.N("end") - request.N("start") == pair.Item2 * 3600, "DST day length");
            }
            var repeated = Rule(); repeated["period"] = J.Obj(("type", "day"), ("timezone", "America/New_York"), ("hour", 1), ("minute", 30));
            store = Create(repeated, Epoch("2026-11-01T06:15:00Z")); Check(Request(store, Epoch("2026-11-01T06:15:00Z")).N("start") == Epoch("2026-11-01T05:30:00Z"), "first repeated wall time");
            repeated.O("period")["hour"] = 2; repeated.O("period")["minute"] = 30;
            store = Create(repeated, Epoch("2026-03-08T12:00:00Z")); Check(Request(store, Epoch("2026-03-08T12:00:00Z")).N("start") == Epoch("2026-03-08T07:00:00Z"), "missing wall time advances to next valid time");
            var once = Rule(); once["period"] = J.Obj(("type", "once"), ("timezone", "UTC"), ("start", Now + 3600), ("end", Now + 7200)); store = Create(once); Evaluate(store);
            Check(Summary(store).S("status") == "scheduled", "future once scheduled"); request = Request(store, Now + 3600);
            Check(Evaluate(store, Usage(request, 101, Now + 7200), Now + 7200).Count == 0 && Summary(store).S("status") == "ended", "half-open once boundary");
            once["period"] = J.Obj(("type", "interval"), ("timezone", "America/New_York"), ("start", Now), ("seconds", 3600)); store = Create(once);
            Check(Request(store).N("end") == Request(store, Now + 3600).N("start"), "interval boundaries");

            store = Create(); changed = Rule(); changed["revision"] = 1; changed["model"] = "m"; store.Apply("save", changed, Source, Now);
            Check(store.Rules[0].S("model") == "m" && Request(store).S("model") == "all" && Request(store, Now + 86400).S("model") == "m", "deferred scope edit");
            store = new BudgetStore(Path.Combine(directory, count + ".json")); Check(Request(store).S("model") == "all", "deferred state reload");
            changed["revision"] = 2; store.Apply("save", J.Obj(("rule", changed), ("recalculateCurrent", true)), Source, Now);
            Check(Request(store).S("model") == "m", "explicit current recalculation");
            changed = Rule("money"); changed["revision"] = 3; changed["amount"] = 10; store.Apply("save", changed, Source, Now); Evaluate(store);
            Check(Summary(store).S("kind") == "token" && Summary(store).N("amount") == 100 && store.Rules[0].N("amount") == 10, "deferred kind preserves current unit/amount");
            var pending = Summary(store).O("pendingChange");
            Check(pending.O("current").S("kind") == "token" && pending.O("current").N("amount") == 100 && pending.O("next").S("kind") == "money" && pending.O("next").N("amount") == 10
                && pending.N("effectiveAt") == Request(store).N("end") && pending.O("current").ContainsKey("prices") && pending.O("next").ContainsKey("fx"), "pending change exposes actual current and next settings including deferred amount and prices");
            Evaluate(store, null, Now + 86400); Check(!Summary(store).B("scheduledChange") && !Summary(store).ContainsKey("pendingChange"), "effective change removes stale next-period comparison");
            var fractional = Rule(); fractional["amount"] = .1; store = Create(fractional); alerts = Evaluate(store, Usage(Request(store), .08));
            Check(alerts.Count == 1 && alerts[0].N("threshold") == 20, "floating point threshold tolerance");

            string corrupt = Path.Combine(directory, "corrupt.json"); File.WriteAllText(corrupt, "{broken"); store = new BudgetStore(corrupt);
            Reject(() => store.Apply("save", Rule(), Source, Now), "corrupt protected file"); Check(File.ReadAllText(corrupt) == "{broken" && store.PersistenceError != null, "corrupt bytes retained");
            Reject(() => store.Apply("recover", new(), Source, Now), "unconfirmed corrupt recovery");
            Check(File.ReadAllText(corrupt) == "{broken" && store.Recovery.B("required"), "unconfirmed recovery changes nothing");
            store.Apply("recover", J.Obj(("confirm", true)), Source, Now);
            string recoveredBackup = store.Recovery.S("backupPath");
            Check(File.ReadAllText(recoveredBackup) == "{broken" && !store.Recovery.B("required") && store.PersistenceError == null && J.Read(corrupt).A("rules").Count == 0, "recovery atomically preserves original and creates readable empty configuration");
            store.Apply("save", Rule(), Source, Now); Check(new BudgetStore(corrupt).Rules.Count == 1, "recovered configuration accepts new rules");
            Reject(() => store.Apply("recover", J.Obj(("confirm", true)), Source, Now), "recovery cannot erase healthy budgets");
            string locked = Path.Combine(directory, "locked.json"); File.WriteAllText(locked, "retained bytes"); var lockedStore = new BudgetStore(locked);
            using (var held = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool recoveryFailed = false; try { lockedStore.Apply("recover", J.Obj(("confirm", true)), Source, Now); } catch (IOException) { recoveryFailed = true; }
                Check(recoveryFailed && lockedStore.Recovery.B("required") && File.ReadAllText(locked) == "retained bytes", "failed backup or replacement never unlocks or clears corrupt store");
            }
            string obstacle = Path.Combine(directory, "not-directory"); File.WriteAllText(obstacle, "retain"); store = new BudgetStore(Path.Combine(obstacle, "budgets.json"));
            bool failed = false; try { store.Apply("save", Rule(), Source, Now); } catch (IOException) { failed = true; }
            Check(failed && store.Rules.Count == 0 && File.ReadAllText(obstacle) == "retain", "atomic write rollback");
            store = Create(); string file = Path.Combine(directory, count + ".json"); var saved = J.Read(file); saved.A("rules")[0]!.AsObject().Remove("currency"); J.Write(file, saved);
            store = new BudgetStore(file); Check(store.Rules[0].S("currency") == "USD", "canonical reload defaults");
            saved = J.Read(file); saved.O("runtime")["r1"] = J.Obj(("deferredUntil", Now + 1000), ("deferred", J.Obj(("period", J.Obj(("type", "once"), ("timezone", "UTC")))))); J.Write(file, saved);
            store = new BudgetStore(file); Check(store.PersistenceError != null && J.Text(J.Read(file)) == J.Text(saved), "invalid deferred state protected");

            foreach (var invalid in new[] { J.Obj(("amount", 0)), J.Obj(("amount", -1)), J.Obj(("period", J.Obj(("type", "day"), ("timezone", "wrong-zone")))),
                J.Obj(("thresholds", new[] { 20, 20 })), J.Obj(("period", J.Obj(("type", "once"), ("timezone", "UTC"), ("start", 10), ("end", 9)))),
                J.Obj(("prices", new JsonArray(J.Obj(("model", "m"), ("input", -1))))) })
            {
                var r = Rule(); foreach (var entry in invalid) r[entry.Key] = entry.Value?.DeepClone();
                var target = new BudgetStore(Path.Combine(directory, "invalid.json")); Reject(() => target.Apply("save", r, Source, Now), "invalid configuration"); Check(target.Rules.Count == 0, "validation rollback");
            }
            // Maximum cardinality verifies that pruning retains every current threshold.
            store = new BudgetStore(Path.Combine(directory, "bounded.json"));
            for (int i = 0; i < 50; i++) { var r = Rule(); r["id"] = "r" + i; r["thresholds"] = new JsonArray(Enumerable.Range(0, 10).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()); store.Apply("save", r, Source, Now); }
            for (int day = 0; day < 3; day++)
            {
                double time = Now + day * 86400; var rows = new JsonArray();
                foreach (var query in store.Requests(Source, time).Rows()) { var row = query.Copy(); row["scope_valid"] = true; row["rows"] = new JsonArray(J.Obj(("model", "m"), ("requests", 1), ("total_tokens", 100))); rows.Add(row); }
                var data = J.Obj(("generated_at", time), ("has_rows", true), ("coverage_complete", true), ("results", rows)); alerts = Evaluate(store, data, time);
                Check(alerts.Count == 50, "maximum rules alerts"); store.Apply("acknowledge", J.Obj(("ids", alerts.Rows().Select(x => x.S("id")).ToArray())), Source, time);
                Check(Evaluate(store, data, time).Count == 0, "maximum thresholds deduplication");
            }
            Check(J.Read(Path.Combine(directory, "bounded.json")).A("ledger").Count == 1000, "bounded two-period ledger");
        }
        finally
        {
            // The only recursively removed directory is the unique test directory created above.
            if (Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(directory, true);
        }
    }
}
