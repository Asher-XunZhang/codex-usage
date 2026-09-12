using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexUsage;

// Exercises the real surface, its virtual UIA children and WPF menu popups with
// synthetic state. Routed events and provider calls never send operating-system input.
internal static class CapsuleAccessibilityTests
{
    private static void CheckMenuFocusPolicy(Action<bool, string> check)
    {
        check(CapsuleWindow.ShouldRestoreMenuFocus(true, null, true), "escape-cancel-can-return-focus-while-capsule-owns-foreground");
        check(!CapsuleWindow.ShouldRestoreMenuFocus(true, null, false), "outside-dismiss-cannot-reactivate-background-capsule");
        check(!CapsuleWindow.ShouldRestoreMenuFocus(false, null, true), "mouse-menu-does-not-start-keyboard-navigation");
        check(new[] { "period", "model", "task", "content", "budget", "keepExpanded", "pin", "edgeAutoHide", "edgeMetric", "budgetPause", "budgetResume", "refresh" }
            .All(command => CapsuleWindow.ShouldRestoreMenuFocus(true, command, true)), "local-menu-commands-retain-trigger-focus");
        check(new[] { "main", "details", "updateStatus", "update-status", "settings-dialog", "budgetEdit", "budgetManage", "view-budget", "mode", "only", "menu", "close", "hide-floating", "quit", "future-navigation-command" }
            .All(command => !CapsuleWindow.ShouldRestoreMenuFocus(true, command, true)), "navigation-and-new-commands-never-reclaim-capsule-focus");
        check(!CapsuleWindow.ShouldRestoreMenuFocus(true, "refresh", false), "completed-async-command-cannot-steal-foreground-back");
    }
    private static async Task<JsonObject> RunBackgroundAsync()
    {
        var state = DemoData.State(); var f = state.O("settings").O("floating");
        f["pinned"] = false; f["edgeAutoHide"] = false; f["keepExpanded"] = false;
        var checks = new JsonArray(); var commands = new List<(string Name, string? Value)>();
        CapsuleWindow? window = null;
        void Check(bool value, string id) { if (!value) throw new InvalidOperationException("Offscreen capsule: " + id); checks.Add(id); }
        CheckMenuFocusPolicy(Check);
        Task Action(string command, string? value)
        {
            commands.Add((command, value));
            if (command == "content") f["content"] = value;
            if (command == "keepExpanded") f["keepExpanded"] = value == "true";
            if (command == "filters-reset") { f["model"] = "all"; f["task"] = "all"; }
            window!.Update(state); return Task.CompletedTask;
        }
        window = new CapsuleWindow(Action, () => Task.FromResult(state.Copy()), () => null) { ShowActivated = false, Topmost = false };
        try
        {
            var surface = window.Surface;
            void Arrange()
            {
                surface.Expansion = 1; surface.AnimationBounds = null; surface.Edge = CapsuleEdge.None;
                surface.CompactBounds = new Rect(260, 0, 76, 76); surface.PanelBounds = new Rect(0, 0, 336, 410);
                surface.HostSize = new Size(336, 410); surface.Measure(new Size(336, 410)); surface.Arrange(new Rect(0, 0, 336, 410)); surface.Redraw();
            }
            byte[] Pixels(int height = 410)
            {
                var bitmap = new RenderTargetBitmap(336, 410, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
                var pixels = new byte[336 * height * 4]; bitmap.CopyPixels(new Int32Rect(0, 0, 336, height), pixels, 336 * 4, 0); return pixels;
            }
            window.Update(state); Arrange();
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(surface)!;
            AutomationPeer Child(string name) => peer.GetChildren().Single(x => x.GetAutomationId() == name);
            var expected = new[] { "details", "contentUsage", "contentBudget", "keepExpanded", "more", "period", "filters", "updateStatus", "refresh", "main", "collapse" };
            Check(surface.KeyboardOrder().SequenceEqual(expected), "reading-order-matches-visible-navigation-and-footer");
            Check(!surface.Regions().Any(x => x.name is "themeDark" or "interval" or "close" or "only"), "low-frequency-preferences-and-ambiguous-footer-removed");
            Check(Child("contentUsage").GetAutomationControlType() == AutomationControlType.RadioButton &&
                ((ISelectionItemProvider)Child("contentUsage").GetPattern(PatternInterface.SelectionItem)).IsSelected, "uia-navigation-exposes-selected-usage");
            Check(Child("keepExpanded").GetPattern(PatternInterface.Toggle) is IToggleProvider && Child("filters").GetPattern(PatternInterface.ExpandCollapse) is IExpandCollapseProvider,
                "uia-retention-and-filter-expansion-patterns-present");
            var header = Pixels(52); string today = surface.TopValue; double week = surface.Level;
            f["days"] = "30"; f["model"] = "selected model"; f["task"] = "selected task";
            state.O("filtered").O("summary")["total_tokens"] = 123;
            window.Update(state); Arrange();
            Check(today == surface.TopValue && week == surface.Level && header.SequenceEqual(Pixels(52)), "top-battery-today-and-week-ignore-local-filters");
            var officialWeek = CapsuleUsageDisplay.Week(state); officialWeek["remaining"] = 91;
            state.O("quota").A("windows").Rows().First(x => x.I("duration_minutes") != 10080)["remaining"] = 2;
            window.Update(state); Arrange();
            Check(Math.Abs(surface.Level - .91) < .000001, "weekly-summary-does-not-switch-to-tighter-short-window");
            state.O("quota")["windows"] = new JsonArray(J.Obj(("duration_minutes", 300), ("remaining", 50)));
            window.Update(state); Arrange();
            Check(surface.Level == -1, "missing-week-is-unknown-even-when-short-quota-exists");
            state["quota"] = DemoData.Quota(); window.Update(state); Arrange();
            await surface.ActivateActionAsync("filters", false);
            Check(surface.FiltersOpen && surface.KeyboardOrder().Contains("model") && surface.KeyboardOrder().Contains("task") &&
                ((IExpandCollapseProvider)Child("filters").GetPattern(PatternInterface.ExpandCollapse)).ExpandCollapseState == ExpandCollapseState.Expanded,
                "expanded-filters-are-reachable-and-announced");
            await surface.ActivateActionAsync("filtersReset", false);
            Check(commands.Last().Name == "filters-reset" && f.S("model") == "all" && f.S("task") == "all", "clear-filters-is-one-atomic-command");
            await surface.ActivateActionAsync("filters", false);
            Check(!surface.FiltersOpen && !surface.KeyboardOrder().Contains("model") && !surface.KeyboardOrder().Contains("task"), "folded-filters-remove-hidden-actions");
            await surface.ActivateActionAsync("details", false);
            Check(commands.Last().Name == "main", "primary-action-keeps-open-main-semantics");
            await surface.ActivateActionAsync("contentBudget", false); Arrange();
            Check(surface.BudgetMode && ((ISelectionItemProvider)Child("contentBudget").GetPattern(PatternInterface.SelectionItem)).IsSelected,
                "shared-content-command-updates-uia-selection");
            f["budgetID"] = "daily"; window.Update(state); Arrange();
            Check(surface.TopValue == J.Compact(state.O("budgets").A("summaries").Rows().First(x => x.S("id") == "daily").N("remaining")), "budget-battery-displays-remaining-not-used");
            var selectedBudget = state.O("budgets").A("summaries").Rows().First(x => x.S("id") == "daily");
            var previousBudget = selectedBudget.Copy();
            selectedBudget["kind"] = "money"; selectedBudget["currency"] = "USD"; selectedBudget["remaining"] = .5;
            window.Update(state); Arrange(); Check(surface.TopValue == "$0.50", "money-battery-preserves-cents");
            selectedBudget["remaining"] = -2000000; window.Update(state); Arrange();
            Check(surface.TopValue == "$−2.00M", "over-budget-battery-keeps-negative-sign-and-compact-scale");
            selectedBudget["kind"] = previousBudget["kind"]?.DeepClone(); selectedBudget["remaining"] = previousBudget["remaining"]?.DeepClone();
            window.Update(state); Arrange();
            await surface.ActivateActionAsync("view-budget", false);
            Check(commands.Last().Name == "view-budget", "budget-details-opens-selected-budget-command");
            Check(!surface.KeyboardOrder().Contains("budgetScope") && !Child("budgetScope").IsEnabled(), "readonly-budget-scope-cannot-activate");
            state.O("budgets").A("summaries").Rows().First(x => x.S("id") == "daily")["paused"] = true;
            window.Update(state); Arrange(); await surface.ActivateActionAsync("budgetResume", false);
            Check(commands.Last().Name == "budgetResume", "paused-budget-exposes-resume-command");
            await surface.ActivateActionAsync("keepExpanded", false); Arrange();
            Check(window.KeepsExpanded && f.B("keepExpanded") && ((IToggleProvider)Child("keepExpanded").GetPattern(PatternInterface.Toggle)).ToggleState == ToggleState.On,
                "retention-persists-and-announces-toggle-state");
            window.Collapse();
            Check(surface.Expansion == 0 && window.KeepsExpanded, "active-collapse-preserves-retention-preference");
            window.Update(state);
            Check(surface.Expansion == 0, "snapshot-does-not-reopen-an-actively-collapsed-window");
            f["keepExpanded"] = false; window.Update(state); Arrange();

            surface.CaptureTextBounds = true;
            foreach (bool light in new[] { false, true }) foreach (bool budget in new[] { false, true }) foreach (bool filters in new[] { false, true })
            {
                f["theme"] = light ? "light" : "dark"; f["content"] = budget ? "budget" : "usage";
                f["model"] = new string('模', 100); f["task"] = new string('任', 100);
                state.O("budgets").A("summaries").Rows().First(x => x.S("id") == "daily")["name"] = new string('预', 100);
                if (surface.FiltersOpen != filters) surface.ToggleFilters();
                window.Update(state); Arrange(); Pixels();
                Check(surface.VisibleTextBounds.All(x => CapsuleMorph.FullyContainsRounded(x.Bounds, surface.DrawingBounds, surface.CurrentRadius, 0)),
                    $"long-content-stays-inside-{(light ? "light" : "dark")}-{(budget ? "budget" : "usage")}-{filters}");
                Check(surface.Regions().Where(x => x.name != "context").All(x => surface.DrawingBounds.Contains(x.bounds)), "control-bounds-stay-inside-" + checks.Count);
            }
            Check(!window.IsVisible && !window.IsActive, "background-fixture-never-shows-or-activates-a-native-window");
            return J.Obj(("success", true), ("version", 2), ("checks", checks), ("background", true),
                ("skipped", new JsonArray("Native keyboard focus and focus pixels", "Visible popup keyboard selection and focus return", "Actual OS UIA event delivery")),
                ("skipReason", "CODEX_USAGE_TEST_BACKGROUND=1: do not activate windows or interrupt the user's foreground application."),
                ("scope", "Unshown WPF rendering, synthetic actions and UIA provider semantics; no pointer movement, activation or user settings."));
        }
        finally { window.Close(); }
    }
    public static async Task<JsonObject> RunAsync()
    {
        if (Environment.GetEnvironmentVariable("CODEX_USAGE_TEST_BACKGROUND") == "1") return await RunBackgroundAsync();
        var app = Application.Current ?? throw new InvalidOperationException("A WPF dispatcher is required.");
        var shutdown = app.ShutdownMode; app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var state = DemoData.State();
        var floating = state.O("settings").O("floating");
        floating["theme"] = "dark"; floating["pinned"] = false; floating["edgeAutoHide"] = false;
        floating["keepExpanded"] = false;
        var actions = new List<(string Command, string? Value)>();
        var checks = new JsonArray();
        CapsuleWindow? window = null;
        Window? navigationTarget = null;
        void Check(bool condition, string id)
        {
            if (!condition) throw new InvalidOperationException("Capsule accessibility: " + id);
            checks.Add(id);
        }
        CheckMenuFocusPolicy(Check);
        async Task Wait(Func<bool> condition)
        {
            var watch = Stopwatch.StartNew();
            while (!condition() && watch.ElapsedMilliseconds < 2000) await Task.Delay(10);
            if (!condition()) throw new InvalidOperationException("Capsule accessibility: asynchronous action timed out.");
        }
        Task Action(string command, string? value)
        {
            actions.Add((command, value));
            if (command == "pin") floating["pinned"] = !window!.Topmost;
            if (command == "keepExpanded") floating["keepExpanded"] = value == "true";
            if (command is "themeLight" or "themeDark") floating["theme"] = command == "themeLight" ? "light" : "dark";
            if (command == "period") floating["days"] = value;
            if (command == "content") floating["content"] = value;
            if (command is "pin" or "keepExpanded" or "themeLight" or "themeDark" or "period" or "content") window!.Update(state);
            if (command == "updateStatus")
            {
                navigationTarget ??= new Window { Title = "合成设置焦点目标", Width = 320, Height = 180, ShowInTaskbar = false, Content = new TextBox { Text = "合成数据更新设置" } };
                navigationTarget.Show(); navigationTarget.Activate();
            }
            return Task.CompletedTask;
        }
        window = new CapsuleWindow(Action, () => Task.FromResult(state.Copy()), () => null)
        {
            ShowActivated = false,
            Left = SystemParameters.WorkArea.Left + 120,
            Top = SystemParameters.WorkArea.Top + 60
        };
        try
        {
            window.Update(state); window.Show(); await Task.Delay(60); window.UpdateLayout();
            var surface = window.Surface;
            var parent = FrameworkElementAutomationPeer.CreatePeerForElement(surface)!;
            AutomationPeer Peer(string id)
            {
                var children = parent.GetChildren();
                return children.SingleOrDefault(x => x.GetAutomationId() == id) ?? throw new InvalidOperationException(
                    $"Missing capsule UIA child {id}; actual={string.Join(',', children.Select(x => x.GetAutomationId()))}; expansion={surface.Expansion}; edge={surface.Edge}; after={checks.LastOrDefault()}");
            }
            async Task UiInvoke(string id)
            {
                int count = actions.Count;
                ((IInvokeProvider)Peer(id).GetPattern(PatternInterface.Invoke)).Invoke();
                await Wait(() => actions.Count > count);
            }
            void RoutedKey(Key key)
            {
                surface.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(surface), Environment.TickCount, key)
                { RoutedEvent = Keyboard.KeyDownEvent });
            }
            byte[] Pixels()
            {
                surface.UpdateLayout(); surface.Redraw();
                int width = (int)Math.Ceiling(surface.ActualWidth), height = (int)Math.Ceiling(surface.ActualHeight);
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(surface); var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0); return pixels;
            }

