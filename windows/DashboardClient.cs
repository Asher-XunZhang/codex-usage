using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace CodexUsage;

// A panel owns one service and one Job Object. Closing the panel releases both.
internal sealed class DashboardClient : IAsyncDisposable
{
    private readonly HttpClient http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private ChildJob? job;
    private Process? process;
    private Uri? endpoint;
    private string identity = "", statePath = "", home = "";
    private string controlToken = "";
    private Task? stderrDrain, stdoutDrain;
    private string lastError = "";
    private bool disposed;
    public bool Running => process is { HasExited: false } && endpoint != null;

    public async Task Start(string codexHome, string cache, int refresh, CancellationToken token, bool force = false)
    {
        await lifecycle.WaitAsync(token);
        try
        {
            if (disposed) throw new ObjectDisposedException(nameof(DashboardClient));
            if (Running && home == codexHome && !force) return;
            await StopCore();
            home = codexHome;
            Directory.CreateDirectory(Paths.Base);
            identity = Guid.NewGuid().ToString("N");
            controlToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            statePath = Path.Combine(Paths.Base, $"main-service-{Environment.ProcessId}-{identity}.json");
            job = new ChildJob();
            process = job.StartWithEnvironment(Paths.Python,
                new Dictionary<string, string> { ["CODEX_USAGE_BACKEND_CONTROL_TOKEN"] = controlToken },
                "-E", "-s", "-B", Path.Combine(Paths.Backend, "dashboard_server.py"),
                "--port", "0", "--codex-home", codexHome, "--cache-path", cache,
                "--state-file", statePath, "--instance-id", identity, "--parent-pid", Environment.ProcessId.ToString(),
                "--refresh-seconds", refresh.ToString());
            process.StandardInput.Close();
            lastError = "";
            stderrDrain = Drain(process.StandardError, true);
            stdoutDrain = Drain(process.StandardOutput, false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(35));
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new IOException(lastError.Length > 0 ? lastError : "统计服务已退出");
                try
                {
                    var state = J.Read(statePath, 4096);
                    if (state.I("pid") == process.Id && Uri.TryCreate(state.S("url"), UriKind.Absolute, out var uri)
                        && uri.Scheme == "http" && uri.Host == "127.0.0.1" && uri.Port > 0)
                    {
                        endpoint = uri;
                        await Health(deadline.Token);
                        return;
                    }
                }
                catch (IOException) { }
                catch (System.Text.Json.JsonException) { }
                catch (HttpRequestException) { }
                await Task.Delay(80, deadline.Token);
            }
        }
        catch { await StopCore(); throw; }
        finally { lifecycle.Release(); }
    }

    private async Task Drain(StreamReader reader, bool errors)
    {
        char[] buffer = new char[2048];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer)) > 0)
            {
                if (errors) { lastError += new string(buffer, 0, count); if (lastError.Length > 8192) lastError = lastError[^8192..]; }
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    public async Task<JsonObject> Health(CancellationToken token)
    {
        var data = await Get("health", token);
        if (process == null || data.I("pid") != process.Id || data.S("instance_id") != identity
            || data.S("application") != "codex-token-usage-dashboard") throw new IOException("统计服务身份不匹配");
        return data;
    }
    public static string Query(string days, string model, string task, string group) =>
        $"days={Uri.EscapeDataString(days)}&model={Uri.EscapeDataString(model)}&task={Uri.EscapeDataString(task)}&group={Uri.EscapeDataString(group)}";
    public Task<JsonObject> Usage(string query, CancellationToken token) => Get("api/usage?" + query, token);
    public async Task<byte[]> Export(string query, CancellationToken token)
    {
        using var response = await http.GetAsync(Address("api/export.csv?" + query), token);
        response.EnsureSuccessStatusCode(); return await response.Content.ReadAsByteArrayAsync(token);
    }
    public Task<JsonObject> Configure(int refresh, CancellationToken token) => Post("api/settings", J.Obj(("refresh_seconds", refresh)), token);

    public async Task<JsonObject> Refresh(CancellationToken token)
    {
        var result = await Post("api/refresh", J.Obj(("wait_ms", 0)), token);
        int ticket = result.I("ticket", -1);
        if (ticket < 0) throw new IOException("统计服务返回了无效的刷新请求");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(120));
        while (true)
        {
            var health = await Health(deadline.Token);
            if (health.I("refresh_completed", -1) >= ticket)
            {
                if (health.S("refresh_error").Length > 0) throw new IOException(health.S("refresh_error"));
                return health;
            }
            await Task.Delay(200, deadline.Token);
        }
    }
    private Uri Address(string path) => new(endpoint ?? throw new IOException("统计服务尚未就绪"), path);
    private async Task<JsonObject> Get(string path, CancellationToken token)
    {
        using var response = await http.GetAsync(Address(path), token);
        response.EnsureSuccessStatusCode(); return J.Parse(await response.Content.ReadAsStringAsync(token));
    }
    private async Task<JsonObject> Post(string path, JsonObject data, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Address(path));
        request.Headers.Add("X-Codex-Instance", identity);
        if (path == "api/shutdown") request.Headers.Add("X-Codex-Control", controlToken);
        request.Content = new StringContent(J.Text(data), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, token);
        response.EnsureSuccessStatusCode(); return J.Parse(await response.Content.ReadAsStringAsync(token));
    }
    private async Task StopCore()
    {
        if (Running)
        {
            try { using var timeout = new CancellationTokenSource(1200); await Post("api/shutdown", new(), timeout.Token); }
            catch (Exception) { }
        }
        if (process is { HasExited: false })
        {
            try { using var timeout = new CancellationTokenSource(1200); await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { }
        }
        job?.Dispose(); job = null;
        process?.Dispose(); process = null; endpoint = null;
        try { if (statePath.Length > 0 && File.Exists(statePath)) File.Delete(statePath); } catch (IOException) { }
        statePath = ""; controlToken = "";
    }
    public async ValueTask DisposeAsync()
    {
        await lifecycle.WaitAsync();
        try { if (disposed) return; disposed = true; await StopCore(); http.Dispose(); }
        finally { lifecycle.Release(); }
    }
}
