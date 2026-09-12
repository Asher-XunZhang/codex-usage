using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace CodexUsage;

internal static class ForegroundTransfer
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool AllowSetForegroundWindow(uint processId);

    // Grant only on an explicit settings action, to this helper's own live host.
    // Windows revokes the grant on subsequent input, so the host consumes it immediately.
    internal static bool GrantHost(JsonObject state)
    {
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "--parent-pid");
        int parent = index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out int parsed) ? parsed : 0;
        GetWindowThreadProcessId(GetForegroundWindow(), out uint foreground);
        return TryGrantHost(state.I("hostPID"), parent, Environment.ProcessId, foreground,
            state.B("demo") || Environment.GetEnvironmentVariable("CODEX_USAGE_TEST_BACKGROUND") == "1",
            pid =>
            {
                try
                {
                    using var host = Process.GetProcessById(pid); using var current = Process.GetCurrentProcess();
                    return !host.HasExited && host.StartTime <= current.StartTime &&
                        HostMatches(host.MainModule?.FileName, Environment.ProcessPath, host.SessionId, current.SessionId);
                }
                catch (Exception) { return false; }
            }, pid => AllowSetForegroundWindow((uint)pid));
    }
    internal static bool HostMatches(string? hostPath, string? currentPath, int hostSession, int currentSession) =>
        !string.IsNullOrWhiteSpace(hostPath) && !string.IsNullOrWhiteSpace(currentPath) && hostSession == currentSession &&
        string.Equals(Path.GetFullPath(hostPath), Path.GetFullPath(currentPath), StringComparison.OrdinalIgnoreCase);
    internal static bool TryGrantHost(int host, int parent, int current, uint foreground, bool background,
        Func<int, bool> trustedHost, Func<int, bool> grant)
    {
        if (background || host <= 1 || host != parent || host == current || foreground != current) return false;
        return trustedHost(host) && grant(host);
    }
}

internal static class J
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    public static JsonObject Obj(params (string key, object? value)[] pairs)
    {
        var obj = new JsonObject(); foreach (var p in pairs) obj[p.key] = p.value is JsonNode n ? n.DeepClone() : JsonSerializer.SerializeToNode(p.value); return obj;
    }
    public static JsonObject Copy(this JsonObject n) => (JsonObject)n.DeepClone();
    public static JsonObject O(this JsonNode? n, string key) => n?[key] as JsonObject ?? new();
    public static JsonArray A(this JsonNode? n, string key) => n?[key] as JsonArray ?? new();
    public static string S(this JsonNode? n, string key, string fallback = "") => n?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;
    public static double? N(this JsonNode? n, string key) => Number(n?[key]);
    public static double? Number(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        double d;
        if (v.TryGetValue<double>(out var real)) d = real;
        else if (v.TryGetValue<int>(out var integer)) d = integer;
        else if (v.TryGetValue<long>(out var wide)) d = wide;
        else if (v.TryGetValue<decimal>(out var precise)) d = (double)precise;
        else return null;
        return double.IsFinite(d) ? d : null;
    }
    public static int I(this JsonNode? n, string key, int fallback = 0) => n.N(key) is double d && d == Math.Truncate(d) && d >= int.MinValue && d <= int.MaxValue ? (int)d : fallback;
    public static bool B(this JsonNode? n, string key, bool fallback = false) => n?[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
    public static IEnumerable<JsonObject> Rows(this JsonArray rows) => rows.OfType<JsonObject>();
    public static JsonArray Array(IEnumerable<JsonObject> rows) => new(rows.Select(x => (JsonNode)x.DeepClone()).ToArray());
    public static string Exact(double? n) => n?.ToString("N0", CultureInfo.InvariantCulture) ?? "未知";
    public static string Compact(double? n) => n is not double d ? "—" : d >= 1e9 ? $"{d / 1e9:F2}B" : d >= 1e6 ? $"{d / 1e6:F2}M" : d >= 1e3 ? $"{d / 1e3:F1}K" : Exact(d);
    public static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
    public static string Date(double? n, string format = "MM/dd HH:mm") => n is double t ? DateTimeOffset.FromUnixTimeMilliseconds((long)(t * 1000)).LocalDateTime.ToString(format) : "—";
    public static string Text(JsonNode? n) => n?.ToJsonString(Options) ?? "null";
    public static JsonObject Parse(string s) => JsonNode.Parse(s) as JsonObject ?? throw new InvalidDataException("预期 JSON 对象");
    public static JsonObject Read(string path, int max = 2_097_152)
    {
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > max) throw new InvalidDataException("文件超出大小限制");
        return Parse(File.ReadAllText(path, Encoding.UTF8));
    }
    public static void Write(string path, JsonObject data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(Text(data)); stream.Write(bytes); stream.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

internal static class Paths
{
    public static string Base = Path.GetFullPath(Environment.GetEnvironmentVariable("CODEX_USAGE_DESKTOP_BASE") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageDashboard", "desktop"));
    public static string App => AppContext.BaseDirectory;
    public static string Python => Path.Combine(App, "python", "python.exe");
    public static string Backend => Path.Combine(App, "backend");
    public static string Identity(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant()))).ToLowerInvariant()[..24];
    public static string Pipe => "CodexUsage-" + Identity(Base);
    public static string Cache(string home) => Path.Combine(Base, "index-" + Identity(home) + ".sqlite");
}