            window.Expand(false, false);
            await surface.ActivateActionAsync(surface.Hit(surface.DrawingBounds.TopLeft + new Vector(38, 38))!, false);
            Check(actions.Last().Command == "main" && !window.KeepsExpanded, "compact-mouse-primary-opens-main");
            window.Expand(false, false); await UiInvoke("details");
            Check(actions.Last().Command == "main" && !window.KeepsExpanded, "compact-uia-primary-opens-main");
            surface.FocusAction("details", true); int before = actions.Count; RoutedKey(Key.Enter);
            await Wait(() => actions.Count > before);
            Check(actions.Last().Command == "main" && !window.KeepsExpanded, "primary-enter-opens-main");
            before = actions.Count; RoutedKey(Key.Space); await Wait(() => actions.Count > before);
            Check(actions.Last().Command == "main" && !window.KeepsExpanded, "primary-space-opens-main");

            var expected = new[] { "details", "contentUsage", "contentBudget", "keepExpanded", "more", "period", "filters", "updateStatus", "refresh", "main", "collapse" };
            Check(surface.KeyboardOrder().SequenceEqual(expected), "tab-order-follows-existing-visual-order");
            surface.FocusAction("details", true);
            foreach (string id in expected.Skip(1).Append("details"))
            {
                await surface.HandleKeyboardAsync(Key.Tab, ModifierKeys.None);
                Check(surface.KeyboardAction == id, "tab-focus-" + id);
            }
            await surface.HandleKeyboardAsync(Key.Tab, ModifierKeys.Shift);
            Check(surface.KeyboardAction == "collapse", "shift-tab-wraps-backward");
            var pin = Peer("keepExpanded"); pin.SetFocus();
            Check(surface.KeyboardAction == "keepExpanded" && pin.HasKeyboardFocus() && parent.GetChildren().Count(x => x.HasKeyboardFocus()) == 1,
                "uia-setfocus-targets-one-independent-action");
            before = actions.Count; RoutedKey(Key.Enter); await Wait(() => actions.Count > before);
            Check(actions.Last().Command == "keepExpanded" && window.KeepsExpanded, "enter-invokes-current-retention-instead-of-main");
            before = actions.Count; RoutedKey(Key.Space); await Wait(() => actions.Count > before);
            Check(actions.Last().Command == "keepExpanded" && !window.KeepsExpanded, "space-invokes-current-retention-instead-of-main");

