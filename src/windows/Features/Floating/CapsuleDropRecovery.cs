using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace CodexUsage;

internal sealed partial class CapsuleWindow
{
    private bool releaseSettling;

    // The drop is committed before animation. Only the visible shape translates
    // to its accessible endpoint; no spring overshoot or persistent render loop.
    private bool AnimateDropRecovery(Rect start)
    {
        var end = panelBounds!.Value;
        double distance = (start.TopLeft - end.TopLeft).Length;
        if (distance < 1 || distance > Math.Max(end.Width, end.Height) ||
            !IsVisible || !SystemParameters.ClientAreaAnimation) return false;
        var dpi = VisualTreeHelper.GetDpi(this);
        var compact = InitialCompactBounds();
        var envelope = PixelEnvelope(Rect.Union(Rect.Union(start, end), compact));
        Surface.CompactBounds = LocalBounds(compact, envelope, dpi);
        Surface.PanelBounds = LocalBounds(end, envelope, dpi);
        SetHostBounds(envelope);
        Surface.AnimationBounds = LocalBounds(start, envelope, dpi);
        Surface.Redraw();
        releaseSettling = true; departure.Stop(); bridgeWatch.Stop(); hoverDelay.Stop();
        var watch = Stopwatch.StartNew(); TimeSpan previous = TimeSpan.MinValue;
        animation = (_, args) =>
        {
            if (args is not RenderingEventArgs frame || frame.RenderingTime == previous) return;
            previous = frame.RenderingTime;
            double progress = Math.Clamp(watch.Elapsed.TotalMilliseconds / 160, 0, 1);
            double t = 1 - Math.Pow(1 - progress, 3);
            var current = new Rect(start.X + (end.X - start.X) * t, start.Y + (end.Y - start.Y) * t,
                start.Width + (end.Width - start.Width) * t, start.Height + (end.Height - start.Height) * t);
            Surface.AnimationBounds = LocalBounds(current, envelope, dpi); Surface.Redraw();
            if (progress < 1) return;
            StopAnimation(); Surface.AnimationBounds = null; RebuildEnvelope();
            pointerTracking = PointerOverVisible(); pointerRetention = null;
            if (!KeepsExpanded && !Surface.KeyboardInteraction &&
                (!pointerTracking || readPointer() is Point p && !OwnsInteraction(p))) QueueDeparture();
        };
        CompositionTarget.Rendering += animation;
        return true;
    }
}
