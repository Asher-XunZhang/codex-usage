using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace CodexUsage;

internal static class CapsuleGeometry
{
    public static Rect Clamp(Rect bounds, Rect work) => new(
        Math.Clamp(bounds.Left, work.Left, Math.Max(work.Left, work.Right - bounds.Width)),
        Math.Clamp(bounds.Top, work.Top, Math.Max(work.Top, work.Bottom - bounds.Height)), bounds.Width, bounds.Height);
    public static bool Contains(Point p, Size size, double expansion)
    {
        if (size.Width <= 0 || size.Height <= 0 || p.X < 0 || p.Y < 0 || p.X >= size.Width || p.Y >= size.Height) return false;
        double radius = Math.Min(Math.Min(size.Width, size.Height) / 2, 38 - 16 * Math.Clamp(expansion, 0, 1));
        double x = p.X < radius ? radius : p.X > size.Width - radius ? size.Width - radius : p.X;
        double y = p.Y < radius ? radius : p.Y > size.Height - radius ? size.Height - radius : p.Y;
        return (p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y) <= radius * radius;
    }
    public static bool Dragged(Point start, Point end, DpiScale dpi) => Math.Sqrt(Math.Pow((end.X - start.X) / dpi.DpiScaleX, 2) + Math.Pow((end.Y - start.Y) / dpi.DpiScaleY, 2)) >= 3;
}

internal sealed record CapsuleBudgetDisplay(string Name, string Used, string Remaining, string Amount, string AmountLabel, string Caption, string Status, string Period, string Scope, double? Fraction, bool Stale)
{
    public static CapsuleBudgetDisplay From(JsonObject state)
    {
        var f = state.O("settings").O("floating"); string id = f.S("budgetID"); var budgets = state.O("budgets");
        var row = budgets.A("summaries").Rows().FirstOrDefault(x => x.S("id") == id);
        var rule = row ?? budgets.A("rules").Rows().FirstOrDefault(x => x.S("id") == id) ?? new();
        string kind = row.S("kind", rule.S("kind", "token")), currency = row.S("currency", rule.S("currency", "USD"));
        string suffix = kind == "quota" ? "%" : kind == "money" ? " " + currency : " Token";
        string Format(double? value, bool compact = false) => value is not double n ? "—" : compact ? J.Compact(n) : n.ToString(n == Math.Truncate(n) ? "N0" : "N2", CultureInfo.InvariantCulture);
        string status = row.S("status", "unknown");
        string caption = status switch { "disabled" => "已停用", "ended" => "已结束", "scheduled" => "待开始", "source_invalid" => "目录变化", "scope_invalid" => "范围失效", "partial" => "部分数据", "unknown" => "待更新", _ => kind == "quota" ? "窗口已用" : "预算已用" };
        string name = row.S("name", rule.S("name", id.Length == 0 ? "选择预算" : "预算已删除"));
        string message = row.S("message", row.S("reason", id.Length == 0 ? "在主面板新建或选择预算" : rule.Count == 0 ? "所选预算已删除；请重新选择" : "等待预算数据更新"));
        if (row.B("paused")) message = "提醒已暂停 · " + message;
        if (kind != "quota" && state.O("settings").I("refresh", 5) == 0) message = "自动更新暂停 · " + message;
        else if (row.N("updatedAt") is double updated) message += " · " + J.Date(updated, "HH:mm:ss");
        string zone = rule.O("period").S("timezone", BudgetStore.LocalIanaZone);
        string Date(double? seconds)
        {
            if (seconds is not double t) return "—";
            try { return TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds((long)(t * 1000)), BudgetStore.Zone(zone)).ToString("MM-dd HH:mm", CultureInfo.InvariantCulture); }
            catch (Exception e) when (e is ArgumentException or System.IO.InvalidDataException) { return "—"; }
        }
        string scope = kind == "quota" ? "账号级 · " + Format(rule.N("windowMinutes")) + " 分钟官方窗口" :
            (rule.S("model", "all") == "all" ? "全部模型" : rule.S("model")) + " · " + (rule.S("task", "all") == "all" ? "全部任务" : rule.S("task"));
        bool stale = row.S("dataStatus") != "updated" || budgets.S("error").Length > 0;
        if (budgets.S("error").Length > 0) message = budgets.S("error");
        bool known = status is not ("unknown" or "partial" or "source_invalid" or "scope_invalid" or "scheduled");
        double? fraction = known ? row.N("remainingFraction") ?? row.N("remainingPercent") / 100 : null;
        string used = (kind == "money" ? currency == "CNY" ? "¥" : "$" : "") + Format(row.N("used"), true) + (kind == "quota" ? "%" : "");
        if (row.S("coverage") == "partial" && row.N("used") is double amount && amount > 0) used = "≥" + used;
        return new(name, used, Format(known ? row.N("remaining") : null) + suffix,
            Format(kind == "quota" ? row.N("quotaFloor") ?? rule.N("amount") : row.N("amount")) + suffix,
            kind == "quota" ? "提醒下限" : "限额", caption, message, Date(row.N("start")) + " — " + Date(row.N("end")), scope, fraction, stale);
    }
}

internal static class CapsuleColors
{
    private static readonly double[] Stops = [.05, .10, .25, .50, .75, 1];
    private static readonly uint[] Dark = [0xEA6565, 0xEE786E, 0xF09858, 0xE9BC60, 0xA5D76D, 0x35DE94];
    private static readonly uint[] Light = [0xD4474F, 0xD76053, 0xC97432, 0xAF811B, 0x708F30, 0x009E68];
    public static Color Color(double value, bool light)
    {
        var colors = light ? Light : Dark; value = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
        int high = 1; while (high < Stops.Length - 1 && value > Stops[high]) high++;
        double t = Math.Clamp((value - Stops[high - 1]) / (Stops[high] - Stops[high - 1]), 0, 1);
        byte C(int shift) => (byte)Math.Round(((colors[high - 1] >> shift) & 255) * (1 - t) + ((colors[high] >> shift) & 255) * t);
        return System.Windows.Media.Color.FromRgb(C(16), C(8), C(0));
    }
}

internal static class CapsuleUsageDisplay
{
    internal static JsonObject Week(JsonObject state) => state.O("quota").A("windows").Rows().FirstOrDefault(x => x.I("duration_minutes") == 10080) ?? new();
    internal static double? Fraction(JsonObject state)
    {
        var quota = state.O("quota"); var week = Week(state);
        double? remaining = week.N("remaining") ?? (week.N("used_percent") is double used ? 100 - used : null);
        // Older cached snapshots may contain the already selected weekly value.
        // Never substitute a known short window for a missing weekly window.
        if (remaining is null && quota.A("windows").Count == 0 && quota.S("capsuleName") == "周余") return quota.N("capsuleFraction");
        return remaining / 100;
    }
    internal const string Name = "周剩余";
}

