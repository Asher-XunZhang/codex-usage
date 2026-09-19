using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexUsage;

internal sealed class ArcColorEditor : Window
{
    internal Action<CapsuleArcStyle?>? Preview;
    internal Func<CapsuleArcStyle, Task<string?>>? Save;
    internal CapsuleArcStyle Draft { get; private set; }
    private bool light, rendering, highSelected, saving, finished;
    private readonly bool[] invalid = new bool[2];
    private readonly ComboBox mode = new() { ItemsSource = new[] { "单色", "渐变" }, Width = 156 };
    private readonly TextBox lowHex = new(), highHex = new();
    private readonly Button reset = new() { Content = "重置默认颜色", MinWidth = 120 };
    private readonly Button low = new(), high = new(), swap = new() { Content = "⇄" }, apply = new() { Content = "应用", IsDefault = true, MinWidth = 82 };
    private readonly TextBlock lowLabel = new(), highLabel = new() { Text = "满额度 · 100%" }, target = new(), notice = new() { TextWrapping = TextWrapping.Wrap }, quota = new(), brightnessLabel = new();
    private readonly Slider fraction = new() { Minimum = 0, Maximum = 100, TickFrequency = 1, IsSnapToTickEnabled = true }, brightness = new() { Minimum = 0, Maximum = 1, SmallChange = .01 };
    private readonly ArcColorWheel wheel = new() { Width = 190, Height = 190 };
    private readonly Border strip = new() { Height = 12, CornerRadius = new(6), Margin = new(0, 10, 0, 16) };
    private readonly Button[] presets = new Button[6];
    private readonly ArcColorPreview orb = new() { Width = 76, Height = 76 };
    private readonly StackPanel body = new();
    private static readonly string[] Solids = ["#35DE94", "#28B8CE", "#3B82F6", "#A78BFA", "#F472B6", "#E9BC60"];
    private static readonly (string, string)[] Gradients = [("#35DE94", "#3B82F6"), ("#28B8CE", "#A78BFA"), ("#A78BFA", "#F472B6"), ("#E9BC60", "#EA6565"), ("#3B82F6", "#A78BFA"), ("#009E68", "#A5D76D")];
    internal ArcColorEditor(CapsuleArcStyle style, bool light, double? fraction, string? warning = null)
    {
        Draft = style; this.light = light;
        Title = "弧线配色"; Width = 480; Height = 716; MinWidth = 450; MinHeight = 500; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "BackgroundBrush"); SetResourceReference(ForegroundProperty, "ForegroundBrush");
        var root = new DockPanel { Margin = new(22) }; Content = root;
        var footer = new DockPanel { Margin = new(0, 16, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        reset.Click += (_, _) => { ClearInvalid(); Draft = Draft.ResetColors(); Changed(); }; footer.Children.Add(reset);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 75, Margin = new(8, 0, 8, 0) }; cancel.Click += (_, _) => Close(); actions.Children.Add(cancel); actions.Children.Add(apply); footer.Children.Add(actions);
        apply.Click += async (_, _) => await ApplyDraft();
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var preview = new DockPanel { Margin = new(0, 0, 0, 20) }; preview.Children.Add(orb);
        var previewControls = new StackPanel { Margin = new(22, 0, 0, 0) }; previewControls.Children.Add(new TextBlock { Text = "折叠浮窗 · 实时预览", Margin = new(0, 0, 0, 7) }); previewControls.Children.Add(quota); previewControls.Children.Add(this.fraction); preview.Children.Add(previewControls); body.Children.Add(preview);
        this.fraction.Value = Math.Round(Math.Clamp(fraction ?? .68, 0, 1) * 100); this.fraction.ValueChanged += (_, _) => RenderPreview();
        var modeRow = new DockPanel { Margin = new(0, 0, 0, 18) }; modeRow.Children.Add(new TextBlock { Text = "配色方式", VerticalAlignment = VerticalAlignment.Center }); mode.HorizontalAlignment = HorizontalAlignment.Right; modeRow.Children.Add(mode); body.Children.Add(modeRow);
        mode.SelectionChanged += (_, _) => { if (rendering) return; ClearInvalid(); Draft = Draft with { Mode = mode.SelectedIndex == 0 ? "solid" : "gradient" }; Changed(); };
        var endpoints = new Grid(); endpoints.ColumnDefinitions.Add(new()); endpoints.ColumnDefinitions.Add(new() { Width = new(45) }); endpoints.ColumnDefinitions.Add(new());
        void Endpoint(int index, TextBlock label, Button swatch, TextBox field)
        {
            var column = new StackPanel(); column.Children.Add(label); var row = new DockPanel { Margin = new(0, 6, 0, 0) };
            swatch.Width = 32; swatch.Height = 32; swatch.Margin = new(0, 0, 8, 0); row.Children.Add(swatch); field.VerticalContentAlignment = VerticalAlignment.Center; field.FontFamily = new("Consolas"); row.Children.Add(field); column.Children.Add(row); Grid.SetColumn(column, index * 2); endpoints.Children.Add(column);
            swatch.Click += (_, _) => { highSelected = index == 1; Render(); };
            field.TextChanged += (_, _) => ChangeHex(index, field.Text);
            AutomationProperties.SetName(field, index == 0 ? "单色或低额度 HEX" : "满额度 HEX"); AutomationProperties.SetName(swatch, index == 0 ? "调节单色或低额度颜色" : "调节满额度颜色");
        }
        Endpoint(0, lowLabel, low, lowHex); Endpoint(1, highLabel, high, highHex);
        swap.VerticalAlignment = VerticalAlignment.Bottom; swap.Margin = new(5, 0, 5, 0); Grid.SetColumn(swap, 1); endpoints.Children.Add(swap);
        swap.Click += (_, _) => { ClearInvalid(); var a = CapsuleArcStyle.Hex(Draft.Endpoint(false, light)); var b = CapsuleArcStyle.Hex(Draft.Endpoint(true, light)); Draft = Draft with { LowHex = b, HighHex = a }; Changed(); }; body.Children.Add(endpoints); body.Children.Add(strip); body.Children.Add(target);
        var picker = new DockPanel { Margin = new(0, 12, 0, 12) }; picker.Children.Add(wheel); var presetBody = new StackPanel { Margin = new(35, 0, 0, 0) }; presetBody.Children.Add(new TextBlock { Text = "常用配色", Margin = new(0, 0, 0, 10) });
        var presetGrid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Rows = 3, Width = 138 };
        for (int i = 0; i < 6; i++) { int index = i; var button = new Button { Height = 36, Margin = new(0, 0, 10, 12) }; presets[i] = button; presetGrid.Children.Add(button); button.Click += (_, _) => { ClearInvalid(); Draft = Draft.Mode == "solid" ? Draft with { SolidHex = Solids[index] } : Draft with { LowHex = Gradients[index].Item1, HighHex = Gradients[index].Item2 }; Changed(); }; }
        presetBody.Children.Add(presetGrid); picker.Children.Add(presetBody); body.Children.Add(picker); body.Children.Add(brightnessLabel); body.Children.Add(brightness);
        body.Children.Add(new TextBlock { Text = "渐变随剩余额度改变整段弧线的颜色。修改会在浮窗实时预览，应用后保存。", TextWrapping = TextWrapping.Wrap, Margin = new(0, 16, 0, 12) }); body.Children.Add(notice);
        wheel.Changed = () => { invalid[highSelected ? 1 : 0] = false; Draft = Draft.SetColor(wheel.Color, highSelected, light); Changed(true); };
        brightness.ValueChanged += (_, _) => { if (!rendering) wheel.SetBrightness(brightness.Value); };
        AutomationProperties.SetName(mode, "配色方式"); AutomationProperties.SetName(this.fraction, "预览剩余额度百分比"); AutomationProperties.SetName(brightness, "颜色明暗"); AutomationProperties.SetName(swap, "交换低额度和满额度颜色");
        Closing += (_, e) => { if (saving) e.Cancel = true; };
        Closed += (_, _) => { finished = true; Preview?.Invoke(null); };
        UpdateTheme(light); if (warning is not null) notice.Text = warning;
    }
    internal void UpdateTheme(bool value) { light = value; Theme.ApplyTo(Resources, !light); Render(); }
    private void ClearInvalid() { invalid[0] = invalid[1] = false; }
    private void ChangeHex(int index, string text)
    {
        if (rendering || saving) return;
        string value = text.Trim().ToUpperInvariant(); if (!value.StartsWith('#')) value = "#" + value;
        var color = CapsuleArcStyle.Parse(value); invalid[index] = color is null;
        if (color is Color valid) { highSelected = Draft.Mode == "gradient" && index == 1; Draft = Draft.SetColor(valid, highSelected, light); Changed(); }
        else Render();
    }
    private void Changed(bool keepWheel = false) { Render(keepWheel); Preview?.Invoke(Draft); }
    private static Brush Fill(Color a, Color? b = null) => b is Color other ? new LinearGradientBrush(a, other, 0) : new SolidColorBrush(a);
    private void Render(bool keepWheel = false)
    {
        rendering = true;
        try
        {
            bool gradient = Draft.Mode == "gradient"; if (!gradient) highSelected = false;
            mode.SelectedIndex = gradient ? 1 : 0; var visibility = gradient ? Visibility.Visible : Visibility.Hidden;
            high.Visibility = highHex.Visibility = highLabel.Visibility = swap.Visibility = visibility; strip.Visibility = gradient ? Visibility.Visible : Visibility.Hidden;
            lowLabel.Text = gradient ? "低额度 · 0%" : "颜色";
            var a = gradient ? Draft.Endpoint(false, light) : Draft.SolidColor(light); var b = Draft.Endpoint(true, light);
            low.Background = Fill(a); high.Background = Fill(b); low.BorderThickness = new(highSelected ? 1 : 3); high.BorderThickness = new(highSelected ? 3 : 1);
            if (!invalid[0]) lowHex.Text = CapsuleArcStyle.Hex(a); if (!invalid[1]) highHex.Text = CapsuleArcStyle.Hex(b);
            var ramp = new LinearGradientBrush(); for (int i = 0; i <= 100; i++) ramp.GradientStops.Add(new(Draft.Color(i / 100d, light), i / 100d)); strip.Background = ramp;
            target.Text = gradient ? highSelected ? "正在调节满额度颜色" : "正在调节低额度颜色" : "正在调节单色";
            if (!keepWheel) wheel.SetColor(highSelected ? b : a); brightness.Value = wheel.Brightness; brightnessLabel.Text = $"明暗  {Math.Round(wheel.Brightness * 100)}%";
            for (int i = 0; i < 6; i++) { var pair = Gradients[i]; presets[i].Background = gradient ? Fill(CapsuleArcStyle.Parse(pair.Item1)!.Value, CapsuleArcStyle.Parse(pair.Item2)) : Fill(CapsuleArcStyle.Parse(Solids[i])!.Value); AutomationProperties.SetName(presets[i], gradient ? $"渐变 {pair.Item1} 到 {pair.Item2}" : "单色 " + Solids[i]); }
            apply.IsEnabled = !saving && !invalid[0] && !invalid[1]; notice.Text = invalid[0] || invalid[1] ? "请输入 6 位十六进制颜色，如 #35DE94。" : gradient ? Draft.IsBuiltinGradient ? "内置渐变" : "自定义渐变" : Draft.SolidHex is null ? "满额度默认色" : "自定义单色";
            RenderPreview();
        }
        finally { rendering = false; }
    }
    private void RenderPreview() { quota.Text = $"预览额度  {fraction.Value:0}%"; orb.ArcStyle = Draft; orb.Light = light; orb.Fraction = fraction.Value / 100; orb.InvalidateVisual(); }
    internal async Task ApplyDraft()
    {
        if (saving || finished || invalid[0] || invalid[1] || Save is null) return;
        saving = true; body.IsEnabled = false; reset.IsEnabled = false; apply.IsEnabled = false; apply.Content = "保存中…";
        try { string? error = await Save(Draft); if (error is not null) { notice.Text = error; return; } saving = false; finished = true; Close(); }
        catch (Exception error) { notice.Text = "配色未能保存，修改已保留：" + error.Message; }
        finally { saving = false; body.IsEnabled = true; reset.IsEnabled = true; apply.Content = "应用"; apply.IsEnabled = !invalid[0] && !invalid[1]; }
    }
}

