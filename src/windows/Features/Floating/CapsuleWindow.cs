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

internal sealed partial class CapsuleWindow : Window
{
    public readonly CapsuleSurface Surface;
    private readonly Func<string, string?, Task> action;
    private readonly Func<Task<JsonObject>> loadChoices;
    private readonly Func<Point?> readPointer;
    private readonly Func<Point, bool> ownsPointer;
    private bool pointerTracking;
    private Rect? pointerRetention;
    private Point? dismissedPointer;
    private bool manuallyCollapsed, leftAfterCollapse;
    private readonly DispatcherTimer departure = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(90) };
    private readonly DispatcherTimer bridgeWatch = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly DispatcherTimer hoverDelay = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(140) };
    internal int HoverEventCount { get; private set; }
    internal int ExpansionTransitions { get; private set; }
    private EventHandler? animation;
    private bool expanded, menu, pressed;
    private bool closed;
    public bool KeepsExpanded { get; private set; }
    internal bool MonitorMutationPending { get; private set; }
    internal bool MonitorCheckPending { get; private set; }
    private Rect? compactBounds;
    private Rect? panelBounds;
    private bool rebuildingEnvelope, pendingEnvelopeDpi;
    private Rect? hotspot;
    private ContextMenu? activeMenu;
    internal ContextMenu? ActiveMenu => activeMenu;
    private long focusIntent;
    internal void NewFocusIntent() => focusIntent++;
    private Point? restoredPixels;
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    private IntPtr Handle => new WindowInteropHelper(this).Handle;
    internal bool InteractionActive => menu || pressed;
    internal bool IsAnimating => animation != null;
    internal Rect PixelBounds
    {
        get
        {
            if (Handle != IntPtr.Zero && GetWindowRect(Handle, out var r)) return new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            var d = VisualTreeHelper.GetDpi(this); return new(Left * d.DpiScaleX, Top * d.DpiScaleY, Width * d.DpiScaleX, Height * d.DpiScaleY);
        }
    }
    private Rect InitialCompactBounds()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return compactBounds ?? new Rect(restoredPixels ?? new Point(Left * dpi.DpiScaleX, Top * dpi.DpiScaleY),
            new Size(76 * dpi.DpiScaleX, 76 * dpi.DpiScaleY));
    }
    private static Rect PixelEnvelope(Rect pixels)
    {
        double x = Math.Floor(pixels.X), y = Math.Floor(pixels.Y);
        return new(x, y, Math.Ceiling(pixels.Right) - x, Math.Ceiling(pixels.Bottom) - y);
    }
    private static Rect LocalBounds(Rect pixels, Rect host, DpiScale dpi) => new(
        (pixels.X - host.X) / dpi.DpiScaleX, (pixels.Y - host.Y) / dpi.DpiScaleY,
        pixels.Width / dpi.DpiScaleX, pixels.Height / dpi.DpiScaleY);
    private void SetHostBounds(Rect pixels)
    {
        pixels = PixelEnvelope(pixels);
        var scale = VisualTreeHelper.GetDpi(this);
        Surface.HostSize = new(pixels.Width / scale.DpiScaleX, pixels.Height / scale.DpiScaleY);
        if (PixelBounds == pixels) return;
        if (Handle != IntPtr.Zero)
            SetWindowPos(Handle, IntPtr.Zero, (int)pixels.X, (int)pixels.Y, (int)pixels.Width, (int)pixels.Height, 0x114);
        else { Width = pixels.Width / scale.DpiScaleX; Height = pixels.Height / scale.DpiScaleY; Left = pixels.X / scale.DpiScaleX; Top = pixels.Y / scale.DpiScaleY; }
    }
    // Only a changed physical anchor/display may replace the native envelope.
    // Ordinary hover, reversal and edge transitions keep all of these coordinates.
    private void RebuildEnvelope()
    {
        if (rebuildingEnvelope) { pendingEnvelopeDpi = true; return; }
        rebuildingEnvelope = true;
        try
        {
            int attempts = 0;
            do
            {
                pendingEnvelopeDpi = false;
                var dpi = VisualTreeHelper.GetDpi(this);
                if (expandedDragging)
                    compactBounds = CapsuleExpandedDragPlacement.AtPointer(expandedLastScreen, expandedGripDip, -(panelOffsetDip ?? new Vector()), dpi).Compact;
                var compact = InitialCompactBounds();
                compactBounds = new(compact.TopLeft, new Size(76 * dpi.DpiScaleX, 76 * dpi.DpiScaleY));
                compact = compactBounds.Value;
                var monitor = MonitorAt(compact);
                if (panelOffsetDip is Vector offset)
                {
                    var panel = new Rect(compact.X + offset.X * dpi.DpiScaleX, compact.Y + offset.Y * dpi.DpiScaleY, 336 * dpi.DpiScaleX, 410 * dpi.DpiScaleY);
                    if (!expandedDragging) panel = CapsuleExpandedDragPlacement.Clamp(new(panel, compact), monitor.Work, dpi).Panel;
                    panelBounds = panel;
                    panelOffsetDip = new((panel.X - compact.X) / dpi.DpiScaleX, (panel.Y - compact.Y) / dpi.DpiScaleY);
                }
                else panelBounds = CapsulePlacement.Expanded(compact, monitor.Work, dpi);
                var envelope = Rect.Union(compact, panelBounds.Value);
                // Reserve a nearby tab even while autohide is disabled, so enabling
                // it later does not resize the HWND merely to show the indicator.
                var edge = DockEdge != CapsuleEdge.None ? DockEdge : CapsulePlacement.Dock(compact, monitor, Monitors(), dpi);
                var tab = CapsulePlacement.Indicator(compact, monitor.Work, edge, dpi, showsMonitor: true);
                if (!tab.IsEmpty) envelope.Union(tab);
                envelope = PixelEnvelope(envelope);
                Surface.CompactBounds = LocalBounds(compact, envelope, dpi);
                Surface.PanelBounds = LocalBounds(panelBounds.Value, envelope, dpi);
                SetHostBounds(envelope);
            }
            while (pendingEnvelopeDpi && ++attempts < 3);
            Surface.Redraw();
        }
        finally { rebuildingEnvelope = false; }
    }
    internal Rect VisualPixelBounds
    {
        get
        {
            var host = PixelBounds; var visual = Surface.DrawingBounds;
            var dpi = VisualTreeHelper.GetDpi(this);
            return new(host.X + visual.X * dpi.DpiScaleX, host.Y + visual.Y * dpi.DpiScaleY, visual.Width * dpi.DpiScaleX, visual.Height * dpi.DpiScaleY);
        }
    }
    private void StopAnimation(bool freeze = false)
    {
        if (animation != null) { CompositionTarget.Rendering -= animation; animation = null; }
        releaseSettling = false;
        // Raw progress and any edge override already describe the exact visible
        // frame. A press/menu freezes that frame without moving or resizing its HWND.
    }
    public CapsuleWindow(Func<string, string?, Task> action, Func<Task<JsonObject>>? loadChoices = null, Func<Point?>? readPointer = null, Func<Point, bool>? ownsPointer = null)
    {
        this.action = action;
        this.loadChoices = loadChoices ?? (() => Ipc.Send(J.Obj(("action", "choices"))));
        this.readPointer = readPointer ?? (() => GetCursorPos(out var p) ? new Point(p.X, p.Y) : null);
        this.ownsPointer = ownsPointer ?? (p => GetAncestor(WindowFromPoint(new NativePoint { X = (int)Math.Round(p.X), Y = (int)Math.Round(p.Y) }), 2) == Handle);
        departure.Tick += (_, _) => ConfirmDeparture();
        bridgeWatch.Tick += CheckPointerBridge;
        hoverDelay.Tick += (_, _) => { hoverDelay.Stop(); EnterPointer(true); };
        Title = "Token 胶囊"; Width = 76; Height = 76; ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ToolTipService.SetIsEnabled(this, false);
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        FocusVisualStyle = null; // Surface owns the single, per-action focus indicator.
        Surface = new CapsuleSurface(this); Content = Surface;
        InputMethod.SetIsInputMethodEnabled(this, false);
        Left = SystemParameters.WorkArea.Right - 214; Top = SystemParameters.WorkArea.Top + 28;
        Surface.MouseEnter += (_, _) => PointerEntered();
        Surface.MouseLeave += (_, _) => PointerLeft();
        InitializeDocking();
        SourceInitialized += (_, _) =>
        {
            bool showExpanded = expanded || KeepsExpanded;
            if (compactBounds is null)
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                compactBounds = new(restoredPixels ?? PixelBounds.TopLeft, new Size(76 * dpi.DpiScaleX, 76 * dpi.DpiScaleY));
            }
            RestoreDock();
            if (showExpanded) Expand(true, false);
        };
        Closed += (_, _) => { closed = true; DisposeDocking(); departure.Stop(); bridgeWatch.Stop(); hoverDelay.Stop(); StopAnimation(); activeMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false); };
        PreviewKeyDown += async (_, e) =>
        {
            Key key = CapsuleSurface.InputKey(e.Key, e.SystemKey, e.ImeProcessedKey);
            if (key == Key.Escape)
            {
                if (menu && activeMenu?.IsOpen == true) { activeMenu.IsOpen = false; e.Handled = true; return; }
                Collapse(); e.Handled = true; return;
            }
            var modifiers = e.KeyboardDevice.Modifiers;
            if (menu || pressed || !CapsuleSurface.HandlesKeyboard(key, modifiers)) return;
            // Window can own focus after native activation or dragging. Handle our
            // navigation before WPF's default Tab processing, wherever focus sits.
            e.Handled = true;
            await Surface.HandleKeyboardAsync(key, modifiers);
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                departure.Stop(); bridgeWatch.Stop(); hoverDelay.Stop(); pointerTracking = false; pointerRetention = null; dismissedPointer = null;
                // An explicit hide ends this pointer interaction. Showing the
                // capsule later starts fresh while retaining the saved preference.
                manuallyCollapsed = false; leftAfterCollapse = false;
                CancelDocking(); CancelMenu(); StopAnimation(); pressed = false; Surface.CancelInteraction();
                // A hidden window must reopen at a complete state, even if hidden during a drag or transition.
                Expand(KeepsExpanded, false);
            }
        };
    }
    private void CheckPointerBridge(object? sender, EventArgs args)
    {
        if (closed || !IsVisible || menu || pressed || KeepsExpanded || Surface.KeyboardInteraction || !expanded) { bridgeWatch.Stop(); return; }
        if (readPointer() is Point p && ContainsPointer(VisualPixelBounds, Surface.Expansion, p) && ownsPointer(p)) { bridgeWatch.Stop(); return; }
        if (!RetainsPointer() || readPointer() is Point at && !OwnsInteraction(at)) { bridgeWatch.Stop(); QueueDeparture(); }
    }
    private bool ContainsPointer(Rect bounds, double expansion, Point point)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        if (Surface.Edge != CapsuleEdge.None && bounds == VisualPixelBounds)
            return CapsuleEdgeIndicator.Shape(new(bounds.Width / dpi.DpiScaleX, bounds.Height / dpi.DpiScaleY), Surface.Edge, Surface.Docking)
                .FillContains(new Point((point.X - bounds.X) / dpi.DpiScaleX, (point.Y - bounds.Y) / dpi.DpiScaleY));
        double geometryProgress = expansion > 0 && expansion < 1 ? (38 - Surface.CurrentRadius) / 16 : expansion;
        return CapsuleGeometry.Contains(new((point.X - bounds.X) / dpi.DpiScaleX, (point.Y - bounds.Y) / dpi.DpiScaleY),
            new(bounds.Width / dpi.DpiScaleX, bounds.Height / dpi.DpiScaleY), geometryProgress);
    }
    private bool PointerOverVisible() => readPointer() is Point p && ContainsPointer(VisualPixelBounds, Surface.Expansion, p);
    private bool RetainsPointer() => readPointer() is Point p && (ContainsPointer(pointerRetention ?? VisualPixelBounds, pointerRetention is null ? Surface.Expansion : 1, p) || IsHotspot(p));
    private bool OwnsInteraction(Point p) => ownsPointer(p) || IsHotspot(p) && !ContainsPointer(VisualPixelBounds, Surface.Expansion, p);
    internal void PointerEntered()
    {
        HoverEventCount++;
        EnterPointer();
    }
    private void EnterPointer(bool hoverConfirmed = false)
    {
        if (HandleDockPointer()) return;
        if (closed || !IsVisible || menu || pressed || releaseSettling || Mouse.LeftButton == MouseButtonState.Pressed || readPointer() is not Point p || !PointerOverVisible()) return;
        if (manuallyCollapsed && !leftAfterCollapse) return;
        // Native hit-test changes while morphing can re-enter at the same screen point.
        // A completed dismissal needs actual pointer movement before it can reopen.
        if (dismissedPointer is Point previous && (p - previous).Length < 1) return;
        if (!ownsPointer(p)) return;
        // Give a press on the compact shape priority over automatic expansion.
        // Once expansion has begun, a press can still claim it as a compact drag.
        if (!hoverConfirmed && !expanded && Surface.Expansion <= .001)
        {
            departure.Stop();
            if (!hoverDelay.IsEnabled) hoverDelay.Start();
            return;
        }
        manuallyCollapsed = false;
        hoverDelay.Stop(); hideDelay.Stop(); departure.Stop(); dismissedPointer = null; pointerTracking = true; Expand(true);
    }
    internal void PointerLeft()
    {
        HoverEventCount++;
        if (!PointerOverVisible()) hoverDelay.Stop();
        if (manuallyCollapsed)
        {
            if (readPointer() is not Point collapsedPointer || !ContainsPointer(CompactPixelBounds, 0, collapsedPointer)) leftAfterCollapse = true;
            return;
        }
        // A synthetic Leave while the pointer is still on the compact shape
        // must not schedule a 90 ms dismissal that defeats the 140 ms hover wait.
        if (!expanded && Surface.Expansion <= .001) return;
        if (closed || !IsVisible || menu || pressed || releaseSettling || KeepsExpanded || Surface.KeyboardInteraction) return;
        // Layout/hit-test changes can emit MouseLeave without physical pointer movement.
        // Keep the interaction in screen coordinates until it leaves the expanded target.
        if (HiddenAtEdge || edgeAnimating) { CheckDockPointer(); return; }
        if (pointerTracking && RetainsPointer() && (IsAnimating || readPointer() is Point p && OwnsInteraction(p))) { bridgeWatch.Start(); return; }
        QueueDeparture();
    }
    private void QueueDeparture()
    {
        if (!departure.IsEnabled && !KeepsExpanded && !Surface.KeyboardInteraction && !menu && !pressed && !closed && IsVisible) departure.Start();
    }
    private void ConfirmDeparture()
    {
        departure.Stop();
        if (closed || !IsVisible || menu || pressed || releaseSettling || KeepsExpanded || Surface.KeyboardInteraction) return;
        if (pointerTracking && RetainsPointer() && (IsAnimating || readPointer() is Point p && OwnsInteraction(p))) { bridgeWatch.Start(); return; }
        DismissHover();
    }
    private void DismissHover()
    {
        hoverDelay.Stop(); departure.Stop(); bridgeWatch.Stop(); dismissedPointer = readPointer(); pointerTracking = false; pointerRetention = null;
        Expand(false); CheckDockPointer();
    }
    internal void PointerMoved(Point screen)
    {
        if (closed || !IsVisible || menu || pressed || releaseSettling) return;
        if (HandleDockPointer()) return;
        if (dismissedPointer is Point previous)
        {
            if ((screen - previous).Length < 1) return;
            dismissedPointer = null;
        }
        if (!pointerTracking) { EnterPointer(); return; }
        if (RetainsPointer() && (IsAnimating || OwnsInteraction(screen))) departure.Stop();
        else QueueDeparture();
    }
    private void ResumePointer()
    {
        departure.Stop();
        if (manuallyCollapsed) { Expand(false); return; }
        dismissedPointer = null;
        if (HiddenAtEdge || edgeAnimating) RevealEdge(false, false);
        if (awaitingRingEntry && !KeepsExpanded && !Surface.KeyboardInteraction) { CheckDockPointer(); return; }
        pointerTracking = PointerOverVisible(); pointerRetention = null;
        Expand(KeepsExpanded || Surface.KeyboardInteraction || pointerTracking);
    }
    internal void BeginKeyboardInteraction()
    {
        if (closed || pressed) return;
        manuallyCollapsed = false;
        departure.Stop(); bridgeWatch.Stop(); hideDelay.Stop(); wakeDelay.Stop(); awaitingRingEntry = false;
        if (HiddenAtEdge || edgeAnimating) RevealEdge(false, false);
        if (!menu) Expand(true, false);
    }
    internal void EndKeyboardInteraction()
    {
        if (!closed && IsVisible && !menu && !pressed) ResumePointer();
    }
    public void Update(JsonObject state)
    {
        var floating = state.O("settings").O("floating");
        Topmost = floating.B("pinned", true);
        Surface.Update(state);
        if (floating.ContainsKey("keepExpanded")) SetKeepsExpanded(floating.B("keepExpanded"));
        UpdateDockSettings(floating);
        RefreshEdgeSize();
    }
    internal bool IsMonitorTaskVisible(string taskID) => IsVisible && !HiddenAtEdge && !edgeAnimating && animation is null &&
        Surface.Edge == CapsuleEdge.None && Surface.Expansion >= .999 && Surface.MonitorMode &&
        TaskMonitorVisual.Featured(Surface.State).Any(task => task.S("id") == taskID);
    internal void SetKeepsExpanded(bool value)
    {
        if (KeepsExpanded == value) return;
        KeepsExpanded = value; manuallyCollapsed = false;
        Surface.WindowStateChanged(); ResumePointer();
    }
    public void Collapse()
    {
        if (closed) return;
        CancelMenu(); pressed = false; Surface.CancelInteraction(); Surface.EndKeyboardNavigation();
        manuallyCollapsed = true;
        leftAfterCollapse = readPointer() is not Point p || !ContainsPointer(CompactPixelBounds, 0, p);
        DismissHover();
    }
    public void Restore(JsonObject floating)
    {
        // End a live gesture before replacing its compact anchor and panel offset.
        // Cancellation may clamp the old drag position.
        if (Handle != IntPtr.Zero) { CancelMenu(); pressed = false; Surface.CancelInteraction(); CancelDocking(); StopAnimation(); }
        RestorePanelOffset(floating);
        var dpi = VisualTreeHelper.GetDpi(this); var compact = InitialCompactBounds();
        double left = floating.N("pixelLeft") ?? (floating.N("left") is double x ? x * dpi.DpiScaleX : compact.Left);
        double top = floating.N("pixelTop") ?? (floating.N("top") is double y ? y * dpi.DpiScaleY : compact.Top);
        restoredPixels = new(left, top);
        compactBounds = new(restoredPixels.Value, new Size(76 * dpi.DpiScaleX, 76 * dpi.DpiScaleY));
        restoredDock = floating.Copy();
        if (Handle != IntPtr.Zero)
        {
            RestoreDock();
        }
        else { Left = left / dpi.DpiScaleX; Top = top / dpi.DpiScaleY; }
    }
    public void Clamp()
    {
        var compact = InitialCompactBounds();
        compactBounds = CapsuleGeometry.Clamp(compact, MonitorAt(compact).Work);
        RebuildEnvelope();
    }
    public bool IsHotspot(Point screen) => hotspot is Rect r && Math.Pow((screen.X - r.X - r.Width / 2) / (r.Width / 2), 2) + Math.Pow((screen.Y - r.Y - r.Height / 2) / (r.Height / 2), 2) <= 1;
    internal void TrackPointer(Point screen) { if (!menu && !pressed && !IsHotspot(screen)) hotspot = null; }
    public void Press(bool value)
    {
        if (value) NewFocusIntent();
        pressed = value;
        if (value) { hoverDelay.Stop(); hideDelay.Stop(); wakeDelay.Stop(); awaitingRingEntry = false; if (HiddenAtEdge || edgeAnimating) RevealEdge(false, false); departure.Stop(); dismissedPointer = null; pointerTracking = false; pointerRetention = null; StopAnimation(true); }
        else if (!menu) ResumePointer();
    }
    public void MoveBy(double x, double y, Point origin, Rect? savedHotspot)
    {
        if (expandedDragging) { MoveExpandedDrag(expandedPressScreen + new Vector(x, y)); return; }
        var dpi = VisualTreeHelper.GetDpi(this);
        compactBounds = new(origin.X + x, origin.Y + y, 76 * dpi.DpiScaleX, 76 * dpi.DpiScaleY);
        RebuildEnvelope();
        if (savedHotspot is Rect r) { r.Offset(x, y); hotspot = r; }
    }
    public Rect? Hotspot => hotspot;
    public async Task FinishDrag() { if (expandedDragging) FinishExpandedDrag(); else FinishDockDrag(); await SaveOrigin(); }
    public async Task SaveOrigin()
    {
        if (closed) return;
        var dpi = VisualTreeHelper.GetDpi(this); var bounds = InitialCompactBounds();
        var monitor = MonitorAt(bounds);
        await action("position", J.Text(J.Obj(("left", bounds.Left / dpi.DpiScaleX), ("top", bounds.Top / dpi.DpiScaleY), ("pixelLeft", bounds.Left), ("pixelTop", bounds.Top),
            ("dockEdge", DockEdge.ToString()), ("monitor", monitor.Id), ("monitorX", (bounds.X - monitor.Work.X) / dpi.DpiScaleX), ("monitorY", (bounds.Y - monitor.Work.Y) / dpi.DpiScaleY),
            ("panelOffsetX", (double?)null), ("panelOffsetY", (double?)null))));
    }
    public void Expand(bool target, bool animate = true)
    {
        if (menu || pressed || closed) return;
        if (HiddenAtEdge || edgeAnimating) RevealEdge(false, false);
        if (target) { hideDelay.Stop(); wakeDelay.Stop(); awaitingRingEntry = false; }
        if (target == expanded && (animate && animation is not null || animation is null && Surface.AnimationBounds is null && Math.Abs(Surface.Expansion - (target ? 1 : 0)) < .001)) return;
        if (target != expanded) ExpansionTransitions++;
        StopAnimation(); bridgeWatch.Stop();
        // Before the first Show only, callers may request a preview endpoint.
        // SourceInitialized normally establishes these bounds before any hover.
        if (panelBounds is null || Surface.CompactBounds is null) RebuildEnvelope();
        if (!expanded && target && Surface.Expansion <= .001)
        {
            // Each compact-to-panel cycle selects its direction from current
            // available space. A previous expanded drag is not a permanent side.
            panelOffsetDip = null; RebuildEnvelope();
            hotspot = compactBounds;
        }
        expanded = target;
        double start = Surface.Expansion;
        var targetBounds = target ? panelBounds!.Value : InitialCompactBounds();
        if (target && pointerTracking) pointerRetention = targetBounds;
        Surface.AnimationBounds = null;
        async void Done()
        {
            StopAnimation(); Surface.AnimationBounds = null;
            Surface.Expansion = target ? 1 : 0; Surface.Redraw();
            if (target && pointerTracking && (!RetainsPointer() || readPointer() is Point p && !OwnsInteraction(p))) QueueDeparture();
            if (target && pointerTracking && readPointer() is Point at && IsHotspot(at) && !ContainsPointer(targetBounds, 1, at)) bridgeWatch.Start();
            if (!target) { pointerTracking = false; pointerRetention = null; hotspot = null; panelOffsetDip = null; RebuildEnvelope(); CheckDockPointer(); await SaveOrigin(); }
        }
        double distance = Math.Abs((target ? 1 : 0) - start);
        if (!animate || !IsVisible || !SystemParameters.ClientAreaAnimation || distance < .000001) { Done(); return; }

        // Expansion is raw linear progress. CapsuleMorph owns shape/content
        // timing, so a reversal retains precisely the same geometry and alpha.
        var watch = Stopwatch.StartNew(); double duration = (target ? 280 : 220) * distance;
        TimeSpan lastFrame = TimeSpan.MinValue;
        animation = (_, args) =>
        {
            if (args is not RenderingEventArgs frame || frame.RenderingTime == lastFrame) return;
            if (target && pointerTracking && !KeepsExpanded && !Surface.KeyboardInteraction && !RetainsPointer())
            {
                QueueDeparture();
            }
            lastFrame = frame.RenderingTime;
            double linear = Math.Clamp(watch.Elapsed.TotalMilliseconds / duration, 0, 1);
            Surface.Expansion = start + ((target ? 1 : 0) - start) * linear; Surface.Redraw();
            if (linear >= 1) Done();
        };
        CompositionTarget.Rendering += animation;
    }
    public async Task Invoke(string name)
    {
        if (closed || pressed || !Surface.ActionEnabled(name)) return;
        if (name == "details") name = "main";
        if (name == "keepExpanded") { SetKeepsExpanded(!KeepsExpanded); await action(name, KeepsExpanded ? "true" : "false"); return; }
        if (name == "collapse") { Collapse(); return; }
        if (name == "filters") { Surface.ToggleFilters(); return; }
        if (name == "filtersReset") { await action("filters-reset", null); return; }
        if (name is "contentUsage" or "contentBudget" or "contentMonitor") { await action("content", name == "contentUsage" ? "usage" : name == "contentBudget" ? "budget" : "monitor"); return; }
        if (name.StartsWith("monitorDetail:", StringComparison.Ordinal)) { await action("monitorDetail", name[14..]); return; }
        if (name == "monitorCheck")
        {
            string home = Surface.State.S("home");
            MonitorCheckPending = true; Surface.BeginMonitorCheck();
            try { await action(name, null); }
            catch (Exception e) { Surface.FailMonitorCheck(e.Message, home); }
            finally { MonitorCheckPending = false; Surface.WindowStateChanged(); }
            return;
        }
        if (name == "monitorClearEnded" || name.StartsWith("monitorStop:", StringComparison.Ordinal))
        {
            MonitorMutationPending = true; Surface.WindowStateChanged();
            try { await action(name == "monitorClearEnded" ? name : "monitorStop", name == "monitorClearEnded" ? null : name[12..]); }
            catch (Exception e) { Surface.ShowError(e.Message); }
            finally { MonitorMutationPending = false; Surface.WindowStateChanged(); }
            return;
        }
        if (name is "context" or "more" or "period" or "model" or "task" or "content" or "budget" or "budgetPause") { await ShowMenu(name); return; }
        await action(name, null);
    }
    private void CancelMenu()
    {
        NewFocusIntent();
        var context = activeMenu; activeMenu = null; menu = false;
        if (context?.IsOpen == true) context.IsOpen = false;
    }
    // New commands default to no focus return until known to stay in this surface.
    internal static bool ShouldRestoreMenuFocus(bool keyboardMenu, string? command, bool ownsForeground) =>
        keyboardMenu && ownsForeground && command is null or "keepExpanded" or "pin" or "edgeAutoHide" or "edgeMetric" or
            "period" or "model" or "task" or "content" or "budget" or "budgetPause" or "budgetResume" or
            "refresh" or "filters-reset" or "themeDark" or "themeLight";
    public async Task ShowMenu(string name)
    {
        if (menu || pressed || closed) return;
        long menuFocusIntent = ++focusIntent;
        bool keyboardMenu = Surface.KeyboardInteraction;
        string? returnFocus = Surface.KeyboardAction;
        hideDelay.Stop(); wakeDelay.Stop(); if (HiddenAtEdge || edgeAnimating) RevealEdge(false, false);
        departure.Stop(); menu = true; StopAnimation(true); Surface.ClearFeedback();
        var context = new ContextMenu { PlacementTarget = Surface, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint, MaxHeight = 480, MaxWidth = 500 };
        IntPtr menuHandle = IntPtr.Zero;
        context.Opened += (_, _) => menuHandle = (PresentationSource.FromVisual(context) as HwndSource)?.Handle ?? IntPtr.Zero;
        Theme.ApplyTo(context.Resources, Theme.Resolve(Surface.State.O("settings"), "floating"));
        if (keyboardMenu)
        {
            context.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            context.PlacementRectangle = Surface.KeyboardFocusBounds;
        }
        activeMenu = context;
        (string command, string? value)? selection = null;
        void Add(string label, string command, string? value = null, bool? selected = null, bool enabled = true)
        {
            var item = new MenuItem { Header = label, ToolTip = label, IsChecked = selected == true, IsCheckable = selected.HasValue, IsEnabled = enabled };
            item.Click += (_, _) => { selection = (command, value); context.IsOpen = false; }; context.Items.Add(item);
        }
        void Section(string label)
        {
            if (context.Items.Count > 0) context.Items.Add(new Separator());
            context.Items.Add(new MenuItem { Header = label, IsEnabled = false, Focusable = false });
        }
        var f = Surface.State.O("settings").O("floating"); string requestedHome = Surface.State.S("home");
        if (name == "period")
        {
            foreach (var p in new[] { ("1", "今天"), ("7", "7 天"), ("30", "30 天"), ("90", "90 天"), ("all", "全部") }) Add(p.Item2, "period", p.Item1, f.S("days", "1") == p.Item1);
        }
        else if (name == "content") { Add("用量统计", "content", "usage", !Surface.BudgetMode && !Surface.MonitorMode); Add("预算提醒", "content", "budget", Surface.BudgetMode); Add("任务监控", "content", "monitor", Surface.MonitorMode); }
        else if (name == "budget")
        {
            foreach (var row in Surface.State.O("budgets").A("rules").Rows()) Add(row.S("name"), "budget", row.S("id"), f.S("budgetID") == row.S("id"));
            Add("管理预算…", "budgetManage");
        }
        else if (name == "budgetPause")
        {
            Add("暂停提醒 30 分钟", "budgetPause", "30"); Add("暂停本周期提醒", "budgetPause", "cycle");
        }
        else if (name is "model" or "task")
        {
            try
            {
                var state = await loadChoices();
                // An older asynchronous menu must never clear the newer menu that replaced it.
                if (activeMenu != context) return;
                if (closed || !IsVisible || requestedHome != Surface.State.S("home") || new[] { "days", "model", "task" }.Any(key => f.S(key) != Surface.State.O("settings").O("floating").S(key)))
                {
                    CancelMenu(); if (!closed && IsVisible) ResumePointer(); return;
                }
                Add(name == "model" ? "全部模型" : "全部任务", name, "all", f.S(name, "all") == "all");
                if (name == "model") foreach (var row in state.O("choices").A("models")) { string id = row?.GetValue<string>() ?? ""; Add(id, name, id, f.S(name) == id); }
                else foreach (var row in state.O("choices").A("tasks").Rows()) Add(row.S("label"), name, row.S("id"), f.S(name) == row.S("id"));
            }
            catch (Exception e) { Add(e.Message, "none", enabled: false); }
        }
        else
        {
            Section("任务监控");
            Add("选择任务与查看消息…", "monitorManage");
            Add("任务提醒设置…", "monitorSettings");
            Section("窗口行为");
            Add("保持展开（离开鼠标不收起）", "keepExpanded", selected: KeepsExpanded);
            Add("始终置顶浮窗", "pin", selected: Topmost);
            Add("贴边自动隐藏", "edgeAutoHide", selected: f.B("edgeAutoHide", true));
            Add("侧签显示剩余额度", "edgeMetric", "remaining", f.S("edgeMetric", "remaining") != "used");
            Add("侧签显示已用额度", "edgeMetric", "used", f.S("edgeMetric") == "used");
            Section("常驻显示方式（保留主面板）");
            string mode = Surface.State.O("settings").S("mode", "both");
            Add("仅托盘", "mode", "tray", mode == "tray"); Add("仅浮窗", "mode", "float", mode == "float"); Add("托盘与浮窗", "mode", "both", mode == "both");
            Section("外观与设置");
            Add("弧线配色…", "arcColors");
            Add("外观与统一设置…", "settings-dialog", "appearance"); Add("数据与更新设置…", "settings-dialog", "updates"); Add("数据更新状态与重试…", "updateStatus");
            context.Items.Add(new Separator()); Add("隐藏浮窗至托盘", "hide-floating");
            context.Items.Add(new Separator()); Add("退出 Codex 用量", "quit");
        }
        context.Closed += async (_, _) =>
        {
            if (activeMenu != context) return;
            menu = false; activeMenu = null; Surface.ClearFeedback(); if (closed || !IsVisible) return;
            // Native menu tracking ends before navigation/settings actions can change geometry.
            try
            {
                if (selection is { } selected)
                {
                    if (selected.command == "keepExpanded") await Invoke(selected.command);
                    else if (Surface.ActionEnabled(selected.command)) await action(selected.command, selected.value);
                }
            }
            catch (Exception e) { Surface.ShowError(e.Message); }
            if (!closed && IsVisible)
            {
                // Let native menu dismissal and any navigation finish before checking
                // ownership. A dismissed background menu must never activate its window.
                await Dispatcher.InvokeAsync(() =>
                {
                    // Closing the popup releases interaction before an async
                    // command or this dispatcher operation finishes. A newer
                    // key, drag, collapse or menu owns focus from that point on.
                    if (closed || !IsVisible || activeMenu != null || focusIntent != menuFocusIntent) return;
                    IntPtr foreground = GetForegroundWindow();
                    bool ownsForeground = foreground != IntPtr.Zero && (foreground == Handle || foreground == menuHandle);
                    if (ShouldRestoreMenuFocus(keyboardMenu, selection?.command, ownsForeground))
                        Surface.FocusAction(returnFocus ?? "details", true);
                    ResumePointer();
                }, DispatcherPriority.Input);
            }
        };
        if (activeMenu != context) return;
        if (closed || !IsVisible) { CancelMenu(); return; }
        context.IsOpen = true;
    }
}
