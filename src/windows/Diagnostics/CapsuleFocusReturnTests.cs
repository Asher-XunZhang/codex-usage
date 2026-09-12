using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexUsage;

/// <summary>Activated synthetic windows exercise native focus return without real settings, IPC, cursor movement or SendInput.</summary>
internal static class CapsuleFocusReturnTests
{
    internal static async Task<JsonObject> RunAsync(string? frames = null)
    {
        var app = Application.Current; var shutdown = app.ShutdownMode;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var checks = new JsonArray(); var failures = new JsonArray(); var observations = new JsonArray(); var images = new JsonArray();
        if (frames != null) Directory.CreateDirectory(frames);
        void Check(bool okay, string id) { checks.Add(id); if (!okay) failures.Add(id); }
        async Task<bool> Until(Func<bool> condition)
        {
            long start = Environment.TickCount64;
            while (!condition() && Environment.TickCount64 - start < 1800) await Task.Delay(15);
            return condition();
        }
        try
        {
            foreach (bool light in new[] { true, false }) foreach (string page in new[] { "usage", "budget", "monitor" })
            {
                string caseID = (light ? "light-" : "dark-") + page;
                CapsuleWindow? window = null; Window? child = null; bool owned = true;
                var state = TaskMonitorDemo.State(); var floating = state.O("settings").O("floating");
                floating["theme"] = light ? "light" : "dark"; floating["content"] = page; floating["budgetID"] = "daily";
                floating["quotaContent"] = "usage"; floating["keepExpanded"] = true; floating["pinned"] = false; floating["edgeAutoHide"] = false;
                try
                {
                    Task Action(string command, string? value)
                    {
                        if (command == "main")
                        {
                            var field = new TextBox { Text = "合成窗口：关闭后回到浮窗", Margin = new Thickness(18) };
                            child = new Window
                            {
                                Title = "Codex 用量 · 焦点回归测试", Width = 310, Height = 130,
                                ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
                                Left = SystemParameters.WorkArea.Left + 470, Top = SystemParameters.WorkArea.Top + 100,
                                Content = field
                            };
                            if (owned) child.Owner = window;
                            child.Show(); child.Activate(); field.Focus();
                        }
                        return Task.CompletedTask;
                    }
                    window = new CapsuleWindow(Action, readPointer: () => null)
                    {
                        ShowActivated = true, Topmost = false,
                        Left = SystemParameters.WorkArea.Left + 110, Top = SystemParameters.WorkArea.Top + 90
                    };
                    window.Update(state); window.Show(); window.Activate(); window.Expand(true, false);
                    var surface = window.Surface;
                    Check(await Until(() => window.IsActive && surface.Expansion == 1), caseID + "-fixture-is-natively-active-and-expanded");
                    Check(surface.FocusVisualStyle == null, caseID + "-surface-does-not-add-a-second-system-focus-adorner");

                    foreach (var entry in new[] { ("main", true), ("main", false), ("details", true) })
                    {
                        bool own = entry.Item2; owned = own;
                        string route = caseID + (entry.Item1 == "details" ? "-header" : "") + (own ? "-owned" : "-independent");
                        surface.FocusAction(entry.Item1, false); surface.ClearFeedback();
                        await Settle();
                        Check(!surface.KeyboardInteraction, route + "-pointer-entry-is-not-keyboard-navigation");
                        var before = Capture(window); Save(before, route + "-before.png");
                        await surface.ActivateActionAsync(entry.Item1, false);
                        Check(child != null && await Until(() => child.IsActive && !window.IsActive), route + "-action-opens-an-active-synthetic-window");
                        child!.Close(); child = null;
                        // Owned close must restore its owner natively. A separate top-level window
                        // has no owner contract; native activation reproduces returning to the capsule.
                        if (!own) window.Activate();
                        Check(await Until(() => window.IsActive && surface.IsKeyboardFocusWithin), route + "-native-return-restores-existing-surface-focus");
                        await Settle();
                        var after = Capture(window); Save(after, route + "-after.png");
                        int differences = Difference(before, after);
                        observations.Add(J.Obj(("case", route), ("active", window.IsActive), ("surfaceFocused", surface.IsKeyboardFocusWithin),
                            ("keyboardInteraction", surface.KeyboardInteraction), ("keyboardAction", surface.KeyboardAction), ("differentPixels", differences)));
                        Check(!surface.KeyboardInteraction, route + "-passive-return-does-not-start-keyboard-navigation");
                        Check(differences == 0, route + "-passive-return-keeps-client-pixels-identical");
                    }

                    // Genuine navigation must still expose the custom focus indicator and retain
                    // its virtual action across an owned-window return.
                    owned = true;
                    surface.FocusAction("details", false);
                    await surface.HandleKeyboardAsync(Key.Tab, ModifierKeys.None);
                    Check(surface.KeyboardInteraction && surface.KeyboardAction == (page == "monitor" ? "monitorMessages" : "contentUsage"), caseID + "-explicit-tab-shows-action-focus");
                    surface.FocusAction("main", true); await Settle();
                    var keyboardBefore = Capture(window); Save(keyboardBefore, caseID + "-keyboard-before.png");
                    await surface.HandleKeyboardAsync(Key.Enter, ModifierKeys.None);
                    Check(child != null && await Until(() => child.IsActive), caseID + "-keyboard-enter-opens-window");
                    child!.Close(); child = null;
                    Check(await Until(() => window.IsActive && surface.IsKeyboardFocusWithin), caseID + "-keyboard-owned-return-restores-surface");
                    await Settle();
                    Check(surface.KeyboardInteraction && surface.Expansion == 1 && surface.KeyboardAction == "main", caseID + "-keyboard-return-retains-explicit-action");
                    var keyboardAfter = Capture(window); Save(keyboardAfter, caseID + "-keyboard-after.png");
                    Check(Difference(keyboardBefore, keyboardAfter) == 0, caseID + "-keyboard-return-retains-one-consistent-focus-indicator");

                    surface.FocusAction("main", false);
                    var peer = FrameworkElementAutomationPeer.CreatePeerForElement(surface)!;
                    peer.GetChildren().Single(x => x.GetAutomationId() == "main").SetFocus();
                    await Settle();
                    Check(surface.KeyboardInteraction && surface.KeyboardAction == "main", caseID + "-explicit-uia-focus-remains-visible");
                    Save(Capture(window), caseID + "-uia-focus.png");

                    // Without Keep expanded, the capsule collapses while the child
                    // owns focus. Explicit keyboard intent must still resume on return.
                    floating["keepExpanded"] = false; window.Update(state);
                    surface.FocusAction("details", true); await Settle();
                    var unretainedBefore = Capture(window);
                    await surface.HandleKeyboardAsync(Key.Enter, ModifierKeys.None);
                    Check(child != null && await Until(() => child.IsActive && surface.Expansion == 0), caseID + "-unretained-capsule-collapses-while-child-has-focus");
                    child!.Close(); child = null;
                    Check(await Until(() => window.IsActive && surface.IsKeyboardFocusWithin && surface.Expansion == 1), caseID + "-explicit-keyboard-return-resumes-unretained-expansion");
                    await Settle();
                    Check(surface.KeyboardInteraction && surface.KeyboardAction == "details" && Difference(unretainedBefore, Capture(window)) == 0,
                        caseID + "-unretained-keyboard-return-preserves-visible-focus");

                    async Task Settle()
                    {
                        await window.Dispatcher.InvokeAsync(() => { window.UpdateLayout(); surface.Redraw(); }, DispatcherPriority.ContextIdle);
                        await Task.Delay(70);
                    }
                }
                catch (Exception error) { failures.Add(caseID + ": " + error); }
                finally { child?.Close(); window?.Close(); }
            }
            return J.Obj(("success", failures.Count == 0), ("checks", checks), ("failures", failures), ("observations", observations), ("images", images),
                ("scope", "Activated synthetic WPF capsules and child windows; owned close uses native owner focus return; independent close uses Window.Activate; no real user state, IPC, SendInput or pointer movement. Tab/Enter/UIA use production action handlers; existing native keyboard tests cover message translation and menu return."));
        }
        finally { app.ShutdownMode = shutdown; }

        void Save(BitmapSource bitmap, string name)
        {
            if (frames == null) return;
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(frames, name)); encoder.Save(file); images.Add(name);
        }
    }

    private static RenderTargetBitmap Capture(Window window)
    {
        // Render the Window rather than only the drawing surface: WPF's separate
        // focus AdornerLayer must be part of the before/after comparison.
        window.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(window); bitmap.Freeze(); return bitmap;
    }
    private static int Difference(BitmapSource first, BitmapSource second)
    {
        if (first.PixelWidth != second.PixelWidth || first.PixelHeight != second.PixelHeight) return -1;
        int stride = first.PixelWidth * 4; var a = new byte[stride * first.PixelHeight]; var b = new byte[a.Length];
        first.CopyPixels(a, stride, 0); second.CopyPixels(b, stride, 0); int count = 0;
        for (int i = 0; i < a.Length; i += 4) if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2] || a[i + 3] != b[i + 3]) count++;
        return count;
    }
}
