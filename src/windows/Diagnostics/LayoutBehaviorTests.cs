using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexUsage;

internal static class LayoutBehaviorTests
{
    internal static async Task<JsonObject> RunAsync(string? frames = null)
    {
        var checks = new JsonArray();
        void Check(bool condition, string description) { if (!condition) throw new InvalidOperationException("Layout behavior: " + description); checks.Add(description); }
        var legacy = J.Obj(("mainTheme", "light"), ("floating", J.Obj(("theme", "dark"))));
        Check(!Theme.Resolve(legacy, "main") && Theme.Resolve(legacy, "floating") && Theme.Resolve(legacy, "tray"), "legacy independent themes remain visible after upgrade");
        var unified = J.Obj(("appearance", J.Obj(("theme", "dark"), ("overrides", J.Obj(("tray", "light"))))));
        Check(Theme.Resolve(unified, "main") && Theme.Resolve(unified, "floating") && !Theme.Resolve(unified, "tray"), "global appearance and independent tray override resolve without depending on float visibility");
        unified.O("appearance")["overrides"] = new JsonObject();
        Check(Theme.Resolve(unified, "tray"), "removing overrides restores global inheritance");
        var invalid = J.Obj(("theme", "invalid")); bool rejected = false; try { Theme.ValidateAppearance(invalid); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "invalid appearance values fail before persisting");
        var malformed = J.Obj(("appearance", J.Obj(("theme", "light"), ("overrides", J.Obj(("floating", true))))));
        Check(Theme.Appearance(malformed).O("overrides").S("floating") == "inherit" && !Theme.Resolve(malformed, "floating"), "malformed stored override safely inherits instead of breaking settings");
        var state = DemoData.State(); state["settings"] = legacy.Copy(); state.O("settings")["mode"] = "tray";
        state["updates"] = J.Obj(("local", J.Obj(("updatedAt", 1789190000), ("status", "本地日志已更新"))), ("quota", J.Obj(("enabled", true), ("updatedAt", 1789190001), ("status", "账号额度已更新"))));
        bool fail = false; var requests = new List<JsonObject>();
        Task<JsonObject> Send(JsonObject request)
        {
            requests.Add(request.Copy()); if (fail) throw new IOException("合成配置保存失败");
            switch (request.S("action"))
            {
                case "settings": foreach (var (key, value) in request.O("patch")) state.O("settings")[key] = value?.DeepClone(); break;
                case "mode": state.O("settings")["mode"] = request.S("value"); break;
                case "floating-settings": var next = state.O("settings").O("floating").Copy(); foreach (var (key, value) in request.O("patch")) next[key] = value?.DeepClone(); state.O("settings")["floating"] = next; break;
            }
            return Task.FromResult(state.Copy());
        }
        var settings = new SettingsWindow(() => state.Copy(), Send);
        try
        {
            foreach (string theme in new[] { "light", "dark" }) foreach (string page in new[] { "appearance", "display", "updates", "help" }) foreach (double width in new[] { 470d, 650d })
            {
                state.O("settings")["appearance"] = J.Obj(("theme", theme)); settings.SelectPage(page);
                var content = (FrameworkElement)settings.Content; content.Measure(new Size(width - 16, 600)); content.Arrange(new Rect(0, 0, width - 16, 600)); content.UpdateLayout();
                Check(ControlsFit(content), $"{page} settings actions fit at {width} DIP in {theme}");
                if (frames != null && width == 650) Save(content, Path.Combine(frames, "settings-" + page + "-" + theme + ".png"));
            }
            settings.SelectPage("appearance");
            var global = Descendants<ComboBox>((DependencyObject)settings.Content).First(); global.SelectedItem = global.Items.Cast<object>().First(x => x.ToString() == "浅色");
            Check(state.O("settings").O("appearance").S("theme") == "light", "global theme selection saves from the common settings form");
            settings.SelectPage("updates"); state.O("settings")["refresh"] = 17; settings.Refresh();
            Check(Descendants<ComboBox>((DependencyObject)settings.Content).Single().SelectedItem?.ToString() == "每 17 秒", "custom or externally changed refresh interval remains correctly represented after polling");
            settings.SelectPage("display");
            var modes = Descendants<ComboBox>((DependencyObject)settings.Content).First(); modes.SelectedItem = modes.Items.Cast<object>().First(x => x.ToString() == "托盘与悬浮窗");
            Check(requests.Last().S("action") == "mode" && state.O("settings").S("mode") == "both", "display selection issues a residency change, not a close or quit command");
            fail = true; modes.SelectedItem = modes.Items.Cast<object>().First(x => x.ToString() == "仅系统托盘");
            settings.SelectPage("appearance"); settings.SelectPage("display");
            Check(Descendants<ComboBox>((DependencyObject)settings.Content).First().SelectedItem?.ToString() == "仅系统托盘", "failed settings remain visible when moving between settings pages");
            bool held = false; try { await settings.PrepareClose(); } catch (IOException) { held = true; }
            Check(held && state.O("settings").S("mode") == "both", "failed settings save preserves the prior mode and prevents silent close");
            fail = false; await settings.PrepareClose(); Check(state.O("settings").S("mode") == "tray", "retrying close commits retained settings before closing");
        }
        finally { fail = false; await settings.PrepareClose(); }
        var detail = new TrayDetailWindow(); int opened = 0, refreshed = 0, configured = 0;
        detail.OpenMain = () => opened++; detail.RefreshData = () => refreshed++; detail.OpenSettings = () => configured++;
        try
        {
            foreach (string theme in new[] { "light", "dark" })
            {
                state.O("settings")["appearance"] = J.Obj(("theme", theme)); detail.Update(state);
                detail.Card.Measure(new Size(368, 570)); detail.Card.Arrange(new Rect(0, 0, 368, 570)); detail.Card.UpdateLayout();
                Check(ControlsFit(detail.Card) && detail.Focusable && detail.IsHitTestVisible, "clicked tray card keeps readable interactive controls in " + theme);
                if (frames != null) { var card = (FrameworkElement)detail.Content; card.Measure(new Size(370, double.PositiveInfinity)); Save(card, Path.Combine(frames, "tray-detail-" + theme + ".png"), card.DesiredSize); }
            }
            var text = Descendants<TextBox>(detail.Card).First(x => x.FontSize == 32); string original = text.Text; detail.Update(state);
            Check(text.IsReadOnly && text.Text == original, "tray values support selection and copying without replacing controls during refresh");
            foreach (var button in Descendants<Button>(detail.Card)) if (button.Content?.ToString() is "打开主面板" or "刷新" or "设置") button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(opened == 1 && refreshed == 1 && configured == 1, "clicked card independently dispatches main, refresh and common settings actions");
        }
        finally { detail.Close(); }
        using (var tray = new Tray())
        {
            var original = tray.EnsureDetail(); original.Close(); var reopened = tray.EnsureDetail();
            Check(!ReferenceEquals(original, reopened), "Alt+F4 or standard close discards the old tray detail Window before the next open");
        }
        var budgetLayouts = BudgetRecoveryTests.RenderLayouts(frames == null ? null : Path.Combine(frames, "budgets"));
        var capsuleButtons = CapsuleButtonLayoutTests.Run(frames == null ? null : Path.Combine(frames, "capsule-buttons"));
        Check(capsuleButtons.B("success"), "capsule action glyphs align inside hit rectangles: " + string.Join("; ", capsuleButtons.A("failures").Take(4)));
        return J.Obj(("success", true), ("checks", checks), ("budgetLayouts", budgetLayouts), ("capsuleButtons", capsuleButtons), ("windowActivation", "none: measurement and routed controls only"));
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T found) yield return found;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) foreach (var item in Descendants<T>(VisualTreeHelper.GetChild(parent, i))) yield return item;
    }
    private static bool ControlsFit(FrameworkElement root)
    {
        foreach (var control in Descendants<Control>(root).Where(x => x.Visibility == Visibility.Visible && x.ActualWidth > 0 && x is Button or ComboBox or CheckBox))
        {
            var bounds = control.TransformToAncestor(root).TransformBounds(new Rect(control.RenderSize));
            if (bounds.Left < -1 || bounds.Right > root.ActualWidth + 1) return false;
        }
        return true;
    }
    private static void Save(FrameworkElement element, string path, Size? requested = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var size = requested ?? element.RenderSize;
        element.Measure(size); element.Arrange(new Rect(new Point(), size)); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(path); encoder.Save(output);
    }
}
