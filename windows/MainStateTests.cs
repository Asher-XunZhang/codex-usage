using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace CodexUsage;

// Failure injection for the same coordinators used by MainWindow. No host, HTTP
// service, account access, desktop window, or user settings are involved.
internal static class MainStateTests
{
    public static async Task RunAsync()
    {
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("Main state: " + name); }
        async Task Fails(Func<Task> action, string name)
        {
            try { await action(); }
            catch (IOException) { return; }
            catch (InvalidOperationException e) when (!e.Message.StartsWith("Main state:")) { return; }
            throw new InvalidOperationException("Main state: expected failure: " + name);
        }
        JsonObject Usage(string name, string stamp = "same-generation") => J.Obj(("meta", J.Obj(("generated_at", stamp))),
            ("groups", new JsonArray(J.Obj(("label", name), ("total_tokens", 123)))), ("summary", J.Obj(("total_tokens", 123))));
        var a = new MainUsageIdentity("synthetic-home-A", "synthetic-cache-A", "days=30&model=all&task=all&group=model");
        var b = a with { Query = "days=7&model=all&task=all&group=task" };
        var session = new MainUsageSession(); session.Select(a);
        await session.LoadAsync((_, _) => Task.FromResult(Usage("A")), CancellationToken.None);
        var exportA = session.CaptureExport();
        Check(session.CanExport && !session.ShouldRead("same-generation"), "a successful unchanged query becomes exportable");
        session.Select(b);
        Check(!session.CanExport, "changing a filter immediately invalidates export before HTTP starts");
        await Fails(() => session.LoadAsync((_, _) => Task.FromException<JsonObject>(new IOException("injected read failure")), CancellationToken.None), "filter read failure propagates");
        Check(session.Displayed == a && session.Current == b && session.ShouldRead("same-generation") && !session.CanExport,
            "a failed new filter remains dirty even when generated_at is unchanged");
        await Fails(() => { session.ValidateExport(exportA); return Task.CompletedTask; }, "old filter CSV is rejected");
        await session.LoadAsync((_, _) => Task.FromResult(Usage("B")), CancellationToken.None);
        Check(session.Displayed == b && session.Snapshot.A("groups").Rows().Single().S("label") == "B" && session.CanExport,
            "retry reads the selected filter and replaces the old snapshot");

        session.Invalidate();
        await Fails(() => session.LoadAsync((_, _) => Task.FromException<JsonObject>(new IOException("scan succeeded, query failed")), CancellationToken.None), "manual refresh must not swallow a query failure");
        Check(!session.CanExport && session.NeedsRead && session.Error.Contains("query failed"), "same-query refresh failure preserves old values but blocks export");
        await session.LoadAsync((_, _) => Task.FromResult(Usage("B retried")), CancellationToken.None);
        Check(session.CanExport && session.Error.Length == 0, "successful retry clears the local read error");

        foreach (bool failOld in new[] { false, true })
        {
            var oldRead = new TaskCompletionSource<JsonObject>();
            var pending = new MainUsageSession(); pending.Select(a); int calls = 0;
            Task<JsonObject> Fetch(MainUsageIdentity id, CancellationToken _) { calls++; return id == a ? oldRead.Task : Task.FromResult(Usage("latest B")); }
            var read = pending.LoadAsync(Fetch, CancellationToken.None);
            pending.Select(b);
            var joined = pending.LoadAsync(Fetch, CancellationToken.None);
            Check(ReferenceEquals(read, joined) && calls == 1, "concurrent UI refreshes share one read");
            if (failOld) oldRead.SetException(new IOException("obsolete A failure")); else oldRead.SetResult(Usage("obsolete A result"));
            var result = await read;
            Check(calls == 2 && pending.Displayed == b && result.A("groups").Rows().Single().S("label") == "latest B" && pending.Error.Length == 0,
                "an obsolete success or failure cannot replace the newer selection");
        }

        var source = Usage("copy isolation");
        session.Select(a); await session.LoadAsync((_, _) => Task.FromResult(source), CancellationToken.None);
        var captured = session.CaptureExport(); source.A("groups")[0]!["label"] = "mutated caller";
        Check(captured.Data.A("groups")[0]!["label"]!.ToString() == "copy isolation", "export captures independent immutable data");
        foreach (var identity in new[] { a with { Home = "different-home" }, a with { Cache = "different-cache" }, b })
        {
            session.Select(identity);
            await Fails(() => { session.ValidateExport(captured); return Task.CompletedTask; }, "directory, cache, and query changes reject an in-progress export");
        }
        session.Select(a); await session.LoadAsync((_, _) => Task.FromResult(Usage("A current")), CancellationToken.None);
        captured = session.CaptureExport();
        await session.LoadAsync((_, _) => Task.FromResult(Usage("A new", "new-generation")), CancellationToken.None);
        await Fails(() => { session.ValidateExport(captured); return Task.CompletedTask; }, "a newer displayed generation rejects a previous export capture");
        await session.LoadAsync((_, _) => Task.FromResult(J.Obj(("meta", J.Obj(("loading", true))))), CancellationToken.None);
        Check(!session.CanExport && session.ShouldRead(""), "an indexing placeholder is not a successful export snapshot");