            var host = window.PixelBounds; var shape = surface.DrawingBounds;
            surface.FocusAction("contentUsage", false); var unfocused = Pixels();
            surface.FocusAction("contentUsage", true); var focused = Pixels();
            Check(surface.KeyboardInteraction && !surface.KeyboardFocusBounds.IsEmpty && !focused.SequenceEqual(unfocused), "keyboard-focus-is-visibly-drawn");
            Check(window.PixelBounds == host && surface.DrawingBounds == shape, "focus-does-not-change-layout-or-native-envelope");

            state["busy"] = true; window.Update(state);
            Check(!surface.KeyboardOrder().Contains("refresh") && !Peer("refresh").IsEnabled(), "busy-refresh-is-disabled-for-tab-and-uia");
            before = actions.Count;
            bool rejected = false;
            try { ((IInvokeProvider)Peer("refresh").GetPattern(PatternInterface.Invoke)).Invoke(); }
            catch (ElementNotEnabledException) { rejected = true; }
            await surface.ActivateActionAsync("refresh", false);
            Check(rejected && actions.Count == before, "disabled-action-cannot-bypass-through-uia-or-pointer-command");
            state["busy"] = false;
            floating["content"] = "budget"; floating["budgetID"] = "daily"; window.Update(state);
            Check(!surface.KeyboardOrder().Contains("budgetScope") && !Peer("budgetScope").IsEnabled() && surface.KeyboardOrder().Contains("view-budget"),
                "budget-readonly-scope-is-skipped-while-edit-is-reachable");
            floating["content"] = "usage"; window.Update(state);

