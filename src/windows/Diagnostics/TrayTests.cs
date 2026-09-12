using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexUsage;

internal static class TrayTests
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    public static async Task<JsonObject> NativeAsync()
    {
        var app = Application.Current ?? throw new InvalidOperationException("Tray integration test requires a WPF dispatcher.");
        var previousShutdown = app.ShutdownMode; app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Tray? tray = null; var checks = new JsonArray();
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Native tray test: " + message); checks.Add(message); }
        bool HasText(DependencyObject element, string text)
        {
            if (element is TextBlock block && block.Text.Contains(text, StringComparison.Ordinal)) return true;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) if (HasText(VisualTreeHelper.GetChild(element, i), text)) return true;
            return false;
        }
        async Task Settle()
        {
            await app.Dispatcher.InvokeAsync(() => tray?.PreviewWindow?.UpdateLayout(), DispatcherPriority.ApplicationIdle);
            await Task.Delay(60);
        }
        try
        {
            tray = new Tray(); tray.Update("Codex 用量 · 合成悬浮卡集成测试", Fixture()); tray.SetVisible(true); await Settle();
            Check(tray.IconVisible, "isolated synthetic icon registered with the Windows shell");
            var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            var open = new TrayCallback(0x406, 1, new Point(work.Right - 20, work.Bottom - 20));
            IntPtr foregroundBefore = GetForegroundWindow();
            Check(tray.Dispatch(open), "NIN_POPUPOPEN traverses the production tray dispatcher"); await Settle();
            var preview = tray.PreviewWindow; var hwnd = preview is null ? IntPtr.Zero : new WindowInteropHelper(preview).Handle;
            Check(preview is not null && tray.PreviewVisible && IsWindowVisible(hwnd) && preview.ActualWidth > 0 && preview.ActualHeight > 0, "OpenPreview and ShowAt create a visible native details window");
            long style = GetWindowLongPtr(hwnd, -20).ToInt64();
            Check((style & 0x08000000) != 0 && (style & 0x80) != 0 && !preview!.ShowActivated && !preview.IsActive && GetForegroundWindow() != hwnd,
                "native WS_EX_NOACTIVATE and tool-window styles keep the preview passive");
            bool foregroundUnchanged = foregroundBefore == GetForegroundWindow(), iconBoundsResolved = tray.IconBoundsResolved;
            var update = Fixture(true); update.O("today").O("summary")["total_tokens"] = 98765L; update["status"] = "合成数据已更新";
            tray.Update("Codex 用量 · 更新测试", update); await Settle();
            Check(tray.PreviewVisible && ReferenceEquals(preview, tray.PreviewWindow) && HasText(preview!, "98,765 tokens") && HasText(preview!, "合成数据已更新"),
                "visible native hover card updates from the new cached snapshot without replacing its HWND");
            tray.Dispatch(open with { Event = 0x407 }); await Settle();
            Check(!tray.PreviewVisible && !IsWindowVisible(hwnd), "NIN_POPUPCLOSE hides the real preview window");
            tray.Dispatch(open); await Settle(); tray.SetVisible(false); await Settle();
            Check(!tray.IconVisible && !tray.PreviewVisible && !IsWindowVisible(hwnd), "hiding the tray removes its icon and closes the preview");
            tray.SetVisible(true); tray.Dispatch(open); await Settle();
            Check(tray.IconVisible && tray.PreviewVisible, "the isolated tray can reopen its cached preview");
            tray.Dispose(); await Settle();
            Check(!tray.IconVisible && !tray.PreviewVisible && !IsWindow(hwnd) && !tray.Dispatch(open), "dispose destroys the preview HWND and rejects late hover callbacks");
            return J.Obj(("success", true), ("checks", checks), ("foregroundUnchanged", foregroundUnchanged), ("shellIconBoundsResolved", iconBoundsResolved));
        }
        finally { tray?.Dispose(); app.ShutdownMode = previousShutdown; }
    }

    internal static JsonObject Fixture(bool light = false) => J.Obj(
        ("settings", J.Obj(("refresh", 5), ("floating", J.Obj(("theme", light ? "light" : "dark"), ("budgetID", "demo"))))),
        ("today", J.Obj(("summary", J.Obj(("total_tokens", 12848200L), ("input_tokens", 12005200L), ("output_tokens", 843000L), ("cached_input_tokens", 9850400L))))),
        ("quota", J.Obj(("stale", false), ("reset_count", 2), ("windows", new JsonArray(
            J.Obj(("label", "5h"), ("remaining", 75), ("resets_at", 1789232400)),
            J.Obj(("label", "周"), ("remaining", 50), ("resets_at", 1789696800)))))),
        ("budgets", J.Obj(("summaries", new JsonArray(J.Obj(("id", "demo"), ("name", "开发预算"), ("status", "warning"), ("message", "本期剩余 20%")))))),
        ("status", "已更新 · 演示数据"));

    public static void Run()
    {
        _ = TrayInteractionTests.Run();
        void Check(bool value, string description) { if (!value) throw new InvalidOperationException("Tray test: " + description); }
        IntPtr Pack(int low, int high) => new(unchecked((int)((uint)(ushort)low | ((uint)(ushort)high << 16))));
        var notification = TrayCallback.Decode(Pack(-1200, -180), Pack(0x406, 1));
        Check(notification.Event == 0x406 && notification.IconId == 1 && notification.Anchor == new Point(-1200, -180), "version-4 event/id and signed negative-monitor anchor decode");
        Check((Tray.RichTooltipFlags & 0x80) == 0 && (Tray.RichTooltipFlags & 4) != 0, "rich hover suppresses standard tooltip without discarding accessible text");
        var hover = new TrayHoverState();
        Check(hover.Handle(notification.Event, true, false), "NIN_POPUPOPEN shows cached details");
        Check(hover.Handle(0x200, true, false), "ordinary mouse movement does not close an active preview");
        Check(!hover.Handle(0x407, true, false), "NIN_POPUPCLOSE closes details");
        foreach (int close in new[] { 0x201, 0x202, 0x203, 0x204, 0x205, 0x7b, 0x400, 0x401, 0x402, 0x405 })
        {
            hover.Handle(0x406, true, false); Check(!hover.Handle(close, true, false), "click/menu/balloon event closes hover before its own action: " + close);
        }
        Check(!hover.Handle(0x406, false, false) && !hover.Handle(0x406, true, true), "hidden tray and open menu suppress late hover requests");
        hover.Handle(0x406, true, false); hover.Close(); Check(!hover.IsOpen, "hide and dispose reset hover state");
        foreach (double scale in new[] { 1d, 1.25, 1.5, 2, 3 })
        {
            var work = new Rect(-1920, -200, 1920, 1080);
            foreach (var icon in new[] { new Rect(-45, 888, 30, 30), new Rect(-1000, -240, 30, 30), new Rect(-1960, 200, 30, 30), new Rect(10, 200, 30, 30), new Rect(-1800, 700, 30, 30) })
            {
                var placed = TrayPlacement.Place(icon, work, new(348 * scale, 435 * scale), 10 * scale);
                Check(work.Contains(placed) && !placed.IntersectsWith(icon), "taskbar edge / overflow preview clamps in physical pixels at DPI " + scale);
            }
        }
        var tiny = TrayPlacement.Place(new Rect(40, 210, 20, 20), new(0, 0, 160, 200), new(348, 440), 10);
        Check(tiny == new Rect(0, 0, 160, 200), "oversized tooltip scales within very small work area");
        var state = Fixture(); var data = TrayPreviewData.From(state);
        Check(data.Total == "12.85M" && data.ExactTotal == "12,848,200" && data.Input == "12,005,200" && data.Cached == "9,850,400", "all daily usage fields use the today snapshot");
        Check(data.Quotas.Count == 2 && data.Quotas[0].Remaining == "剩余 75%" && data.Quotas[0].Reset.Contains("重置") && data.QuotaStatus.Contains("2 张"), "quota windows include remaining values, reset times and credits");
        Check(data.Budgets.Single().Contains("本期剩余 20%"), "selected budget summary appears in hover");
        state.O("today").O("summary")["total_tokens"] = 9007199254740993L;
        Check(TrayPreviewData.From(state).ExactTotal == "9,007,199,254,740,993", "tooltip exact count preserves integers above double precision");
        state["today"] = new JsonObject(); state["quota"] = new JsonObject(); state["busy"] = true;
        data = TrayPreviewData.From(state);
        Check(data.ExactTotal == "未知" && data.Quotas.Count == 0 && data.QuotaStatus == "账号额度暂不可用" && data.Status.Contains("上次记录"), "missing data remains unknown and never becomes a zero quota");
        var app = Application.Current; var shutdown = app.ShutdownMode; app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var preview = new TrayPreview(); Check(!preview.ShowActivated && !preview.ShowInTaskbar && !preview.Focusable && !preview.IsHitTestVisible, "hover is passive and nonactivating"); preview.Close();
            // No icon is registered by construction or by hiding/disposal.
            var tray = new Tray(); int activations = 0; tray.OpenMain = () => activations++;
            tray.Menu = () => { activations++; return new ContextMenu(); };
            tray.SetVisible(false); tray.Update("synthetic", Fixture());
            Check(tray.Dispatch(notification) && tray.Dispatch(notification with { Event = 0x407 }) && !tray.PreviewVisible && !tray.PendingClick && activations == 0, "hover notifications never take the click or main-panel action path");
            Check(!tray.Dispatch(notification with { Event = 0x203, IconId = 2 }) && activations == 0, "version-4 callbacks for a different icon are ignored");
            foreach (int input in new[] { 0x201, 0x202, 0x203, 0x400, 0x401, 0x205, 0x7b }) tray.Dispatch(notification with { Event = input });
            Check(activations == 0 && !tray.PendingClick, "hidden production dispatcher ignores late mouse, keyboard and context-menu callbacks");
            tray.Dispose(); tray.Dispose(); tray.Update("ignored", Fixture()); tray.SetVisible(true);
            Check(!tray.Dispatch(notification) && !tray.PreviewVisible && !tray.PendingClick, "late callbacks after dispose cannot resurrect windows or timers");
            foreach (bool light in new[] { false, true })
            {
                var card = TrayPreview.BuildCard(TrayPreviewData.From(Fixture(light))); var bitmap = Bitmap(card);
                var pixel = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixel, bitmap.PixelWidth * 4, 0);
                Check(pixel[(25 * bitmap.PixelWidth + 25) * 4 + 3] == 255 && pixel[3] == 0 && bitmap.PixelHeight > 250 && bitmap.PixelHeight < 420, "offline " + (light ? "light" : "dark") + " hover is a complete compact summary card");
            }
        }
        finally { app.ShutdownMode = shutdown; }
        Console.WriteLine("Tray tests passed: version-4 hover, dismissal, passive lifecycle, DPI placement, cached data and offline themes.");
    }
    private static RenderTargetBitmap Bitmap(FrameworkElement card)
    {
        card.Measure(new Size(348, double.PositiveInfinity)); var size = card.DesiredSize;
        card.Arrange(new Rect(new Point(), size)); card.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32); bitmap.Render(card); return bitmap;
    }
    public static void Render(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (bool light in new[] { false, true })
        {
            var bitmap = Bitmap(TrayPreview.BuildCard(TrayPreviewData.From(Fixture(light)))); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(directory, "tray-hover-" + (light ? "light" : "dark") + ".png")); png.Save(file);
        }
    }
}
