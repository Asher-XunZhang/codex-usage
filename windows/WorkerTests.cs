using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace CodexUsage;

// Exercises the packaged runtime against synthetic logs only. No account or user logs are read.
internal static class WorkerTests
{
    public static async Task RunAsync()
    {
        string parentDirectory = Path.GetFullPath(Path.GetTempPath());
        string directory = Path.Combine(parentDirectory, "CodexUsage-WorkerTests-" + Guid.NewGuid().ToString("N"));
        string previousBase = Paths.Base;
        string? previousProxy = Environment.GetEnvironmentVariable("HTTP_PROXY");
        var client = new DashboardClient();
        void Check(bool condition, string description) { if (!condition) throw new InvalidOperationException("Worker test: " + description); }
        bool Alive(int pid) { try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
        int finalPid = 0;
        try
        {
            Paths.Base = Path.Combine(directory, "desktop");
            string home = Path.Combine(directory, "home"); Directory.CreateDirectory(Path.Combine(home, "sessions"));
            string log = Path.Combine(home, "sessions", "root.jsonl");
            JsonObject Event(string type, JsonObject payload) => J.Obj(("timestamp", DateTimeOffset.UtcNow.ToString("o")), ("type", type), ("payload", payload));
            JsonObject Counts(int input, int output) => J.Obj(("input_tokens", input), ("output_tokens", output), ("total_tokens", input + output), ("cached_input_tokens", 40), ("reasoning_output_tokens", 5), ("cache_write_input_tokens", 0));
            JsonObject Record(string id, JsonObject totals) => Event("token_usage_record", J.Obj(("thread_id", "root"), ("turn_id", "t1"), ("root_turn_id", "t1"), ("response_id", id), ("usage", Counts(100, 20)), ("turn_token_usage", totals), ("thread_token_usage", totals)));
            var records = new[] {
                Event("session_meta", J.Obj(("id", "root"), ("source", "vscode"))),
                Event("event_msg", J.Obj(("type", "task_started"), ("turn_id", "t1"))),
                Event("turn_context", J.Obj(("turn_id", "t1"), ("root_turn_id", "t1"), ("model", "test-model"))),
                Record("r1", Counts(100, 20))
            };
            await File.WriteAllTextAsync(log, string.Join("\n", records.Select(J.Text)) + "\n");
            Environment.SetEnvironmentVariable("HTTP_PROXY", "http://127.0.0.1:9");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            string query = DashboardClient.Query("all", "all", "all", "model");
            await client.Start(home, Paths.Cache(home), 0, deadline.Token);
            await client.Refresh(deadline.Token);
            var health = await client.Health(deadline.Token); int firstPid = health.I("pid");
            Check(firstPid > 0 && health.B("ready"), "service identity and initial indexing");
            var snapshot = await client.Usage(query, deadline.Token);
            Check(snapshot.O("summary").N("total_tokens") == 120, "initial token accounting");
            await client.Start(home, Paths.Cache(home), 0, deadline.Token);
            Check((await client.Health(deadline.Token)).I("pid") == firstPid, "repeated start retains one worker");
            await File.AppendAllTextAsync(log, J.Text(Record("r2", Counts(200, 40))) + "\n");
            await Task.Delay(1250, deadline.Token);
            Check((await client.Usage(query, deadline.Token)).O("summary").N("total_tokens") == 120, "refresh disabled does not scan appended logs");
            await client.Refresh(deadline.Token);
            snapshot = await client.Usage(DashboardClient.Query("all", "test-model", "root", "task"), deadline.Token);
            Check(snapshot.O("summary").N("total_tokens") == 240 && snapshot.A("groups").Rows().Single().S("id") == "root", "manual refresh and independent model/task filters");
            var csv = await client.Export(query, deadline.Token);
            Check(csv.Length > 3 && csv[0] == 239 && csv[1] == 187 && csv[2] == 191 && Encoding.UTF8.GetString(csv).Contains("240"), "CSV export preserves UTF-8 BOM and token counts");
            Check((await client.Configure(60, deadline.Token)).I("refresh_seconds") == 60, "automatic refresh configuration");
            Check((await client.Configure(0, deadline.Token)).I("refresh_seconds") == 0, "automatic refresh can be disabled");
            await client.Start(home, Paths.Cache(home), 0, deadline.Token, force: true);
            await client.Refresh(deadline.Token);
            finalPid = (await client.Health(deadline.Token)).I("pid");
            Check(finalPid != firstPid && !Alive(firstPid), "recovery terminates the previous worker");
            Check((await client.Usage(query, deadline.Token)).O("summary").N("total_tokens") == 240, "recovery preserves the persistent index");
            using var finalWorker = Process.GetProcessById(finalPid);
            _ = finalWorker.Handle; // Retain the real exit status after DashboardClient releases its handle.
            await client.DisposeAsync();
            Check(!Alive(finalPid), "closing the panel terminates its worker");
            Check(finalWorker.HasExited && finalWorker.ExitCode == 0, "private control credential shuts down gracefully without the Job Object kill fallback");
            Check(Directory.GetFiles(Paths.Base, "main-service-*.json").Length == 0, "closing the panel removes its service state");
            Console.WriteLine("Worker tests passed: isolated runtime, identity, refresh disabled/manual, filters, CSV, configuration, recovery, process cleanup.");
        }
        finally
        {
            await client.DisposeAsync(); Paths.Base = previousBase;
            Environment.SetEnvironmentVariable("HTTP_PROXY", previousProxy);
            // Delete only the uniquely named synthetic directory this invocation created.
            string resolved = Path.GetFullPath(directory);
            if (resolved.StartsWith(Path.Combine(parentDirectory, "CodexUsage-WorkerTests-"), StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
}
