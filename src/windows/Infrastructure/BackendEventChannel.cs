using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace CodexUsage;

// One reader and one latest snapshot per worker. No unbounded event/waiter queue.
internal sealed class BackendEventChannel
{
    private readonly object gate = new();
    private readonly int pid;
    private readonly string identity;
    private JsonObject? handshake, latest;
    private string? failure;
    private TaskCompletionSource pulse = NewPulse();
    internal event Action? Changed;
    internal BackendEventChannel(int pid, string identity) { this.pid = pid; this.identity = identity; }
    private static TaskCompletionSource NewPulse() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Signal() { var previous = pulse; pulse = NewPulse(); previous.TrySetResult(); }
    internal void Stop(string error)
    {
        lock (gate) { if (failure != null) return; failure = error; Signal(); }
        Changed?.Invoke();
    }
    internal JsonObject? Snapshot()
    {
        lock (gate) { if (failure != null) throw new IOException(failure); return latest?.Copy(); }
    }
    internal async Task<JsonObject> Wait(bool ready, int ticket, CancellationToken token)
    {
        while (true)
        {
            Task changed;
            lock (gate)
            {
                if (failure != null) throw new IOException(failure);
                var value = ready ? handshake : latest;
                if (value != null && (ready || value.I("refresh_completed", -1) >= ticket)) return value.Copy();
                changed = pulse.Task;
            }
            await changed.WaitAsync(token).ConfigureAwait(false);
        }
    }
    internal void Accept(string line)
    {
        if (line.Length > 4096) throw new InvalidDataException("统计服务事件超出大小限制");
        var value = J.Parse(line);
        if (value.I("protocol") != 1 || value.I("pid") != pid || value.S("instance_id") != identity
            || value.S("event") is not ("desktop_ready" or "desktop_state")) throw new InvalidDataException("统计服务事件身份不匹配");
        lock (gate)
        {
            if (failure != null) return;
            if (value.S("event") == "desktop_ready")
            {
                if (handshake != null || !Uri.TryCreate(value.S("url"), UriKind.Absolute, out var uri)
                    || uri.Scheme != "http" || uri.Host != "127.0.0.1" || uri.Port <= 0 || uri.UserInfo.Length > 0)
                    throw new InvalidDataException("统计服务握手无效");
                handshake = value;
            }
            else
            {
                if (handshake == null) throw new InvalidDataException("统计服务未握手即发送状态");
                latest = value;
            }
            Signal();
        }
        Changed?.Invoke();
    }
    internal async Task Read(StreamReader reader)
    {
        char[] buffer = new char[2048]; var line = new StringBuilder();
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                for (int i = 0; i < count; i++)
                {
                    if (buffer[i] == '\n') { Accept(line.ToString()); line.Clear(); }
                    else { if (line.Length >= 4096) throw new InvalidDataException("统计服务事件超出大小限制"); line.Append(buffer[i]); }
                }
            Stop(line.Length > 0 ? "统计服务事件被截断" : "统计服务连接已结束");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ObjectDisposedException or System.Text.Json.JsonException)
        { Stop("统计服务事件中断：" + e.Message); }
    }
}
