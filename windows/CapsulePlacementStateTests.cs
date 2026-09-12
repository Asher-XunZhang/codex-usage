using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CodexUsage;

internal static class CapsulePlacementStateTests
{
    internal static async Task<JsonObject> RunAsync()
    {
        var app = Application.Current; var shutdown = app.ShutdownMode; app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var window = new CapsuleWindow((_, _) => Task.CompletedTask, readPointer: () => null) { ShowActivated = false, Topmost = false };
        var checks = new JsonArray();
        void Check(bool condition, string id) { if (!condition) throw new InvalidOperationException("Capsule placement state: " + id); checks.Add(id); }
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            var position = J.Obj(("pixelLeft", SystemParameters.WorkArea.Left + 200), ("pixelTop", SystemParameters.WorkArea.Top + 160));
            window.SetKeepsExpanded(true); window.Collapse(); window.Restore(position);
            Check(window.KeepsExpanded && window.Surface.Expansion == 0 && !window.IsAnimating, "restore-preserves-active-collapse-with-retention-enabled");
            window.DisplayChanged(null, EventArgs.Empty); await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(window.KeepsExpanded && window.Surface.Expansion == 0 && !window.IsAnimating, "display-change-preserves-active-collapse-with-retention-enabled");
            window.SetKeepsExpanded(false); window.SetKeepsExpanded(true); window.Restore(position);
            Check(window.Surface.Expansion == 1, "restore-still-expands-normal-retained-window");
            window.DisplayChanged(null, EventArgs.Empty); await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(window.Surface.Expansion == 1, "display-change-still-expands-normal-retained-window");
            window.Collapse(); window.Press(true); var compact = window.CompactPixelBounds;
            var origin = window.BeginDrag(new Point(compact.X + compact.Width / 2, compact.Y + compact.Height / 2));
            window.MoveBy(12, 8, origin, null); await window.FinishDrag();
            Check(window.KeepsExpanded && window.Surface.Expansion == 1 && !window.InteractionActive, "new-drag-explicitly-resumes-retained-expansion");
            Check(!window.IsVisible && !window.IsActive, "placement-state-fixture-never-shows-or-activates-window");
            return J.Obj(("success", true), ("checks", checks), ("scope", "Hidden HWND, synthetic pointer and state; no foreground activation or operating-system input."));
        }
        finally { window.Close(); app.ShutdownMode = shutdown; }
    }
}
