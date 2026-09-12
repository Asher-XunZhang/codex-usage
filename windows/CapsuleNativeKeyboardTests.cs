using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace CodexUsage;

internal static class CapsuleNativeKeyboardTests
{
    [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] keys);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] keys);
    // This changes only this test thread's key table. It sends no input and
    // restores the previous table before returning to the dispatcher.
    private sealed class Modifiers : IDisposable
    {
        private readonly byte[] original = new byte[256];
        internal Modifiers(ModifierKeys value)
        {
            if (!GetKeyboardState(original)) throw new InvalidOperationException("Cannot read test-thread keyboard state.");
            var keys = (byte[])original.Clone();
            foreach (int key in new[] { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C }) keys[key] = 0;
            if (value.HasFlag(ModifierKeys.Shift)) keys[0x10] = keys[0xA0] = 0x80;
            if (value.HasFlag(ModifierKeys.Control)) keys[0x11] = keys[0xA2] = 0x80;
            if (value.HasFlag(ModifierKeys.Alt)) keys[0x12] = keys[0xA4] = 0x80;
            if (!SetKeyboardState(keys)) throw new InvalidOperationException("Cannot set test-thread keyboard state.");
        }
        public void Dispose() { if (!SetKeyboardState(original)) throw new InvalidOperationException("Cannot restore test-thread keyboard state."); }
    }
    internal static async Task<JsonObject> RunAsync()
    {
        var app = Application.Current; var shutdown = app.ShutdownMode; app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var checks = new JsonArray(); var events = new JsonArray(); int mainActions = 0, keepActions = 0;
        var window = new CapsuleWindow((command, _) => { if (command == "main") mainActions++; if (command == "keepExpanded") keepActions++; return Task.CompletedTask; }, readPointer: () => null)
        { ShowActivated = false, Topmost = false };
        JsonObject Snapshot(string phase) => J.Obj(("phase", phase), ("at", Environment.TickCount),
            ("focused", Keyboard.FocusedElement?.GetType().Name), ("windowFocused", window.IsKeyboardFocused),
            ("active", window.IsActive), ("surfaceFocused", window.Surface.IsKeyboardFocusWithin),
            ("keyboardInteraction", window.Surface.KeyboardInteraction), ("keyboardAction", window.Surface.KeyboardAction),
            ("expanded", window.Surface.Expansion), ("animating", window.IsAnimating),
            ("interactionActive", window.InteractionActive), ("keepExpanded", window.KeepsExpanded));
        void Check(bool condition, string id)
        {
            if (!condition) throw new InvalidOperationException("Native capsule keyboard: " + id + "\n" +
                J.Text(J.Obj(("state", Snapshot("failure")), ("events", events))));
            checks.Add(id);
        }
        async Task Wait(Func<bool> ready, string id)
        {
            long started = Environment.TickCount64;
            while (!ready() && Environment.TickCount64 - started < 2000) await Task.Delay(15);
            Check(ready(), id);
        }
        try
        {
            var state = DemoData.State(); state.O("settings").O("floating")["pinned"] = false; state.O("settings").O("floating")["edgeAutoHide"] = false;
            window.Update(state); var surface = window.Surface;
            Check(!InputMethod.GetIsInputMethodEnabled(window) && !InputMethod.GetIsInputMethodEnabled(surface), "drawing-only-capsule-disables-ime-locally");
            Check(CapsuleSurface.InputKey(Key.System, Key.F10, Key.None) == Key.F10 && CapsuleSurface.InputKey(Key.ImeProcessed, Key.None, Key.Space) == Key.Space,
                "system-and-ime-routed-keys-retain-underlying-command-key");
            var inputSource = HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle())!;
            using (var modifiers = new Modifiers(ModifierKeys.Control))
            {
                var key = new KeyEventArgs(Keyboard.PrimaryDevice, inputSource, Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                window.RaiseEvent(key);
                Check(key.Handled && window.KeepsExpanded && keepActions == 1, "window-preview-route-handles-ctrl-space-without-surface-focus");
                key = new KeyEventArgs(Keyboard.PrimaryDevice, inputSource, Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                window.RaiseEvent(key);
                Check(key.Handled && !window.KeepsExpanded && keepActions == 2, "window-preview-route-toggles-retention-only-once-per-key");
            }
            if (Environment.GetEnvironmentVariable("CODEX_USAGE_TEST_BACKGROUND") == "1")
            {
                Check(!window.IsVisible && !window.IsActive, "keyboard-background-fixture-never-shows-or-activates-window");
                return J.Obj(("success", true), ("checks", checks), ("nativeSkipped", "Background mode tests Window preview routing without native activation; run --capsule-keyboard-tests without CODEX_USAGE_TEST_BACKGROUND for HWND keyboard translation."));
            }
            window.Show(); window.Activate(); await Task.Delay(80);
            var hwnd = new WindowInteropHelper(window).Handle; var source = HwndSource.FromHwnd(hwnd)!;
            window.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler((_, e) => events.Add(J.Obj(("phase", "preview"), ("key", e.Key.ToString()), ("systemKey", e.SystemKey.ToString()), ("modifiers", e.KeyboardDevice.Modifiers.ToString()), ("repeat", e.IsRepeat), ("handled", e.Handled), ("source", e.OriginalSource?.GetType().Name)))), true);
            window.AddHandler(Keyboard.KeyDownEvent, new KeyEventHandler((_, e) => events.Add(J.Obj(("phase", "bubble"), ("key", e.Key.ToString()), ("handled", e.Handled), ("source", e.OriginalSource?.GetType().Name)))), true);
            bool NativeKey(Key key, ModifierKeys modifiers = ModifierKeys.None, bool system = false)
            {
                events.Add(Snapshot("before-native-" + key));
                using var keys = new Modifiers(modifiers);
                var message = new MSG { hwnd = hwnd, message = system ? 0x104 : 0x100, wParam = new IntPtr(KeyInterop.VirtualKeyFromKey(key)), lParam = new IntPtr(1), time = Environment.TickCount };
                bool handled = ((IKeyboardInputSink)source).TranslateAccelerator(ref message, modifiers);
                message.message = system ? 0x105 : 0x101; message.lParam = new IntPtr(unchecked((int)0xC0000001));
                ((IKeyboardInputSink)source).TranslateAccelerator(ref message, modifiers);
                var after = Snapshot("after-native-" + key); after["handled"] = handled; events.Add(after);
                return handled;
            }
            void FocusContainer()
            {
                // Window is a focus scope: without clearing its remembered child,
                // WPF redirects Keyboard.Focus(window) straight back to Surface.
                FocusManager.SetFocusedElement(window, null); Keyboard.Focus(window);
            }
            surface.EndKeyboardNavigation(); FocusContainer();
            Check(ReferenceEquals(Keyboard.FocusedElement, window), "native-fixture-starts-with-focus-on-window-container");
            Check(NativeKey(Key.Tab), "native-tab-is-handled-through-window-preview"); await Task.Delay(70);
            Check(surface.KeyboardInteraction && surface.KeyboardAction == "contentUsage", "native-tab-enters-independent-surface-action-order");
            FocusContainer();
            Check(ReferenceEquals(Keyboard.FocusedElement, window), "native-ctrl-space-fixture-has-container-focus");
            bool beforeKeep = window.KeepsExpanded; int beforeActions = keepActions;
            Check(NativeKey(Key.Space, ModifierKeys.Control), "native-ctrl-space-reaches-window-with-container-focus");
            Check(window.KeepsExpanded != beforeKeep && keepActions == beforeActions + 1, "native-ctrl-space-executes-exactly-once");
            surface.FocusAction("period", true);
            Check(NativeKey(Key.F10, ModifierKeys.Shift, true), "native-system-f10-is-handled"); await Task.Delay(70);
            Check(window.ActiveMenu?.IsOpen == true, "native-shift-system-f10-opens-context-menu");
            window.ActiveMenu!.IsOpen = false;
            // Popup fade can outlive IsOpen=false. Real pointer-down is rejected
            // until Closed clears InteractionActive; do not bypass that guard.
            await Wait(() => window.ActiveMenu is null && !window.InteractionActive,
                "native-menu-dismissal-completes-before-drag");
            await Wait(() => surface.KeyboardInteraction && surface.KeyboardAction == "period",
                "native-menu-focus-restoration-completes");
            Check(surface.KeyboardInteraction && surface.KeyboardAction == "period", "native-menu-cancel-returns-to-trigger");
            window.Press(true); var compact = window.CompactPixelBounds;
            var origin = window.BeginDrag(new Point(compact.X + compact.Width / 2, compact.Y + compact.Height / 2)); window.MoveBy(12, 8, origin, null); await window.FinishDrag();
            events.Add(Snapshot("after-drag"));
            Check(!window.InteractionActive, "drag-completion-releases-interaction-before-keyboard-input");
            surface.EndKeyboardNavigation(); FocusContainer();
            Check(ReferenceEquals(Keyboard.FocusedElement, window), "post-drag-fixture-focus-is-on-window-container");
            Check(NativeKey(Key.Tab), "native-tab-remains-handled-after-drag"); await Task.Delay(70);
            Check(surface.KeyboardInteraction && surface.KeyboardAction == "contentUsage", "drag-does-not-strand-keyboard-focus-on-container");
            surface.FocusAction("details", true); Check(NativeKey(Key.Enter) && mainActions == 1, "native-enter-executes-current-primary-action-once");
            return J.Obj(("success", true), ("checks", checks), ("events", events), ("scope", "Activated synthetic WPF window; native MSG translated through HwndSource, thread-local modifier state restored, no SendInput, cursor movement or user state."));
        }
        finally { window.Close(); app.ShutdownMode = shutdown; }
    }
}
