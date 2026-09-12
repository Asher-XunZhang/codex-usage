using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexUsage;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string? Arg(string name) { int index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
        bool main = args.Contains("--main"), demo = args.Contains("--demo"); string? output = Arg("--output");
        if (args.Contains("--worker-tests"))
        {
            try { WorkerTests.RunAsync().GetAwaiter().GetResult(); if (output is not null) J.Write(output, J.Obj(("success", true), ("checks", "isolated-worker-lifecycle"))); return 0; }
            catch (Exception e) { if (output is not null) J.Write(output, J.Obj(("success", false), ("error", e.ToString()))); return 1; }
        }
        if (args.Contains("--send"))
        {
            try { var reply = Ipc.Send(J.Read(Arg("--input")!), Arg("--pipe")).GetAwaiter().GetResult(); J.Write(output!, reply); return 0; } catch (Exception e) { if (output is not null) J.Write(output, J.Obj(("error", e.Message))); return 1; }
        }
        if (args.Contains("--budget-harness"))
        {
            try { BudgetTests.Replay(Arg("--input")!, output!); return 0; } catch (Exception e) { if (output is not null) J.Write(output, J.Obj(("error", e.ToString()))); return 1; }
        }
        if (args.Contains("--native-read"))
        {
            try { var request = J.Read(Arg("--input")!); var result = NativeIndex.Read(Arg("--cache")!, request.O("filters"), request.A("requests"), request.B("choices"), request.N("now")); J.Write(output!, result); return 0; }
            catch (Exception e) { if (output is not null) J.Write(output, J.Obj(("error", e.ToString()))); return 1; }
        }
        if (demo && Environment.GetEnvironmentVariable("CODEX_USAGE_DESKTOP_BASE") is null) Paths.Base = Path.Combine(Path.GetTempPath(), "CodexUsage-demo");
        if (args.Contains("--check-runtime"))
        {
            try { using var job = new ChildJob(); string result = Processes.Run(job, Paths.Python, ["-E", "-s", "-B", "-c", "import sys,json,sqlite3,ssl;print(json.dumps({'python':sys.version.split()[0],'sqlite':sqlite3.sqlite_version,'ssl':ssl.OPENSSL_VERSION,'isolated':sys.flags.isolated}))"]).GetAwaiter().GetResult(); if (output is not null) J.Write(output, J.Parse(result)); return 0; }
            catch (Exception e) { if (output is not null) J.Write(output, J.Obj(("error", e.Message))); return 1; }
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; Theme.Initialize();
        if (args.Contains("--layout-tests"))
        {
            app.Startup += async (_, _) =>
            {
                try { var result = await LayoutBehaviorTests.RunAsync(Arg("--frames")); if (output != null) J.Write(output, result); app.Shutdown(); }
                catch (Exception e) { if (output != null) J.Write(output, J.Obj(("success", false), ("error", e.ToString()))); app.Shutdown(1); }
            };
            return app.Run();
        }
        if (args.Contains("--reliability-tests"))
        {
            app.Startup += async (_, _) =>
            {
                try
                {
                    ReliabilityTests.Run();
                    await MainStateTests.RunAsync();
                    var budgets = await BudgetRecoveryTests.RunAsync();
                    var accessibility = await CapsuleAccessibilityTests.RunAsync();
                    if (output != null) J.Write(output, J.Obj(("success", true), ("budgetRecovery", budgets), ("accessibility", accessibility), ("checks", "query-failures, export-identity, settings-recovery, independent-refresh, drafts, budget-errors, keyboard-and-UIA")));
                    app.Shutdown();
                }
                catch (Exception e) { if (output != null) J.Write(output, J.Obj(("success", false), ("error", e.ToString()))); app.Shutdown(1); }
            };
            return app.Run();
        }
        if (args.Contains("--native-surface-tests"))
        {
            app.Startup += async (_, _) =>
            {
                try
                {
                    var result = await CapsuleNativeSurfaceTests.RunAsync();
                    if (output != null) J.Write(output, result); app.Shutdown(result.B("success") ? 0 : 1);
                }
                catch (Exception e) { if (output != null) J.Write(output, J.Obj(("success", false), ("error", e.ToString()))); app.Shutdown(1); }
            };
            return app.Run();
        }
        if (args.Contains("--morph-tests"))
        {
            app.Startup += (_, _) =>
            {
                try
                {
                    var result = CapsuleMorphRenderTests.Run(Arg("--frames"));
                    if (output != null) J.Write(output, result); app.Shutdown(result.B("success") ? 0 : 1);
                }
                catch (Exception e) { if (output != null) J.Write(output, J.Obj(("success", false), ("error", e.ToString()))); app.Shutdown(1); }
            };
            return app.Run();
        }
        if (args.Contains("--dock-tests"))
        {
            app.Startup += async (_, _) =>
            {
                try { var result = await CapsuleDockingTests.RunAsync(); if (output != null) J.Write(output, result); app.Shutdown(result.B("success") ? 0 : 1); }
                catch (Exception e) { if (output != null) J.Write(output, J.Obj(("success", false), ("error", e.ToString()))); app.Shutdown(1); }
            };
            return app.Run();
        }
        if (args.Contains("--capsule-keyboard-tests"))
        {
            app.Startup += async (_, _) =>
            {
                try { var result = await CapsuleNativeKeyboardTests.RunAsync(); if (output != null) J.Write(output, result); app.Shutdown(result.B("success") ? 0 : 1); }
                catch (Exception e) { if (output != null) J.Write(output, J.Obj(("success", false), ("error", e.ToString()))); app.Shutdown(1); }
            };
            return app.Run();
        }
        if (args.Contains("--tray-interaction-tests"))
        {
            app.Startup += async (_, _) =>
            {
                try { var result = await TrayInteractionTests.RunAsync(); if (output != null) J.Write(output, result); app.Shutdown(result.B("success") ? 0 : 1); }
                catch (Exception e) { if (output != null) J.Write(output, J.Obj(("success", false), ("error", e.ToString()))); app.Shutdown(1); }
            };
            return app.Run();
        }
        if (args.Contains("--tray-hover-tests"))
        {
            app.Startup += async (_, _) =>
            {
                try { var result = await TrayTests.NativeAsync(); if (output != null) J.Write(output, result); app.Shutdown(); }
                catch (Exception e) { if (output != null) J.Write(output, J.Obj(("success", false), ("error", e.ToString()))); app.Shutdown(1); }
            };
            return app.Run();
        }
        if (args.Contains("--hover-tests"))
        {
            app.Startup += async (_, _) =>
            {
                try
                {
                    var result = await CapsuleTests.TestHoverAsync(); var regressions = await CapsuleHoverRegression.RunAsync();
                    result["regressions"] = regressions; result["success"] = result.B("success") && regressions.B("success");
                    if (output != null) J.Write(output, result); app.Shutdown(result.B("success") ? 0 : 1);
                }
                catch (Exception e) { if (output != null) J.Write(output, J.Obj(("success", false), ("error", e.ToString()))); app.Shutdown(1); }
            };
            return app.Run();
        }
        if (args.Contains("--capsule-hover-test"))
        {
            app.Startup += async (_, _) =>
            {
                var capsule = new CapsuleWindow((_, _) => Task.CompletedTask) { Title = "浮窗悬停验收", ShowInTaskbar = true, Left = 720, Top = 260 };
                capsule.Update(DemoData.State());
                int tooltipOpenings = 0; capsule.Surface.ToolTipOpening += (_, _) => tooltipOpenings++;
                capsule.Closed += (_, _) => { if (output != null) J.Write(output, J.Obj(("hoverEvents", capsule.HoverEventCount), ("transitions", capsule.ExpansionTransitions), ("tooltipOpenings", tooltipOpenings))); app.Shutdown(); };
                app.MainWindow = capsule; capsule.Show();
                if (Enum.TryParse<CapsuleEdge>(Arg("--dock-edge"), true, out var edge) && edge is not CapsuleEdge.None && Enum.IsDefined(edge))
                {
                    var bounds = capsule.CompactPixelBounds;
                    var s = System.Windows.Forms.Screen.FromPoint(new((int)bounds.X, (int)bounds.Y));
                    var work = new Rect(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height);
                    var target = CapsulePlacement.CompactAtEdge(bounds, work, edge);
                    capsule.Press(true); var origin = capsule.BeginDrag(new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
                    capsule.MoveBy(target.X - origin.X, target.Y - origin.Y, origin, null); await capsule.FinishDrag();
                }
                if (args.Contains("--keep-expanded")) await capsule.Invoke("keepExpanded");
            };
            return app.Run();
        }
        if (args.Contains("--preview-controls"))
        {
            string directory = output ?? Path.Combine(Paths.Base, "control-previews"); Directory.CreateDirectory(directory);
            app.Startup += async (_, _) =>
            {
                try { var result = await ControlPreviews.Render(directory); J.Write(Path.Combine(directory, "result.json"), result); app.Shutdown(); }
                catch (Exception e) { J.Write(Path.Combine(directory, "result.json"), J.Obj(("success", false), ("error", e.ToString()))); app.Shutdown(1); }
            };
            return app.Run();
        }
        if (args.Contains("--capsule-benchmark"))
        {
            app.Startup += async (_, _) =>
            {
                try { var result = await CapsuleTests.BenchmarkAsync(cycles: int.TryParse(Arg("--cycles"), out int cycles) ? cycles : 4); if (output is not null) J.Write(output, result); app.Shutdown(); }
                catch (Exception e) { if (output is not null) J.Write(output, J.Obj(("success", false), ("error", e.ToString()))); app.Shutdown(1); }
            };
            return app.Run();
        }
        if (args.Contains("--self-test"))
        {
            try { SelfTests.Run(output); return 0; } catch (Exception e) { if (output is not null) J.Write(output, J.Obj(("success", false), ("error", e.ToString()))); return 1; }
        }
        if (args.Contains("--preview"))
        {
            string directory = output ?? Path.Combine(Paths.Base, "previews"); Directory.CreateDirectory(directory);
            app.Startup += async (_, _) => { try { await Previews.Render(directory); J.Write(Path.Combine(directory, "result.json"), J.Obj(("success", true))); app.Shutdown(); } catch (Exception e) { J.Write(Path.Combine(directory, "result.json"), J.Obj(("success", false), ("error", e.ToString()))); app.Shutdown(1); } };
            return app.Run();
        }
        using var mutex = new Mutex(true, "Local\\" + Paths.Pipe + (main ? "-main-lock" : "-host-lock"), out bool owned);
        if (!owned) { try { Ipc.Send(J.Obj(("action", main ? "focus" : "main"), ("page", Arg("--page")), ("budgetID", Arg("--budget-id"))), main ? Paths.Pipe + "-main" : Paths.Pipe).GetAwaiter().GetResult(); return 0; } catch (Exception) { return 1; } }
        Host? host = null; Process? parentWatch = null; using var stop = new CancellationTokenSource();
        app.DispatcherUnhandledException += (_, e) => { try { Directory.CreateDirectory(Paths.Base); J.Write(Path.Combine(Paths.Base, "last-error.json"), J.Obj(("at", J.Now), ("error", e.Exception.ToString()))); } catch { } e.Handled = true; MessageBox.Show(e.Exception.Message, "Codex 用量", MessageBoxButton.OK, MessageBoxImage.Error); };
        app.Exit += (_, _) => { stop.Cancel(); host?.Dispose(); parentWatch?.Dispose(); };
        app.Startup += async (_, _) =>
        {
            try
            {
                if (main)
                {
                    if (!int.TryParse(Arg("--parent-pid"), out int pid) || pid <= 1) throw new ArgumentException("主面板需要由常驻宿主启动");
                    parentWatch = Process.GetProcessById(pid);
                    _ = parentWatch.WaitForExitAsync(stop.Token).ContinueWith(t => { if (!t.IsCanceled) app.Dispatcher.BeginInvoke(() => app.Shutdown()); }, TaskScheduler.Default);
                    JsonObject state = await Ipc.Send(J.Obj(("action", "state")));
                    Theme.Apply(Theme.Resolve(state.O("settings"), "main"));
                    var window = new MainWindow(state, Arg("--page"), Arg("--budget-id"));
                    if (Environment.GetEnvironmentVariable("CODEX_USAGE_TEST_BACKGROUND") == "1") { window.ShowActivated = false; window.ShowInTaskbar = false; window.Opacity = 0; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = window.Top = -12000; }
                    _ = Ipc.Listen(Paths.Pipe + "-main", request => app.Dispatcher.InvokeAsync(() => window.Handle(request)).Task.Unwrap(), stop.Token,
                        request => { if (request.S("action") == "close") app.Dispatcher.BeginInvoke(() => _ = window.CompleteClose()); },
                        request => { if (request.S("action") == "close") app.Dispatcher.BeginInvoke(window.CancelPreparedClose); });
                    window.Closed += (_, _) => app.Shutdown(); app.MainWindow = window; window.Show();
                }
                else
                {
                    var settings = new Settings(); Theme.Apply(Theme.Resolve(settings.Data, "main")); host = new Host(demo, args.Contains("--no-quota"));
                    if (!args.Contains("--tray")) await host.OpenMain();
                }
            }
            catch (Exception e) { J.Write(Path.Combine(Paths.Base, "last-error.json"), J.Obj(("error", e.ToString()))); MessageBox.Show(e.Message, "Codex 用量启动失败"); app.Shutdown(1); }
        };
        int exit = app.Run(); mutex.ReleaseMutex(); return exit;
    }
}

internal static class Previews
{
    public static void Save(FrameworkElement view, string path, int width, int height)
    {
        view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        BitmapSource rendered = bitmap;
        if (view is Window)
        {
            // Window rendering omits the native title bar; trim only the unused transparent rows.
            var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
            int bottom = height;
            while (bottom > 1 && !Enumerable.Range(0, width).Any(x => pixels[((bottom - 1) * width + x) * 4 + 3] != 0)) bottom--;
            if (bottom < height) rendered = new CroppedBitmap(bitmap, new Int32Rect(0, 0, width, bottom));
        }
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(rendered)); using var file = File.Create(path); encoder.Save(file);
    }
    public static async Task Render(string directory)
    {
        CapsuleEdgeIndicatorTests.Render(directory);
        TrayTests.Render(directory);
        J.Write(Path.Combine(directory, "layout-checks.json"), MainTests.CheckLayouts(Path.Combine(directory, "layouts")));
        foreach (bool light in new[] { false, true })
        {
            Theme.Apply(!light); string theme = light ? "light" : "dark";
            var state = DemoData.State(); state.O("settings").O("floating")["theme"] = theme;
            using var noOp = new CancellationTokenSource();
            var capsule = new CapsuleWindow((_, _) => Task.CompletedTask) { ShowActivated = false, Left = -5000, Top = -5000 }; capsule.Update(state);
            foreach (double? fraction in new double?[] { null, 0, .05, .10, .25, .50, .75, 1 })
            {
                state.O("quota")["capsuleFraction"] = fraction; state.O("quota")["stale"] = fraction is null; capsule.Surface.Update(state); capsule.Surface.BeginAnimation(CapsuleSurface.LevelProperty, null); capsule.Surface.Level = fraction ?? -1;
                Save(capsule.Surface, Path.Combine(directory, $"compact-{theme}-{(fraction is null ? "unknown" : (fraction * 100).ToString())}.png"), 76, 76);
            }
            state.O("quota")["capsuleFraction"] = .5; state.O("quota")["stale"] = false; capsule.Surface.Update(state); capsule.Surface.Level = .5; capsule.Surface.Expansion = 1;
            Save(capsule.Surface, Path.Combine(directory, $"expanded-{theme}.png"), 336, 410);
            state.O("settings").O("floating")["content"] = "budget"; state.O("settings").O("floating")["budgetID"] = "daily"; capsule.Surface.Update(state); Save(capsule.Surface, Path.Combine(directory, $"budget-capsule-{theme}.png"), 336, 410); capsule.Close();
            var main = new MainWindow(DemoData.State()) { ShowActivated = false, Left = -5000, Top = -5000 }; main.Show(); await Task.Delay(100); main.UpdateLayout(); Save(main, Path.Combine(directory, $"main-{theme}.png"), 1100, 780);
            await main.Handle(J.Obj(("action", "focus"), ("page", "budget"), ("budgetID", "daily"))); await Task.Delay(200); Save(main, Path.Combine(directory, $"budget-{theme}.png"), 1100, 780); main.Close();
        }
    }
}

