using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CodexUsage;

internal enum TrayClickAction { None, ToggleDetail, OpenMain, Menu }

// Input-only state: a raw release and its version-4 selection notification are
// one gesture. A new mouse down, keyboard selection or hide ends old suppression.
internal sealed class TrayClickState(int doubleClickMilliseconds)
{
    internal bool PendingClick { get; private set; }
    private bool mouseDown, acceptDismissDown, suppressRelease, ignoreRelease;
    private int lastRelease;
    private long lastReleaseAt, pendingAt;
    internal void Dismissed(bool iconMousePress)
    {
        suppressRelease = iconMousePress;
        acceptDismissDown = iconMousePress && !mouseDown;
    }
    internal void Reset()
    {
        PendingClick = mouseDown = acceptDismissDown = suppressRelease = ignoreRelease = false;
        lastRelease = 0;
    }
    internal TrayClickAction CompleteClick()
    {
        if (!PendingClick) return TrayClickAction.None;
        PendingClick = false; return TrayClickAction.ToggleDetail;
    }
    internal TrayClickAction Handle(int notification, bool available, long now)
    {
        if (!available) { Reset(); return TrayClickAction.None; }
        if (notification == 0x201)
        {
            if (!acceptDismissDown) suppressRelease = false;
            acceptDismissDown = false; mouseDown = true; lastRelease = 0; ignoreRelease = false;
        }
        else if (notification == 0x203)
        {
            PendingClick = suppressRelease = acceptDismissDown = false;
            mouseDown = true; ignoreRelease = true; lastRelease = 0;
            return TrayClickAction.OpenMain;
        }
        else if (notification is 0x202 or 0x400)
        {
            if (!mouseDown && lastRelease != 0 && lastRelease != notification && now - lastReleaseAt <= doubleClickMilliseconds)
                return TrayClickAction.None;
            mouseDown = false; lastRelease = notification; lastReleaseAt = now;
            if (ignoreRelease) { ignoreRelease = false; return TrayClickAction.None; }
            if (suppressRelease) { suppressRelease = acceptDismissDown = PendingClick = false; return TrayClickAction.None; }
            if (PendingClick && now - pendingAt <= doubleClickMilliseconds)
            {
                PendingClick = false; return TrayClickAction.OpenMain;
            }
            PendingClick = true; pendingAt = now;
        }
        else if (notification == 0x401) { Reset(); return TrayClickAction.ToggleDetail; }
        else if (notification is 0x7b or 0x205) { Reset(); return TrayClickAction.Menu; }
        return TrayClickAction.None;
    }
}

