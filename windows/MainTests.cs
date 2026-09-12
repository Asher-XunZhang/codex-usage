using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexUsage;

internal static class MainTests
{
    public static void Run()
    {
        Task.Run(MainStateTests.RunAsync).GetAwaiter().GetResult();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("Main panel test: " + name); }
        CheckForegroundTransfer(Check);
        string Exact(string json) => UsageNumbers.Exact(JsonNode.Parse(json));
        Check(Exact("9007199254740993") == "9,007,199,254,740,993", "tooltip preserves integers above JavaScript/double precision");
        Check(Exact("18446744073709551615") == "18,446,744,073,709,551,615", "tooltip preserves UInt64 maximum");
        Check(Exact("null") == "未知" && Exact("false") == "未知" && Exact("\"10\"") == "未知", "unknown and nonnumeric data never become zero");
        Check(Exact("-1") == "未知" && Exact("0.1") == "未知", "invalid counts remain unknown");
        Check(Exact("0") == "0" && Exact("12e3") == "12,000", "zero and exact scientific notation");
        Check(UsageNumbers.Exact(JsonValue.Create(12)) == "12", "native integer values");
        Check(UsageNumbers.Exact(JsonValue.Create(ulong.MaxValue)) == "18,446,744,073,709,551,615", "native unsigned integer values");
        var size = new Size(964, 180); var plot = TrendView.Plot(size);
        foreach (int count in new[] { 1, 7, 30, 90, 10000 })
        {
            Check(TrendView.IndexAt(new Point(plot.Left, plot.Top), size, count) == 0, "first date is reachable");
            Check(TrendView.IndexAt(new Point(plot.Right - .001, plot.Bottom - .001), size, count) == count - 1, "last date is reachable");
            foreach (int index in new[] { 0, count / 2, count - 1 }) Check(TrendView.IndexAt(new Point(plot.Left + (index + .5) * plot.Width / count, plot.Top + 10), size, count) == index, "hover matches the drawn day at every density");
            Check(TrendView.IndexAt(new Point(plot.Right, plot.Top + 10), size, count) == null, "right edge does not claim an outside day");
        }
        Check(TrendView.IndexAt(new Point(plot.Left + 5, plot.Top - 1), size, 7) == null, "axis labels do not show a day");
        Check(TrendView.IndexAt(new Point(plot.Left + 5, plot.Top + 1), size, 0) == null, "empty charts do not expose fake values");
        Check(TrendView.IndexAt(new Point(48, 12), new Size(40, 30), 7) == null, "collapsed charts are safe");
        string query = DashboardClient.Query("30", "model&other=bad", "中文 / a+b", "task");
        Check(query.Contains("model=model%26other%3Dbad") && query.Contains("task=%E4%B8%AD%E6%96%87%20%2F%20a%2Bb"), "filter identities cannot alter other URL parameters");
        if (Application.Current != null) { CheckFailurePresentation(Check); CheckBudgetUpdates(Check); CheckLayouts(); }
        Console.WriteLine("Main panel tests passed: exact tooltip values, hover geometry, unknown counts, query encoding.");
    }

    internal static void CheckForegroundTransfer(Action<bool, string> check)
    {
        int grants = 0, target = 0;
        bool Grant(int pid) { grants++; target = pid; return true; }
        check(ForegroundTransfer.TryGrantHost(200, 200, 300, 300, false, _ => true, Grant) && grants == 1 && target == 200,
            "an explicit foreground helper can grant only its verified parent host");
        foreach (var sample in new[] { (0, 200, 300u, false), (1, 1, 300u, false), (201, 200, 300u, false), (300, 300, 300u, false), (200, 200, 900u, false), (200, 200, 300u, true) })
            check(!ForegroundTransfer.TryGrantHost(sample.Item1, sample.Item2, 300, sample.Item3, sample.Item4, _ => true, Grant),
                "invalid, unrelated, background or self targets never receive foreground permission");
        check(!ForegroundTransfer.TryGrantHost(200, 200, 300, 300, false, _ => false, Grant) && grants == 1,
            "an exited or mismatched host cannot trigger the native grant");
        check(!ForegroundTransfer.TryGrantHost(200, 200, 300, 300, false, _ => true, _ => false),
            "Windows permission denial is reported without activation workarounds");
        check(ForegroundTransfer.HostMatches(@"C:\App\CodexUsage.exe", @"c:\app\CodexUsage.exe", 1, 1) &&
            !ForegroundTransfer.HostMatches(@"C:\Other\CodexUsage.exe", @"C:\App\CodexUsage.exe", 1, 1) &&
            !ForegroundTransfer.HostMatches(@"C:\App\CodexUsage.exe", @"C:\App\CodexUsage.exe", 2, 1) &&
            !ForegroundTransfer.HostMatches(null, @"C:\App\CodexUsage.exe", 1, 1),
            "foreground handoff requires the same executable path and desktop session");
    }

    // Offscreen visual roots use real WPF DPI rounding without creating a desktop
    // window, moving the pointer, or starting a host/backend.
    public static JsonObject CheckLayouts(string? directory = null)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        bool originalTheme = Theme.Dark;
        var samples = new JsonArray();
        try
        {
            foreach (bool dark in new[] { false, true })
                foreach (double dpi in new[] { 1d, 1.25, 1.5 })
                {
                    Theme.Apply(dark);
                    var main = new MainWindow(DemoData.State());
                    main.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    var content = (FrameworkElement)main.Content; main.Content = null;
                    var root = new Border { Child = content, Background = Theme.Background, UseLayoutRounding = true };
                    TextElement.SetFontFamily(root, new FontFamily("Segoe UI, Microsoft YaHei UI")); TextElement.SetFontSize(root, 13);
                    VisualTreeHelper.SetRootDpi(root, new DpiScale(dpi, dpi));
                    // Reserve 16 x 40 DIPs for the native frame at the 1040 x 718 minimum.
                    var available = new Size(1024, 678);
                    void Layout()
                    {
                        root.Measure(available); root.Arrange(new Rect(available)); root.UpdateLayout();
                        root.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                        VisualTreeHelper.SetRootDpi(root, new DpiScale(dpi, dpi));
                        root.UpdateLayout();
                    }
                    void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException($"Main layout ({(dark ? "dark" : "light")}, {dpi:P0}): {message}"); }
                    Rect Bounds(FrameworkElement element, Visual relative) => element.TransformToAncestor(relative).TransformBounds(new Rect(element.RenderSize));
                    void Contained(FrameworkElement element, FrameworkElement parent, string name)
                    {
                        var box = Bounds(element, parent); const double tolerance = .81;
                        Check(box.Left >= -tolerance && box.Top >= -tolerance && box.Right <= parent.ActualWidth + tolerance && box.Bottom <= parent.ActualHeight + tolerance,
                            $"{name} stays inside {parent.GetType().Name}: {box} / {parent.RenderSize}");
                    }
                    try
                    {
                        Layout();
                        foreach (var segment in Visuals<SegmentedGroup>(root)) segment.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                        Layout();
                        Check(Math.Abs(VisualTreeHelper.GetDpi(root).DpiScaleX - dpi) < .0001, "requested DPI is applied to the visual root");
                        var footerButton = Controls<Button>(root).Single(button => Equals(button.Content, "统计说明"));
                        var footer = (FrameworkElement)footerButton.Parent;
                        Contained(footerButton, footer, "coverage button"); Contained(footer, (FrameworkElement)footer.Parent, "footer in its scrollable page");
                        Check(footerButton.ActualHeight >= footerButton.MinHeight && footerButton.ActualHeight >= 30, "coverage button keeps its full minimum height");
                        var search = Controls<TextBox>(root).Single(box => AutomationProperties.GetName(box) == "搜索明细");
                        Contained(search, (FrameworkElement)search.Parent, "search");
                        var chrome = (Border)search.Template.FindName("SearchChrome", search);
                        Check(Math.Abs(chrome.CornerRadius.TopLeft - chrome.ActualHeight / 2) <= .81 && search.ActualWidth > search.ActualHeight * 6, "search keeps a long pill outline");
                        var placeholder = (TextBlock)search.Template.FindName("Placeholder", search);
                        Check(placeholder.Visibility == Visibility.Visible, "empty search has a placeholder");
                        var table = Controls<DataGrid>(root).Single();
                        var headers = Visuals<DataGridColumnHeader>(table).Where(header => header.Column != null).ToArray();
                        Check(headers.Length == 6, "all column headers are realized");
                        var inputHeader = headers.Single(header => header.Column.SortMemberPath == "input_tokens");
                        // Drive the native header input handler without requiring a visible
                        // UI Automation tree or synthesizing desktop mouse/keyboard input.
                        void InvokeHeader(DataGridColumnHeader header) => typeof(DataGridColumnHeader).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(header, null);
                        InvokeHeader(inputHeader); Layout();
                        Check(inputHeader.Column.SortDirection == ListSortDirection.Descending, "native header invocation sorts descending; actual " + inputHeader.Column.SortDirection + "; sort allowed " + inputHeader.Column.CanUserSort);
                        string FirstInput() => ((TextBlock)Visuals<DataGridCell>(Visuals<DataGridRow>(table).First()).Single(cell => cell.Column == inputHeader.Column).Content).Text;
                        string descendingFirst = FirstInput();
                        InvokeHeader(inputHeader); Layout();
                        Check(inputHeader.Column.SortDirection == ListSortDirection.Ascending && FirstInput() != descendingFirst, "second header invocation reverses the row order");
                        var totalHeader = headers.Single(header => header.Column.SortMemberPath == "total_tokens");
                        InvokeHeader(totalHeader); Layout();
                        var grip = (Thumb)inputHeader.Template.FindName("PART_RightHeaderGripper", inputHeader);
                        var originalWidth = inputHeader.Column.Width; double beforeResize = inputHeader.Column.ActualWidth;
                        grip.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                        grip.RaiseEvent(new DragDeltaEventArgs(24, 0) { RoutedEvent = Thumb.DragDeltaEvent }); Layout();
                        grip.RaiseEvent(new DragCompletedEventArgs(24, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
                        Check(Math.Abs(inputHeader.Column.ActualWidth - beforeResize - 24) <= 1, "native header grip changes the column width");
                        inputHeader.Column.Width = originalWidth; Layout();
                        var row = Visuals<DataGridRow>(table).First();
                        var alignment = new JsonArray();
                        foreach (var header in headers)
                        {
                            var column = header.Column;
                            var cell = Visuals<DataGridCell>(row).Single(candidate => candidate.Column == column);
                            var value = (TextBlock)cell.Content;
                            var label = (ContentPresenter)header.Template.FindName("HeaderLabel", header);
                            Check(header.Template.FindName("PART_LeftHeaderGripper", header) is Thumb && header.Template.FindName("PART_RightHeaderGripper", header) is Thumb,
                                "column resizing retains both native header grips");
                            Check(column.CanUserSort && column.CanUserResize && header.Focusable, "native sort, resize, and keyboard focus remain enabled");
                            double largestDelta = 0;
                            foreach (ListSortDirection? sort in new ListSortDirection?[] { null, ListSortDirection.Ascending, ListSortDirection.Descending })
                            {
                                column.SortDirection = sort; Layout();
                                var titleBounds = Bounds(label, table); var textBounds = Bounds(value, table);
                                double delta = column.DisplayIndex == 0 ? titleBounds.Left - textBounds.Left : titleBounds.Right - textBounds.Right;
                                largestDelta = Math.Max(largestDelta, Math.Abs(delta));
                                Check(Math.Abs(delta) * dpi <= 1.01, $"header and cell align within one pixel for {column.Header}, {sort}: {delta:F3} DIPs; header={Bounds(header, table)}, cell={Bounds(cell, table)}, headerDpi={VisualTreeHelper.GetDpi(header).DpiScaleX}, cellDpi={VisualTreeHelper.GetDpi(cell).DpiScaleX}");
                            }
                            alignment.Add(J.Obj(("column", column.Header?.ToString()), ("maxDeltaPixels", largestDelta * dpi)));
                        }
                        foreach (var column in table.Columns) column.SortDirection = column.SortMemberPath == "total_tokens" ? ListSortDirection.Descending : null;
                        Layout();
                        string imageName = $"main-min-{(dark ? "dark" : "light")}-{dpi * 100:0}.png";
                        if (directory is not null)
                        {
                            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(available.Width * dpi), (int)Math.Ceiling(available.Height * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
                            bitmap.Render(root); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            using var file = File.Create(Path.Combine(directory, imageName)); encoder.Save(file);
                        }
                        var buttonBounds = Bounds(footerButton, root);
                        samples.Add(J.Obj(("theme", dark ? "dark" : "light"), ("dpiScale", dpi), ("contentWidthDip", root.ActualWidth), ("contentHeightDip", root.ActualHeight),
                            ("footerButton", J.Obj(("x", buttonBounds.X), ("y", buttonBounds.Y), ("width", buttonBounds.Width), ("height", buttonBounds.Height))),
                            ("headerAlignment", alignment), ("sortAndResize", true), ("image", imageName)));
                        search.Text = "不存在的合成任务"; Layout();
                        Check(placeholder.Visibility == Visibility.Collapsed && table.Items.Count == 0, "typing replaces the placeholder and filters the table");
                        Contained(footerButton, footer, "coverage button with empty results"); Contained(footer, (FrameworkElement)footer.Parent, "footer with empty results");
                        search.Clear(); Layout(); Check(table.Items.Count > 0, "clearing search restores rows");
                    }
                    finally { root.Child = null; main.Close(); }
                }
        }
        finally { Theme.Apply(originalTheme); }
        var responsive = CheckResponsiveLayouts(directory);
        return J.Obj(("success", true), ("measurement", "Synthetic offscreen WPF visual roots at 1040 x 718 and the 760 x 640 minimum, reserving 16 x 40 DIPs for native chrome; real 100/125/150% DPI layout rounding. Fixed global controls, page wrapping, independent table scrolling. No desktop window or user data."),
            ("samples", samples), ("responsive", responsive));
    }

    private static JsonArray CheckResponsiveLayouts(string? directory)
    {
        var samples = new JsonArray(); bool originalTheme = Theme.Dark;
        try
        {
            foreach (bool dark in new[] { false, true }) foreach (double dpi in new[] { 1d, 1.25, 1.5 })
            {
                Theme.Apply(dark); var main = new MainWindow(DemoData.State()); main.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                var content = (FrameworkElement)main.Content; main.Content = null;
                var root = new Border { Child = content, Background = Theme.Background, UseLayoutRounding = true };
                TextElement.SetFontFamily(root, new FontFamily("Segoe UI, Microsoft YaHei UI")); TextElement.SetFontSize(root, 13); VisualTreeHelper.SetRootDpi(root, new DpiScale(dpi, dpi));
                var available = new Size(744, 600);
                void Layout() { root.Measure(available); root.Arrange(new Rect(available)); root.UpdateLayout(); root.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); VisualTreeHelper.SetRootDpi(root, new DpiScale(dpi, dpi)); root.UpdateLayout(); }
                object Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException($"Main responsive ({dark}, {dpi}): {message}"); }
                Rect Bounds(FrameworkElement element) => element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
                try
                {
                    Layout();
                    foreach (var segment in Visuals<SegmentedGroup>(root)) segment.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    Layout();
                    var settings = (Button)Field("settingsButton"); var mode = (Button)Field("displayMode");
                    var settingsBefore = Bounds(settings); var modeBefore = Bounds(mode);
                    Check(settingsBefore.Right <= available.Width && modeBefore.Left >= 0 && settingsBefore.Bottom < 80, "global controls fit the fixed top at the minimum width");
                    var range = (WrapPanel)Field("rangeControls"); var metrics = (UniformGrid)Field("metrics");
                    var firstRange = (FrameworkElement)range.Children[0]; var lastRange = (FrameworkElement)range.Children[^1];
                    Check(lastRange.TranslatePoint(new Point(), range).Y > firstRange.TranslatePoint(new Point(), range).Y && metrics.Columns == 3, "ranges wrap and metrics use three columns at narrow widths");
                    var scroll = (ScrollViewer)Field("usageScroller"); var table = (DataGrid)Field("table");
                    Check(scroll.ScrollableHeight > 0 && table.ActualHeight >= 104 && ScrollViewer.GetHorizontalScrollBarVisibility(table) == ScrollBarVisibility.Auto,
                        "short windows scroll page content while the table keeps its own bounded viewport and horizontal scroll");
                    string name = $"main-narrow-{(dark ? "dark" : "light")}-{dpi * 100:0}.png";
                    if (directory is not null)
                    {
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(available.Width * dpi), (int)Math.Ceiling(available.Height * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32); bitmap.Render(root);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(directory, name)); encoder.Save(file);
                    }
                    scroll.ScrollToEnd(); Layout();
                    Check(Bounds(settings) == settingsBefore && Bounds(mode) == modeBefore, "scrolling local results never moves global settings or display mode");
                    var coverage = Controls<Button>(root).Single(button => Equals(button.Content, "统计说明"));
                    Check(Bounds(coverage).Bottom <= available.Height + 1, "data explanation remains fully reachable at the end of the page");
                    typeof(MainWindow).GetMethod("SelectPage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, new object[] { "budget", false }); Layout();
                    Check(Bounds(settings) == settingsBefore && Bounds(mode) == modeBefore && settings.IsEnabled && mode.IsEnabled,
                        "switching to budgets retains global controls at exactly the same positions");
                    samples.Add(J.Obj(("theme", dark ? "dark" : "light"), ("dpiScale", dpi), ("image", name), ("fixedGlobals", true), ("wrap", true), ("separateTableScroll", true)));
                }
                finally { root.Child = null; main.Close(); }
            }
        }
        finally { Theme.Apply(originalTheme); }
        return samples;
    }

    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value) yield return value;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var descendant in Visuals<T>(VisualTreeHelper.GetChild(root, i))) yield return descendant;
    }

    private static IEnumerable<T> Controls<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value) yield return value;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Controls<T>(child)) yield return descendant;
    }
    private static void CheckFailurePresentation(Action<bool, string> check)
    {
        // Window.IsEnabled propagates into its content only after WPF connects the
        // window's visual root. Raising Loaded or measuring detached content cannot
        // establish that invariant. Use an invisible, nonactivating synthetic window.
        var main = new MainWindow(DemoData.State()) { Opacity = 0, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        object? Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main);
        object? Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(main, args);
        try
        {
            main.Show(); main.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); main.UpdateLayout();
            check(((JsonObject)Field("snapshot")!).O("summary").Count > 0, "synthetic panel starts with displayed usage");
            Call("SelectUsageIdentity");
            check(((JsonObject)Field("snapshot")!).Count == 0 && ((DataGrid)Field("table")!).Items.Count == 0 && !((Button)Field("export")!).IsEnabled,
                "a new query immediately hides previous cards/table and disables export");
            Call("SetStatus", "合成读取错误：请重试"); Call("SelectPage", "budget", false);
            check(((TextBlock)Field("sharedStatus")!).Text.Contains("合成读取错误"), "errors remain visible after switching to the budget page");
            var host = DemoData.State(); host["settingsError"] = "合成设置损坏"; Call("ApplyHost", host);
            check(((TextBlock)Field("sharedStatus")!).Text.Contains("Ctrl+,") && ((TextBlock)Field("sharedStatus")!).Text.Contains("合成设置损坏"),
                "budget page exposes settings recovery feedback and its keyboard entry");
            host["settingsError"] = ""; host["updates"] = J.Obj(("quota", J.Obj(("error", "合成账号更新失败")))); Call("ApplyHost", host);
            check(((TextBlock)Field("status")!).Text.Contains("合成账号更新失败") && ((TextBlock)Field("quotaText")!).Text.Contains("更新失败"),
                "quota source failures remain distinct from local usage feedback");
            main.UpdateLayout();
            check(VisualTreeHelper.GetParent((BudgetView)Field("budget")!) is not null,
                "the active budget view is realized in the window visual tree before testing inherited input state");
            var prepared = main.Handle(J.Obj(("action", "close")));
            check(prepared.IsCompletedSuccessfully && prepared.GetAwaiter().GetResult().B("closing") && !main.IsEnabled && !((BudgetView)Field("budget")!).IsEnabled,
                "a prepared close disables the actual window and active budget controls until its reply is delivered");
            main.CancelPreparedClose();
            check(main.IsEnabled && ((BudgetView)Field("budget")!).IsEnabled && !(bool)Field("closing")!
                && ((TextBlock)Field("sharedStatus")!).Text.Contains("关闭通信中断"),
                "a failed close reply restores user input and visible retry feedback without losing the saved window");
            var retried = main.Handle(J.Obj(("action", "close")));
            check(retried.IsCompletedSuccessfully && retried.GetAwaiter().GetResult().B("closing") && !main.IsEnabled,
                "the restored window can prepare a second close successfully");
        }
        finally { main.CancelPreparedClose(); main.Close(); main.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); }
    }
    private static void CheckBudgetUpdates(Action<bool, string> check)
    {
        var current = DemoData.State();
        var view = new BudgetView(_ => Task.FromResult(current.Copy()), _ => { });
        view.Update(current); var first = view.Content;
        for (int i = 0; i < 5; i++)
        {
            var unrelated = current.Copy(); unrelated["status"] = "轮询 " + i; unrelated["busy"] = i % 2 == 0;
            unrelated.O("quota")["updated_at"] = J.Now + i; unrelated.O("settings")["filterDays"] = "7";
            check(view.StateMatches(unrelated, current.O("choices")), "unrelated host state is ignored by budget controls");
            view.Update(unrelated);
            check(ReferenceEquals(first, view.Content), "idle budget updates retain existing controls");
        }
        current.O("budgets").A("summaries")[0]!["remaining"] = 100;
        view.Update(current); check(!ReferenceEquals(first, view.Content), "changed budget values update the presentation");
        view.Select("daily", true); var editor = view.Content;
        var name = Controls<TextBox>(view).Single(x => AutomationProperties.GetName(x) == "预算名称");
        name.Text = "尚未保存的名称";
        var tasks = Controls<ComboBox>(view).Single(x => AutomationProperties.GetName(x) == "任务");
        int count = tasks.Items.Count;
        current.O("choices").A("tasks").Add(J.Obj(("id", "arrived-later"), ("label", "异步到达的任务")));
        view.Update(current);
        check(ReferenceEquals(editor, view.Content) && name.Text == "尚未保存的名称" && tasks.Items.Count == count + 1,
              "new choices update the editor without replacing unsaved fields");
        var replyWithoutChoices = current.Copy(); replyWithoutChoices.Remove("choices");
        replyWithoutChoices.O("settings")["floating"] = J.Obj(("days", "90"));
        view.Update(replyWithoutChoices);
        check(tasks.Items.Count == count + 1 && view.StateMatches(replyWithoutChoices, current.O("choices")),
              "partial command replies retain choices and accept changed floating settings");
        view.Select("daily"); check(!view.IsEditing && view.SelectedId == "daily" && !ReferenceEquals(editor, view.Content),
              "explicit selection still changes presentation after idle caching");

        var draftState = current.Copy();
        draftState.O("settings")["budgetDraft"] = J.Obj(("rule", current.O("budgets").A("rules")[0]),
            ("fields", J.Obj(("name", "重启后恢复的草稿"))), ("prices", new JsonArray()));
        var restored = new BudgetView(_ => Task.FromResult(draftState.Copy()), _ => { });
        restored.Update(draftState);
        check(restored.IsEditing && Controls<TextBox>(restored).Single(x => AutomationProperties.GetName(x) == "预算名称").Text == "重启后恢复的草稿",
              "initial draft restoration is not skipped by state caching");
        restored.Select("daily");
    }
}