            var light = (ISelectionItemProvider)Peer("contentBudget").GetPattern(PatternInterface.SelectionItem);
            var dark = (ISelectionItemProvider)Peer("contentUsage").GetPattern(PatternInterface.SelectionItem);
            Check(dark.IsSelected && !light.IsSelected && Peer("contentUsage").GetAutomationControlType() == AutomationControlType.RadioButton,
                "content-uia-exposes-current-radio-selection");
            light.Select(); await Wait(() => surface.BudgetMode);
            var selection = (ISelectionProvider)parent.GetPattern(PatternInterface.Selection);
            Check(light.IsSelected && !dark.IsSelected && selection.IsSelectionRequired && !selection.CanSelectMultiple && selection.GetSelection().Length == 1,
                "content-uia-selection-updates-after-select");
            var toggle = (IToggleProvider)pin.GetPattern(PatternInterface.Toggle);
            Check(toggle.ToggleState == ToggleState.Off && pin.GetAutomationControlType() == AutomationControlType.CheckBox, "retention-uia-exposes-unchecked-toggle");
            toggle.Toggle(); await Wait(() => window.KeepsExpanded);
            Check(toggle.ToggleState == ToggleState.On, "retention-uia-toggle-updates-state");
            floating["keepExpanded"] = false; floating["content"] = "usage"; window.Update(state);

