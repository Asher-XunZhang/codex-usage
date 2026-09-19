using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace CodexUsage;

// Tests the production event coordinator without a desktop or child process.
internal static class BackendEventTests
{
    private static JsonObject Event(string kind, int completed = 0) => J.Obj(
        ("protocol", 1), ("pid", 1234), ("instance_id", "event-fixture"), ("event", kind),
        ("url", "http://127.0.0.1:54321"), ("ready", true), ("scanning", false),
        ("refresh_completed", completed), ("refresh_error", null));

    public static async Task RunAsync()
    {
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException("Backend events: " + name); }
        async Task Fails(Task task, string name)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (IOException) { return; }
            throw new InvalidOperationException("Backend events: expected failure: " + name);
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var channel = new BackendEventChannel(1234, "event-fixture");
        channel.Accept(J.Text(Event("desktop_ready")));
        channel.Accept(J.Text(Event("desktop_state", 8)));
        Check((await channel.Wait(true, -1, deadline.Token)).S("url") == "http://127.0.0.1:54321", "early handshake retained");
        var early = await channel.Wait(false, 7, deadline.Token);
        Check(early.I("refresh_completed") == 8, "completion before waiter registration retained");
        early["refresh_completed"] = -1;
        Check(channel.Snapshot()!.I("refresh_completed") == 8, "returned snapshots do not mutate channel state");
        var ninth = channel.Wait(false, 9, deadline.Token);
        var tenth = channel.Wait(false, 10, deadline.Token);
        Check(!ninth.IsCompleted && !tenth.IsCompleted, "future receipts remain pending");
        channel.Accept(J.Text(Event("desktop_state", 9)));
        Check((await ninth.WaitAsync(deadline.Token)).I("refresh_completed") == 9 && !tenth.IsCompleted, "ticket broadcast does not finish newer receipt");
        channel.Accept(J.Text(Event("desktop_state", 10)));
        Check((await tenth.WaitAsync(deadline.Token)).I("refresh_completed") == 10, "next receipt completes");

        using var cancelled = new CancellationTokenSource();
        var cancelledWait = channel.Wait(false, 11, cancelled.Token);
        var survivingWait = channel.Wait(false, 11, deadline.Token);
        cancelled.Cancel();
        try { await cancelledWait; throw new InvalidOperationException("Backend events: cancellation lost"); }
        catch (OperationCanceledException) { }
        channel.Accept(J.Text(Event("desktop_state", 11)));
        Check((await survivingWait).I("refresh_completed") == 11, "one cancelled reader does not cancel shared signal");
        var closingWait = channel.Wait(false, 12, deadline.Token);
        channel.Stop("fixture closed");
        await Fails(closingWait, "close releases pending receipt");
        await Fails(channel.Wait(true, -1, deadline.Token), "close rejects cached handshake");
        channel.Accept(J.Text(Event("desktop_state", 12)));
        await Fails(channel.Wait(false, 12, deadline.Token), "late event cannot revive closed channel");

        var badEvents = new List<JsonObject>();
        foreach (var (key, value) in new (string, object)[] { ("protocol", 2), ("pid", 999), ("instance_id", "old-worker"), ("event", "other") })
        { var row = Event("desktop_ready"); row[key] = JsonSerializerNode(value); badEvents.Add(row); }
        foreach (string url in new[] { "https://127.0.0.1:1234", "http://localhost:1234", "http://user@127.0.0.1:1234", "http://127.0.0.1:0" })
        { var row = Event("desktop_ready"); row["url"] = url; badEvents.Add(row); }
        var invalidWires = new List<string> { "{invalid}\n", "[]\n", new string('x', 4097) + "\n", J.Text(Event("desktop_ready")), J.Text(Event("desktop_state", 1)) + "\n", J.Text(Event("desktop_ready")) + "\n" + J.Text(Event("desktop_ready")) + "\n" };
        invalidWires.AddRange(badEvents.ConvertAll(row => J.Text(row) + "\n"));
        foreach (string wire in invalidWires)
        {
            var invalid = new BackendEventChannel(1234, "event-fixture");
            using var input = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(wire)));
            await invalid.Read(input);
            await Fails(invalid.Wait(false, 1, deadline.Token), "invalid, out-of-order or truncated stream terminates wait");
        }

        var fragmented = new BackendEventChannel(1234, "event-fixture");
        int readyEvents = 0, stateEvents = 0, failures = 0;
        fragmented.Changed += () =>
        {
            try { if (fragmented.Snapshot() is null) readyEvents++; else stateEvents++; }
            catch (IOException) { failures++; }
        };
        using var fragmentedInput = new StreamReader(new ChunkedStream(Encoding.UTF8.GetBytes(J.Text(Event("desktop_ready")) + "\r\n" + J.Text(Event("desktop_state", 3)) + "\r\n")));
        await fragmented.Read(fragmentedInput);
        Check(readyEvents == 1 && stateEvents == 1 && failures == 1, "fragmented CRLF frames and clean EOF each publish once");
        fragmented.Stop("duplicate close");
        Check(failures == 1, "closure is idempotent");
        Console.WriteLine("Backend event tests passed: early receipts, concurrent tickets, snapshot ownership, cancellation, identity, framing, bounded lines and closure.");
    }
    private static JsonNode? JsonSerializerNode(object value) => System.Text.Json.JsonSerializer.SerializeToNode(value);
    private sealed class ChunkedStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(7, buffer.Length)], cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => base.ReadAsync(buffer, offset, Math.Min(7, count), cancellationToken);
    }
}
