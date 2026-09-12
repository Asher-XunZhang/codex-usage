using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace CodexUsage;

internal sealed record MainUsageIdentity(string Home, string Cache, string Query);
internal sealed record MainExportSnapshot(MainUsageIdentity Identity, long Serial, JsonObject Data);
internal sealed record MainTableSelection(string Search, string SortKey, bool Ascending)
{
    public List<JsonObject> Rows(JsonObject snapshot, bool visible = true)
    {
        var rows = snapshot.A("groups").Rows().Where(row => !visible || Search.Trim().Length == 0 || row.S("label").Contains(Search.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (!visible) return rows;
        BigInteger? Number(JsonNode? value)
        {
            string? raw = value?.ToJsonString();
            if (BigInteger.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) && integer >= 0) return integer;
            return decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n >= 0 && decimal.Truncate(n) == n ? new BigInteger(n) : null;
        }
        rows.Sort((a, b) =>
        {
            int label = StringComparer.CurrentCultureIgnoreCase.Compare(a.S("label"), b.S("label"));
            if (SortKey == "label") return Ascending ? label : -label;
            BigInteger? left = Number(a[SortKey]), right = Number(b[SortKey]);
            if (left is null) return right is null ? label : 1;
            if (right is null) return -1;
            int compared = left.Value.CompareTo(right.Value);
            return compared == 0 ? label : Ascending ? compared : -compared;
        });
        return rows;
    }
    public JsonObject ExportData(JsonObject snapshot, bool visible) { var result = snapshot.Copy(); result["groups"] = J.Array(Rows(snapshot, visible)); return result; }
}

// Owns query/snapshot identity, including requests that finish after a newer selection.
// A failed read remains dirty independently of the backend's generated_at timestamp.
internal sealed class MainUsageSession
{
    private Task<JsonObject>? reading;
    private int version;
    private long serial;
    public MainUsageIdentity? Current { get; private set; }
    public MainUsageIdentity? Displayed { get; private set; }
    public JsonObject Snapshot { get; private set; } = new();
    public bool NeedsRead { get; private set; } = true;
    public bool Reading => reading is { IsCompleted: false };
    public string Error { get; private set; } = "";
    public bool CanExport => !Reading && !NeedsRead && Error.Length == 0 && Current is not null && Current == Displayed
        && Snapshot.Count > 0 && !Snapshot.O("meta").B("loading");

    public bool Select(MainUsageIdentity identity)
    {
        if (Current == identity) return false;
        Current = identity; Invalidate(); return true;
    }
    public void Invalidate() { version++; NeedsRead = true; Error = ""; }
    public bool ShouldRead(string generatedAt) => NeedsRead || Current != Displayed || Snapshot.O("meta").S("generated_at") != generatedAt;
    public Task<JsonObject> LoadAsync(Func<MainUsageIdentity, CancellationToken, Task<JsonObject>> fetch, CancellationToken token)
    {
        if (reading is { IsCompleted: false }) return reading;
        return reading = Read(fetch, token);
    }
    private async Task<JsonObject> Read(Func<MainUsageIdentity, CancellationToken, Task<JsonObject>> fetch, CancellationToken token)
    {
        NeedsRead = true;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var request = Current ?? throw new InvalidOperationException("尚未选择统计范围");
            int requestVersion = version;
            JsonObject result;
            try { result = await fetch(request, token); }
            catch (Exception e)
            {
                if (requestVersion != version && !token.IsCancellationRequested) continue;
                NeedsRead = true; Error = e.Message; throw;
            }
            if (requestVersion != version) continue;
            Snapshot = result.Copy(); Displayed = request; serial++;
            NeedsRead = Snapshot.O("meta").B("loading"); Error = "";
            return Snapshot.Copy();
        }
    }
    public MainExportSnapshot CaptureExport()
    {
        if (!CanExport) throw new InvalidOperationException("当前筛选尚未成功更新，请重试读取后再导出。");
        return new(Current!, serial, Snapshot.Copy());
    }
    public void ValidateExport(MainExportSnapshot captured)
    {
        if (!CanExport || captured.Identity != Current || captured.Serial != serial)
            throw new InvalidOperationException("数据来源、筛选或快照已变化，请再次导出当前结果。");
    }
    public static byte[] Csv(JsonObject snapshot)
    {
        string Escape(string value)
        {
            if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        var lines = new List<string> { "范围,输入,其中缓存输入,非缓存输入,输出（含推理）,总数,模型调用" };
        foreach (var row in snapshot.A("groups").Rows())
            lines.Add(string.Join(",", new[] { Escape(row.S("label")) }.Concat(new[] { "input_tokens", "cached_input_tokens", "noncached_input_tokens", "output_tokens", "total_tokens", "requests" }
                .Select(key => row[key]?.ToJsonString() ?? "不可统计"))));
        return new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n")).ToArray();
    }
}

internal sealed class MainSettingsQueue
{
    private JsonObject pending = new();
    private Task? saving;
    public bool HasPending => pending.Count > 0 || saving is { IsCompleted: false };
    public string Error { get; private set; } = "";
    public void Enqueue(JsonObject patch) { foreach (var item in patch) pending[item.Key] = item.Value?.DeepClone(); }
    public Task FlushAsync(Func<JsonObject, Task> send)
    {
        if (saving is { IsCompleted: false }) return saving;
        return saving = Flush(send);
    }
    private async Task Flush(Func<JsonObject, Task> send)
    {
        while (pending.Count > 0)
        {
            var batch = pending; pending = new();
            try { await send(batch); }
            catch (Exception e)
            {
                // A new selection made during the failed save always wins over the old batch.
                foreach (var item in pending) batch[item.Key] = item.Value?.DeepClone();
                pending = batch; Error = e.Message; throw;
            }
        }
        Error = "";
    }
}
