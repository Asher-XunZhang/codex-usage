using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace CodexUsage;

internal static class Quota
{
    public static JsonObject Describe(JsonObject raw, double? now = null)
    {
        var result = raw.Copy(); var windows = new JsonArray();
        foreach (var row in raw.A("windows").Rows())
        {
            if (row.N("used_percent") is not double used || row.I("duration_minutes") <= 0) continue;
            var value = row.Copy(); int minutes = row.I("duration_minutes");
            value["remaining"] = Math.Clamp(100 - used, 0, 100);
            value["label"] = minutes == 10080 ? "周" : minutes == 300 ? "5h" : minutes % 60 == 0 ? $"{minutes / 60}h" : $"{minutes}分钟";
            windows.Add(value);
        }
        result["windows"] = windows;
        bool stale = raw.N("updated_at") is not double updated || (now ?? J.Now) - updated > 180 || updated > (now ?? J.Now) + 5 || raw.S("error").Length > 0;
        result["stale"] = stale;
        string compact = string.Join(" / ", windows.Rows().Select(x => $"{x.S("label")}余 {Math.Floor(x.N("remaining")!.Value)}%"));
        result["compact"] = compact.Length == 0 ? "额度 —" : compact + (stale ? "*" : "");
        result["detail"] = windows.Count == 0 ? raw.S("error", "正在读取账号额度…") : string.Join("   ", windows.Rows().Select(x => $"{x.S("label")}余 {Math.Floor(x.N("remaining")!.Value)}%" + (x.N("resets_at") is double t ? " · " + J.Date(t) + " 重置" : ""))) + (stale ? " · 上次记录" : "");
        result["resetLabel"] = raw.N("reset_count") is double count ? $"重置卡 {count:N0} 张" : "重置卡数量未知";
        var selected = windows.Rows().OrderBy(x => x.N("remaining")).FirstOrDefault();
        result["capsuleName"] = selected is null ? "额度" : selected.S("label") + "余";
        result["capsuleFraction"] = selected?.N("remaining") / 100;
        return result;
    }
    public static string? Locate()
    {
        var candidates = new List<string>();
        foreach (string path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            candidates.Add(Path.Combine(path.Trim('"'), "codex.exe"));
            string npm = Path.Combine(path.Trim('"'), "node_modules", "@openai", "codex", "vendor");
            foreach (string arch in new[] { "x86_64-pc-windows-msvc", "aarch64-pc-windows-msvc" }) candidates.Add(Path.Combine(npm, arch, "codex", "codex.exe"));
        }
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string bins = Path.Combine(local, "OpenAI", "Codex", "bin");
        if (Directory.Exists(bins))
        {
            try { candidates.AddRange(Directory.GetDirectories(bins).OrderByDescending(Directory.GetLastWriteTimeUtc).Select(x => Path.Combine(x, "codex.exe"))); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        foreach (string folder in new[] { Path.Combine(local, "Programs", "Codex"), Path.Combine(local, "Programs", "ChatGPT"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin") })
        {
            candidates.Add(Path.Combine(folder, "resources", "codex.exe")); candidates.Add(Path.Combine(folder, "codex.exe"));
        }
        return candidates.FirstOrDefault(File.Exists);
    }
    public static async Task<JsonObject> Read(ChildJob job, JsonObject previous, bool demo = false)
    {
        if (demo) return DemoData.Quota();
        string? executable = Locate();
        if (executable is null) return Failure(previous, "未找到 Codex；请先安装并登录 Codex");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(22));
        using var child = job.Start(executable, "app-server", "--stdio", "-c", "analytics.enabled=false");
        var error = Processes.ReadBounded(child.StandardError, 65536, deadline.Token);
        async Task Send(JsonObject value) { await child.StandardInput.WriteLineAsync(J.Text(value).AsMemory(), deadline.Token); await child.StandardInput.FlushAsync(deadline.Token); }
        try
        {
            await Send(J.Obj(("id", 1), ("method", "initialize"), ("params", J.Obj(("clientInfo", J.Obj(("name", "codex_usage_readonly"), ("version", "1.1.0"))), ("capabilities", J.Obj(("experimentalApi", true), ("requestAttestation", false)))))));
            var line = new StringBuilder(); char[] ch = new char[1]; int total = 0;
            while (await child.StandardOutput.ReadAsync(ch.AsMemory(), deadline.Token) > 0)
            {
                if (++total > 1_048_576) throw new IOException("额度响应超出大小限制");
                if (ch[0] != '\n') { line.Append(ch[0]); continue; }
                var row = J.Parse(line.ToString()); line.Clear();
                if (row["method"] is not null) continue;
                if (row["error"] is not null) throw new IOException("Codex 未返回可用额度");
                if (row.I("id") == 1) { await Send(J.Obj(("method", "initialized"))); await Send(J.Obj(("id", 2), ("method", "account/rateLimits/read"))); }
                if (row.I("id") != 2) continue;
                var raw = row.O("result"); var bucket = raw.O("rateLimitsByLimitId").O("codex");
                if (bucket.Count == 0) bucket = raw.O("rateLimits");
                var windows = new JsonArray();
                foreach (string key in new[] { "primary", "secondary" })
                {
                    var w = bucket.O(key); if (w.N("usedPercent") is not double used || w.N("windowDurationMins") is not double minutes) continue;
                    windows.Add(J.Obj(("used_percent", used), ("duration_minutes", minutes), ("resets_at", w.N("resetsAt"))));
                }
                var result = J.Obj(("windows", windows), ("updated_at", J.Now), ("reset_count", raw.O("rateLimitResetCredits").N("availableCount")));
                if (windows.Count == 0) result["error"] = "当前账号未返回订阅额度";
                return Describe(result);
            }
            return Failure(previous, "额度读取失败，请确认 Codex 已登录后重试");
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or System.Text.Json.JsonException)
        {
            return Failure(previous, "额度暂不可用，请确认 Codex 已登录");
        }
        finally
        {
            try { child.StandardInput.Close(); if (!child.HasExited && !child.WaitForExit(250)) child.Kill(true); } catch (InvalidOperationException) { }
            try { await error; } catch (Exception) { }
        }
    }
    private static JsonObject Failure(JsonObject old, string error) { var next = old.Copy(); next["error"] = error; next["attempted_at"] = J.Now; return Describe(next); }
}
