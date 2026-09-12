using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexUsage;

internal static class UsageNumbers
{
    public static string Exact(JsonNode? value)
    {
        if (value is not JsonValue || value.GetValueKind() != System.Text.Json.JsonValueKind.Number) return "未知";
        string raw;
        try { raw = value.ToJsonString(); } catch (ArgumentException) { return "未知"; }
        if (BigInteger.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) && integer >= 0) return integer.ToString("N0", CultureInfo.InvariantCulture);
        if (decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var dec) && dec >= 0 && dec == decimal.Truncate(dec)) return dec.ToString("N0", CultureInfo.InvariantCulture);
        return "未知";
    }
    public static double? Count(JsonNode? value)
    {
        if (Exact(value) == "未知") return null;
        return double.TryParse(value!.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;
    }
}

public sealed class TrendView : FrameworkElement
{
    private List<JsonObject> data = new();
    private int? hovered;
    private readonly ToolTip tooltip = new() { Placement = PlacementMode.Relative, StaysOpen = true, IsHitTestVisible = false, Padding = new Thickness(12), MaxWidth = 450 };
    private static readonly Brush Input = new SolidColorBrush(Color.FromRgb(15, 140, 117));
    private static readonly Brush Output = new SolidColorBrush(Color.FromRgb(122, 102, 209));
    public int? HoveredDayIndex => hovered;
    public string? HoverText => hovered is int index && index < data.Count ? TooltipText(data[index]) : null;
    public TrendView()
    {
        Focusable = true; ClipToBounds = true; MinHeight = 65;
        AutomationProperties.SetName(this, "每日 Token 趋势");
        AutomationProperties.SetHelpText(this, "悬停或使用左右方向键查看日期、输入、输出和合计");
        tooltip.PlacementTarget = this;
        MouseMove += (_, e) => SetHover(IndexAt(e.GetPosition(this), new Size(ActualWidth, ActualHeight), data.Count), e.GetPosition(this));
        MouseLeave += (_, _) => Dismiss();
        LostKeyboardFocus += (_, _) => Dismiss();
        IsVisibleChanged += (_, _) => { if (!IsVisible) Dismiss(); };
        SizeChanged += (_, _) => Dismiss();
        Unloaded += (_, _) => Dismiss();
        KeyDown += (_, e) =>
        {
            if (data.Count == 0) return;
            if (e.Key == Key.Escape) { Dismiss(); e.Handled = true; return; }
            if (e.Key != Key.Left && e.Key != Key.Right && e.Key != Key.Home && e.Key != Key.End) return;
            int next = e.Key == Key.Home ? 0 : e.Key == Key.End ? data.Count - 1 : Math.Clamp((hovered ?? (e.Key == Key.Right ? -1 : data.Count)) + (e.Key == Key.Right ? 1 : -1), 0, data.Count - 1);
            Rect plot = Plot(new Size(ActualWidth, ActualHeight)); SetHover(next, new Point(plot.Left + (next + .5) * plot.Width / data.Count, plot.Top)); e.Handled = true;
        };
    }
    public void Update(JsonArray rows) { data = rows.Rows().Select(x => x.Copy()).ToList(); Dismiss(); AutomationProperties.SetName(this, $"每日 Token 趋势，{data.Count} 天"); InvalidateVisual(); }
    public void Dismiss() { hovered = null; tooltip.IsOpen = false; AutomationProperties.SetItemStatus(this, ""); InvalidateVisual(); }
    public static Rect Plot(Size size) => new(48, 12, Math.Max(0, size.Width - 64), Math.Max(0, size.Height - 40));
    public static int? IndexAt(Point point, Size size, int count)
    {
        Rect area = Plot(size);
        if (count <= 0 || area.Width <= 0 || area.Height <= 0 || point.X < area.Left || point.X >= area.Right || point.Y < area.Top || point.Y >= area.Bottom) return null;
        return Math.Min(count - 1, (int)((point.X - area.Left) / area.Width * count));
    }
    private static string Label(JsonObject row) => string.IsNullOrWhiteSpace(row.S("date")) ? "日期未知" : row.S("date").Trim();
    private static string TooltipText(JsonObject row) => $"{Label(row)}\n输入：{UsageNumbers.Exact(row["input_tokens"])} tokens\n输出：{UsageNumbers.Exact(row["output_tokens"])} tokens\n合计：{UsageNumbers.Exact(row["total_tokens"])} tokens";
    private void SetHover(int? index, Point point)
    {
        if (index == null) { if (hovered != null) Dismiss(); return; }
        bool changed = hovered != index; hovered = index;
        if (changed || !tooltip.IsOpen)
        {
            var row = data[index.Value];
            var panel = new StackPanel { MinWidth = 190 };
            panel.Children.Add(new TextBlock { Text = Label(row), FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) });
            foreach (var field in new[] { ("输入", "input_tokens"), ("输出", "output_tokens"), ("合计", "total_tokens") })
            {
                var line = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                line.Children.Add(new TextBlock { Text = field.Item1, Width = 42, FontSize = 11 });
                line.Children.Add(new TextBlock { Text = UsageNumbers.Exact(row[field.Item2]), TextAlignment = TextAlignment.Right, FontFamily = new FontFamily("Consolas"), FontSize = 11 });
                panel.Children.Add(line);
            }
            tooltip.Content = panel;
            AutomationProperties.SetItemStatus(this, TooltipText(row));
        }
        tooltip.HorizontalOffset = point.X + 14; tooltip.VerticalOffset = point.Y + 14;
        tooltip.IsOpen = true; InvalidateVisual();
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        Rect plot = Plot(RenderSize); if (plot.Width <= 0 || plot.Height <= 0) return;
        Brush foreground = TryFindResource("SecondaryBrush") as Brush ?? Brushes.Gray;
        Brush guide = new SolidColorBrush(Color.FromArgb(35, 128, 128, 128));
        double maximum = Math.Max(1, data.Select(x => UsageNumbers.Count(x["total_tokens"]) ?? 0).DefaultIfEmpty().Max());
        double ceiling = Math.Min(double.MaxValue / 1.08, maximum) * 1.08;
        void Text(string text, double x, double y, double width = 100)
        {
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Consolas"), 10, foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = width, Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(formatted, new Point(x, y));
        }
        for (int i = 0; i < 4; i++)
        {
            double y = plot.Bottom - (double)i / 3 * plot.Height;
            dc.DrawLine(new Pen(guide, 1), new Point(plot.Left, y), new Point(plot.Right, y));
            Text(J.Compact(ceiling * i / 3), 0, y - 7, 44);
        }
        if (data.Count == 0) { Text("该范围暂无可计入的记录", Math.Max(plot.Left, plot.Left + plot.Width / 2 - 78), plot.Top + plot.Height / 2 - 8, 180); return; }
        double stride = plot.Width / data.Count, width = Math.Max(.5, Math.Min(24, stride * .68));
        if (hovered is int selected) dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(15, 128, 128, 128)), null, new Rect(plot.Left + selected * stride, plot.Top, stride, plot.Height));
        for (int index = 0; index < data.Count; index++)
        {
            var row = data[index]; double total = UsageNumbers.Count(row["total_tokens"]) ?? 0;
            if (total <= 0) continue;
            double input = UsageNumbers.Count(row["input_tokens"]) ?? 0, output = UsageNumbers.Count(row["output_tokens"]) ?? 0;
            double x = plot.Left + (index + .5) * stride - width / 2, height = total / maximum / 1.08 * plot.Height;
            double denominator = Math.Max(total, input + output), inputHeight = height * input / denominator, outputHeight = height * output / denominator;
            dc.DrawRectangle(guide, null, new Rect(x, plot.Bottom - height, width, height));
            dc.DrawRectangle(Input, null, new Rect(x, plot.Bottom - inputHeight, width, inputHeight));
            dc.DrawRectangle(Output, null, new Rect(x, plot.Bottom - inputHeight - outputHeight, width, outputHeight));
        }
        foreach (int index in new[] { 0, data.Count / 2, data.Count - 1 }.Distinct())
        {
            string date = Label(data[index]); if (date.Length > 5) date = date[^5..];
            Text(date, Math.Min(plot.Right - 34, Math.Max(plot.Left, plot.Left + (index + .5) * stride - 15)), plot.Bottom + 9, 42);
        }
    }
}
