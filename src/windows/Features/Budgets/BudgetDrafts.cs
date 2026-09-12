using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace CodexUsage;

/// <summary>Each editor owns a draft by rule ID; navigation never discards another editor's work.</summary>
internal sealed class BudgetDrafts
{
    private readonly Dictionary<string, JsonObject> entries = new(StringComparer.Ordinal);
    public string ActiveId { get; private set; } = "";
    public long Revision { get; private set; }
    public IEnumerable<JsonObject> Items => entries.Values.OrderByDescending(x => x.N("updatedAt") ?? 0).Select(x => x.Copy());
    public void Load(JsonObject? saved)
    {
        entries.Clear(); ActiveId = ""; Revision = 0; if (saved == null) return;
        if (saved.I("version") == 2)
        {
            foreach (var draft in saved.A("drafts").Rows()) Read(draft);
            ActiveId = saved.S("activeId");
        }
        else { Read(saved); ActiveId = saved.O("rule").S("id"); }
        if (!entries.ContainsKey(ActiveId)) ActiveId = entries.Keys.FirstOrDefault() ?? "";
    }
    private void Read(JsonObject draft)
    {
        string id = draft.O("rule").S("id");
        if (id.Length > 0 && draft["fields"] is JsonObject) entries[id] = draft.Copy();
    }
    public JsonObject? Find(string id) => entries.TryGetValue(id, out var draft) ? draft.Copy() : null;
    public void Capture(JsonObject draft, double? now = null)
    {
        string id = draft.O("rule").S("id"); if (id.Length == 0) throw new ArgumentException("草稿缺少预算标识");
        if (entries.TryGetValue(id, out var existing))
        {
            var previous = existing.Copy(); previous.Remove("updatedAt"); var current = draft.Copy(); current.Remove("updatedAt");
            if (JsonNode.DeepEquals(previous, current)) { if (ActiveId != id) { ActiveId = id; Revision++; } return; }
        }
        var copy = draft.Copy(); copy["updatedAt"] = now ?? J.Now; entries[id] = copy; ActiveId = id;
        Revision++;
    }
    public void Remove(string id)
    {
        if (!entries.Remove(id)) return; Revision++; if (ActiveId == id) ActiveId = Items.FirstOrDefault()?.O("rule").S("id") ?? "";
    }
    public JsonObject? Snapshot() => entries.Count == 0 ? null : J.Obj(("version", 2), ("activeId", ActiveId), ("drafts", J.Array(Items)));
}
