using System;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace CodexUsage;

internal static class QuotaToggleTests
{
    internal static async Task<JsonObject> RunAsync()
    {
        var checks = new JsonArray();
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException("Quota toggle: " + name); checks.Add(name); }
        object? Field(Host host, string name) => typeof(Host).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host);
        JsonObject QuotaState(Host host) => host.State().O("updates").O("quota");
        JsonObject Toggle(bool enabled) => J.Obj(("action", "settings"), ("patch", J.Obj(("quotaEnabled", enabled))));
        string previousBase = Paths.Base;
        string? previousBackground = Environment.GetEnvironmentVariable("CODEX_USAGE_TEST_BACKGROUND");
        string directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CodexUsage-quota-toggle-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        Host? host = null;
        try
        {
            Paths.Base = directory; Environment.SetEnvironmentVariable("CODEX_USAGE_TEST_BACKGROUND", "1");
            string settingsFile = Path.Combine(directory, "settings.json");
            string home = Path.Combine(directory, "empty-codex-home"); Directory.CreateDirectory(home);
            J.Write(settingsFile, J.Obj(("quotaEnabled", false), ("mode", "float"), ("refresh", 0), ("home", home)));
            // Exercise the actual host and persistence boundaries. Demo supplies only
            // the quota payload; no Codex process or real account can be contacted.
            host = new Host(demo: true);
            Check(!QuotaState(host).B("enabled") && !QuotaState(host).B("busy"), "persisted off switch is honored on host startup");
            string retained = host.State().O("quota").ToJsonString();
            await host.Handle(J.Obj(("action", "refresh-quota"))); await host.Refresh(true);
            Check(Field(host, "quotaTask") is null && (double)Field(host, "lastQuota")! == 0, "disabled startup, direct retry and combined refresh never start a quota query");
            Check(host.State().O("quota").ToJsonString() == retained, "disabled refresh retains the last quota snapshot");
            await host.Handle(Toggle(true));
            Check(QuotaState(host).B("enabled") && Field(host, "quotaTask") is Task, "enabling quota immediately starts its own refresh");
            Check(new Settings(settingsFile).Data.B("quotaEnabled"), "enabled preference reaches disk before being reported as saved");
            await host.Handle(Toggle(false));
            Check(!QuotaState(host).B("enabled") && !new Settings(settingsFile).Data.B("quotaEnabled", true), "disabling quota updates runtime and durable preference");
            host.Dispose(); host = new Host(demo: true);
            Check(!QuotaState(host).B("enabled") && Field(host, "quotaTask") is null, "a reopened host stays disabled without a startup query");

            long revision = (long)Field(host, "quotaRevision")!;
            using (var locked = new FileStream(settingsFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool rejected = false;
                try { await host.Handle(Toggle(true)); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { rejected = true; }
                Check(rejected && !QuotaState(host).B("enabled") && !host.State().O("settings").B("quotaEnabled", true), "failed persistence does not enable quota in memory");
                Check((long)Field(host, "quotaRevision")! == revision && Field(host, "quotaTask") is null, "failed toggle does not invalidate in-flight generations or start a query");
            }
            Check(!new Settings(settingsFile).Data.B("quotaEnabled", true), "failed toggle preserves the original setting on disk");
            bool invalidRejected = false;
            try { await host.Handle(J.Obj(("action", "settings"), ("patch", J.Obj(("quotaEnabled", "true"))))); } catch (ArgumentException) { invalidRejected = true; }
            Check(invalidRejected && Field(host, "quotaTask") is null, "malformed toggle is rejected without starting quota work");

            host.Dispose(); host = null;
            new Settings(settingsFile).Update(J.Obj(("quotaEnabled", true)));
            host = new Host(demo: true, noQuota: true);
            Check(QuotaState(host).B("locked") && !QuotaState(host).B("enabled") && host.State().O("settings").B("quotaEnabled"), "noQuota launch override disables effective reading while preserving the user preference");
            await host.Handle(J.Obj(("action", "refresh-quota"))); await host.Refresh(true);
            await host.Handle(Toggle(false)); await host.Handle(Toggle(true));
            Check(!QuotaState(host).B("enabled") && Field(host, "quotaTask") is null && (double)Field(host, "lastQuota")! == 0, "noQuota override remains effective through retries and off-on changes");
            host.Dispose(); host = new Host(demo: true);
            Check(QuotaState(host).B("enabled") && !QuotaState(host).B("locked"), "reopening without noQuota restores the saved enabled preference");
        }
        finally
        {
            host?.Dispose(); Paths.Base = previousBase;
            Environment.SetEnvironmentVariable("CODEX_USAGE_TEST_BACKGROUND", previousBackground);
            if (directory.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(directory, true);
        }
        return J.Obj(("success", true), ("checks", checks), ("realAccountAccess", false), ("lateQuotaResponseTested", false),
            ("limitation", "Quota.Read 无异步注入点，demo 同步返回；迟到响应由源码审查覆盖，未计为动态测试通过。"));
    }
}