internal sealed class ArcColorPreview : FrameworkElement
{
    internal CapsuleArcStyle ArcStyle = new();
    internal bool Light;
    internal double Fraction;
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawEllipse(Theme.Color(Light ? "#F5F5F2" : "#17191B"), null, new(38, 38), 38, 38);
        dc.DrawEllipse(null, new(Theme.Color(Light ? "#DCE3DD" : "#343D38"), 5), new(38, 38), 33.5, 33.5);
        var pen = new Pen(new SolidColorBrush(ArcStyle.Color(Fraction, Light)), 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (Fraction >= 1) dc.DrawEllipse(null, pen, new(38, 38), 33.5, 33.5);
        else if (Fraction > 0) { var shape = new StreamGeometry(); using (var p = shape.Open()) { double angle = Fraction * Math.PI * 2 - Math.PI / 2; p.BeginFigure(new(38, 4.5), false, false); p.ArcTo(new(38 + 33.5 * Math.Cos(angle), 38 + 33.5 * Math.Sin(angle)), new(33.5, 33.5), 0, Fraction > .5, SweepDirection.Clockwise, true, false); } dc.DrawGeometry(null, pen, shape); }
        var text = new FormattedText($"{Fraction * 100:0}%", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 19, Theme.Color(Light ? "#202823" : "#F5F7F6"), VisualTreeHelper.GetDpi(this).PixelsPerDip); dc.DrawText(text, new((76 - text.Width) / 2, (76 - text.Height) / 2));
    }
}