        var csvData = J.Parse("""
            {"groups":[{"label":"=SUM(中文,\"a\")","input_tokens":9007199254740993,"cached_input_tokens":null,"noncached_input_tokens":0,"output_tokens":18446744073709551615,"total_tokens":18446744073709551615,"requests":2}]}
            """);
        byte[] csv = MainUsageSession.Csv(csvData);
        string text = Encoding.UTF8.GetString(csv);
        Check(csv.Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }) && text.Contains("\"'=SUM(中文,\"\"a\"\")\""), "CSV keeps Excel BOM, Unicode, CSV escaping and formula neutralization");
        Check(text.Contains("9007199254740993,不可统计,0,18446744073709551615,18446744073709551615,2\r\n"), "CSV preserves exact large counts and unknown versus zero");

        var tableData = J.Parse("""
            {"groups":[{"label":"keep-small","total_tokens":9007199254740992},{"label":"drop-large","total_tokens":18446744073709551615},{"label":"keep-large","total_tokens":9007199254740993},{"label":"keep-unknown","total_tokens":null}]}
            """);
        var visibleSelection = new MainTableSelection("keep", "total_tokens", false);
        var visibleRows = visibleSelection.ExportData(tableData, true).A("groups").Rows().Select(row => row.S("label")).ToArray();
        Check(visibleRows.SequenceEqual(new[] { "keep-large", "keep-small", "keep-unknown" }), "visible export applies the same search and exact numeric ordering, with unknown values last");
        Check(visibleSelection.ExportData(tableData, false).A("groups").Count == 4 && tableData.A("groups").Count == 4,
            "full-range export ignores display search and projections never mutate the accepted snapshot");
        Check((visibleSelection with { Ascending = true }).Rows(tableData).First().S("label") == "keep-small", "ascending sort changes both the table and visible CSV order");
        Check((visibleSelection with { Search = "missing" }).Rows(tableData).Count == 0, "zero visible matches export a header-only CSV rather than all hidden rows");
        var enormousCounts = J.Parse("""{"groups":[{"label":"smaller","total_tokens":999999999999999999999999999999},{"label":"larger","total_tokens":1000000000000000000000000000000}]}""");
        Check(new MainTableSelection("", "total_tokens", false).Rows(enormousCounts).First().S("label") == "larger",
            "table and CSV sorting preserve integer order beyond decimal or double precision");

        var queue = new MainSettingsQueue(); var blockedSave = new TaskCompletionSource();
        var sent = new List<JsonObject>();
        queue.Enqueue(J.Obj(("filterDays", "30"), ("mainTheme", "dark")));
        Task Save(JsonObject patch) { sent.Add(patch.Copy()); return sent.Count == 1 ? blockedSave.Task : Task.CompletedTask; }
        var firstSave = queue.FlushAsync(Save);
        queue.Enqueue(J.Obj(("filterDays", "7"), ("mainPage", "budget")));
        Check(ReferenceEquals(firstSave, queue.FlushAsync(Save)) && sent.Count == 1, "settings writes serialize while the host is busy");
        blockedSave.SetException(new IOException("injected permission failure"));
        await Fails(() => firstSave, "settings persistence failure reaches the caller");
        Check(queue.HasPending && queue.Error.Contains("permission"), "failed writes remain queued and visible");
        await queue.FlushAsync(Save);
        Check(sent.Count == 2 && sent[1].S("filterDays") == "7" && sent[1].S("mainTheme") == "dark" && sent[1].S("mainPage") == "budget",
            "retry merges unsaved keys while keeping the newest value selected during the failure");
        Check(!queue.HasPending && queue.Error.Length == 0, "confirmed persistence clears pending/error state");

        var nextSave = new TaskCompletionSource(); sent.Clear();
        queue.Enqueue(J.Obj(("filterDays", "1")));
        Task DelayedSuccess(JsonObject patch) { sent.Add(patch.Copy()); return sent.Count == 1 ? nextSave.Task : Task.CompletedTask; }
        var saving = queue.FlushAsync(DelayedSuccess); queue.Enqueue(J.Obj(("filterDays", "90"))); nextSave.SetResult(); await saving;
        Check(sent.Count == 2 && sent[1].S("filterDays") == "90" && !queue.HasPending,
            "new edits arriving during a successful save are flushed before completion");
        Console.WriteLine("Main state tests passed: failed filter retry, stale responses, manual query failure, export identity/CSV, serialized durable retry queue.");
    }
}