            surface.FocusAction("period", true); await window.ShowMenu("period");
            var menu = window.ActiveMenu!;
            Check(menu.IsOpen && menu.PlacementTarget == surface && menu.PlacementRectangle == surface.KeyboardFocusBounds,
                "keyboard-menu-anchors-to-focused-control");
            Check(menu.Items.OfType<MenuItem>().All(x => x.IsCheckable) && menu.Items.OfType<MenuItem>().Count(x => x.IsChecked) == 1,
                "menu-uia-exposes-checked-and-unchecked-choices");
            menu.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(menu), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
            await Wait(() => !window.InteractionActive && surface.KeyboardInteraction);
            Check(surface.KeyboardAction == "period" && Peer("period").HasKeyboardFocus(), "escape-menu-restores-opening-action-focus");
            await window.ShowMenu("period"); menu = window.ActiveMenu!;
            var seven = menu.Items.OfType<MenuItem>().Single(x => x.Header?.ToString() == "7 天");
            seven.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, seven));
            await Wait(() => floating.S("days") == "7" && !window.InteractionActive && surface.KeyboardInteraction);
            Check(surface.KeyboardAction == "period" && actions.Last() == ("period", "7"), "menu-selection-executes-and-restores-opening-focus");
            await surface.HandleKeyboardAsync(Key.F10, ModifierKeys.Shift); menu = window.ActiveMenu!;
            Check(menu.IsOpen && menu.Items.OfType<MenuItem>().Any(x => x.Header?.ToString() == "保持展开（离开鼠标不收起）" && x.IsCheckable),
                "keyboard-context-menu-exposes-explicit-keep-expanded");
            var status = menu.Items.OfType<MenuItem>().Single(x => x.Header?.ToString() == "数据更新状态与重试…");
            status.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, status));
            await Wait(() => actions.Last().Command == "updateStatus" && !window.InteractionActive);
            await Task.Delay(100);
            Check(navigationTarget?.IsActive == true && !window.IsActive, "update-status-keeps-its-new-window-in-foreground");
            navigationTarget!.Hide(); surface.FocusAction("period", true);
            await window.ShowMenu("period"); navigationTarget.Show(); navigationTarget.Activate();
            await Wait(() => !window.InteractionActive); await Task.Delay(100);
            Check(navigationTarget.IsActive && !window.IsActive, "outside-menu-dismiss-keeps-other-window-in-foreground");
            navigationTarget.Hide(); surface.FocusAction("period", true);
            await window.ShowMenu("more"); menu = window.ActiveMenu!;
            Check(menu.Items.OfType<MenuItem>().Any(x => x.Header?.ToString() == "数据与更新设置…") && !menu.Items.OfType<MenuItem>().Any(x => x.Header?.ToString() == "深色主题"), "more-opens-shared-settings-without-duplicate-preferences");
            menu.IsOpen = false; await Wait(() => !window.InteractionActive);

            await surface.HandleKeyboardAsync(Key.Space, ModifierKeys.Control);
            Check(window.KeepsExpanded, "ctrl-space-enables-explicit-keep-expanded");
            await surface.HandleKeyboardAsync(Key.Space, ModifierKeys.Control);
            Check(!window.KeepsExpanded, "ctrl-space-disables-explicit-keep-expanded");
            surface.EndKeyboardNavigation();
            await window.Invoke("keepExpanded");
            window.Press(true); var anchor = window.CompactPixelBounds;
            var origin = window.BeginDrag(anchor.TopLeft + new Vector(anchor.Width / 2, anchor.Height / 2));
            window.MoveBy(36, 24, origin, null); await window.FinishDrag();
            await Wait(() => !window.IsAnimating && surface.Expansion == 1);
            Check(window.KeepsExpanded && !window.InteractionActive && !window.HiddenAtEdge, "keep-expanded-drag-finishes-expanded-with-matching-state");
            Check(Math.Abs(window.CompactPixelBounds.X - origin.X - 36) <= 1 && Math.Abs(window.CompactPixelBounds.Y - origin.Y - 24) <= 1,
                "keep-expanded-drag-preserves-new-compact-anchor");
            await window.Invoke("keepExpanded"); window.Expand(false, false);
            Check(!window.KeepsExpanded && !selection.IsSelectionRequired && selection.GetSelection().Length == 0,
                "collapsed-surface-releases-content-selection");
            Check(!pin.IsEnabled() && pin.IsOffscreen(), "cached-uia-peer-cannot-activate-hidden-control");

            surface.Edge = CapsuleEdge.Left;
            surface.AnimationBounds = new Rect(surface.CompactBounds!.Value.TopLeft, new Size(28, 72));
            state.O("quota").Remove("windows"); state.O("quota")["capsuleName"] = "周余";
            state.O("quota")["capsuleFraction"] = .25; state.O("quota")["stale"] = false;
            floating["edgeMetric"] = "used"; window.Update(state); surface.Redraw();
            Check(AutomationProperties.GetName(surface).Contains("已用 75%") && Peer("details").GetName().Contains("已用 75%"),
                "used-side-tab-readout-matches-visible-percent");
            floating["edgeMetric"] = "remaining"; window.Update(state);
            Check(AutomationProperties.GetName(surface).Contains("剩余 25%") && Peer("details").GetName().Contains("剩余 25%"),
                "remaining-side-tab-readout-matches-visible-percent");
            state.O("quota")["stale"] = true; window.Update(state);
            Check(Peer("details").GetHelpText().Contains("数据可能已过期"), "side-tab-readout-exposes-stale-data");
            state.O("quota")["capsuleFraction"] = null; floating["edgeMetric"] = "used"; window.Update(state);
            Check(AutomationProperties.GetName(surface).Contains("已用 未知"), "side-tab-unknown-quota-does-not-announce-zero");
            surface.Edge = CapsuleEdge.None; surface.AnimationBounds = null; surface.Redraw();
            return J.Obj(("success", true), ("version", 1), ("checks", checks),
                ("scope", "Synthetic WPF window, routed keyboard events, UIA providers and native menus; no operating-system input or user settings."));
        }
        finally { navigationTarget?.Close(); window.Close(); app.ShutdownMode = shutdown; }
    }
}
