using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CodexUsage;

internal sealed partial class CapsuleWindow
{
    internal CapsuleEdge DockEdge { get; private set; }
    internal bool HiddenAtEdge => Surface.Edge != CapsuleEdge.None;
    internal bool DockMotionActive => edgeAnimating;
    internal bool AwaitingRingEntry => awaitingRingEntry;
    internal Rect CompactPixelBounds => InitialCompactBounds();
    internal bool DockTimersActive => dockWatch.IsEnabled || hideDelay.IsEnabled || wakeDelay.IsEnabled;
    private bool autoHide = true, edgeAnimating, awaitingRingEntry;
    private Point? revealedPointer;
    private JsonObject restoredDock = new();
    private readonly DispatcherTimer hideDelay = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(550) };
    private readonly DispatcherTimer wakeDelay = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(120) };
    // Only docked, visible capsules sample proximity. There is no full-screen hit window,
    // input hook, data refresh, or rendering callback while idle.
    private readonly DispatcherTimer dockWatch = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };

    private static CapsuleMonitor[] Monitors() => System.Windows.Forms.Screen.AllScreens.Select(s => new CapsuleMonitor(s.DeviceName,
        new(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height), new(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height))).ToArray();
    private static CapsuleMonitor MonitorAt(Rect bounds)
    {
        var s = System.Windows.Forms.Screen.FromPoint(new((int)(bounds.X + bounds.Width / 2), (int)(bounds.Y + bounds.Height / 2)));
        return new(s.DeviceName, new(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height), new(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height));
    }
    private void InitializeDocking()
    {
        dockWatch.Tick += (_, _) => CheckDockPointer();
        hideDelay.Tick += (_, _) => { hideDelay.Stop(); if (MayHide() && !PointerNearCompact()) HideToEdge(); };
        wakeDelay.Tick += (_, _) => { wakeDelay.Stop(); if (MayWake() && PointerNearTab()) RevealEdge(); };
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        SystemParameters.StaticPropertyChanged += SystemSettingChanged;
        IsVisibleChanged += (_, _) => { if (IsVisible) StartDockWatch(); };
    }
    private void DisposeDocking()
    {
        dockWatch.Stop(); hideDelay.Stop(); wakeDelay.Stop();
        SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        SystemParameters.StaticPropertyChanged -= SystemSettingChanged;
    }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (Surface is null || closed || compactBounds is not Rect compact) return;
        compactBounds = new(compact.TopLeft, new Size(76 * newDpi.DpiScaleX, 76 * newDpi.DpiScaleY));
        if (rebuildingEnvelope) { pendingEnvelopeDpi = true; return; }
        if (expandedDragging) MoveExpandedDrag(expandedLastScreen);
        else if (pressed && Surface.RingVisible)
        {
            // The suggested HWND rectangle belongs to the large envelope. The
            // physical pointer grip remains anchored to the logical compact rect.
            RebuildEnvelope();
        }
        else DisplayChanged(this, EventArgs.Empty);
    }
    private void SystemSettingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SystemParameters.WorkArea)) DisplayChanged(sender, EventArgs.Empty);
    }
    internal void DisplayChanged(object? sender, EventArgs args)
    {
        if (closed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (closed) return;
            CancelMenu(); pressed = false; Surface.CancelInteraction(); CancelDocking(); StopAnimation();
            var compact = InitialCompactBounds();
            var dpi = VisualTreeHelper.GetDpi(this);
            compact = new(compact.TopLeft, new Size(76 * dpi.DpiScaleX, 76 * dpi.DpiScaleY));
            var monitor = MonitorAt(compact);
            compactBounds = CapsuleGeometry.Clamp(compact, monitor.Work);
            DockEdge = autoHide ? CapsulePlacement.Dock(compactBounds.Value, monitor, Monitors(), dpi) : CapsuleEdge.None;
            if (DockEdge != CapsuleEdge.None) compactBounds = CapsulePlacement.CompactAtEdge(compactBounds.Value, monitor.Work, DockEdge);
            RebuildEnvelope(); SetCompact();
            if (KeepsExpanded && !manuallyCollapsed) Expand(true, false);
            StartDockWatch(); _ = SaveOrigin();
        });
    }
    private void UpdateDockSettings(JsonObject floating)
    {
        bool enabled = floating.B("edgeAutoHide", true);
        if (enabled != autoHide)
        {
            autoHide = enabled;
            if (!enabled)
            {
                hideDelay.Stop(); wakeDelay.Stop();
                if (HiddenAtEdge || edgeAnimating) RevealEdge(false);
                DockEdge = CapsuleEdge.None;
            }
            else if (compactBounds is Rect compact)
            {
                var monitor = MonitorAt(compact);
                DockEdge = CapsulePlacement.Dock(compact, monitor, Monitors(), VisualTreeHelper.GetDpi(this));
            }
        }
        StartDockWatch();
    }
    private void RestoreDock()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var savedMonitor = Monitors().FirstOrDefault(x => x.Id == restoredDock.S("monitor"));
        var compact = InitialCompactBounds();
        if (savedMonitor is not null && restoredDock.N("monitorX") is double x && restoredDock.N("monitorY") is double y)
            compact = new(savedMonitor.Work.X + x * dpi.DpiScaleX, savedMonitor.Work.Y + y * dpi.DpiScaleY, 76 * dpi.DpiScaleX, 76 * dpi.DpiScaleY);
        var monitor = savedMonitor ?? MonitorAt(compact);
        compact = CapsuleGeometry.Clamp(compact, monitor.Work);
        DockEdge = autoHide && Enum.TryParse<CapsuleEdge>(restoredDock.S("dockEdge"), out var savedEdge) && Enum.IsDefined(savedEdge) ? savedEdge : CapsuleEdge.None;
        if (DockEdge != CapsuleEdge.None)
        {
            compact = CapsulePlacement.CompactAtEdge(compact, monitor.Work, DockEdge);
            // A newly attached display may turn yesterday's outer edge into a seam.
            DockEdge = CapsulePlacement.Dock(compact, monitor, Monitors(), dpi);
        }
        compactBounds = compact; RebuildEnvelope(); SetCompact();
        if (KeepsExpanded && !manuallyCollapsed) Expand(true, false);
        StartDockWatch();
    }
    private void StartDockWatch()
    {
        if (closed || !IsVisible || DockEdge == CapsuleEdge.None || !autoHide)
        { dockWatch.Stop(); hideDelay.Stop(); wakeDelay.Stop(); return; }
        if (!dockWatch.IsEnabled) dockWatch.Start();
        CheckDockPointer();
    }
    private bool MayHide() => autoHide && DockEdge != CapsuleEdge.None && !closed && IsVisible && !expanded && Surface.Expansion == 0 && !IsAnimating && !HiddenAtEdge && !menu && !pressed && !KeepsExpanded && !Surface.KeyboardInteraction;
    private bool MayWake() => HiddenAtEdge && !edgeAnimating && !closed && IsVisible && !menu && !pressed && !KeepsExpanded && !Surface.KeyboardInteraction;
    private bool PointerNearCompact()
    {
        if (readPointer() is not Point p) return false;
        var r = InitialCompactBounds(); var dpi = VisualTreeHelper.GetDpi(this);
        r.Inflate(8 * dpi.DpiScaleX, 8 * dpi.DpiScaleY); return r.Contains(p);
    }
    private bool PointerNearTab()
    {
        if (readPointer() is not Point p) return false;
        var r = VisualPixelBounds; var center = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        var dpi = VisualTreeHelper.GetDpi(this); r.Inflate(8 * dpi.DpiScaleX, 8 * dpi.DpiScaleY);
        // Outside the drawn tab the transparent host does not own the pointer.
        // Check ownership at the tab itself to avoid waking underneath another window.
        return r.Contains(p) && ownsPointer(center);
    }
    internal void CheckDockPointer()
    {
        if (closed || !IsVisible || DockEdge == CapsuleEdge.None || !autoHide) { hideDelay.Stop(); wakeDelay.Stop(); return; }
        if (menu || pressed || KeepsExpanded || Surface.KeyboardInteraction || edgeAnimating) { hideDelay.Stop(); wakeDelay.Stop(); return; }
        if (HiddenAtEdge)
        {
            hideDelay.Stop();
            if (PointerNearTab()) { if (!wakeDelay.IsEnabled) wakeDelay.Start(); }
            else wakeDelay.Stop();
        }
        else
        {
            wakeDelay.Stop();
            if (MayHide() && !PointerNearCompact()) { if (!hideDelay.IsEnabled) hideDelay.Start(); }
            else hideDelay.Stop();
        }
    }
    private bool HandleDockPointer()
    {
        if (HiddenAtEdge || edgeAnimating) { CheckDockPointer(); return true; }
        if (!awaitingRingEntry) return false;
        if (readPointer() is not Point p || revealedPointer is Point old && (p - old).Length < 1 || !PointerOverVisible() || !ownsPointer(p)) return true;
        awaitingRingEntry = false; revealedPointer = null; dismissedPointer = null;
        return false;
    }
    private void SetCompact()
    {
        Surface.Edge = CapsuleEdge.None; Surface.DockClip = null; Surface.AnimationBounds = null;
        Surface.Expansion = 0; expanded = false; hotspot = null; pointerTracking = false; pointerRetention = null;
        Surface.Redraw();
    }
    private void CancelDocking()
    {
        dockWatch.Stop(); hideDelay.Stop(); wakeDelay.Stop(); awaitingRingEntry = false; revealedPointer = null;
        if (HiddenAtEdge || edgeAnimating) { StopAnimation(); edgeAnimating = false; SetCompact(); }
    }
    internal Point BeginDrag(Point pressPoint)
    {
        expandedDragging = false; panelOffsetDip = null;
        manuallyCollapsed = false;
        hideDelay.Stop(); wakeDelay.Stop(); StopAnimation(); edgeAnimating = false;
        var dpi = VisualTreeHelper.GetDpi(this);
        var compact = InitialCompactBounds();
        if (!compact.Contains(pressPoint)) compact = new(pressPoint.X - 38 * dpi.DpiScaleX, pressPoint.Y - 38 * dpi.DpiScaleY, 76 * dpi.DpiScaleX, 76 * dpi.DpiScaleY);
        compactBounds = compact; DockEdge = CapsuleEdge.None; dockWatch.Stop(); awaitingRingEntry = false;
        RebuildEnvelope(); SetCompact(); return compact.TopLeft;
    }
    private void FinishDockDrag()
    {
        StopAnimation(); edgeAnimating = false; var dpi = VisualTreeHelper.GetDpi(this);
        var compact = InitialCompactBounds();
        var bounds = new Rect(compact.TopLeft, new Size(76 * dpi.DpiScaleX, 76 * dpi.DpiScaleY));
        var monitor = MonitorAt(bounds); bounds = CapsuleGeometry.Clamp(bounds, monitor.Work);
        DockEdge = autoHide ? CapsulePlacement.Dock(bounds, monitor, Monitors(), dpi) : CapsuleEdge.None;
        compactBounds = CapsulePlacement.CompactAtEdge(bounds, monitor.Work, DockEdge); RebuildEnvelope(); SetCompact();
        pressed = false; awaitingRingEntry = true; revealedPointer = readPointer(); dismissedPointer = revealedPointer;
        if (KeepsExpanded)
        {
            awaitingRingEntry = false; revealedPointer = null; dismissedPointer = null;
            Expand(true);
        }
        StartDockWatch();
    }
    internal void RevealEdge(bool animate = true, bool requireEntry = true)
    {
        if (closed || (!HiddenAtEdge && !edgeAnimating)) return;
        hideDelay.Stop(); wakeDelay.Stop();
        var compact = InitialCompactBounds(); var dpi = VisualTreeHelper.GetDpi(this);
        var monitor = MonitorAt(compact);
        Rect? initial = edgeAnimating ? VisualPixelBounds : null;
        StopAnimation(); edgeAnimating = false; Surface.Edge = CapsuleEdge.None;
        awaitingRingEntry = requireEntry; revealedPointer = readPointer(); dismissedPointer = revealedPointer;
        if (!animate || !IsVisible || !SystemParameters.ClientAreaAnimation) { SetCompact(); StartDockWatch(); return; }
        AnimateEdge(false, compact, monitor.Work, dpi, initial);
    }
    private void HideToEdge()
    {
        if (!MayHide()) return;
        awaitingRingEntry = false; revealedPointer = null; dismissedPointer = null;
        var compact = InitialCompactBounds(); var dpi = VisualTreeHelper.GetDpi(this);
        AnimateEdge(true, compact, MonitorAt(compact).Work, dpi);
    }
    private void AnimateEdge(bool hide, Rect compact, Rect work, DpiScale dpi, Rect? initial = null)
    {
        var tab = CapsulePlacement.Indicator(compact, work, DockEdge, dpi);
        if (tab.IsEmpty) { SetCompact(); return; }
        var host = PixelBounds;
        Rect Local(Rect r) => LocalBounds(r, host, dpi);
        var offscreen = compact;
        switch (DockEdge)
        {
            case CapsuleEdge.Left: offscreen.X = work.Left - compact.Width; break;
            case CapsuleEdge.Right: offscreen.X = work.Right; break;
            case CapsuleEdge.Top: offscreen.Y = work.Top - compact.Height; break;
            case CapsuleEdge.Bottom: offscreen.Y = work.Bottom; break;
        }
        void Done()
        {
            StopAnimation(); edgeAnimating = false;
            if (hide) { Surface.Edge = DockEdge; Surface.DockClip = Local(work); Surface.AnimationBounds = Local(tab); Surface.Redraw(); }
            else { SetCompact(); revealedPointer = readPointer(); }
            StartDockWatch();
        }
        if (!SystemParameters.ClientAreaAnimation) { Done(); return; }
        edgeAnimating = true; Surface.Edge = CapsuleEdge.None; Surface.Expansion = 0;
        var start = initial ?? (hide ? compact : offscreen); var end = hide ? offscreen : compact;
        Surface.DockClip = Local(work); Surface.AnimationBounds = Local(start); Surface.Redraw();
        var watch = Stopwatch.StartNew(); TimeSpan previous = TimeSpan.MinValue;
        animation = (_, args) =>
        {
            if (args is not RenderingEventArgs frame || frame.RenderingTime == previous) return;
            previous = frame.RenderingTime;
            // A real return during withdrawal reverses from the current circle position.
            if (hide && PointerNearCompact()) { RevealEdge(); return; }
            double progress = Math.Clamp(watch.Elapsed.TotalMilliseconds / (hide ? 220 : 160), 0, 1);
            double t = 1 - Math.Pow(1 - progress, 3);
            Surface.AnimationBounds = Local(new(start.X + (end.X - start.X) * t, start.Y + (end.Y - start.Y) * t, compact.Width, compact.Height));
            Surface.Redraw(); if (progress >= 1) Done();
        };
        CompositionTarget.Rendering += animation;
    }
}
