using System;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace CodexUsage;

// Reproduce independent query/push completion order using the real MainWindow.
// The demo window is never shown and never starts a worker or reads user data.
internal static class MainPushTests
{
    internal static async Task<JsonObject> RunAsync()
    {
        var checks = new JsonArray();
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("Main state push: " + description);
            checks.Add(description);
        }
        FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(MainWindow).FullName, name);
        var initial = DemoData.State(); initial["home"] = "fixture-home-A"; initial["cache"] = "fixture-cache-A";
        initial["hostPID"] = 4321; initial["stateRevision"] = 10;
        var window = new MainWindow(initial);
        JsonObject Choices() => (JsonObject)Field("budgetChoices").GetValue(window)!;
        string Stamp() => (string)Field("choicesStamp").GetValue(window)!;
        int Refresh() => (int)Field("refreshSeconds").GetValue(window)!;
        Task Push(JsonObject state) => window.Handle(J.Obj(("action", "host-state"), ("targetPID", Environment.ProcessId), ("state", state)));
        try
        {
            // Match an already-started source without starting its real backend.
            Field("currentHome").SetValue(window, "fixture-home-A");
            Field("currentCache").SetValue(window, "fixture-cache-A");
            await Push(initial);
            var queryReply = initial.Copy();
            queryReply["choices"] = J.Obj(("models", new JsonArray("new-model-A")), ("tasks", new JsonArray()));

            var newerPush = initial.Copy(); newerPush["stateRevision"] = 11; newerPush.O("settings")["refresh"] = 30;
            newerPush.O("settings")["quotaEnabled"] = false;
            await Push(newerPush);
            Check(Refresh() == 30, "newer host push changes current refresh settings");
            await window.Handle(J.Obj(("action", "state"), ("state", queryReply)));
            Check(Refresh() == 30 && ((JsonObject)Field("state").GetValue(window)!).O("settings")["quotaEnabled"]?.GetValue<bool>() == false,
                "late global reply cannot roll back newer refresh or account settings");
            Check(window.AcceptBudgetChoices(queryReply, "fixture-home-A", "fixture-cache-A", "scan-A"),
                "same-source query is accepted even after a newer unrelated host revision");
            Check(Choices().A("models")[0]?.GetValue<string>() == "new-model-A" && Stamp() == "scan-A" && Refresh() == 30,
                "query replaces budget choices without replacing newer host settings");
            queryReply.O("choices").A("models")[0] = "mutated-reply";
            Check(Choices().A("models")[0]?.GetValue<string>() == "new-model-A", "query results are copied before ownership transfers");

            var wrongCache = queryReply.Copy(); wrongCache["cache"] = "other-cache";
            Check(!window.AcceptBudgetChoices(wrongCache, "fixture-home-A", "fixture-cache-A", "wrong-scan") && Stamp() == "scan-A",
                "reply metadata mismatch cannot mark the requested source as complete");
            var withoutChoices = initial.Copy(); withoutChoices.Remove("choices");
            Check(!window.AcceptBudgetChoices(withoutChoices, "fixture-home-A", "fixture-cache-A", "missing-scan") && Stamp() == "scan-A",
                "missing query payload preserves the last valid choices and completion stamp");

            var changedSource = newerPush.Copy(); changedSource["stateRevision"] = 12;
            changedSource["home"] = "fixture-home-B"; changedSource["cache"] = "fixture-cache-B";
            await Push(changedSource);
            Check(Choices().Count == 0 && Stamp().Length == 0, "changing data source clears previous budget choices immediately");
            Check(!window.AcceptBudgetChoices(queryReply, "fixture-home-A", "fixture-cache-A", "late-scan-A") && Choices().Count == 0 && Stamp().Length == 0,
                "late query from the previous source cannot repopulate the new source");
            var newQuery = changedSource.Copy(); newQuery["choices"] = J.Obj(("models", new JsonArray("model-B")), ("tasks", new JsonArray()));
            Check(window.AcceptBudgetChoices(newQuery, "fixture-home-B", "fixture-cache-B", "scan-B") && Choices().A("models")[0]?.GetValue<string>() == "model-B",
                "new source can publish its own matching query");
            await window.CompleteClose();
            Check(!window.AcceptBudgetChoices(newQuery, "fixture-home-B", "fixture-cache-B", "after-close"), "closed window rejects late query completion");
            return J.Obj(("success", true), ("checks", checks));
        }
        finally { await window.CompleteClose(); }
    }
}
