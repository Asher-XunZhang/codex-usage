using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace CodexUsage;

internal static class TrayInteractionTests
{
    internal static JsonObject Run()
    {
        var checks = new JsonArray();
        void Check(bool condition, string id) { if (!condition) throw new InvalidOperationException("Tray interaction: " + id); checks.Add(id); }
        TrayClickState Fresh() => new(500);
        var clicks = Fresh();
        Check(!TrayDetailWindow.IsIconMouseDismissal(true, false), "alt-tab-with-pointer-on-icon-is-not-mouse-dismissal");
        Check(!TrayDetailWindow.IsIconMouseDismissal(false, true) && TrayDetailWindow.IsIconMouseDismissal(true, true), "only-left-press-on-icon-arms-dismissal");
        Check(clicks.Handle(0x400, true, 10) == TrayClickAction.None && clicks.PendingClick && clicks.CompleteClick() == TrayClickAction.ToggleDetail,
            "version-four-selection-opens-detail-once");
        Check(clicks.CompleteClick() == TrayClickAction.None, "completed-click-cannot-run-twice");
        clicks = Fresh(); clicks.Handle(0x201, true, 10); clicks.Handle(0x202, true, 20);
        Check(clicks.Handle(0x400, true, 20) == TrayClickAction.None && clicks.CompleteClick() == TrayClickAction.ToggleDetail,
            "raw-release-and-version-four-selection-are-one-click");
        clicks = Fresh(); clicks.Handle(0x400, true, 10);
        Check(clicks.Handle(0x202, true, 10) == TrayClickAction.None && clicks.CompleteClick() == TrayClickAction.ToggleDetail,
            "version-four-selection-before-raw-release-is-not-double-click");
        clicks = Fresh(); clicks.Handle(0x201, true, 10); clicks.Handle(0x202, true, 20); clicks.Handle(0x400, true, 20);
        Check(clicks.Handle(0x203, true, 80) == TrayClickAction.OpenMain, "native-double-click-opens-main-once");
        Check(clicks.Handle(0x202, true, 90) == TrayClickAction.None && clicks.Handle(0x400, true, 90) == TrayClickAction.None && clicks.CompleteClick() == TrayClickAction.None,
            "double-click-trailing-release-and-selection-do-not-reopen-detail");
        clicks = Fresh(); clicks.Handle(0x400, true, 10);
        Check(clicks.Handle(0x400, true, 80) == TrayClickAction.OpenMain && !clicks.PendingClick, "two-distinct-semantic-selections-preserve-double-click-main-action");
        clicks = Fresh(); clicks.Handle(0x201, true, 10); clicks.Dismissed(true);
        Check(clicks.Handle(0x202, true, 20) == TrayClickAction.None && !clicks.PendingClick && clicks.Handle(0x400, true, 20) == TrayClickAction.None,
            "icon-mouse-dismissal-consumes-only-its-current-release");
        clicks.Handle(0x201, true, 700); clicks.Handle(0x202, true, 710); clicks.Handle(0x400, true, 710);
        Check(clicks.CompleteClick() == TrayClickAction.ToggleDetail, "next-normal-click-after-mouse-dismissal-still-opens-detail");
        clicks = Fresh(); clicks.Dismissed(true); clicks.Handle(0x201, true, 10); clicks.Handle(0x202, true, 20);
        Check(!clicks.PendingClick, "deactivation-before-queued-mouse-down-still-suppresses-same-click");
        clicks = Fresh(); clicks.Handle(0x201, true, 10); clicks.Dismissed(true);
        clicks.Handle(0x201, true, 700); clicks.Handle(0x202, true, 710);
        Check(clicks.CompleteClick() == TrayClickAction.ToggleDetail, "aborted-mouse-gesture-does-not-suppress-new-mouse-down");
        clicks = Fresh(); clicks.Dismissed(TrayDetailWindow.IsIconMouseDismissal(true, false)); clicks.Handle(0x400, true, 10);
        Check(clicks.CompleteClick() == TrayClickAction.ToggleDetail, "alt-tab-dismissal-does-not-swallow-next-selection");
        clicks = Fresh(); clicks.Dismissed(true);
        Check(clicks.Handle(0x401, true, 10) == TrayClickAction.ToggleDetail && !clicks.PendingClick, "keyboard-selection-bypasses-and-clears-mouse-suppression");
        clicks = Fresh(); clicks.Dismissed(false); clicks.Handle(0x400, true, 10);
        Check(clicks.CompleteClick() == TrayClickAction.ToggleDetail, "escape-dismissal-does-not-suppress-next-selection");
        clicks = Fresh(); clicks.Handle(0x202, true, 10);
        Check(clicks.Handle(0x7b, true, 20) == TrayClickAction.Menu && clicks.CompleteClick() == TrayClickAction.None, "context-menu-cancels-pending-single-click");
        foreach (int notification in new[] { 0x201, 0x202, 0x203, 0x400, 0x401, 0x205, 0x7b })
        {
            clicks = Fresh(); clicks.Handle(0x202, true, 10); clicks.Dismissed(true);
            Check(clicks.Handle(notification, false, 20) == TrayClickAction.None && !clicks.PendingClick && clicks.CompleteClick() == TrayClickAction.None,
                "hidden-tray-rejects-late-input-" + notification.ToString("X"));
        }
        return J.Obj(("success", true), ("checks", checks), ("scope", "Pure notification sequences and dismissal causes; no HWND, shell icon, pointer or foreground changes."));
    }

