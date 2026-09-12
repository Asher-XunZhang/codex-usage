using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace CodexUsage;

internal readonly record struct TrayCallback(int Event, int IconId, Point Anchor)
{
    // NOTIFYICON_VERSION_4 packs the event/id in lParam and signed screen coordinates in wParam.
    internal static TrayCallback Decode(IntPtr wParam, IntPtr lParam) => new(
        (int)((long)lParam & 0xffff), (int)(((long)lParam >> 16) & 0xffff),
        new(unchecked((short)((long)wParam & 0xffff)), unchecked((short)(((long)wParam >> 16) & 0xffff))));
}

internal sealed class TrayHoverState
{
    public bool IsOpen { get; private set; }
    public bool Handle(int notification, bool available, bool blocked)
    {
        if (notification == 0x406) IsOpen = available && !blocked;
        else if (notification is 0x407 or 0x201 or 0x202 or 0x203 or 0x204 or 0x205 or 0x206 or 0x207 or 0x208 or 0x209 or 0x7b or 0x400 or 0x401 or 0x402 or 0x405) IsOpen = false;
        return IsOpen;
    }
    public void Close() => IsOpen = false;
}

internal static class TrayPlacement
{
    internal static Rect Place(Rect icon, Rect work, Size requested, double gap)
    {
        double w = Math.Min(requested.Width, work.Width), h = Math.Min(requested.Height, work.Height);
        double cx = icon.X + icon.Width / 2, cy = icon.Y + icon.Height / 2;
        var above = new Rect(cx - w / 2, icon.Top - gap - h, w, h);
        var below = new Rect(cx - w / 2, icon.Bottom + gap, w, h);
        var right = new Rect(icon.Right + gap, cy - h / 2, w, h);
        var left = new Rect(icon.Left - gap - w, cy - h / 2, w, h);
        Rect preferred = icon.Top >= work.Bottom ? above : icon.Bottom <= work.Top ? below : icon.Right <= work.Left ? right : icon.Left >= work.Right ? left :
            cy > work.Top + work.Height / 2 ? above : below;
        foreach (var candidate in new[] { preferred, above, below, right, left })
        {
            var clamped = new Rect(Math.Clamp(candidate.X, work.Left, work.Right - w), Math.Clamp(candidate.Y, work.Top, work.Bottom - h), w, h);
            if (!clamped.IntersectsWith(icon)) return clamped;
        }
        return new(Math.Clamp(preferred.X, work.Left, work.Right - w), Math.Clamp(preferred.Y, work.Top, work.Bottom - h), w, h);
    }
}