internal sealed class CapsuleSurface : FrameworkElement
{
    private readonly CapsuleWindow window;
    private readonly DrawingVisual drawing = new();
    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => index == 0 ? drawing : throw new ArgumentOutOfRangeException(nameof(index));
    public JsonObject State { get; private set; } = new();
    public double Expansion;
    internal Rect? CompactBounds;
    internal Rect? AnimationBounds;
    internal Rect? PanelBounds;
    internal Rect? DockClip;
    internal CapsuleEdge Edge;
    internal Size? HostSize;
    internal bool RingVisible => Edge == CapsuleEdge.None && Expansion <= .001;
    private Rect HostBounds => new(HostSize ?? new Size(ActualWidth, ActualHeight));
    internal Rect DrawingBounds => AnimationBounds ?? (CompactBounds is Rect compact && PanelBounds is Rect panel ? CapsuleMorph.Frame(compact, panel, Expansion).Shape : HostBounds);
    internal double CurrentRadius => AnimationBounds is null && CompactBounds is Rect compact && PanelBounds is Rect panel ? CapsuleMorph.Frame(compact, panel, Expansion).Radius : 38 - 16 * Expansion;
    internal bool CaptureTextBounds;
    internal List<(string Text, Rect Bounds)> VisibleTextBounds { get; } = new();
    private bool ContainsDrawing(Point point)
    {
        if (DockClip is Rect clip && !clip.Contains(point)) return false;
        var r = DrawingBounds; var local = new Point(point.X - r.X, point.Y - r.Y);
        return Edge != CapsuleEdge.None ? CapsuleEdgeIndicator.Shape(r.Size, Edge).FillContains(local) : CapsuleGeometry.Contains(local, r.Size, (38 - CurrentRadius) / 16);
    }
    private readonly record struct TextKey(string Text, double Width, double Size, Color Color, bool Bold, TextAlignment Alignment, double Dpi);
    private static readonly FontFamily Fonts = new("Segoe UI, Microsoft YaHei UI");
    private static readonly Typeface RegularFont = new(Fonts, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface BoldFont = new(Fonts, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private sealed class TextRun(FormattedText text)
    {
        public FormattedText Text { get; } = text;
        private Rect? ink;
        public Rect Ink => ink ??= Text.BuildGeometry(new()).Bounds;
    }
    private readonly Dictionary<TextKey, TextRun> textCache = new();
    private readonly Dictionary<(Color color, double opacity), SolidColorBrush> brushCache = new();
    private DrawingGroup? expandedDrawing;
    private readonly List<(string Text, Rect Bounds)> expandedTextBounds = new();
    private (bool retained, bool light, bool filters, string? hover, string? down, double dpi, bool diagnostics) expandedKey;
    internal int RenderCount { get; private set; }
    internal int TextShapeCount { get; private set; }
    internal double RenderMilliseconds { get; private set; }
    private static readonly SolidColorBrush[] DarkPalette = [Theme.Color("#17191B"), Theme.Color("#F5F7F6"), Theme.Color("#ADB6B2"), Theme.Color("#57E6B2"), Theme.Color("#343D38"), Theme.Color("#343A37")];
    private static readonly SolidColorBrush[] LightPalette = [Theme.Color("#F5F5F2"), Theme.Color("#202823"), Theme.Color("#626C67"), Theme.Color("#047857"), Theme.Color("#DCE3DD"), Theme.Color("#D6DDD7")];
    public bool Light => !Theme.Resolve(State.O("settings"), "floating");
    public bool BudgetMode => State.O("settings").O("floating").S("content", "usage") == "budget";
    public bool MonitorMode => State.O("settings").O("floating").S("content", "usage") == "monitor";
    internal bool BudgetQuota => BudgetMode || MonitorMode && State.O("settings").O("floating").S("quotaContent", "usage") == "budget";
    private string SelectedContentAction => MonitorMode ? "contentMonitor" : BudgetMode ? "contentBudget" : "contentUsage";
    internal static Rect MonitorTaskBounds(int index) => new(16, 140 + index * 83, 304, 77);
    private JsonObject Budget => State.O("budgets").A("summaries").Rows().FirstOrDefault(x => x.S("id") == State.O("settings").O("floating").S("budgetID")) ?? new();
    private JsonObject Summary => State.O("filtered").O("summary");
    private JsonObject Quota => State.O("quota");
    private CapsuleBudgetDisplay budgetDisplay = CapsuleBudgetDisplay.From(new());
    private double? Fraction => BudgetQuota ? budgetDisplay.Fraction : CapsuleUsageDisplay.Fraction(State);
    private string DisplayName => BudgetQuota ? budgetDisplay.Name : CapsuleUsageDisplay.Name;
    internal string TopValue => BudgetQuota ? BudgetBalance : J.Compact(State.O("today").O("summary").N("total_tokens"));
    private string Total => TopValue;
    private string BudgetBalance
    {
        get
        {
            if (budgetDisplay.Fraction is null || Budget.N("remaining") is not double remaining) return "—";
            bool money = Budget.S("kind") == "money";
            string currency = Budget.S("currency", "USD");
            string prefix = money ? currency switch { "CNY" => "¥", "USD" => "$", _ => currency + " " } : "";
            string amount = money && Math.Abs(remaining) < 1000 ? Math.Abs(remaining).ToString("N2", CultureInfo.InvariantCulture) : J.Compact(Math.Abs(remaining));
            return prefix + (remaining < 0 ? "−" : "") + amount + (Budget.S("kind") == "quota" ? "%" : "");
        }
    }
    internal bool FiltersOpen { get; private set; }
    internal bool BudgetPaused => Budget.B("paused");
    private string FilterSummary
    {
        get
        {
            var f = State.O("settings").O("floating");
            return (f.S("model", "all") == "all" ? "全部模型" : f.S("model")) + " · " +
                (f.S("task", "all") == "all" ? "全部任务" : State.O("filtered").O("filters").O("selected_task").S("label", f.S("task")));
        }
    }
    internal void ToggleFilters()
    {
        FiltersOpen = !FiltersOpen; expandedDrawing = null; NormalizeKeyboardAction();
        capsulePeer?.NotifyStateChanges(); Redraw();
    }
    internal void WindowStateChanged() { capsulePeer?.NotifyStateChanges(); Redraw(); }
    private string Range => BudgetMode ? budgetDisplay.Caption : State.O("settings").O("floating").S("days", "1") switch { "1" => "今日", "all" => "全部", var d => d + "天" };
    private bool Stale => BudgetQuota ? budgetDisplay.Stale : Quota.B("stale", true);
    private string? hover, down;
    private string? keyboardAction;
    private bool keyboardFocusVisible, settingFocus;
    private CapsuleEdge accessibleEdge;
    private bool accessibleExpanded;
    private CapsulePeer? capsulePeer;
    internal string? KeyboardAction => keyboardAction;
    internal bool KeyboardInteraction => keyboardFocusVisible && IsKeyboardFocusWithin;
    internal Rect KeyboardFocusBounds => Regions().FirstOrDefault(x => x.name == keyboardAction).bounds;
    private string? interactionError;
    private Point pressScreen, pressOrigin;
    private Rect? pressHotspot;
    private DpiScale pressDpi;
    private bool dragged;
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(double), typeof(CapsuleSurface), new FrameworkPropertyMetadata(-1d, (o, _) => ((CapsuleSurface)o).Redraw()));
    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    private double levelTarget = -1;
    public CapsuleSurface(CapsuleWindow owner)
    {
        window = owner; AddVisualChild(drawing); Focusable = true; Cursor = Cursors.Hand;
        InputMethod.SetIsInputMethodEnabled(this, false);
        // The capsule already is the hover detail view. A ToolTip on this single
        // drawing surface treats the whole HWND as its owner, including resize envelopes.
        ToolTipService.SetIsEnabled(this, false);
        AutomationProperties.SetName(this, "Token 用量胶囊");
        MouseMove += Move; MouseLeftButtonDown += Down; MouseLeftButtonUp += Up;
        Loaded += (_, _) => PrepareDetails();
        GotKeyboardFocus += (_, _) =>
        {
            if (!settingFocus) FocusAction(keyboardAction ?? "details", true);
        };
        LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            capsulePeer?.NotifyFocusChanged(null);
            Redraw();
            if (!IsKeyboardFocusWithin && !window.InteractionActive) window.EndKeyboardInteraction();
        });
        MouseRightButtonUp += async (_, e) => { if (!ContainsDrawing(e.GetPosition(this))) return; e.Handled = true; FocusAction(Hit(e.GetPosition(this)) ?? "details", false); await window.ShowMenu("context"); };
        MouseLeave += (_, _) => { if (PresentationSource.FromVisual(this) != null) window.TrackPointer(PointToScreen(Mouse.GetPosition(this))); ClearFeedback(); };
        LostMouseCapture += async (_, _) => { if (down is not null) { bool moving = dragged && down == "details"; down = null; if (moving) await window.FinishDrag(); else window.Press(false); ClearFeedback(); } };
        KeyDown += async (_, e) =>
        {
            Key key = InputKey(e.Key, e.SystemKey, e.ImeProcessedKey);
            if (!HandlesKeyboard(key, e.KeyboardDevice.Modifiers)) return;
            e.Handled = true;
            await HandleKeyboardAsync(key, e.KeyboardDevice.Modifiers);
        };
    }
    public void Update(JsonObject state)
    {
        expandedDrawing = null; interactionError = null;
        double previous = Level; State = state; budgetDisplay = CapsuleBudgetDisplay.From(state); double next = Fraction is double d && double.IsFinite(d) ? Math.Clamp(d, 0, 1) : -1;
        // A task-state refresh can arrive while the independent quota tween is
        // still running. Compare its destination, not the currently animated value.
        if (Math.Abs(levelTarget - next) > .000001)
        {
            levelTarget = next;
            BeginAnimation(LevelProperty, null); Level = next;
            if (previous >= 0 && next >= 0 && RingVisible && !window.DockMotionActive && IsVisible && SystemParameters.ClientAreaAnimation) BeginAnimation(LevelProperty, new DoubleAnimation(previous, next, TimeSpan.FromSeconds(.22)) { FillBehavior = FillBehavior.Stop, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } });
        }
        UpdateAccessibility(); NormalizeKeyboardAction(); capsulePeer?.NotifyStateChanges(); PrepareDetails(); Redraw();
    }
    private void UpdateAccessibility()
    {
        accessibleEdge = Edge;
        string help = BudgetQuota ? $"预算 {DisplayName}，剩余 {budgetDisplay.Remaining}，已用 {budgetDisplay.Used}，{budgetDisplay.Status}，{budgetDisplay.Scope}" :
            $"账号周剩余 {Percent()}；本地今日 {UsageNumbers.Exact(State.O("today").O("summary")["total_tokens"])} tokens；当前范围 {Range}，{FilterSummary}，{UsageNumbers.Exact(Summary["total_tokens"])} tokens；输入 {UsageNumbers.Exact(Summary["input_tokens"])}，输出 {UsageNumbers.Exact(Summary["output_tokens"])}";
        string name = "Token 用量胶囊";
        if (Edge != CapsuleEdge.None)
        {
            var display = CapsuleEdgeDisplay.From(State);
            string value = display.Fraction is null ? "未知" : display.Percent;
            name = $"额度侧签，{display.Name}，{(display.Used ? "已用" : "剩余")} {value}";
            help = name + (display.Stale ? "，上次记录，数据可能已过期。" : "，已更新。") + help;
        }
        else if (Stale) help += "；上次记录，数据可能已过期";
        if (TaskMonitorVisual.SummaryStatus(State).Length > 0)
            help += "；" + TaskMonitorVisual.FocusText(State) + "，" + TaskMonitorGlyph.Label(TaskMonitorVisual.SummaryStatus(State)) + "；" + TaskMonitorVisual.SummaryText(State);
        help += "；Enter 或空格执行当前项，Tab 切换操作，Shift+F10 打开菜单，Ctrl+Space 切换保持展开";
        AutomationProperties.SetName(this, name); AutomationProperties.SetHelpText(this, help);
    }
    internal IReadOnlyList<string> KeyboardOrder() => Regions().Where(x => x.name != "context" && ActionEnabled(x.name))
        .OrderBy(x => x.bounds.Top).ThenBy(x => x.bounds.Left).Select(x => x.name).ToArray();
    private void NormalizeKeyboardAction()
    {
        if (keyboardAction is not null && !Regions().Any(x => x.name == keyboardAction && ActionEnabled(x.name)))
            keyboardAction = KeyboardOrder().FirstOrDefault();
    }
    internal void FocusAction(string name, bool keyboard)
    {
        window.NewFocusIntent();
        keyboardFocusVisible = keyboard;
        if (keyboard) window.BeginKeyboardInteraction();
        keyboardAction = Regions().Any(x => x.name == name && ActionEnabled(x.name)) ? name : KeyboardOrder().FirstOrDefault();
        settingFocus = true;
        try { if (keyboard && !window.IsActive) window.Activate(); Focus(); }
        finally { settingFocus = false; }
        capsulePeer?.NotifyFocusChanged(keyboardAction); Redraw();
    }
    internal void EndKeyboardNavigation()
    {
        window.NewFocusIntent();
        keyboardFocusVisible = false; keyboardAction = "details"; capsulePeer?.NotifyFocusChanged(keyboardAction); Redraw();
    }
    internal static Key InputKey(Key key, Key systemKey, Key imeKey) => key == Key.System ? systemKey : key == Key.ImeProcessed ? imeKey : key;
    internal static bool HandlesKeyboard(Key key, ModifierKeys modifiers) =>
        key == Key.Tab && (modifiers & ~ModifierKeys.Shift) == ModifierKeys.None ||
        key == Key.Space && modifiers == ModifierKeys.Control ||
        key is Key.Enter or Key.Space && modifiers == ModifierKeys.None ||
        key == Key.Apps || key == Key.F10 && modifiers == ModifierKeys.Shift;
    internal async Task<bool> HandleKeyboardAsync(Key key, ModifierKeys modifiers)
    {
        window.NewFocusIntent();
        if (key == Key.Tab && (modifiers & ~ModifierKeys.Shift) == ModifierKeys.None)
        {
            window.BeginKeyboardInteraction();
            var order = KeyboardOrder(); if (order.Count == 0) return true;
            int current = Array.IndexOf(order.ToArray(), keyboardAction ?? "details");
            int next = (current + (modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1) + order.Count) % order.Count;
            FocusAction(order[next], true); return true;
        }
        if (key == Key.Space && modifiers == ModifierKeys.Control)
        {
            await window.Invoke("keepExpanded"); return true;
        }
        if ((key is Key.Enter or Key.Space) && modifiers == ModifierKeys.None)
        {
            await ActivateActionAsync(keyboardAction ?? "details", true); return true;
        }
        if (key == Key.Apps || key == Key.F10 && modifiers == ModifierKeys.Shift)
        {
            FocusAction(keyboardAction ?? "details", true); await window.ShowMenu("context"); return true;
        }
        return false;
    }
    internal async Task ActivateActionAsync(string name, bool keyboard)
    {
        if (!ActionEnabled(name) || !Regions().Any(x => x.name == name)) return;
        if (keyboard) FocusAction(name, true);
        await window.Invoke(name);
    }
    internal void ShowError(string message) { interactionError = message; expandedDrawing = null; Redraw(); }
    public void ClearFeedback() { hover = null; Redraw(); }
    internal void CancelInteraction() { down = null; hover = null; dragged = false; if (IsMouseCaptured) ReleaseMouseCapture(); Redraw(); }
    internal bool ActionEnabled(string name) => name != "budgetScope" && (name != "refresh" || !State.B("busy")) &&
        (name is not ("budgetPause" or "budgetResume" or "view-budget") || State.O("budgets").A("rules").Rows().Any(x => x.S("id") == State.O("settings").O("floating").S("budgetID")));
    internal string? Hit(Point local)
    {
        if (!ContainsDrawing(local)) return null;
        if (Edge != CapsuleEdge.None) return "details";
        // The old circle is a hover-retention bridge only. In upward layouts it
        // overlaps footer buttons and must never override their actual actions.
        return Regions().FirstOrDefault(x => x.name is not ("context" or "budgetScope") && x.bounds.Contains(local)).name;
    }
    protected override HitTestResult? HitTestCore(PointHitTestParameters parameters) => ContainsDrawing(parameters.HitPoint) ? new PointHitTestResult(this, parameters.HitPoint) : null;
    private void Down(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount > 1) return;
        if (window.InteractionActive) return;
        down = Hit(e.GetPosition(this)); if (down is null || !ActionEnabled(down)) { down = null; return; }
        FocusAction(down, false);
        e.Handled = true; pressScreen = PointToScreen(e.GetPosition(this)); pressDpi = VisualTreeHelper.GetDpi(this); pressHotspot = window.Hotspot; dragged = false;
        window.Press(true); pressOrigin = window.PixelBounds.TopLeft; CaptureMouse(); Redraw();
    }
    private void Move(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(this); var screen = PointToScreen(p);
        double dx = screen.X - pressScreen.X, dy = screen.Y - pressScreen.Y;
        if (down is not null)
        {
            bool wasDragged = dragged;
            dragged |= CapsuleGeometry.Dragged(pressScreen, screen, pressDpi);
            if (dragged && !wasDragged && down == "details") { pressOrigin = window.BeginDrag(pressScreen); pressHotspot = null; }
            if (dragged && down == "details") window.MoveBy(dx, dy, pressOrigin, pressHotspot);
        }
        else { window.TrackPointer(screen); window.PointerMoved(screen); }
        string? next = Expansion > .99 && !dragged && !window.InteractionActive ? Hit(p) : down is not null && !dragged ? Hit(p) : null;
        if (next == "details" || next is not null && !ActionEnabled(next)) next = null;
        if (next != hover) { hover = next; Redraw(); }
    }
    private async void Up(object sender, MouseButtonEventArgs e)
    {
        string? selected = down; if (selected is null) return;
        Point local = e.GetPosition(this); bool moved = dragged || CapsuleGeometry.Dragged(pressScreen, PointToScreen(local), pressDpi);
        string? target = !moved && selected == Hit(local) && ActionEnabled(selected) ? selected : null;
        down = null; ReleaseMouseCapture(); ClearFeedback();
        if (moved && selected == "details") { if (!dragged) window.BeginDrag(pressScreen); await window.FinishDrag(); return; }
        window.Press(false);
        if (moved) return;
        if (target != null) await ActivateActionAsync(target, false);
    }
    public List<(string name, string label, Rect bounds)> Regions()
    {
        if (Edge != CapsuleEdge.None) return [("details", "打开主面板", DrawingBounds), ("context", "浮窗功能菜单，包含保持展开", DrawingBounds)];
        if (Expansion < .999)
        {
            // Fading controls must never fall through to the header's open-main
            // action. During the morph only the circle / percent / title is a grip.
            var grip = DrawingBounds;
            if (Expansion > .12 && CompactBounds is Rect compact && PanelBounds is Rect panel)
            {
                var frame = CapsuleMorph.Frame(compact, panel, Expansion);
                grip = frame.DetailsAlpha > 0 ? new Rect(panel.X, panel.Y, panel.Width, 52) : frame.PrimaryBounds;
                if (!grip.IsEmpty && frame.DetailsAlpha == 0) grip.Inflate(6, 6);
                grip.Intersect(DrawingBounds);
            }
            return [("details", "打开主面板", grip), ("context", "浮窗功能菜单，包含保持展开", DrawingBounds)];
        }
        var offset = (PanelBounds ?? DrawingBounds).TopLeft;
        return LocalRegions().Select(x => { var r = x.bounds; r.Offset(offset.X, offset.Y); r.Intersect(DrawingBounds); return (x.name, x.label, r); }).Where(x => !x.r.IsEmpty).Select(x => (x.name, x.label, x.r)).ToList();
    }
    private List<(string name, string label, Rect bounds)> LocalRegions(bool includeExpanded = false)
    {
        double width = DrawingBounds.Width > 0 ? DrawingBounds.Width : 76;
        var header = new Rect(0, 0, width, includeExpanded || Expansion > .99 ? 52 : 76);
        var r = new List<(string, string, Rect)> { ("details", "打开主面板", header) };
        if (!includeExpanded && Expansion < .999) { r.Add(("context", "浮窗功能菜单", header)); return r; }
        r.AddRange(new (string, string, Rect)[] {
            ("contentUsage","用量",new(16,62,40,26)),("contentBudget","预算",new(56,62,40,26)),
            ("contentMonitor","任务监控，" + TaskMonitorVisual.SummaryText(State),new(96,62,62,26)),
            ("keepExpanded","保持展开",new(166,62,90,26)),("more","更多浮窗操作",new(266,62,54,26)),
            ("main","打开主面板",new(16,375,150,24)),("collapse","收起为圆环",new(246,375,74,24))});
        if (MonitorMode)
        {
            var tasks = TaskMonitorVisual.Featured(State);
            for (int index = 0; index < tasks.Count; index++)
            {
                var task = tasks[index];
                r.Add(("monitorDetail:" + task.S("id"), "查看任务，" + task.S("title") + "，" + TaskMonitorGlyph.Label(task.S("status")) + "，" + TaskMonitorVisual.Metadata(task), new(263, MonitorTaskBounds(index).Top + 12, 49, 28)));
            }
            r.Add(("monitorManage", tasks.Count == 0 ? "选择正在执行的任务" : "查看全部任务与消息", new(16, 306, 304, 28)));
            r.Add(("monitorSettings", "任务提醒设置，" + TaskMonitorVisual.SourceText(State), new(16, 337, 204, 29)));
            r.Add(("monitorCheck", "检查监控连接", new(230, 339, 90, 27)));
        }
        else
        {
            r.Add(("updateStatus", "数据更新状态与重试", new(16, 334, 232, 32)));
            r.Add(("refresh", "刷新本地用量与账号额度", new(258, 340, 62, 26)));
        }
        if (BudgetMode)
        {
            r.Add(("budget", "选择预算，当前 " + budgetDisplay.Name, new(16, 100, 188, 28)));
            r.Add(("view-budget", "在主面板查看当前预算", new(216, 100, 104, 28)));
            r.Add(("budgetScope", "预算范围，只读，" + budgetDisplay.Scope, new(16, 169, 304, 22)));
            r.Add((BudgetPaused ? "budgetResume" : "budgetPause", BudgetPaused ? "恢复预算提醒" : "暂停预算提醒", new(214, 290, 106, 26)));
        }
        else if (!MonitorMode)
        {
            r.Add(("period", "本地统计范围，" + Range, new(122, 142, 114, 26)));
            r.Add(("filters", "模型与任务筛选，" + FilterSummary, new(246, 142, 74, 26)));
            if (FiltersOpen)
            {
                r.Add(("model", "浮窗模型筛选", new(16, 176, 147, 24)));
                r.Add(("task", "浮窗任务筛选", new(173, 176, 147, 24)));
                r.Add(("filtersReset", "清除模型和任务条件", new(214, 201, 106, 19)));
            }
        }
        r.Add(("context", "浮窗功能菜单", header));
        return r;
    }
    private string Percent() => Fraction is double f && double.IsFinite(f) ? Math.Floor(Math.Clamp(f, 0, 1) * 100 + 1e-9).ToString(CultureInfo.InvariantCulture) + "%" + (Stale ? "*" : "") : "—" + (Stale ? "*" : "");
    protected override void OnRender(DrawingContext dc) => Redraw();
    internal void Redraw()
    {
        bool edgeChanged = accessibleEdge != Edge;
        if (edgeChanged) UpdateAccessibility();
        bool full = Expansion >= .999 && Edge == CapsuleEdge.None;
        if (edgeChanged || accessibleExpanded != full)
        {
            accessibleExpanded = full; NormalizeKeyboardAction();
            capsulePeer?.NotifyStateChanges(); capsulePeer?.NotifyFocusChanged(keyboardAction);
        }
        using var dc = drawing.RenderOpen(); Draw(dc);
        if (KeyboardInteraction && keyboardAction is not null)
        {
            var focus = KeyboardFocusBounds;
            if (!focus.IsEmpty && focus.Width > 4 && focus.Height > 4)
            {
                dc.PushClip(Edge != CapsuleEdge.None ? new RectangleGeometry(DrawingBounds) : new RectangleGeometry(DrawingBounds, CurrentRadius, CurrentRadius));
                focus.Inflate(-2, -2);
                double corner = keyboardAction == "details" && RingVisible ? Math.Min(focus.Width, focus.Height) / 2 : 5;
                dc.DrawRoundedRectangle(null, new Pen(Light ? Brushes.Black : Brushes.White, 1.5), focus, corner, corner);
                dc.Pop();
            }
        }
    }
    private void PrepareDetails()
    {
        if (!IsVisible || Expansion != 0 || Edge != CapsuleEdge.None || window.DockMotionActive || expandedDrawing is not null || PanelBounds is null) return;
        // Warm text and the static detail drawing when a snapshot arrives / the
        // surface loads, so the first hover does not do font layout on its last frame.
        double saved = Expansion;
        try { Expansion = 1; var cache = new DrawingGroup(); using var dc = cache.Open(); Draw(dc); }
        finally { Expansion = saved; VisibleTextBounds.Clear(); }
    }
    private void Draw(DrawingContext dc)
    {
        long renderStart = Stopwatch.GetTimestamp(); RenderCount++;
        VisibleTextBounds.Clear();
        if (Edge != CapsuleEdge.None)
        {
            dc.PushTransform(new TranslateTransform(DrawingBounds.X, DrawingBounds.Y));
            CapsuleEdgeIndicator.Draw(dc, DrawingBounds.Size, Edge, State, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.Pop();
            RenderMilliseconds += Stopwatch.GetElapsedTime(renderStart).TotalMilliseconds; return;
        }
        bool light = Light; var palette = light ? LightPalette : DarkPalette; var bg = palette[0]; var ink = palette[1]; var secondary = palette[2]; var accent = palette[3];
        var track = palette[4]; var border = palette[5];
        var bounds = DrawingBounds; double w = bounds.Width, h = bounds.Height, radius = CurrentRadius;
        if (w < 1 || h < 1) return;
        if (DockClip is Rect workClip) dc.PushClip(new RectangleGeometry(workClip));
        var shape = new RectangleGeometry(new Rect(bounds.X + .5, bounds.Y + .5, w - 1, h - 1), radius, radius); dc.PushClip(shape); dc.DrawGeometry(bg, null, shape);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Point textOffset = new(); bool cachingContent = false;
        TextRun Layout(string value, double width, double size, SolidColorBrush color, bool bold = false, TextAlignment alignment = TextAlignment.Left)
        {
            var key = new TextKey(value, width, size, color.Color, bold, alignment, pixelsPerDip);
            if (!textCache.TryGetValue(key, out var run))
            {
                if (textCache.Count >= 192) textCache.Clear();
                var text = new FormattedText(value, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, bold ? BoldFont : RegularFont, size, color, pixelsPerDip) { MaxTextWidth = width, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis, TextAlignment = alignment };
                run = new(text);
                textCache[key] = run; TextShapeCount++;
            }
            return run;
        }
        void Text(string value, double x, double y, double width, double size, SolidColorBrush color, bool bold = false, TextAlignment alignment = TextAlignment.Left)
        {
            if (width < 1) return;
            var run = Layout(value, width, size, color, bold, alignment);
            dc.DrawText(run.Text, new Point(x, y));
            if (CaptureTextBounds && !run.Ink.IsEmpty)
            {
                var box = run.Ink; box.Offset(x + textOffset.X, y + textOffset.Y);
                if (cachingContent) expandedTextBounds.Add((value, box));
                else VisibleTextBounds.Add((value, box));
            }
        }
        void TextInRect(string value, Rect area, double size, SolidColorBrush color, TextAlignment alignment = TextAlignment.Center, double padding = 0)
        {
            double width = area.Width - 2 * padding; if (width < 1) return;
            var run = Layout(value, width, size, color); var glyphs = run.Ink; if (glyphs.IsEmpty) return;
            double x = alignment switch
            {
                TextAlignment.Left => area.Left + padding - glyphs.Left,
                TextAlignment.Right => area.Right - padding - glyphs.Right,
                _ => area.Left + (area.Width - glyphs.Width) / 2 - glyphs.Left
            };
            // Font line boxes include unequal ascender/descender space. Center
            // the visible glyphs, including fallback symbols, inside the action.
            double y = area.Top + (area.Height - glyphs.Height) / 2 - glyphs.Top;
            Text(value, x, y, width, size, color);
        }
        SolidColorBrush Translucent(SolidColorBrush color, double alpha)
        {
            var key = (color.Color, alpha); if (brushCache.TryGetValue(key, out var cached)) return cached;
            var fill = new SolidColorBrush(color.Color) { Opacity = alpha }; fill.Freeze(); brushCache[key] = fill; return fill;
        }
        void Box(double x, double y, double width, double height, double alpha = .05, double corner = 8) { dc.DrawRoundedRectangle(Translucent(ink, alpha), null, new(x, y, width, height), corner, corner); }
        var compact = AnimationBounds is not null ? bounds : CompactBounds ?? new Rect(bounds.TopLeft, new Size(76, 76));
        var panel = PanelBounds ?? bounds;
        var morph = CapsuleMorph.Frame(compact, panel, Expansion);
        if (morph.ArcAlpha > 0)
        {
            dc.PushOpacity(morph.ArcAlpha);
            dc.PushTransform(new TranslateTransform(compact.X, compact.Y)); textOffset = compact.TopLeft;
            w = compact.Width;
            dc.DrawEllipse(null, new Pen(track, 5), new Point(w / 2, 38), 33.5, 33.5);
            if (Level > 0)
            {
                var pen = new Pen(new SolidColorBrush(CapsuleColors.Color(Level, light)), 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                if (Level >= .999999) dc.DrawEllipse(null, pen, new(w / 2, 38), 33.5, 33.5);
                else { var arc = new StreamGeometry(); using (var c = arc.Open()) { c.BeginFigure(new(w / 2, 4.5), false, false); double angle = Level * 2 * Math.PI - Math.PI / 2; c.ArcTo(new(w / 2 + 33.5 * Math.Cos(angle), 38 + 33.5 * Math.Sin(angle)), new(33.5, 33.5), 0, Level > .5, SweepDirection.Clockwise, true, false); } dc.DrawGeometry(null, pen, arc); }
            }
            Text(DisplayName, w / 2 - 23, 10, 46, 8.5, secondary, alignment: TextAlignment.Center);
            Text(Total, w / 2 - 25, 43, 50, 10, ink, alignment: TextAlignment.Center);
            TextInRect(BudgetQuota ? "余量" : "今日", new(w / 2 - 15, 56, 19, 11), 8.5, secondary);
            TaskMonitorGlyph.Draw(dc, new(w / 2 + 6, 56.5, 10, 10), TaskMonitorVisual.SummaryStatus(State), light);
            dc.Pop(); dc.Pop();
        }
        if (morph.DetailsAlpha > 0)
        {
            dc.PushOpacity(morph.DetailsAlpha);
            dc.PushTransform(new TranslateTransform(panel.X, panel.Y)); textOffset = panel.TopLeft;
            w = panel.Width;
            dc.PushClip(new RectangleGeometry(new(2.5, 2.5, w - 5, 47), 23.5, 23.5));
            dc.DrawRectangle(Translucent(ink, .045), null, new(2.5, 2.5, w - 5, 47));
            if (Level > 0) { var liquid = new LinearGradientBrush(light ? System.Windows.Media.Color.FromRgb(209, 240, 222) : System.Windows.Media.Color.FromRgb(14, 97, 66), light ? System.Windows.Media.Color.FromRgb(189, 224, 204) : System.Windows.Media.Color.FromRgb(5, 51, 38), 90); dc.DrawRectangle(liquid, null, new(2.5, 2.5, (w - 5) * Level, 47)); }
            dc.Pop();
            Text((BudgetQuota ? (MonitorMode ? budgetDisplay.Name : "预算剩余") : DisplayName) + (Stale ? " · 上次" : ""), 85, 21, 90, 10, secondary);
            Text(BudgetQuota ? "剩余" : "今日", 181, 21, 34, 10, secondary); Text(Total, 222, 14, 94, 18, ink, alignment: TextAlignment.Right);
            var key = (window.KeepsExpanded, light, FiltersOpen, hover, down, pixelsPerDip, CaptureTextBounds);
            if (expandedDrawing == null || expandedKey != key)
            {
                expandedKey = key; expandedDrawing = new DrawingGroup();
                var outer = dc;
                expandedTextBounds.Clear(); cachingContent = true; textOffset = new();
                using (var content = expandedDrawing.Open())
                {
                    dc = content;
                    DrawExpandedContent();
                }
                dc = outer; cachingContent = false; expandedDrawing.Freeze();
            }
            dc.DrawDrawing(expandedDrawing);
            if (CaptureTextBounds) foreach (var item in expandedTextBounds) { var r = item.Bounds; r.Offset(panel.X, panel.Y); VisibleTextBounds.Add((item.Text, r)); }
            dc.Pop(); dc.Pop();
        }
        // One percent glyph run lives for the entire morph. Shape the font once,
        // scale it gently, and constrain the complete ink rectangle to the current
        // rounded contour rather than using clipping to reveal pieces of a number.
        var primary = Layout(Percent(), 200, 18, ink);
        if (!primary.Ink.IsEmpty)
        {
            double scale = morph.PrimaryFontSize / 18;
            var wanted = new Rect(morph.PrimaryCenter.X - primary.Ink.Width * scale / 2, morph.PrimaryCenter.Y - primary.Ink.Height * scale / 2, primary.Ink.Width * scale, primary.Ink.Height * scale);
            var safe = CapsuleMorph.SafeText(wanted, bounds, radius);
            if (!safe.IsEmpty)
            {
                dc.PushTransform(new TranslateTransform(safe.X - primary.Ink.X * scale, safe.Y - primary.Ink.Y * scale));
                dc.PushTransform(new ScaleTransform(scale, scale)); dc.DrawText(primary.Text, new()); dc.Pop(); dc.Pop();
                if (CaptureTextBounds) VisibleTextBounds.Add((Percent(), safe));
            }
        }
        dc.DrawGeometry(null, new Pen(border, 1), shape);
        dc.Pop();
        if (DockClip is not null) dc.Pop();
        RenderMilliseconds += Stopwatch.GetElapsedTime(renderStart).TotalMilliseconds;

        void DrawExpandedContent()
        {
            var actions = LocalRegions(includeExpanded: true);
            Rect ActionRect(string name) => actions.First(x => x.name == name).bounds;
            void ActionText(string name, string value, double size, SolidColorBrush color, TextAlignment alignment = TextAlignment.Center, double padding = 8) =>
                TextInRect(value, ActionRect(name), size, color, alignment, padding);
            void ActionBox(string name, double opacity) { var r = ActionRect(name); Box(r.X, r.Y, r.Width, r.Height, opacity); }
            foreach (var r in actions.Where(x => x.name is not ("details" or "context"))) if (hover == r.name && ActionEnabled(r.name))
            {
                Rect glow = r.bounds;
                for (int i = 10; i >= 1; i--) { var c = Translucent(accent, .012 + .003 * (10 - i)); var outer = glow; outer.Inflate(i, i); dc.DrawRoundedRectangle(null, new Pen(c, 2), outer, 8 + i, 8 + i); }
                if (down == r.name) Box(glow.X, glow.Y, glow.Width, glow.Height, .12);
            }
            var navigation = Rect.Union(ActionRect("contentUsage"), ActionRect("contentMonitor"));
            var selectedNavigation = ActionRect(SelectedContentAction); selectedNavigation.Inflate(-1, -1);
            Box(navigation.X, navigation.Y, navigation.Width, navigation.Height, .06, 8); Box(selectedNavigation.X, selectedNavigation.Y, selectedNavigation.Width, selectedNavigation.Height, .12, 7);
            ActionText("contentUsage", "用量", 11, BudgetMode || MonitorMode ? secondary : accent, padding: 4);
            ActionText("contentBudget", "预算", 11, BudgetMode ? accent : secondary);
            int unread = State.O("monitor").O("summary").I("unread");
            ActionText("contentMonitor", "监控" + (unread > 0 ? " " + (unread > 9 ? "9+" : unread.ToString(CultureInfo.InvariantCulture)) : ""), 10, MonitorMode ? accent : secondary, padding: 3);
            ActionText("keepExpanded", (window.KeepsExpanded ? "✓ " : "") + "保持展开", 10, window.KeepsExpanded ? accent : ink);
            ActionText("more", "更多 ···", 10, ink, padding: 0);
            if (MonitorMode)
            {
                Text(TaskMonitorVisual.SummaryText(State), 20, 103, 296, 10, ink);
                Text(TaskMonitorVisual.FocusText(State), 20, 120, 296, 9, secondary);
                var tasks = TaskMonitorVisual.Featured(State);
                if (tasks.Count == 0)
                {
                    TaskMonitorGlyph.Draw(dc, new(157, 169, 22, 22), "idle", light);
                    TextInRect("选择正在执行的任务", new(28, 206, 280, 21), 13, ink);
                    TextInRect("本轮结束后提醒你", new(28, 232, 280, 18), 10, secondary);
                    TextInRect("监控与用量筛选互相独立", new(28, 260, 280, 17), 9, secondary);
                }
                for (int index = 0; index < tasks.Count; index++)
                {
                    var task = tasks[index]; var card = MonitorTaskBounds(index); double top = card.Top;
                    Box(card.X, card.Y, card.Width, card.Height, .045);
                    TaskMonitorGlyph.Draw(dc, new(26, top + 10, 11, 11), task.S("status"), light);
                    Text(TaskMonitorGlyph.Label(task.S("status")), 43, top + 8, 207, 10, TaskMonitorGlyph.Brush(task.S("status"), light));
                    var title = new FormattedText(task.S("title", "未命名任务"), CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                        RegularFont, 11, ink, pixelsPerDip) { MaxTextWidth = 227, MaxLineCount = 2, LineHeight = 14, Trimming = TextTrimming.CharacterEllipsis };
                    dc.DrawText(title, new(26, top + 25));
                    if (CaptureTextBounds)
                    {
                        var box = title.BuildGeometry(new()).Bounds;
                        if (!box.IsEmpty) { box.Offset(26, top + 25); expandedTextBounds.Add((task.S("title", "未命名任务"), box)); }
                    }
                    Text(TaskMonitorVisual.Metadata(task), 26, top + 58, 276, 9, secondary);
                    ActionText("monitorDetail:" + task.S("id"), "查看", 10, accent, padding: 4);
                }
                ActionBox("monitorManage", .055); ActionText("monitorManage", tasks.Count == 0 ? "选择任务" : "查看全部任务与消息", 10, accent);
                var monitorSource = ActionRect("monitorSettings");
                TextInRect(interactionError ?? TaskMonitorVisual.SourceText(State), new(monitorSource.X, monitorSource.Y, monitorSource.Width, 15), 9, secondary, TextAlignment.Left, 4);
                TextInRect("提醒设置", new(monitorSource.X, monitorSource.Y + 15, monitorSource.Width, 14), 9, accent, TextAlignment.Left, 4);
                ActionBox("monitorCheck", .06); ActionText("monitorCheck", "检查连接", 10, accent, padding: 3);
            }
            else if (BudgetMode)
            {
                ActionBox("budget", .055); var selector = ActionRect("budget");
                TextInRect(budgetDisplay.Name, new Rect(selector.X + 10, selector.Y, selector.Width - 37, selector.Height), 11, accent, TextAlignment.Left);
                TextInRect("⌄", new Rect(selector.Right - 21, selector.Y, 14, selector.Height), 12, secondary);
                ActionText("view-budget", "查看预算 ↗", 10, ink, padding: 2);
                Text(budgetDisplay.Period, 20, 143, 296, 11, ink);
                ActionText("budgetScope", budgetDisplay.Scope, 10, secondary, TextAlignment.Left, 4);
                Text(budgetDisplay.Caption, 20, 207, 140, 10, secondary); Text(budgetDisplay.AmountLabel, 180, 207, 140, 10, secondary);
                Text(budgetDisplay.Used, 20, 225, 140, 23, ink); Text(budgetDisplay.Amount, 180, 230, 140, 15, ink);
                Text("预算剩余 " + budgetDisplay.Remaining, 20, 261, 296, 10, accent);
                string reminder = BudgetPaused ? "提醒已暂停 · 统计继续" : Budget.S("status") switch
                {
                    "disabled" => "提醒已停用 · 统计继续", "ended" => "本周期已结束", "scheduled" => "等待预算开始",
                    "unknown" or "partial" or "source_invalid" or "scope_invalid" => "等待可用预算数据", _ => "监测中 · 统计继续"
                };
                Text(reminder, 20, 295, 188, 10, secondary);
                string reminderAction = BudgetPaused ? "budgetResume" : "budgetPause";
                ActionBox(reminderAction, .065); ActionText(reminderAction, BudgetPaused ? "恢复提醒" : "暂停提醒 ⌄", 10, accent, padding: 5);
                Text(interactionError ?? budgetDisplay.Status, 20, 319, 296, 9, secondary);
            }
            else
            {
                var f = State.O("settings").O("floating"); var week = CapsuleUsageDisplay.Week(State);
                Text(week.N("resets_at") is double reset ? "周额度 · " + J.Date(reset, "MM-dd HH:mm") + " 重置" : "周额度重置时间未知", 20, 102, 296, 10, secondary);
                var shortWindow = Quota.A("windows").Rows().Where(x => x.I("duration_minutes") != 10080).OrderBy(x => x.I("duration_minutes")).FirstOrDefault();
                double? shortRemaining = shortWindow.N("remaining") ?? (shortWindow.N("used_percent") is double used ? 100 - used : null);
                string shortQuota = shortWindow is null ? "短周期额度 —" : shortWindow.S("label", "短周期") + " 剩余 " + (shortRemaining is double shortValue && double.IsFinite(shortValue) ? Math.Floor(Math.Clamp(shortValue, 0, 100)).ToString("0", CultureInfo.InvariantCulture) + "%" : "—") +
                    (shortWindow.N("resets_at") is double shortReset ? " · " + J.Date(shortReset, "HH:mm") + " 重置" : "");
                Text(shortQuota + " · " + Quota.S("resetLabel", "重置卡 —"), 20, 119, 296, 10, secondary);
                Text("本地用量", 20, 149, 96, 11, ink);
                ActionBox("period", .055); ActionText("period", Range + " ⌄", 10, accent, padding: 10);
                ActionText("filters", (FiltersOpen ? "收拢" : "筛选") + (f.S("model", "all") != "all" || f.S("task", "all") != "all" ? " ·" : " ⌄"), 10, accent, padding: 0);
                if (FiltersOpen)
                {
                    ActionBox("model", .055); ActionBox("task", .055);
                    ActionText("model", f.S("model", "all") == "all" ? "全部模型 ⌄" : f.S("model") + " ⌄", 10, accent, TextAlignment.Left);
                    ActionText("task", f.S("task", "all") == "all" ? "全部任务 ⌄" : State.O("filtered").O("filters").O("selected_task").S("label", f.S("task")) + " ⌄", 10, accent, TextAlignment.Left);
                    ActionText("filtersReset", "清除条件", 9, secondary, padding: 6);
                    Text("筛选仅影响下方本地用量", 20, 204, 188, 9, secondary);
                }
                else Text(FilterSummary, 20, 181, 296, 10, secondary);
                Text(J.Compact(Summary.N("total_tokens")), 20, 221, 296, 28, ink);
                Text(UsageNumbers.Exact(Summary["total_tokens"]) + " tokens", 20, 256, 296, 9, secondary);
                Text("输入（含缓存）", 20, 277, 142, 9, secondary); Text("输出（含推理）", 180, 277, 140, 9, secondary);
                Text(J.Compact(Summary.N("input_tokens")), 20, 290, 142, 17, ink); Text(J.Compact(Summary.N("output_tokens")), 180, 290, 140, 17, ink);
                Text(interactionError ?? "其中缓存 " + J.Compact(Summary.N("cached_input_tokens")), 20, 317, 296, 10, secondary);
            }
            if (!MonitorMode)
            {
                var statusArea = ActionRect("updateStatus"); double rowHeight = statusArea.Height / 2;
                TextInRect(SourceStamp("local"), new Rect(statusArea.X, statusArea.Y, statusArea.Width, rowHeight), 9, secondary, TextAlignment.Left, 4);
                TextInRect(SourceStamp("quota"), new Rect(statusArea.X, statusArea.Y + rowHeight, statusArea.Width, rowHeight), 9, secondary, TextAlignment.Left, 4);
                ActionBox("refresh", .06); ActionText("refresh", State.B("busy") ? "更新中" : "刷新", 10, State.B("busy") ? secondary : accent, padding: 3);
            }
            dc.DrawLine(new Pen(border, 1), new(16, 371), new(320, 371));
            ActionBox("main", .08); ActionText("main", "打开主面板", 11, accent, padding: 12);
            ActionText("collapse", "收起", 11, secondary, padding: 0);
        }
    }
    private string SourceStamp(string source)
    {
        var update = State.O("updates").O(source); bool local = source == "local";
        double? stamp = update.N("updatedAt") ?? (local ? DateTimeOffset.TryParse(State.O("today").O("meta").S("generated_at"), out var generated) ? generated.ToUnixTimeMilliseconds() / 1000d : null : Quota.N("updated_at"));
        string error = update.S("error", local ? "" : Quota.S("error"));
        string status = update.B("busy") ? "更新中" : error.Length > 0 ? "失败 · 上次 " : "";
        return (local ? "本地 " : "账号 ") + status + (stamp is double seconds ? J.Date(seconds, "HH:mm:ss") : "待更新") + (local && State.O("settings").I("refresh", 5) == 0 ? " · 自动更新暂停" : "");
    }
    protected override AutomationPeer OnCreateAutomationPeer() => capsulePeer ??= new CapsulePeer(this);
    private sealed class CapsulePeer(CapsuleSurface owner) : FrameworkElementAutomationPeer(owner), ISelectionProvider
    {
        private readonly Dictionary<string, ActionPeer> actions = new();
        internal ActionPeer Action(string name)
        {
            if (!actions.TryGetValue(name, out var peer)) actions[name] = peer = new ActionPeer(owner, name);
            return peer;
        }
        protected override string GetClassNameCore() => "CodexUsageCapsule";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override List<AutomationPeer> GetChildrenCore() => owner.Regions().Select(x => (AutomationPeer)Action(x.name)).ToList();
        public override object? GetPattern(PatternInterface pattern) => pattern == PatternInterface.Selection ? this : base.GetPattern(pattern);
        public bool CanSelectMultiple => false;
        public bool IsSelectionRequired => owner.Expansion >= .999 && owner.Edge == CapsuleEdge.None;
        public IRawElementProviderSimple[] GetSelection() => IsSelectionRequired ? [ProviderFromPeer(Action(owner.SelectedContentAction))] : [];
        internal void NotifyStateChanges()
        {
            // A drawn surface has no WPF child elements to invalidate this cache
            // when morph endpoints or usage/budget mode change its virtual controls.
            ResetChildrenCache();
            foreach (var action in actions.Values.ToArray()) action.NotifyStateChanges();
            InvalidatePeer();
        }
        internal void NotifyFocusChanged(string? name)
        {
            foreach (var action in actions.Values.ToArray()) action.NotifyFocusChanged();
            if (name is not null && owner.IsKeyboardFocusWithin) Action(name).RaiseAutomationEvent(AutomationEvents.AutomationFocusChanged);
        }
    }
    private sealed class ActionPeer : AutomationPeer, IInvokeProvider, IToggleProvider, ISelectionItemProvider, IExpandCollapseProvider
    {
        private readonly CapsuleSurface owner;
        private readonly string name;
        private bool selected, toggled, focused, filtersExpanded;
        internal ActionPeer(CapsuleSurface owner, string name)
        {
            this.owner = owner; this.name = name;
            selected = IsSelected; toggled = owner.window.KeepsExpanded; filtersExpanded = owner.FiltersOpen;
        }
        private (string name, string label, Rect bounds)? Region => owner.Regions().Where(x => x.name == name).Select(x => ((string name, string label, Rect bounds)?)x).FirstOrDefault();
        private bool IsPage => name is "contentUsage" or "contentBudget" or "contentMonitor";
        public void Invoke()
        {
            if (!IsEnabledCore()) throw new ElementNotEnabledException();
            owner.Dispatcher.BeginInvoke(async () => await owner.ActivateActionAsync(name, true));
        }
        public void Toggle() => Invoke();
        public ToggleState ToggleState => owner.window.KeepsExpanded ? ToggleState.On : ToggleState.Off;
        public ExpandCollapseState ExpandCollapseState => owner.FiltersOpen ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
        public void Expand() { if (!owner.FiltersOpen) Invoke(); }
        public void Collapse() { if (owner.FiltersOpen) Invoke(); }
        public bool IsSelected => IsPage && name == owner.SelectedContentAction;
        public IRawElementProviderSimple SelectionContainer => ProviderFromPeer(owner.capsulePeer!);
        public void Select() { if (!IsSelected) Invoke(); }
        public void AddToSelection() => Select();
        public void RemoveFromSelection() { if (IsSelected) throw new InvalidOperationException("必须保留一个浮窗内容页。"); }
        public override object? GetPattern(PatternInterface pattern) => pattern switch
        {
            PatternInterface.Invoke => this,
            PatternInterface.Toggle when name == "keepExpanded" => this,
            PatternInterface.SelectionItem when IsPage => this,
            PatternInterface.ExpandCollapse when name == "filters" => this,
            _ => null
        };
        internal void NotifyStateChanges()
        {
            if (IsPage && selected != IsSelected)
            {
                RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, selected, IsSelected);
                selected = IsSelected;
                if (selected) RaiseAutomationEvent(AutomationEvents.SelectionItemPatternOnElementSelected);
            }
            if (name == "keepExpanded" && toggled != owner.window.KeepsExpanded)
            {
                RaisePropertyChangedEvent(TogglePatternIdentifiers.ToggleStateProperty,
                    toggled ? ToggleState.On : ToggleState.Off, ToggleState);
                toggled = owner.window.KeepsExpanded;
            }
            if (name == "filters" && filtersExpanded != owner.FiltersOpen)
            {
                RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty,
                    filtersExpanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed, ExpandCollapseState);
                filtersExpanded = owner.FiltersOpen;
            }
            InvalidatePeer();
        }
        internal void NotifyFocusChanged()
        {
            bool current = HasKeyboardFocusCore();
            if (current != focused) RaisePropertyChangedEvent(AutomationElementIdentifiers.HasKeyboardFocusProperty, focused, current);
            focused = current;
        }
        protected override string GetNameCore() => name == "details" && owner.Edge != CapsuleEdge.None ? "打开主面板，" + AutomationProperties.GetName(owner) : Region?.label ?? name;
        protected override string GetAutomationIdCore() => name;
        protected override string GetClassNameCore() => "CapsuleAction";
        protected override AutomationControlType GetAutomationControlTypeCore() => IsPage ? AutomationControlType.RadioButton : name == "keepExpanded" ? AutomationControlType.CheckBox : AutomationControlType.Button;
        protected override Rect GetBoundingRectangleCore()
        {
            if (Region is not { } item || !owner.IsVisible) return Rect.Empty;
            var dpi = VisualTreeHelper.GetDpi(owner); return new(owner.PointToScreen(item.bounds.TopLeft), new Size(item.bounds.Width * dpi.DpiScaleX, item.bounds.Height * dpi.DpiScaleY));
        }
        protected override Point GetClickablePointCore() => Region is { } item ? owner.PointToScreen(new(item.bounds.X + item.bounds.Width / 2, item.bounds.Y + item.bounds.Height / 2)) : new(double.NaN, double.NaN);
        protected override bool IsEnabledCore() => Region is not null && owner.ActionEnabled(name);
        protected override bool IsOffscreenCore() => !owner.IsVisible || Region is null;
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
        protected override bool IsKeyboardFocusableCore() => IsEnabledCore();
        protected override bool HasKeyboardFocusCore() => owner.IsKeyboardFocusWithin && owner.keyboardAction == name;
        protected override bool IsPasswordCore() => false;
        protected override bool IsRequiredForFormCore() => false;
        protected override string GetHelpTextCore() => name == "details" ? AutomationProperties.GetHelpText(owner) + "；单击、Enter、空格或辅助技术均打开主面板；拖动可移动浮窗。保持展开可在功能菜单或 Ctrl+Space 切换。" : GetNameCore();
        protected override string GetItemStatusCore() => IsPage ? IsSelected ? "已选择" : "未选择" : name == "keepExpanded" ? owner.window.KeepsExpanded ? "保持展开已启用" : "保持展开未启用" : name == "filters" ? owner.FiltersOpen ? "已展开" : "已收拢" : "";
        protected override string GetItemTypeCore() => "";
        protected override string GetLocalizedControlTypeCore() => IsPage ? "单选按钮" : name == "keepExpanded" ? "复选框" : "按钮";
        protected override string GetAcceleratorKeyCore() => name == "keepExpanded" ? "Ctrl+Space" : "";
        protected override string GetAccessKeyCore() => "";
        protected override AutomationPeer? GetLabeledByCore() => null;
        protected override AutomationOrientation GetOrientationCore() => AutomationOrientation.None;
        protected override List<AutomationPeer>? GetChildrenCore() => null;
        protected override void SetFocusCore()
        {
            if (!IsEnabledCore()) throw new ElementNotEnabledException();
            owner.FocusAction(name, true);
        }
    }
}
