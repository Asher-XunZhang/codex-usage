using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CodexUsage;

/// <summary>Real layered-window hit testing and idle observations without any injected OS input.</summary>
internal static class CapsuleNativeSurfaceTests
{
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();

    public static async Task<JsonObject> RunAsync()
    {
        var app = Application.Current ?? throw new InvalidOperationException("Native surface tests require a WPF application.");
        if (!app.Dispatcher.CheckAccess()) throw new InvalidOperationException("Native surface tests require the WPF dispatcher.");
        var shutdown = app.ShutdownMode; app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var checks = new JsonArray(); var failures = new JsonArray(); var hits = new JsonArray(); var idle = new JsonArray();
        var window = new CapsuleWindow((_, _) => Task.CompletedTask, () => Task.FromResult(new JsonObject()), () => null)
        { ShowActivated = false, Topmost = true };
        void Check(bool success, string id)
        {
            checks.Add(J.Obj(("id", id), ("success", success)));
            if (!success) failures.Add(id);
        }
        async Task Presented()
        {
            await app.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await Task.Delay(100);
            DwmFlush();
        }
        try
        {
            var state = DemoData.State();
            state.O("settings")["refresh"] = 0;
            state.O("settings").O("floating")["pinned"] = true;
            state.O("settings").O("floating")["edgeAutoHide"] = false;
            window.Update(state);
            // Choose a location away from the current pointer without moving it.
            // Tests query pixels directly; an unrelated physical hover is unnecessary.
            var screen = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            var work = new Rect(screen.X, screen.Y, screen.Width, screen.Height);
            var dpi = VisualTreeHelper.GetDpi(window);
            bool hasPointer = GetCursorPos(out var cursor);
            var origin = new Point(work.Left + work.Width * .25, work.Top + work.Height * .2);
            foreach (double fraction in new[] { .25, .75 })
            {
                var candidate = new Rect(work.Left + work.Width * fraction, origin.Y, 76 * dpi.DpiScaleX, 76 * dpi.DpiScaleY);
                var envelope = Rect.Union(candidate, CapsulePlacement.Expanded(candidate, work, dpi));
                envelope.Inflate(120, 120);
                if (!hasPointer || !envelope.Contains(new Point(cursor.X, cursor.Y))) { origin = candidate.TopLeft; break; }
            }
            window.Restore(J.Obj(("pixelLeft", origin.X), ("pixelTop", origin.Y)));
            window.Show(); await Presented();
            var hwnd = new WindowInteropHelper(window).Handle;
            var host = window.PixelBounds;
            var compact = window.CompactPixelBounds;
            var panel = window.Surface.PanelBounds ?? throw new InvalidOperationException("The fixed panel bounds were not initialized before Show.");
            var circleCenter = new Point(compact.X + compact.Width / 2, compact.Y + compact.Height / 2);
            var circleCorner = new Point(compact.Left + 1, compact.Top + 1);
            var header = window.Surface.PointToScreen(new Point(panel.X + panel.Width / 2, panel.Y + 28));
            var body = window.Surface.PointToScreen(new Point(panel.X + panel.Width / 2, panel.Y + panel.Height / 2));
            bool SameHost() => new WindowInteropHelper(window).Handle == hwnd && window.PixelBounds == host;
            void Hit(Point point, bool expected, string id)
            {
                var hit = WindowFromPoint(new NativePoint { X = (int)Math.Round(point.X), Y = (int)Math.Round(point.Y) });
                var root = hit == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hit, 2);
                bool owned = hit == hwnd || root == hwnd;
                hits.Add(J.Obj(("id", id), ("x", point.X), ("y", point.Y), ("belongsToCapsule", owned),
                    ("window", hit.ToInt64().ToString("X")), ("root", root.ToInt64().ToString("X"))));
                Check(owned == expected, id);
            }
            Check(hwnd != IntPtr.Zero && host.Contains(compact) && host.Width > compact.Width && host.Height > compact.Height,
                "first-show-has-complete-fixed-envelope");
            Hit(circleCenter, true, "compact-center-is-native-capsule");
            Hit(circleCorner, false, "compact-transparent-corner-passes-through");
            Hit(body, false, "compact-transparent-envelope-passes-through");

            int frameSamples = 0, sizes = 0, positions = 0;
            var source = HwndSource.FromHwnd(hwnd)!;
            IntPtr Observe(IntPtr h, int message, IntPtr w, IntPtr l, ref bool handled)
            {
                if (message == 0x5) sizes++;
                if (message == 0x47) positions++;
                return IntPtr.Zero;
            }
            source.AddHook(Observe);
            try
            {
                async Task Transition(bool expand, string id)
                {
                    bool stable = SameHost();
                    void Frame(object? sender, EventArgs args) { frameSamples++; stable &= SameHost(); }
                    CompositionTarget.Rendering += Frame;
                    try
                    {
                        window.Expand(expand);
                        var wait = Stopwatch.StartNew();
                        while ((window.IsAnimating || window.Surface.Expansion != (expand ? 1 : 0)) && wait.ElapsedMilliseconds < 3000)
                            await Task.Delay(10);
                        await Presented();
                        Check(!window.IsAnimating && window.Surface.Expansion == (expand ? 1 : 0), id + "-endpoint");
                        Check(stable && SameHost(), id + "-fixed-hwnd-and-bounds");
                    }
                    finally { CompositionTarget.Rendering -= Frame; }
                }
                for (int cycle = 0; cycle < 4; cycle++)
                {
                    await Transition(true, "expand-" + cycle);
                    Hit(header, true, "expanded-header-" + cycle);
                    Hit(body, true, "expanded-body-" + cycle);
                    await Transition(false, "collapse-" + cycle);
                    Hit(body, false, "collapsed-former-body-passes-through-" + cycle);
                }
                int beforeIdle = window.Surface.RenderCount;
                await Task.Delay(200);
                Check(!window.IsAnimating && window.Surface.RenderCount == beforeIdle, "repeated-transitions-stop-rendering-for-idle-200ms");
                Check(sizes == 0 && positions == 0, "ordinary-transitions-send-no-native-size-or-position-change");

                async Task MeasureIdle(string name)
                {
                    using var process = Process.GetCurrentProcess();
                    process.Refresh(); var cpu = process.TotalProcessorTime; long privateBytes = process.PrivateMemorySize64;
                    int renders = window.Surface.RenderCount; double progress = window.Surface.Expansion;
                    var watch = Stopwatch.StartNew();
                    await Task.Delay(5000);
                    double elapsed = watch.Elapsed.TotalMilliseconds;
                    process.Refresh(); double cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
                    idle.Add(J.Obj(("state", name), ("elapsedMs", elapsed), ("processCpuMs", cpuMs),
                        ("wholeMachineCpuPercent", cpuMs / elapsed / Environment.ProcessorCount * 100),
                        ("privateBytesBefore", privateBytes), ("privateBytesAfter", process.PrivateMemorySize64),
                        ("renderCountDelta", window.Surface.RenderCount - renders), ("isAnimating", window.IsAnimating)));
                    Check(!window.IsAnimating && window.Surface.Expansion == progress, name + "-idle-retains-endpoint");
                }
                await MeasureIdle("compact");
                await Transition(true, "idle-panel-expand");
                await MeasureIdle("panel");
                Check(SameHost(), "native-envelope-remains-fixed-after-idle-observations");
                return J.Obj(("success", failures.Count == 0), ("version", 1), ("checks", checks), ("failures", failures),
                    ("hits", hits), ("frameSamples", frameSamples), ("nativeResizeMessages", sizes), ("nativePositionMessages", positions),
                    ("idle", idle), ("clientAreaAnimation", SystemParameters.ClientAreaAnimation),
                    ("conditions", "Visible topmost WPF capsule with synthetic data and a null injected pointer. Only WindowFromPoint/GetAncestor query native hit ownership; no OS mouse movement, clicks or keyboard input. Idle samples cover this test process for five seconds per state; they do not measure the real host, its workers, GPU memory or display presentation rate. Resource observations have no machine-specific pass threshold."));
            }
            finally { source.RemoveHook(Observe); }
        }
        finally { window.Close(); app.ShutdownMode = shutdown; }
    }
}
