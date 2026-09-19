using System;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;

namespace CodexUsage;

internal static class CapsuleArcStyleTests
{
    internal static JsonObject Run()
    {
        var checks = new JsonArray();
        void Check(bool value, string description) { if (!value) throw new InvalidOperationException("Arc style: " + description); checks.Add(description); }
        foreach (bool light in new[] { false, true })
        {
            var builtin = new CapsuleArcStyle();
            foreach (double fraction in new[] { double.NaN, -1, 0, .05, .1, .25, .5, .75, 1, 2 })
                Check(builtin.Color(fraction, light) == CapsuleColors.Color(fraction, light), "legacy palette preserved for " + fraction + ", light=" + light);
            var solid = builtin with { Mode = "solid" };
            Check(solid.Color(0, light) == CapsuleColors.Color(1, light) && solid.Color(.5, light) == solid.Color(1, light), "default solid always uses theme full-quota color");
            var edited = builtin.SetColor(Colors.Blue, false, light);
            Check(edited.LowHex == "#0000FF" && edited.HighHex == CapsuleArcStyle.Hex(CapsuleColors.Color(1, light)), "editing one endpoint freezes both theme defaults into an explicit pair");
            Check(edited.ResetColors() == builtin, "gradient reset restores all built-in color stops");
            var both = edited with { Mode = "solid", SolidHex = "#ABCDEF" };
            Check(both.ResetColors().LowHex == edited.LowHex && both.ResetColors().SolidHex is null, "reset affects only current mode and retains other-mode draft");
        }
        var gradient = new CapsuleArcStyle(LowHex: "#000000", HighHex: "#FFFFFF");
        Check(gradient.Color(.5, false) == Color.FromRgb(128, 128, 128), "custom gradient interpolates sRGB endpoint components at the quota fraction");
        foreach (string value in new[] { "", "#FFF", "#AABBCCDD", "#GG0000", "#１２３４５６", "AABBCC" })
            Check(CapsuleArcStyle.Parse(value) is null, "rejects malformed color " + value);
        var floating = new JsonObject { ["arcStyle"] = gradient.ToJson() };
        Check(CapsuleArcStyle.Load(floating, out var warning) == gradient && warning is null, "saved style round-trips without losing its endpoint pair");
        floating.O("arcStyle")["lowHex"] = null; string original = floating.ToJsonString();
        Check(CapsuleArcStyle.Load(floating, out warning) == new CapsuleArcStyle() && warning is not null && floating.ToJsonString() == original, "corrupt style falls back with warning while preserving original storage");
        var state = new JsonObject { ["monitor"] = new JsonObject { ["summary"] = new JsonObject { ["unread"] = 120, ["active"] = 0, ["status"] = "completed" } } };
        Check(CapsuleMonitorBadge.ShowsDockedMonitor(state) && CapsuleMonitorBadge.Count(CapsuleMonitorBadge.Unread(state)) == "99+", "unread messages retain compact monitoring after the last task ends and cap at 99+");
        state.O("monitor").O("summary")["unread"] = 0;
        Check(!CapsuleMonitorBadge.ShowsDockedMonitor(state), "clearing the final unread message shortens a finished-task side tab");
        state.O("monitor").O("summary")["active"] = 1;
        Check(CapsuleMonitorBadge.ShowsDockedMonitor(state), "active recurring subscriptions retain side-tab monitor context even after a round completes");
        var work = new Rect(-1920, 0, 1920, 1080); var compact = new Rect(-76, 300, 76, 76);
        foreach (double scale in new[] { 1d, 1.25, 1.5, 2 }) foreach (CapsuleEdge edge in new[] { CapsuleEdge.Left, CapsuleEdge.Right, CapsuleEdge.Top, CapsuleEdge.Bottom })
        {
            var small = CapsulePlacement.Indicator(compact, work, edge, new(scale, scale));
            var large = CapsulePlacement.Indicator(compact, work, edge, new(scale, scale), true);
            Check(work.Contains(small) && work.Contains(large) && (edge is CapsuleEdge.Left or CapsuleEdge.Right ? large.Height > small.Height && large.Width == small.Width : large.Width > small.Width && large.Height == small.Height), "monitor side-tab expansion remains within work area at scale " + scale + ", " + edge);
        }
        return J.Obj(("success", true), ("checks", checks));
    }
}