    internal static async Task<JsonObject> RunAsync()
    {
        var result = Run(); result["placement"] = await CapsulePlacementStateTests.RunAsync();
        if (Environment.GetEnvironmentVariable("CODEX_USAGE_TEST_BACKGROUND") == "1")
        {
            result["nativeSkipped"] = "Background mode does not show or activate tray details; use --tray-interaction-tests without CODEX_USAGE_TEST_BACKGROUND for native foreground acceptance.";
            return result;
        }
        var app = Application.Current; var shutdown = app.ShutdownMode; app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        using var tray = new Tray();
        var other = new Window { Title = "合成托盘失焦目标", Width = 260, Height = 120, ShowInTaskbar = false };
        var checks = new JsonArray(); int mainActions = 0;
        void Check(bool condition, string id) { if (!condition) throw new InvalidOperationException("Native tray interaction: " + id); checks.Add(id); }
        Task SettleClick() => Task.Delay(System.Windows.Forms.SystemInformation.DoubleClickTime + 100);
        try
        {
            tray.OpenMain = () => mainActions++; tray.Update("合成托盘点击测试", TrayTests.Fixture()); tray.SetVisible(true);
            Check(tray.IconVisible, "synthetic-icon-registered");
            var select = new TrayCallback(0x401, 1, new Point(SystemParameters.WorkArea.Right - 30, SystemParameters.WorkArea.Bottom - 30));
            tray.Dispatch(select); await Task.Delay(70); var detail = tray.EnsureDetail();
            Check(detail.IsVisible && detail.IsActive, "keyboard-selection-activates-interactive-detail");
            detail.Dismiss(true); tray.Dispatch(select with { Event = 0x202 }); tray.Dispatch(select with { Event = 0x400 }); await SettleClick();
            Check(!detail.IsVisible && mainActions == 0, "same-mouse-dismissal-does-not-reopen-detail");
            tray.Dispatch(select with { Event = 0x201 }); tray.Dispatch(select with { Event = 0x202 }); tray.Dispatch(select with { Event = 0x400 }); await SettleClick();
            Check(detail.IsVisible && mainActions == 0, "next-single-click-opens-detail-without-main");
            other.Show(); other.Activate(); await Task.Delay(100);
            Check(!detail.IsVisible && other.IsActive, "keyboard-foreground-change-dismisses-detail");
            tray.Dispatch(select with { Event = 0x400 }); await SettleClick();
            Check(detail.IsVisible && detail.IsActive, "selection-after-keyboard-dismissal-is-not-swallowed");
            detail.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(detail), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Check(!detail.IsVisible, "escape-dismisses-detail");
            tray.Dispatch(select); await Task.Delay(70); Check(detail.IsVisible, "keyboard-reopen-after-escape");
            tray.Dispatch(select with { Event = 0x201 }); tray.Dispatch(select with { Event = 0x202 }); tray.Dispatch(select with { Event = 0x203 });
            tray.Dispatch(select with { Event = 0x202 }); tray.Dispatch(select with { Event = 0x400 }); await SettleClick();
            Check(mainActions == 1 && !detail.IsVisible, "double-click-opens-main-once-without-late-detail");
            tray.SetVisible(false);
            foreach (int notification in new[] { 0x202, 0x203, 0x400, 0x401 }) tray.Dispatch(select with { Event = notification });
            await SettleClick(); Check(mainActions == 1 && !detail.IsVisible && !tray.PendingClick, "hidden-native-tray-ignores-late-selection");
            result["native"] = J.Obj(("success", true), ("checks", checks), ("scope", "Synthetic registered tray and activated WPF windows; injected notifications, no operating-system input or user state."));
            return result;
        }
        finally { other.Close(); tray.Dispose(); app.ShutdownMode = shutdown; }
    }
}
