using System;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;

namespace CodexUsage;

internal sealed partial class CapsuleWindow
{
    private bool expandedDragging;
    private Point expandedPressScreen, expandedLastScreen, expandedGripDip;
    // Keep the grip stable only for this expanded session. The next expansion
    // must choose its direction afresh from the compact anchor's available space.
    private Vector? panelOffsetDip;

    internal Point BeginExpandedDrag(Point screen)
    {
        NewFocusIntent(); manuallyCollapsed = false;
        hideDelay.Stop(); wakeDelay.Stop(); departure.Stop(); bridgeWatch.Stop(); dockWatch.Stop(); StopAnimation();
        edgeAnimating = false; awaitingRingEntry = false; DockEdge = CapsuleEdge.None;
        var dpi = VisualTreeHelper.GetDpi(this); var panel = VisualPixelBounds; var compact = InitialCompactBounds();
        expandedDragging = true; expandedPressScreen = expandedLastScreen = screen;
        expandedGripDip = new((screen.X - panel.X) / dpi.DpiScaleX, (screen.Y - panel.Y) / dpi.DpiScaleY);
        panelOffsetDip = new((panel.X - compact.X) / dpi.DpiScaleX, (panel.Y - compact.Y) / dpi.DpiScaleY);
        Surface.Edge = CapsuleEdge.None; Surface.DockClip = null; Surface.AnimationBounds = null;
        expanded = true; Surface.Expansion = 1; hotspot = null;
        return panel.TopLeft;
    }

    private void MoveExpandedDrag(Point screen)
    {
        expandedLastScreen = screen;
        var placement = CapsuleExpandedDragPlacement.AtPointer(screen, expandedGripDip, -(panelOffsetDip ?? new Vector()), VisualTreeHelper.GetDpi(this));
        compactBounds = placement.Compact; RebuildEnvelope();
    }

    private void FinishExpandedDrag(bool animate = true)
    {
        StopAnimation(); var dpi = VisualTreeHelper.GetDpi(this);
        var panel = panelBounds ?? VisualPixelBounds;
        var monitor = MonitorAt(new Rect(expandedLastScreen, new Size(1, 1)));
        var placed = CapsuleExpandedDragPlacement.Clamp(new(panel, InitialCompactBounds()), monitor.Work, dpi);
        // Detect the drop against the dragged panel BEFORE recovery shifts its
        // compact anchor away from the edge. Recovery must not erase dock intent.
        DockEdge = autoHide ? CapsuleExpandedDragPlacement.DropEdge(panel, expandedLastScreen, monitor, Monitors(), dpi, placed.Compact) : CapsuleEdge.None;
        compactBounds = CapsulePlacement.CompactAtEdge(placed.Compact, monitor.Work, DockEdge);
        panelOffsetDip = new((placed.Panel.X - compactBounds.Value.X) / dpi.DpiScaleX, (placed.Panel.Y - compactBounds.Value.Y) / dpi.DpiScaleY);
        expandedDragging = false; RebuildEnvelope();
        Surface.AnimationBounds = null; Surface.Edge = CapsuleEdge.None; Surface.DockClip = null;
        expanded = true; Surface.Expansion = 1; pressed = false;
        awaitingRingEntry = false; revealedPointer = null; dismissedPointer = null; hotspot = null;
        pointerTracking = PointerOverVisible(); pointerRetention = null;
        StartDockWatch();
        if (animate && AnimateDropRecovery(panel)) return;
        // Leave the panel intact on release. Normal departure may collapse it,
        // but finishing a drag must not change the Keep expanded preference.
        if (!KeepsExpanded && !Surface.KeyboardInteraction && !pointerTracking) QueueDeparture();
    }

    internal void CancelExpandedDrag()
    {
        // Escape/hide/display changes must not collapse toward a compact anchor
        // that was temporarily outside the work area during an expanded drag.
        if (expandedDragging) FinishExpandedDrag(false);
    }

    private void RestorePanelOffset(JsonObject floating)
    {
        // Ignore legacy persistent direction. It could strand the compact shape
        // away from the edge after recovering an offscreen expanded panel.
        panelOffsetDip = null;
    }
}