internal sealed record TrayQuotaLine(string Label, string Remaining, string Reset, double? Fraction);
internal sealed record TrayPreviewData(bool Light, string Total, string ExactTotal, string Input, string Output, string Cached,
    string Status, string QuotaStatus, IReadOnlyList<TrayQuotaLine> Quotas, IReadOnlyList<string> Budgets)
{
    internal string Monitor { get; init; } = "";
    internal static TrayPreviewData From(JsonObject state)
    {
        var today = state.O("today").O("summary"); var quota = state.O("quota");
        var quotas = quota.A("windows").Rows().Take(2).Select(row =>
        {
            double? remaining = row.N("remaining") ?? (row.N("used_percent") is double used ? Math.Clamp(100 - used, 0, 100) : null);
            if (remaining is double value && !double.IsFinite(value)) remaining = null;
            string reset = row.N("resets_at") is double t ? J.Date(t, "MM-dd HH:mm") + " 重置" : "重置时间未知";
            string label = row.S("label"); if (label.Length == 0) { int minutes = row.I("duration_minutes"); label = minutes == 10080 ? "周额度" : minutes == 300 ? "5 小时额度" : minutes > 0 ? minutes + " 分钟额度" : "账号额度"; }
            else label = label == "周" ? "周额度" : label == "5h" ? "5 小时额度" : label + "额度";
            return new TrayQuotaLine(label, remaining is double n ? "剩余 " + Math.Floor(Math.Clamp(n, 0, 100)).ToString(CultureInfo.InvariantCulture) + "%" : "剩余未知", reset, remaining / 100);
        }).ToArray();
        var budgets = state.O("budgets"); string selected = state.O("settings").O("floating").S("budgetID");
        var budgetLines = budgets.A("summaries").Rows().OrderByDescending(x => x.S("id") == selected).ThenByDescending(x => x.S("status") is "warning" or "exceeded")
            .Take(2).Select(x => x.S("name", "预算") + " · " + (x.B("paused") ? "提醒暂停 · " : "") + x.S("message", x.S("status", "待更新"))).ToList();
        if (budgets.S("error").Length > 0) budgetLines.Insert(0, budgets.S("error"));
        var localUpdate = state.O("updates").O("local"); var quotaUpdate = state.O("updates").O("quota");
        string status = localUpdate.S("error").Length > 0 ? "本地更新失败 · 可从菜单重试" : localUpdate.B("busy", state.B("busy")) ? "本地更新中 · 显示上次记录" : state.O("settings").I("refresh", 5) == 0 ? "本地自动更新已暂停" : state.S("status", "等待本地用量数据");
        string quotaStatus = quotaUpdate.B("busy") ? "账号额度更新中" : quota.S("error").Length > 0 ? quota.S("error") : quota.B("stale", true) ? "上次额度记录" : "账号额度";
        if (quotas.Length == 0 && quota.S("error").Length == 0) quotaStatus = "账号额度暂不可用";
        if (quota.N("reset_count") is double count) quotaStatus += " · 重置卡 " + count.ToString("N0", CultureInfo.InvariantCulture) + " 张";
        return new(!Theme.Resolve(state.O("settings"), "tray"), J.Compact(today.N("total_tokens")), UsageNumbers.Exact(today["total_tokens"]),
            UsageNumbers.Exact(today["input_tokens"]), UsageNumbers.Exact(today["output_tokens"]), UsageNumbers.Exact(today["cached_input_tokens"]),
            status, quotaStatus, quotas, budgetLines.Take(2).ToArray())
        { Monitor = state.O("monitor").A("watches").Count > 0 || state.O("monitor").O("summary").I("unread") > 0 ? TaskMonitorVisual.SummaryText(state) : "" };
    }
}