internal sealed class Settings
{
    public JsonObject Data { get; private set; }
    public string Error { get; private set; } = "";
    private readonly string path;
    public Settings() : this(Path.Combine(Paths.Base, "settings.json")) { }
    internal Settings(string path)
    {
        this.path = path;
        try { Data = J.Read(path, 262144); } catch (Exception e) { Data = new(); Error = "设置文件无法读取，原文件已保留：" + e.Message; }
    }
    public string Home => Path.GetFullPath(Data.S("home", Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")));
    public int Refresh => Math.Clamp(Data.I("refresh", 5), 0, 3600);
    public JsonObject Floating => Data.O("floating");
    public void Update(JsonObject patch)
    {
        if (Error.Length > 0) throw new InvalidDataException(Error);
        var next = Data.Copy(); foreach (var item in patch) next[item.Key] = item.Value?.DeepClone();
        if (Encoding.UTF8.GetByteCount(J.Text(next)) > 262144) throw new InvalidDataException("设置过大");
        J.Write(path, next); Data = next;
    }
    public string Recover()
    {
        if (Error.Length == 0) throw new InvalidOperationException("设置可正常读取，无需恢复。");
        // An external repair must be adopted, never overwritten by a stale recovery prompt.
        JsonObject? repaired = null;
        try { repaired = J.Read(path, 262144); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        if (repaired is not null) { Data = repaired; Error = ""; return ""; }
        string backup = path + "." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "." + Guid.NewGuid().ToString("N") + ".backup";
        string replacement = path + "." + Guid.NewGuid().ToString("N") + ".recovery";
        try
        {
            J.Write(replacement, new());
            // Replacement and preservation of the exact original happen in one filesystem operation.
            File.Replace(replacement, path, backup);
            Data = new(); Error = ""; return backup;
        }
        finally { if (File.Exists(replacement)) File.Delete(replacement); }
    }
}

internal static class Ipc
{
    public static async Task<JsonObject> Send(JsonObject request, string? pipe = null, int timeout = 12000)
    {
        using var cancel = new CancellationTokenSource(timeout);
        using var client = new NamedPipeClientStream(".", pipe ?? Paths.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(cancel.Token);
        await Write(client, request, cancel.Token);
        var reply = await Read(client, cancel.Token);
        if (reply.S("error").Length > 0) throw new InvalidOperationException(reply.S("error"));
        return reply;
    }
    public static async Task Listen(string pipe, Func<JsonObject, Task<JsonObject>> handler, CancellationToken cancellation, Action<JsonObject>? replied = null, Action<JsonObject>? replyFailed = null)
    {
        while (!cancellation.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await server.WaitForConnectionAsync(cancellation);
                _ = HandleClient(server, handler, cancellation, replied, replyFailed);
            }
            catch (OperationCanceledException) { server.Dispose(); if (cancellation.IsCancellationRequested) return; }
            catch (IOException) { server.Dispose(); }
        }
    }
    private static async Task HandleClient(NamedPipeServerStream server, Func<JsonObject, Task<JsonObject>> handler, CancellationToken cancellation, Action<JsonObject>? replied, Action<JsonObject>? replyFailed)
    {
        using (server)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(15000);
                JsonObject response; JsonObject? request = null;
                try { request = await Read(server, deadline.Token); response = await handler(request); }
                catch (Exception e) { response = J.Obj(("error", e.Message)); }
                try { await Write(server, response, deadline.Token); }
                catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    if (request is not null && !response.ContainsKey("error")) replyFailed?.Invoke(request);
                    throw;
                }
                if (request is not null && !response.ContainsKey("error")) replied?.Invoke(request);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }
    private static async Task<JsonObject> Read(Stream stream, CancellationToken token)
    {
        byte[] size = new byte[4]; await stream.ReadExactlyAsync(size, token);
        int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(size);
        if (length < 2 || length > 2_097_152) throw new InvalidDataException("消息大小无效");
        byte[] data = new byte[length]; await stream.ReadExactlyAsync(data, token); return J.Parse(Encoding.UTF8.GetString(data));
    }
    private static async Task Write(Stream stream, JsonObject obj, CancellationToken token)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(J.Text(obj));
        if (bytes.Length > 2_097_152) throw new InvalidDataException("消息过大");
        byte[] size = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(size, bytes.Length);
        await stream.WriteAsync(size, token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }
}