internal static class SelfTests
{
    public static void Run(string? output)
    {
        ReliabilityTests.Run(); BudgetTests.Run(); MainTests.Run(); CapsuleTests.Run(); TrayTests.Run();
        var placement = CapsulePlacementTests.Run(); var indicator = CapsuleEdgeIndicatorTests.Run(); var morph = CapsuleMorphGeometryTests.Run();
        if (J.Number(JsonValue.Create(42)) != 42 || J.Number(JsonValue.Create(true)) is not null) throw new Exception("JSON numeric types");
        if (CapsuleColors.Color(.5, false) != System.Windows.Media.Color.FromRgb(233, 188, 96) || CapsuleColors.Color(.05, true) != System.Windows.Media.Color.FromRgb(212, 71, 79)) throw new Exception("Quota colors");
        var quota = Quota.Describe(J.Obj(("windows", new JsonArray(J.Obj(("used_percent", 150), ("duration_minutes", 300)), J.Obj(("used_percent", 20), ("duration_minutes", 10080)))), ("updated_at", J.Now)));
        if (quota.N("capsuleFraction") != 0) throw new Exception("Quota min/clamp");
        if (output is not null) J.Write(output, J.Obj(("success", true), ("placement", placement), ("indicator", indicator), ("morph", morph), ("checks", new JsonArray("budget-core", "chart-geometry", "main-dpi-layout", "capsule-geometry-and-interactions", "tray-hover", "exact-integers", "json-types", "quota-colors", "quota-selection"))));
    }
}