internal sealed class ArcColorWheel : FrameworkElement
{
    internal Action? Changed;
    private double hue, saturation;
    internal double Brightness { get; private set; } = 1;
    internal Color Color => Rgb(hue, saturation, Brightness);
    private static readonly BitmapSource Wheel = CreateWheel();
    internal ArcColorWheel()
    {
        Focusable = true; Cursor = Cursors.Cross; AutomationProperties.SetName(this, "色轮，左右键调整色相，上下键调整饱和度");
        MouseLeftButtonDown += (_, e) => { Focus(); CaptureMouse(); Pick(e.GetPosition(this)); e.Handled = true; };
        MouseMove += (_, e) => { if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) Pick(e.GetPosition(this)); };
        MouseLeftButtonUp += (_, e) => { if (IsMouseCaptured) { ReleaseMouseCapture(); e.Handled = true; } };
        KeyDown += (_, e) => { double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? .05 : .01; switch (e.Key) { case Key.Left: hue = (hue - step + 1) % 1; break; case Key.Right: hue = (hue + step) % 1; break; case Key.Up: saturation = Math.Min(1, saturation + step); break; case Key.Down: saturation = Math.Max(0, saturation - step); break; default: return; } e.Handled = true; Update(); };
        GotKeyboardFocus += (_, _) => InvalidateVisual(); LostKeyboardFocus += (_, _) => InvalidateVisual();
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new WheelPeer(this);
    private sealed class WheelPeer(ArcColorWheel wheel) : FrameworkElementAutomationPeer(wheel), IRangeValueProvider
    {
        protected override string GetClassNameCore() => nameof(ArcColorWheel);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
        public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.RangeValue ? this : base.GetPattern(patternInterface);
        public bool IsReadOnly => !wheel.IsEnabled;
        public double LargeChange => 18;
        public double SmallChange => 3.6;
        public double Maximum => 360;
        public double Minimum => 0;
        public double Value => wheel.hue * 360;
        public void SetValue(double value)
        {
            if (!wheel.IsEnabled) throw new ElementNotEnabledException();
            if (!double.IsFinite(value) || value < 0 || value > 360) throw new ArgumentOutOfRangeException(nameof(value));
            wheel.hue = value / 360 % 1; wheel.Update();
        }
    }
    internal void SetBrightness(double value) { Brightness = Math.Clamp(value, 0, 1); Update(); }
    internal void SetColor(Color color)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d, max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
        if (delta > 0) hue = (((max == r ? (g - b) / delta : max == g ? (b - r) / delta + 2 : (r - g) / delta + 4) / 6) + 1) % 1;
        if (max > 0) saturation = delta / max; Brightness = max; InvalidateVisual();
    }
    private void Pick(Point point) { double x = (point.X - ActualWidth / 2) / (ActualWidth / 2), y = (ActualHeight / 2 - point.Y) / (ActualHeight / 2); hue = (Math.Atan2(y, x) / (2 * Math.PI) + 1) % 1; saturation = Math.Min(1, Math.Sqrt(x * x + y * y)); Update(); }
    private void Update() { InvalidateVisual(); AutomationProperties.SetHelpText(this, $"色相 {hue * 360:0} 度，饱和度 {saturation * 100:0}%，明暗 {Brightness * 100:0}%"); Changed?.Invoke(); }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawImage(Wheel, new(0, 0, ActualWidth, ActualHeight)); dc.DrawEllipse(new SolidColorBrush(Colors.Black) { Opacity = 1 - Brightness }, null, new(ActualWidth / 2, ActualHeight / 2), ActualWidth / 2, ActualHeight / 2);
        var point = new Point(ActualWidth / 2 * (1 + Math.Cos(hue * 2 * Math.PI) * saturation), ActualHeight / 2 * (1 - Math.Sin(hue * 2 * Math.PI) * saturation)); dc.DrawEllipse(null, new(Brushes.Black, 4), point, 5, 5); dc.DrawEllipse(null, new(Brushes.White, 2), point, 5, 5);
        if (IsKeyboardFocused) dc.DrawEllipse(null, new(SystemColors.HighlightBrush, 2), new(ActualWidth / 2, ActualHeight / 2), ActualWidth / 2 - 2, ActualHeight / 2 - 2);
    }
    internal static Color Rgb(double hue, double saturation, double brightness)
    {
        double h = hue * 6, c = brightness * saturation, x = c * (1 - Math.Abs(h % 2 - 1)), m = brightness - c;
        (double r, double g, double b) = h switch { < 1 => (c, x, 0d), < 2 => (x, c, 0d), < 3 => (0d, c, x), < 4 => (0d, x, c), < 5 => (x, 0d, c), _ => (c, 0d, x) };
        return System.Windows.Media.Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
    private static BitmapSource CreateWheel()
    {
        const int size = 320; var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) { double dx = (x + .5) / size * 2 - 1, dy = 1 - (y + .5) / size * 2, distance = Math.Sqrt(dx * dx + dy * dy); if (distance > 1) continue; var color = Rgb((Math.Atan2(dy, dx) / (2 * Math.PI) + 1) % 1, distance, 1); int i = (y * size + x) * 4; pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = (byte)(Math.Min(1, (1 - distance) * size / 2) * 255); }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4); bitmap.Freeze(); return bitmap;
    }
}