internal sealed class TrayPreview : Window
{
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    private Rect icon;
    internal TrayPreview()
    {
        Title = "Codex 用量详情"; Width = 348; Height = 360; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; ShowActivated = false; Focusable = false; IsHitTestVisible = false; Topmost = true;
        if (Environment.GetEnvironmentVariable("CODEX_USAGE_TEST_BACKGROUND") == "1") Opacity = 0;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(hwnd, -20, new IntPtr(GetWindowLongPtr(hwnd, -20).ToInt64() | 0x08000000 | 0x80 | 0x20));
            HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int message, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (message == 0x21) { handled = true; return new IntPtr(3); } // MA_NOACTIVATE
                if (message == 0x84) { handled = true; return new IntPtr(-1); } // HTTRANSPARENT
                return IntPtr.Zero;
            });
        };
    }
    internal void ShowAt(JsonObject state, Rect anchor)
    {
        icon = anchor; Update(state); if (!IsVisible) Show();
    }
    internal void Update(JsonObject state)
    {
        var card = BuildCard(TrayPreviewData.From(state));
        card.Measure(new Size(348, double.PositiveInfinity)); var desired = card.DesiredSize;
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        // Place the hidden HWND on the target monitor before asking for its effective DPI.
        if (!IsVisible) SetWindowPos(hwnd, IntPtr.Zero, (int)icon.X, (int)icon.Y, 0, 0, 0x15);
        double dpi = Math.Max(96, GetDpiForWindow(hwnd)) / 96d;
        var screen = System.Windows.Forms.Screen.FromPoint(new((int)(icon.X + icon.Width / 2), (int)(icon.Y + icon.Height / 2))).WorkingArea;
        var bounds = TrayPlacement.Place(icon, new(screen.X, screen.Y, screen.Width, screen.Height), new(desired.Width * dpi, desired.Height * dpi), 10 * dpi);
        Width = bounds.Width / dpi; Height = bounds.Height / dpi;
        Content = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = card };
        SetWindowPos(hwnd, new IntPtr(-1), (int)Math.Round(bounds.X), (int)Math.Round(bounds.Y), (int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), 0x10);
    }
    internal static FrameworkElement BuildCard(TrayPreviewData data)
    {
        var bg = Theme.Color(data.Light ? "#FCFDFC" : "#202624"); var panel = Theme.Color(data.Light ? "#EEF3EF" : "#2A332F");
        var ink = Theme.Color(data.Light ? "#202823" : "#F5F7F6"); var secondary = Theme.Color(data.Light ? "#626C67" : "#ADB6B2");
        var accent = Theme.Color(data.Light ? "#047857" : "#57E6B2"); var line = Theme.Color(data.Light ? "#D6DDD7" : "#3C4340");
        TextBlock Text(string value, double size = 12, Brush? color = null, bool bold = false) => new()
        {
            Text = value,
            FontSize = size,
            Foreground = color ?? ink,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var stack = new StackPanel();
        var header = new DockPanel(); var today = Text("今日", 11, secondary); DockPanel.SetDock(today, Dock.Right); header.Children.Add(today); header.Children.Add(Text("◉  Codex 用量", 13, ink, true)); stack.Children.Add(header);
        var total = Text(data.Total, 28, accent, true); total.Margin = new(0, 10, 0, 0); stack.Children.Add(total);
        var exact = Text(data.ExactTotal + " tokens", 11, secondary); exact.Margin = new(0, 1, 0, 12); stack.Children.Add(exact);
        var quotaStatus = Text(data.QuotaStatus, 10, secondary); quotaStatus.MaxHeight = 30; quotaStatus.Margin = new(0, 13, 0, 6); stack.Children.Add(quotaStatus);
        foreach (var quota in data.Quotas)
        {
            var row = new DockPanel(); var left = Text(quota.Label, 11); left.Width = 112; row.Children.Add(left); var right = Text(quota.Remaining, 11, accent, true); right.TextAlignment = TextAlignment.Right; row.Children.Add(right); stack.Children.Add(row);
            var track = new Grid { Height = 3, Margin = new(0, 5, 0, 4), Background = panel };
            if (quota.Fraction is double f) { track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Clamp(f, 0, 1), GridUnitType.Star) }); track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - Math.Clamp(f, 0, 1), GridUnitType.Star) }); track.Children.Add(new Border { Background = accent, CornerRadius = new(1.5) }); }
            stack.Children.Add(track); var reset = Text(quota.Reset, 10, secondary); reset.Margin = new(0, 0, 0, 9); stack.Children.Add(reset);
        }
        if (data.Budgets.Count > 0)
        {
            stack.Children.Add(new Border { Height = 1, Background = line, Margin = new(0, 3, 0, 8) });
            foreach (string budget in data.Budgets.Take(1)) { var text = Text(budget, 11, secondary); text.MaxHeight = 32; text.Margin = new(0, 0, 0, 4); stack.Children.Add(text); }
        }
        var status = Text(data.Status, 11, secondary); status.MaxHeight = 32; status.Margin = new(0, 9, 0, 0); stack.Children.Add(status);
        if (data.Monitor.Length > 0)
        {
            var monitor = Text("任务：" + data.Monitor, 11, secondary); monitor.Margin = new(0, 8, 0, 0); stack.Children.Add(monitor);
        }
        var hint = Text("单击图标查看详情与操作", 11, secondary); hint.Margin = new(0, 6, 0, 0); stack.Children.Add(hint);
        return new Border { Width = 348, Background = bg, BorderBrush = line, BorderThickness = new(1), CornerRadius = new(13), Padding = new(18, 15, 18, 14), Child = stack };
    }
}