internal sealed class Tray : IDisposable
{
    // https://learn.microsoft.com/windows/win32/api/shellapi/ns-shellapi-notifyicondataw
    internal const uint RichTooltipFlags = 1 | 2 | 4; // Keep NIF_TIP for accessibility; omit NIF_SHOWTIP for the version-4 rich popup.
    [StructLayout(LayoutKind.Sequential)] private struct Identifier { public uint Size; public IntPtr Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Data
    {
        public uint Size; public IntPtr Window; public uint Id, Flags, Callback; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref Data data);
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref Identifier icon, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string text);
    private readonly HwndSource source;
    private readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private Data data;
    private bool visible;
    private bool desired;
    private bool disposed;
    private readonly TrayHoverState hover = new();
    private TrayPreview? preview;
    private TrayDetailWindow? detail;
    private System.Windows.Point lastAnchor;
    private readonly TrayClickState clicks;
    private JsonObject snapshot = new();
    private string iconMarker = "";
    internal bool PreviewVisible => preview?.IsVisible == true;
    internal TrayPreview? PreviewWindow => preview;
    internal bool IconVisible => visible;
    internal bool IconBoundsResolved { get; private set; }
    internal bool PendingClick => clicks.PendingClick;
    private readonly DispatcherTimer clickTimer = new();
    public Action? OpenMain, RefreshData, OpenSettings;
    internal Action<string?>? OpenMonitor;
    internal Action? OpenMonitorSettings;
    internal Func<JsonObject, Task<JsonObject>>? MonitorRequest;
    internal bool IsViewingMonitor(string taskID) => detail?.IsViewingMonitor(taskID) == true;
    public Func<ContextMenu>? Menu;
    private ContextMenu? openMenu;
    public Tray()
    {
        source = new HwndSource(new HwndSourceParameters("Codex Usage Tray") { Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000) });
        source.AddHook(Message);
        data = new Data { Size = (uint)Marshal.SizeOf<Data>(), Window = source.Handle, Id = 1, Callback = 0x8001, Flags = RichTooltipFlags, Tip = "Codex 用量", Info = "", Title = "" };
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var track = new Pen(System.Drawing.Color.FromArgb(65, 86, 76), 4); using var arc = new Pen(System.Drawing.Color.FromArgb(53, 222, 148), 4) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawEllipse(track, 4, 4, 24, 24); g.DrawArc(arc, 4, 4, 24, 24, -90, 270);
            using var brush = new SolidBrush(System.Drawing.Color.FromArgb(87, 230, 178)); g.FillEllipse(brush, 12, 12, 8, 8);
        }
        data.Icon = bitmap.GetHicon();
        int doubleClick = System.Windows.Forms.SystemInformation.DoubleClickTime;
        clicks = new TrayClickState(doubleClick); clickTimer.Interval = TimeSpan.FromMilliseconds(doubleClick);
        clickTimer.Tick += (_, _) => { clickTimer.Stop(); ApplyClick(clicks.CompleteClick()); };
    }
    public void SetVisible(bool value)
    {
        if (disposed && value) return;
        desired = value;
        if (!value) { ClosePreview(); detail?.Dismiss(); clickTimer.Stop(); clicks.Reset(); openMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false); }
        if (value && !visible) { data.Flags = RichTooltipFlags; visible = Shell_NotifyIcon(0, ref data); if (visible) { data.Version = 4; Shell_NotifyIcon(4, ref data); } }
        else if (!value && visible) { Shell_NotifyIcon(2, ref data); visible = false; }
    }
    public void Update(string title, JsonObject state)
    {
        if (disposed) return;
        snapshot = state;
        if (preview?.IsVisible == true) preview.Update(snapshot);
        if (detail?.IsVisible == true) detail.Update(snapshot);
        var summary = state.O("monitor").O("summary");
        string marker = summary.I("attention") > 0 ? "attention" : summary.I("unread") > 0 ? "unread" : "";
        bool iconChanged = marker != iconMarker;
        if (iconChanged) UpdateIcon(marker);
        string text = title.Length > 127 ? title[..127] : title;
        if (text == data.Tip && visible == desired && !iconChanged) return;
        data.Tip = text;
        if (desired && !visible) SetVisible(true);
        if (visible) { data.Flags = iconChanged ? 6u : 4u; Shell_NotifyIcon(1, ref data); }
    }
    private void UpdateIcon(string marker)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var track = new Pen(System.Drawing.Color.FromArgb(65, 86, 76), 4);
            using var arc = new Pen(System.Drawing.Color.FromArgb(53, 222, 148), 4) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawEllipse(track, 4, 4, 24, 24); g.DrawArc(arc, 4, 4, 24, 24, -90, 270);
            using var green = new SolidBrush(System.Drawing.Color.FromArgb(87, 230, 178)); g.FillEllipse(green, 12, 12, 8, 8);
            if (marker.Length > 0)
            {
                using var ink = new SolidBrush(marker == "attention" ? System.Drawing.Color.FromArgb(245, 185, 65) : System.Drawing.Color.FromArgb(98, 172, 255));
                using var outline = new Pen(System.Drawing.Color.FromArgb(24, 35, 28), 2);
                if (marker == "attention") { Point[] points = [new(25, 18), new(31, 24), new(25, 30), new(19, 24)]; g.FillPolygon(ink, points); g.DrawPolygon(outline, points); }
                else { g.FillEllipse(ink, 20, 20, 11, 11); g.DrawEllipse(outline, 20, 20, 11, 11); }
            }
        }
        var previous = data.Icon; data.Icon = bitmap.GetHicon(); iconMarker = marker;
        if (previous != IntPtr.Zero) DestroyIcon(previous);
    }
    public void ShowMenu()
    {
        ClosePreview(); detail?.Dismiss(); clicks.Reset(); clickTimer.Stop();
        if (disposed || Menu is null) return; openMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        openMenu = Menu(); SetForegroundWindow(source.Handle); openMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint; openMenu.IsOpen = true;
    }
    private void ClosePreview() { hover.Close(); preview?.Hide(); }
    private void OpenPreview(System.Windows.Point anchor)
    {
        if (detail?.IsVisible == true) return;
        preview ??= new TrayPreview(); preview.ShowAt(snapshot, IconBounds(anchor));
    }
    private System.Windows.Rect IconBounds(System.Windows.Point anchor)
    {
        var identifier = new Identifier { Size = (uint)Marshal.SizeOf<Identifier>(), Window = source.Handle, Id = data.Id };
        IconBoundsResolved = Shell_NotifyIconGetRect(ref identifier, out var rect) == 0 && rect.Right > rect.Left && rect.Bottom > rect.Top;
        var bounds = IconBoundsResolved ?
            new System.Windows.Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top) : new System.Windows.Rect(anchor.X - 1, anchor.Y - 1, 2, 2);
        return bounds;
    }
    private void ToggleDetail(System.Windows.Point anchor)
    {
        if (disposed || !desired || !visible) return;
        ClosePreview(); openMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        if (detail?.IsVisible == true) { detail.Dismiss(); return; }
        EnsureDetail().ShowAt(snapshot, IconBounds(anchor));
    }
    internal TrayDetailWindow EnsureDetail()
    {
        if (detail != null) return detail;
        var created = new TrayDetailWindow { OpenMain = () => OpenMain?.Invoke(), RefreshData = () => RefreshData?.Invoke(), OpenSettings = () => OpenSettings?.Invoke(),
            OpenMonitor = id => OpenMonitor?.Invoke(id), OpenMonitorSettings = () => OpenMonitorSettings?.Invoke(), MonitorRequest = request => MonitorRequest?.Invoke(request) ?? Task.FromResult(snapshot), Dismissed = clicks.Dismissed };
        created.Closed += (_, _) => { if (ReferenceEquals(detail, created)) detail = null; };
        return detail = created;
    }
    private IntPtr Message(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (disposed) return IntPtr.Zero;
        if ((uint)msg == taskbarCreated) { ClosePreview(); visible = false; SetVisible(desired); }
        if (msg == 0x8001) handled = Dispatch(TrayCallback.Decode(wParam, lParam));
        return IntPtr.Zero;
    }
    internal bool Dispatch(TrayCallback notification)
    {
        if (disposed || notification.IconId != data.Id) return false;
        int action = notification.Event;
        bool wasOpen = hover.IsOpen;
        hover.Handle(action, visible && desired, openMenu?.IsOpen == true || clickTimer.IsEnabled || detail?.IsVisible == true);
        if (action == 0x406 && hover.IsOpen) OpenPreview(notification.Anchor);
        else if (wasOpen && !hover.IsOpen) preview?.Hide();
        lastAnchor = notification.Anchor;
        var click = clicks.Handle(action, visible && desired, Environment.TickCount64);
        if (clicks.PendingClick) { if (!clickTimer.IsEnabled) clickTimer.Start(); } else clickTimer.Stop();
        ApplyClick(click);
        return true;
    }
    private void ApplyClick(TrayClickAction click)
    {
        if (disposed || !visible || !desired) return;
        if (click == TrayClickAction.OpenMain) { detail?.Dismiss(); openMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false); OpenMain?.Invoke(); }
        else if (click == TrayClickAction.ToggleDetail) ToggleDetail(lastAnchor);
        else if (click == TrayClickAction.Menu) ShowMenu();
    }
    public void Dispose() { if (disposed) return; disposed = true; clickTimer.Stop(); SetVisible(false); preview?.Close(); preview = null; detail?.Close(); detail = null; source.Dispose(); if (data.Icon != IntPtr.Zero) { DestroyIcon(data.Icon); data.Icon = IntPtr.Zero; } }
}
