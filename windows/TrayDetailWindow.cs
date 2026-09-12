using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace CodexUsage;

// A clicked tray card is a normal focusable window; the hover card remains passive.
internal sealed class TrayDetailWindow : Window
{
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    private readonly TextBox quotas = Readout(13), total = Readout(32), breakdown = Readout(12), budget = Readout(12), updates = Readout(11);
    private readonly Button refresh;
    private readonly Grid layout;
    private readonly StackPanel usageBody, monitorBody = new();
    private readonly ScrollViewer scroll;
    private readonly ToggleButton usageTab, monitorTab;
    private readonly Button openMain;
    private JsonObject snapshot = new();
    private bool monitorPage;
    private string monitorSignature = "";
    private Rect icon;
    internal Action? OpenMain, RefreshData, OpenSettings;
    internal Action<string?>? OpenMonitor;
    internal Action? OpenMonitorSettings;
    internal Func<JsonObject, Task<JsonObject>>? MonitorRequest;
    internal Action<bool>? Dismissed;
    internal FrameworkElement Card => layout;
    internal TrayDetailWindow()
    {
        Title = "Codex 用量 · 托盘详情"; Width = 370; Height = 540; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; Topmost = true;
        layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new DockPanel { Margin = new Thickness(16, 12, 16, 8) };
        var close = new Button { Content = "关闭详情", Padding = new Thickness(8, 4, 8, 4) }; close.Click += (_, _) => Dismiss();
        DockPanel.SetDock(close, Dock.Right); header.Children.Add(close); header.Children.Add(Label("Codex 用量", 15, true)); layout.Children.Add(header);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        usageTab = new ToggleButton { Content = "用量", IsChecked = true, MinWidth = 70 }; monitorTab = new ToggleButton { Content = "监控", MinWidth = 90 };
        tabs.Children.Add(usageTab); tabs.Children.Add(monitorTab);
        var navigation = new SegmentedGroup(tabs) { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(16, 0, 16, 8) }; Grid.SetRow(navigation, 1); layout.Children.Add(navigation);
        var body = usageBody = new StackPanel { Margin = new Thickness(16, 2, 16, 4) };
        monitorBody.Margin = new Thickness(16, 2, 16, 4);
        body.Children.Add(Label("账号额度", 13, true)); body.Children.Add(quotas);
        body.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 10) });
        body.Children.Add(Label("本地 · 今日 Token")); body.Children.Add(total); body.Children.Add(breakdown);
        body.Children.Add(budget); body.Children.Add(updates);
        scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetRow(scroll, 2); layout.Children.Add(scroll);
        var footer = new DockPanel { Margin = new Thickness(16, 10, 16, 16) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; refresh = new Button { Content = "刷新", Margin = new Thickness(0, 0, 7, 0) };
        var settings = new Button { Content = "设置" }; buttons.Children.Add(refresh); buttons.Children.Add(settings); DockPanel.SetDock(buttons, Dock.Right); footer.Children.Add(buttons);
        var main = openMain = new Button { Content = "打开主面板", HorizontalAlignment = HorizontalAlignment.Left }; footer.Children.Add(main); Grid.SetRow(footer, 3); layout.Children.Add(footer);
        main.Click += (_, _) => { Dismiss(); if (monitorPage) OpenMonitor?.Invoke(null); else OpenMain?.Invoke(); };
        refresh.Click += async (_, _) => { if (!monitorPage) { RefreshData?.Invoke(); return; } if (MonitorRequest is not null) { refresh.IsEnabled = false; try { Update(await MonitorRequest(J.Obj(("action", "monitor"), ("operation", "check"), ("payload", new JsonObject())))); } catch (Exception e) { monitorBody.Children.Add(Label(e.Message)); } finally { refresh.IsEnabled = true; } } };
        settings.Click += (_, _) => { Dismiss(); if (monitorPage) OpenMonitorSettings?.Invoke(); else OpenSettings?.Invoke(); };
        usageTab.Click += (_, _) => SelectMonitor(false); monitorTab.Click += (_, _) => SelectMonitor(true);
        var border = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Child = layout };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); Content = border;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Dismiss(); e.Handled = true; } };
        Deactivated += (_, _) =>
        {
            bool onIcon = GetCursorPos(out var pointer) && icon.Contains(new Point(pointer.X, pointer.Y));
            Dismiss(IsIconMouseDismissal(onIcon, (GetKeyState(0x01) & 0x8000) != 0));
        };
    }
    internal static bool IsIconMouseDismissal(bool onIcon, bool leftPressed) => onIcon && leftPressed;
    private static TextBlock Label(string text, double size = 12, bool bold = false) => new() { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
    private static TextBox Readout(double size) => new() { IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, FontSize = size, Padding = new Thickness(0), Margin = new Thickness(0, 5, 0, 7), TextWrapping = TextWrapping.Wrap, IsUndoEnabled = false };
    internal bool IsViewingMonitor(string taskID) => IsVisible && monitorPage && TaskMonitorVisual.Featured(snapshot).Any(x => x.S("id") == taskID);
    internal void SelectMonitor(bool selected)
    {
        monitorPage = selected; usageTab.IsChecked = !selected; monitorTab.IsChecked = selected;
        scroll.Content = selected ? monitorBody : usageBody; refresh.Content = selected ? "检查" : "刷新"; Update(snapshot);
    }
    internal void Update(JsonObject state)
    {
        snapshot = state;
        Theme.ApplyTo(Resources, Theme.Resolve(state.O("settings"), "tray")); var data = TrayPreviewData.From(state);
        void Set(TextBox target, string text) { if (target.Text != text) target.Text = text; }
        Set(quotas, string.Join("\n", data.Quotas.Select(x => x.Label + " · " + x.Remaining + "\n" + x.Reset)) + "\n" + data.QuotaStatus);
        Set(total, data.Total); total.SetResourceReference(ForegroundProperty, "AccentBrush");
        Set(breakdown, data.ExactTotal + " tokens\n输入 " + data.Input + " · 输出 " + data.Output + "\n其中缓存 " + data.Cached);
        Set(budget, string.Join("\n", data.Budgets)); budget.Visibility = data.Budgets.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var sources = state.O("updates");
        Set(updates, "本地：" + J.Date(sources.O("local").N("updatedAt"), "HH:mm:ss") + " · " + data.Status + "\n账号：" + J.Date(sources.O("quota").N("updatedAt"), "HH:mm:ss") + " · " + data.QuotaStatus);
        refresh.IsEnabled = monitorPage || !state.B("busy");
        int unread = state.O("monitor").O("summary").I("unread"); monitorTab.Content = unread > 0 ? "监控 " + (unread > 9 ? "9+" : unread.ToString()) : "监控";
        var monitored = state.O("monitor");
        string signature = J.Text(J.Obj(("watches", monitored.A("watches")), ("summary", monitored.O("summary")), ("source", monitored.O("sourceStatus").S("text")), ("error", monitored.S("error"))));
        if (signature != monitorSignature)
        {
            monitorSignature = signature; monitorBody.Children.Clear();
            monitorBody.Children.Add(Label(TaskMonitorVisual.SummaryText(state), 12));
            var rows = TaskMonitorVisual.Featured(state).Take(2).ToArray();
            foreach (var row in rows)
            {
                string taskID = row.S("id");
                var task = new StackPanel(); task.Children.Add(Label(TaskMonitorGlyph.Label(row.S("status")), 12, true)); task.Children.Add(Label(row.S("title", "Codex 任务"), 13)); task.Children.Add(Label(row.S("project"), 11));
                var detail = new Button { Content = task, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10) };
                detail.Click += (_, _) => { Dismiss(); OpenMonitor?.Invoke(taskID); }; monitorBody.Children.Add(detail);
            }
            if (rows.Length == 0) monitorBody.Children.Add(Label("选择正在执行的任务，本轮结束后提醒你。"));
            var all = new Button { Content = rows.Length == 0 ? "选择任务" : "查看全部任务与消息", Margin = new Thickness(0, 12, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
            all.Click += (_, _) => { Dismiss(); OpenMonitor?.Invoke(null); }; monitorBody.Children.Add(all);
            monitorBody.Children.Add(Label(monitored.O("sourceStatus").S("text", "任务监控尚未准备"), 11));
            if (monitored.S("error").Length > 0) monitorBody.Children.Add(Label(monitored.S("error"), 11));
        }
    }
    internal void ShowAt(JsonObject state, Rect anchor)
    {
        icon = anchor; Update(state);
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        if (!IsVisible) SetWindowPos(hwnd, IntPtr.Zero, (int)icon.X, (int)icon.Y, 0, 0, 0x15);
        double dpi = Math.Max(96, GetDpiForWindow(hwnd)) / 96d;
        var work = System.Windows.Forms.Screen.FromPoint(new((int)anchor.X, (int)anchor.Y)).WorkingArea;
        double width = Math.Max(1, Math.Min(370, work.Width / dpi - 16));
        var card = (FrameworkElement)Content; card.Measure(new Size(width, double.PositiveInfinity));
        double height = Math.Min(card.DesiredSize.Height, work.Height / dpi - 16);
        var bounds = TrayPlacement.Place(anchor, new(work.X, work.Y, work.Width, work.Height), new(Math.Max(1, width) * dpi, Math.Max(1, height) * dpi), 10 * dpi);
        Width = bounds.Width / dpi; Height = bounds.Height / dpi;
        SetWindowPos(hwnd, new IntPtr(-1), (int)Math.Round(bounds.X), (int)Math.Round(bounds.Y), (int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), 0x10);
        if (!IsVisible) Show(); Activate();
    }
    internal void Dismiss(bool onIcon = false) { if (!IsVisible) return; Hide(); Dismissed?.Invoke(onIcon); }
}
