using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CodexUsage;

public sealed class MainWindow : Window
{
    private sealed record Choice(string Id, string Label) { public override string ToString() => Label; }
    private sealed class UsageRow
    {
        public JsonObject Data { get; }
        public UsageRow(JsonObject row) { Data = row; }
        public string Label => Data.S("label", "未命名");
        public string Input => UsageNumbers.Exact(Data["input_tokens"]);
        public string Cached => UsageNumbers.Exact(Data["cached_input_tokens"]);
        public string Noncached => UsageNumbers.Exact(Data["noncached_input_tokens"]);
        public string Output => UsageNumbers.Exact(Data["output_tokens"]);
        public string Total => UsageNumbers.Exact(Data["total_tokens"]);
    }
    private sealed class Metric
    {
        public readonly TextBlock Value = Label("—", 28, true);
        public readonly TextBlock Detail = Label("等待本机统计", 11);
        public Border View { get; }
        public Metric(string title, Brush? color = null)
        {
            var stack = new StackPanel();
            stack.Children.Add(Label(title, 12)); Value.Margin = new Thickness(0, 3, 0, 2); stack.Children.Add(Value); stack.Children.Add(Detail);
            if (color != null) Value.Foreground = color;
            View = Panel(stack, 13); View.Margin = new Thickness(0, 0, 9, 0);
        }
        public void Update(JsonNode? value, string suffix = "tokens")
        {
            Value.Text = J.Compact(UsageNumbers.Count(value)); Detail.Text = UsageNumbers.Exact(value) + " " + suffix;
            View.ToolTip = Detail.Text; AutomationProperties.SetName(View, Detail.Text);
        }
    }

    private JsonObject state, snapshot = new(), budgetChoices = new();
    private readonly bool demo;
    private readonly DashboardClient client = new();
    private readonly MainUsageSession usageSession = new();
    private readonly MainSettingsQueue settingsQueue = new();
    private Task<bool>? usageLoad;
    private Task? preparingClose, completingClose;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer pollTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer settingsTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly SemaphoreSlim serviceGate = new(1, 1);
    private readonly ContentControl pageContent = new();
    private readonly ScrollViewer usageScroller = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false };
    private readonly UniformGrid metrics = new() { Columns = 5 };
    private readonly WrapPanel rangeControls = new(), detailControls = new();
    private readonly ToggleButton usageTab = new() { Content = "用量概览", MinWidth = 94 }, budgetTab = new() { Content = "预算", MinWidth = 74 }, monitorTab = new() { Content = "任务监控", MinWidth = 116 };
    private readonly TextBlock status = Label("正在准备本机统计…", 12), coverage = Label("仅统计这台电脑的已记录用量 · 日界线 UTC+08:00", 11);
    private readonly TextBlock sharedStatus = Label("本机用量统计", 11);
    private string statusMessage = "正在准备本机统计…";
    private readonly TextBlock quotaText = Label("正在读取账号额度…", 12), resetCards = Label("重置卡数量未知", 12, true), rowCount = Label("", 11);
    private readonly TextBlock weekQuota = Label("—", 24, true), shortQuota = Label("—", 24, true), weekReset = Label("周额度暂不可用", 11), shortReset = Label("短周期额度暂不可用", 11), updateTimes = Label("本机 — · 账号 —", 11);
    private readonly ComboBox period = Picker("时间范围"), models = Picker("模型筛选"), tasks = Picker("任务筛选");
    private readonly Dictionary<string, ToggleButton> periodSegments = new();
    private readonly ToggleButton modelGroup = new() { Content = "按模型", MinWidth = 70 }, taskGroup = new() { Content = "按任务", MinWidth = 70 };
    private readonly TextBox search = new() { Width = 300, MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "搜索模型或任务（仅影响窗口显示）" };
    private readonly Button refresh = new() { Content = "刷新", MinWidth = 68 }, export = new() { Content = "导出 CSV…", MinWidth = 96, IsEnabled = false }, displayMode = new() { Content = "显示方式", MinWidth = 104 }, settingsButton = new() { Content = "设置", MinWidth = 64 }, resetFilters = new() { Content = "重置筛选", MinWidth = 82 };
    private readonly TrendView trend = new();
    private readonly DataGrid table = new() { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, CanUserReorderColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.None, SelectionMode = DataGridSelectionMode.Single, RowHeight = 32, BorderThickness = new Thickness(0), EnableRowVirtualization = true, EnableColumnVirtualization = true, MinHeight = 86 };
    private readonly Metric totalCard = new("总 Token", new SolidColorBrush(Color.FromRgb(15, 140, 117))), inputCard = new("输入（含缓存）"), outputCard = new("输出（含推理）", new SolidColorBrush(Color.FromRgb(122, 102, 209))), cacheCard = new("其中缓存输入"), callsCard = new("模型调用 / 任务");
    private readonly Grid usage;
    private readonly BudgetView budget;
    private readonly TaskMonitorView monitor;
    private string days = "30", model = "all", task = "all", group = "model", page = "usage", currentHome = "", currentCache = "", stamp = "", publishedStamp = "", sortKey = "total_tokens";
    private bool sortAscending, applying, polling, refreshBusy, closed, closing, started, choicesBusy;
    private int refreshSeconds = 5, failures;
    private readonly HashSet<string> refreshIDs = new();
    private string? pendingBudget;
    private string viewingSignature = "", choicesStamp = "";
    private string monitorViewingSignature = "";
    private string? pendingMonitor, pendingMessage;
    private string? appliedThemePreference;

    public MainWindow(JsonObject initial, string? page = null, string? budgetId = null, string? monitorId = null, string? messageId = null)
    {
        state = initial.Copy(); demo = state.B("demo");
        Title = "Codex 用量"; Width = 1100; Height = 780; MinWidth = 760; MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "BackgroundBrush"); SetResourceReference(ForegroundProperty, "ForegroundBrush");
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 13;
        var settings = state.O("settings");
        days = new[] { "1", "7", "30", "90", "all" }.Contains(settings.S("filterDays")) ? settings.S("filterDays") : "30";
        model = settings.S("filterModel", "all"); task = settings.S("filterTask", "all"); group = settings.S("filterGroup") == "task" ? "task" : "model";
        string requestedPage = page ?? settings.S("mainPage", "usage");
        this.page = requestedPage is "budget" or "budgets" ? "budget" : requestedPage == "monitor" ? "monitor" : "usage";
        pendingBudget = budgetId;
        pendingMonitor = monitorId; pendingMessage = messageId;
        budget = new BudgetView(SendHost, SetStatus);
        monitor = new TaskMonitorView(SendHost, SetStatus);
        monitor.ViewingChanged += (_, _) => _ = PublishViewing();
        budgetChoices = initial.O("choices").Count > 0 ? initial.O("choices").Copy() : initial.O("usage").O("filters").Copy();
        budget.ViewingChanged += (_, _) => _ = PublishViewing();
        usage = BuildUsage();
        usageScroller.Content = usage;
        usageScroller.SizeChanged += (_, _) => ResizeUsage();
        usageScroller.ScrollChanged += (_, e) => { if (e.ViewportHeightChange != 0 || e.ViewportWidthChange != 0) ResizeUsage(); };
        var root = new DockPanel { Margin = new Thickness(22, 14, 22, 14), LastChildFill = true };
        var navigation = new DockPanel { MinHeight = 36, Margin = new Thickness(0, 0, 0, 6) };
        var globalActions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        displayMode.Margin = new Thickness(0, 0, 8, 0); globalActions.Children.Add(displayMode); globalActions.Children.Add(settingsButton);
        DockPanel.SetDock(globalActions, Dock.Right); navigation.Children.Add(globalActions);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        tabs.Children.Add(usageTab); tabs.Children.Add(budgetTab); tabs.Children.Add(monitorTab);
        var tabGroup = SegmentGroup(tabs); DockPanel.SetDock(tabGroup, Dock.Left); navigation.Children.Add(tabGroup);
        var brand = Label("Codex 用量", 15, true); brand.Margin = new Thickness(12, 0, 0, 0); navigation.Children.Add(brand);
        DockPanel.SetDock(navigation, Dock.Top); root.Children.Add(navigation);
        sharedStatus.Margin = new Thickness(0, 0, 0, 7); sharedStatus.MinHeight = 17; DockPanel.SetDock(sharedStatus, Dock.Top); root.Children.Add(sharedStatus);
        root.Children.Add(pageContent); Content = root;
        displayMode.Click += (_, _) => ShowDisplayModes(); settingsButton.Click += async (_, _) => await OpenSettings();
        usageTab.Click += (_, _) => SelectPage("usage"); budgetTab.Click += (_, _) => SelectPage("budget"); monitorTab.Click += (_, _) => SelectPage("monitor");
        settingsTimer.Tick += async (_, _) => { settingsTimer.Stop(); await FlushSettings(); };
        pollTimer.Tick += async (_, _) => await Poll();
        Loaded += async (_, _) =>
        {
            ApplyHost(state); SelectPage(this.page, false);
            if (pendingBudget != null) { budget.Select(pendingBudget); pendingBudget = null; }
            if (pendingMonitor != null || pendingMessage != null) { SelectPage("monitor", false); monitor.Select(pendingMonitor ?? "", pendingMessage ?? ""); pendingMonitor = pendingMessage = null; }
            if (demo) { Render(state.O("usage")); return; }
            started = true; pollTimer.Start();
            await EnsureService(); await LoadUsage();
        };
        Closing += OnClosing;
        Activated += (_, _) => _ = PublishViewing();
        Deactivated += (_, _) => { trend.Dismiss(); _ = PublishViewing(); };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) trend.Dismiss(); _ = PublishViewing(); };
        PreviewKeyDown += OnKey;
        if (!demo) SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
        RestoreFrame(settings.O("mainWindow"));
    }

    private static TextBlock Label(string text, double size = 13, bool bold = false)
    {
        var field = new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        field.SetResourceReference(TextBlock.ForegroundProperty, bold ? "ForegroundBrush" : "SecondaryBrush"); return field;
    }
    private static Border Panel(UIElement child, double padding = 14)
    {
        var border = new Border { Child = child, CornerRadius = new CornerRadius(13), Padding = new Thickness(padding), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(34, 128, 128, 128)) };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); return border;
    }
    private static Border SegmentGroup(System.Windows.Controls.Panel child) => new SegmentedGroup(child);
    private static ComboBox Picker(string name)
    {
        var control = new ComboBox { SelectedValuePath = "Id", MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 420 };
        AutomationProperties.SetName(control, name); control.ToolTip = name;
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding("Label")); text.SetBinding(FrameworkElement.ToolTipProperty, new Binding("Label"));
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); text.SetValue(FrameworkElement.MaxWidthProperty, 580d);
        control.ItemTemplate = new DataTemplate { VisualTree = text };
        var style = new Style(typeof(ComboBoxItem), Application.Current.TryFindResource(typeof(ComboBoxItem)) as Style);
        style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding("Label")));
        control.ItemContainerStyle = style;
        return control;
    }
    private Grid BuildUsage()
    {
        var layout = new Grid();
        for (int i = 0; i < 9; i++) layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions[5].Height = new GridLength(1.05, GridUnitType.Star); layout.RowDefinitions[5].MinHeight = 114;
        layout.RowDefinitions[7].Height = new GridLength(1, GridUnitType.Star); layout.RowDefinitions[7].MinHeight = 104;
        void Add(UIElement element, int row) { Grid.SetRow(element, row); layout.Children.Add(element); }
        var account = new Grid();
        foreach (var fraction in new[] { 1.05, 1.05, 1.25 }) account.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fraction, GridUnitType.Star) });
        StackPanel AccountValue(TextBlock number, TextBlock note, string label)
        {
            var column = new StackPanel(); var line = new StackPanel { Orientation = Orientation.Horizontal };
            number.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush"); line.Children.Add(number); var suffix = Label(label, 12); suffix.Margin = new Thickness(8, 0, 0, 0); line.Children.Add(suffix);
            column.Children.Add(line); column.Children.Add(note); return column;
        }
        var week = AccountValue(weekQuota, weekReset, "周剩余"); var shortWindow = AccountValue(shortQuota, shortReset, "短周期剩余");
        Grid.SetColumn(shortWindow, 1); account.Children.Add(week); account.Children.Add(shortWindow);
        var accountNotes = new StackPanel(); accountNotes.Children.Add(resetCards); accountNotes.Children.Add(quotaText);
        quotaText.FontSize = 10; quotaText.Text = "账号级额度 · 不受本机筛选影响";
        resetCards.ToolTip = "只读显示，不提供兑换或使用重置卡的操作";
        Grid.SetColumn(accountNotes, 2); account.Children.Add(accountNotes);
        AutomationProperties.SetName(account, "账号额度摘要，不受本地筛选影响");
        var accountPanel = Panel(account, 10); accountPanel.Margin = new Thickness(0, 0, 0, 7); Add(accountPanel, 0);

        var update = new DockPanel { MinHeight = 40, Margin = new Thickness(0, 0, 0, 4) };
        var updateActions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        updateTimes.Margin = new Thickness(0, 0, 12, 0); updateTimes.TextWrapping = TextWrapping.Wrap; updateActions.Children.Add(updateTimes);
        refresh.ToolTip = "分别更新本机日志与账号额度"; updateActions.Children.Add(refresh);
        var updateSettings = new Button { Content = "更新设置", MinWidth = 82, Margin = new Thickness(8, 0, 0, 0) };
        updateActions.Children.Add(updateSettings); DockPanel.SetDock(updateActions, Dock.Right); update.Children.Add(updateActions);
        update.Children.Add(Label("本机用量", 19, true)); Add(update, 1);
        AutomationProperties.SetName(updateTimes, "本机与账号各自的更新时间");
        updateSettings.Click += async (_, _) => await OpenSettings("updates");

        var periods = new[] { new Choice("1", "今天"), new Choice("7", "7 天"), new Choice("30", "30 天"), new Choice("90", "90 天"), new Choice("all", "全部") };
        period.ItemsSource = periods; period.SelectedValue = days;
        var periodBar = new UniformGrid { Columns = 5 };
        foreach (var choice in periods)
        {
            var button = new ToggleButton { Content = choice.Label, FontSize = 12, Padding = new Thickness(6, 2, 6, 2), MinHeight = 26, IsChecked = choice.Id == days };
            button.Click += (_, _) => { if (period.SelectedValue as string != choice.Id) period.SelectedValue = choice.Id; };
            periodSegments[choice.Id] = button; periodBar.Children.Add(button);
        }
        models.ItemsSource = new[] { new Choice("all", "全部模型") }; models.SelectedValue = "all";
        tasks.ItemsSource = new[] { new Choice("all", "全部任务") }; tasks.SelectedValue = "all";
        var periodGroup = SegmentGroup(periodBar); periodGroup.Width = 248; models.Width = 174; tasks.Width = 238;
        foreach (var control in new FrameworkElement[] { periodGroup, models, tasks, resetFilters }) { control.Margin = new Thickness(0, 0, 8, 5); rangeControls.Children.Add(control); }
        AutomationProperties.SetName(rangeControls, "本机统计范围"); Add(rangeControls, 2);
        var scope = Label("UTC+08:00 · 时间、模型与任务同时作用于指标、趋势及明细", 10); scope.Margin = new Thickness(0, 0, 0, 6); Add(scope, 3);
        foreach (var card in new[] { totalCard, inputCard, outputCard, cacheCard, callsCard }) { card.Value.FontSize = 24; card.View.Padding = new Thickness(10); card.View.Margin = new Thickness(0, 0, 8, 6); metrics.Children.Add(card.View); }
        Add(metrics, 4);
        var chart = new DockPanel();
        var chartTitle = new DockPanel { Height = 22, Margin = new Thickness(0, 0, 0, 3) };
        var legend = new StackPanel { Orientation = Orientation.Horizontal }; var input = Label("● 输入", 11); input.Foreground = new SolidColorBrush(Color.FromRgb(15, 140, 117)); input.Margin = new Thickness(0, 0, 16, 0); legend.Children.Add(input);
        var output = Label("● 输出", 11); output.Foreground = new SolidColorBrush(Color.FromRgb(122, 102, 209)); legend.Children.Add(output);
        DockPanel.SetDock(legend, Dock.Right); chartTitle.Children.Add(legend); chartTitle.Children.Add(Label("每日趋势", 14, true)); DockPanel.SetDock(chartTitle, Dock.Top); chart.Children.Add(chartTitle); chart.Children.Add(trend);
        Add(Panel(chart, 10), 5);

        var detailTabs = new StackPanel { Orientation = Orientation.Horizontal }; var detailTitle = Label("用量明细", 14, true); detailTitle.Margin = new Thickness(0, 0, 12, 0); detailTabs.Children.Add(detailTitle);
        var grouping = new StackPanel { Orientation = Orientation.Horizontal }; grouping.Children.Add(modelGroup); grouping.Children.Add(taskGroup); detailTabs.Children.Add(SegmentGroup(grouping)); rowCount.Margin = new Thickness(10, 0, 12, 0); detailTabs.Children.Add(rowCount);
        detailTabs.Margin = new Thickness(0, 5, 12, 5); detailControls.Children.Add(detailTabs);
        var tableActions = new StackPanel { Orientation = Orientation.Horizontal };
        search.SetResourceReference(StyleProperty, "UsageSearchBox"); search.Width = 256; AutomationProperties.SetName(search, "搜索明细"); tableActions.Children.Add(search);
        export.Margin = new Thickness(8, 0, 0, 0); tableActions.Children.Add(export); tableActions.Margin = new Thickness(0, 5, 0, 5); detailControls.Children.Add(tableActions); Add(detailControls, 6);
        BuildColumns(); table.SetResourceReference(DataGrid.BackgroundProperty, "PanelBrush"); AutomationProperties.SetName(table, "Token 用量明细");
        ScrollViewer.SetHorizontalScrollBarVisibility(table, ScrollBarVisibility.Auto); ScrollViewer.SetVerticalScrollBarVisibility(table, ScrollBarVisibility.Auto); Add(table, 7);
        var footer = new DockPanel { Margin = new Thickness(0, 5, 0, 0), MinHeight = 30 }; var info = new Button { Content = "统计说明", MinWidth = 86, Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(info, Dock.Right); footer.Children.Add(info); footer.Children.Add(coverage); Add(footer, 8);
        period.SelectionChanged += async (_, _) => await FilterChanged(); models.SelectionChanged += async (_, _) => await FilterChanged(); tasks.SelectionChanged += async (_, _) => await FilterChanged();
        modelGroup.Click += async (_, _) => { if (group != "model") { group = "model"; await FilterChanged(); } }; taskGroup.Click += async (_, _) => { if (group != "task") { group = "task"; await FilterChanged(); } };
        resetFilters.ToolTip = "重置模型与任务，保留时间范围、分组和明细搜索"; resetFilters.IsEnabled = model != "all" || task != "all";
        resetFilters.Click += async (_, _) => await ResetFilters(); search.TextChanged += (_, _) => FilterRows();
        refresh.Click += async (_, _) => await ManualRefresh(); export.Click += (_, _) => ShowExportOptions(); info.Click += (_, _) => ShowCoverage();
        return layout;
    }
    private void ResizeUsage()
    {
        double width = usageScroller.ViewportWidth > 0 ? usageScroller.ViewportWidth : usageScroller.ActualWidth;
        bool narrow = width > 0 && width < 850;
        metrics.Columns = narrow ? 3 : 5;
        double height = Math.Max(narrow ? 860 : 680, usageScroller.ViewportHeight);
        if (Math.Abs(usage.Height - height) > .1 || double.IsNaN(usage.Height)) usage.Height = height;
    }
    private void BuildColumns()
    {
        var cellStyle = new Style(typeof(DataGridCell));
        cellStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("ForegroundBrush")));
        cellStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        cellStyle.Setters.Add(new Setter(Control.TemplateProperty, (ControlTemplate)XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="DataGridCell">
  <Grid Background="{DynamicResource PanelBrush}"><Border x:Name="Selected" Background="Transparent"/><ContentPresenter VerticalAlignment="Center" HorizontalAlignment="Stretch"/><Border x:Name="Focus" BorderBrush="Transparent" BorderThickness="1" IsHitTestVisible="False"/></Grid>
  <ControlTemplate.Triggers><Trigger Property="IsSelected" Value="True"><Setter TargetName="Selected" Property="Background" Value="{DynamicResource SelectionBrush}"/></Trigger><Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="Focus" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger></ControlTemplate.Triggers>
</ControlTemplate>
""")));
        table.CellStyle = cellStyle;
        var headerTemplate = (ControlTemplate)XamlReader.Parse("""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="DataGridColumnHeader">
  <Grid>
    <Border x:Name="HeaderBody" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}">
      <Grid HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center"><Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
        <Path x:Name="SortArrow" Grid.Column="0" Data="M 0,0 L 3.5,4 L 7,0" Width="7" Height="4" Margin="0,0,6,0" Fill="{DynamicResource SecondaryBrush}" Visibility="Collapsed" VerticalAlignment="Center"/>
        <ContentPresenter x:Name="HeaderLabel" Grid.Column="1" ContentSource="Content" RecognizesAccessKey="True" VerticalAlignment="Center"/>
      </Grid>
    </Border>
    <Border x:Name="FocusRing" BorderBrush="Transparent" BorderThickness="1" IsHitTestVisible="False"/>
    <Thumb x:Name="PART_LeftHeaderGripper" Width="7" HorizontalAlignment="Left" Cursor="SizeWE"><Thumb.Template><ControlTemplate TargetType="Thumb"><Border Background="Transparent"/></ControlTemplate></Thumb.Template></Thumb>
    <Thumb x:Name="PART_RightHeaderGripper" Width="7" HorizontalAlignment="Right" Cursor="SizeWE"><Thumb.Template><ControlTemplate TargetType="Thumb"><Border Background="Transparent"/></ControlTemplate></Thumb.Template></Thumb>
  </Grid>
  <ControlTemplate.Triggers>
    <Trigger Property="SortDirection" Value="Ascending"><Setter TargetName="SortArrow" Property="Visibility" Value="Visible"/><Setter TargetName="SortArrow" Property="Data" Value="M 0,4 L 3.5,0 L 7,4"/></Trigger>
    <Trigger Property="SortDirection" Value="Descending"><Setter TargetName="SortArrow" Property="Visibility" Value="Visible"/></Trigger>
    <Trigger Property="HorizontalContentAlignment" Value="Left"><Setter TargetName="HeaderLabel" Property="Grid.Column" Value="0"/><Setter TargetName="SortArrow" Property="Grid.Column" Value="1"/><Setter TargetName="SortArrow" Property="Margin" Value="6,0,0,0"/></Trigger>
    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="HeaderBody" Property="Background" Value="{DynamicResource HoverBrush}"/></Trigger>
    <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="FocusRing" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>
""");
        foreach (var definition in new[] { ("Label", "模型 / 任务", "label", 2.15, 190d), ("Input", "输入", "input_tokens", 1.15, 104d), ("Cached", "缓存输入", "cached_input_tokens", 1.15, 104d), ("Noncached", "非缓存输入", "noncached_input_tokens", 1.15, 104d), ("Output", "输出（含推理）", "output_tokens", 1.15, 116d), ("Total", "总计", "total_tokens", 1.15, 104d) })
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis)); style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(8, 0, 10, 0)));
            style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center)); style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(definition.Item1)));
            if (definition.Item1 != "Label") { style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right)); style.Setters.Add(new Setter(TextBlock.FontFamilyProperty, new FontFamily("Consolas"))); }
            if (definition.Item1 == "Total") style.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
            var headerStyle = new Style(typeof(DataGridColumnHeader), Application.Current.TryFindResource(typeof(DataGridColumnHeader)) as Style);
            headerStyle.Setters.Add(new Setter(Control.TemplateProperty, headerTemplate));
            // One shared inset avoids separately rounding a cell border and margin at fractional DPI.
            headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 10, 4)));
            headerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, definition.Item1 == "Label" ? HorizontalAlignment.Left : HorizontalAlignment.Right));
            headerStyle.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
            headerStyle.Setters.Add(new Setter(FocusableProperty, true));
            table.Columns.Add(new DataGridTextColumn { Header = definition.Item2, Binding = new Binding(definition.Item1), SortMemberPath = definition.Item3, Width = new DataGridLength(definition.Item4, DataGridLengthUnitType.Star), MinWidth = definition.Item5, ElementStyle = style, HeaderStyle = headerStyle, SortDirection = definition.Item3 == sortKey ? ListSortDirection.Descending : null });
        }
        table.Sorting += (_, e) => { e.Handled = true; sortAscending = sortKey == e.Column.SortMemberPath ? !sortAscending : e.Column.SortMemberPath == "label"; sortKey = e.Column.SortMemberPath; FilterRows(); };
    }

    private string Query => DashboardClient.Query(days, model, task, group);
    private MainTableSelection TableSelection => new(search.Text, sortKey, sortAscending);
    private string LocalSnapshotTime => DateTimeOffset.TryParse(stamp, out var time) ? time.LocalDateTime.ToString("HH:mm:ss") : "—";
    private MainUsageIdentity CurrentIdentity => new(state.S("home"), state.S("cache"), Query);
    private string IntervalDescription => refreshSeconds == 0 ? "本机自动更新已暂停" : $"本机每 {refreshSeconds} 秒更新";
    private void SetStatus(string text) { statusMessage = text; PresentStatus(); }
    private void PresentStatus()
    {
        if (closed) return;
        string settingsProblem = state.S("settingsError").Length > 0 ? "设置文件需要恢复 · 按 Ctrl+, 打开设置：" + state.S("settingsError")
            : settingsQueue.Error.Length > 0 ? "设置尚未保存 · 将自动重试，可按 Ctrl+S 立即重试：" + settingsQueue.Error : "";
        var updates = state.O("updates"); var local = updates.O("local"); var quota = updates.O("quota");
        var messages = new List<string> { settingsProblem.Length > 0 ? settingsProblem : statusMessage };
        if (local.S("error").Length > 0 && !messages[0].Contains(local.S("error"))) messages.Add("本机更新失败：" + local.S("error"));
        if (quota.B("busy")) messages.Add("账号额度更新中…");
        else if (quota.S("error").Length > 0) messages.Add("账号额度更新失败：" + quota.S("error"));
        string text = string.Join(" · ", messages);
        status.Text = text; status.ToolTip = text; AutomationProperties.SetName(status, text);
        sharedStatus.Text = page == "monitor" && settingsProblem.Length == 0 ? "任务提醒独立于用量自动更新 · 关闭主面板后继续监控" : text;
        sharedStatus.ToolTip = sharedStatus.Text; AutomationProperties.SetName(sharedStatus, sharedStatus.Text);
    }
    private void SelectUsageIdentity()
    {
        if (!usageSession.Select(CurrentIdentity)) return;
        export.IsEnabled = false;
        // A different directory or filter must never label the previous snapshot's numbers.
        snapshot = new(); stamp = ""; trend.Update(new JsonArray()); FilterRows();
        foreach (var card in new[] { totalCard, inputCard, outputCard, cacheCard, callsCard }) { card.Update(null); card.Detail.Text = "等待当前筛选"; }
        coverage.Text = "当前筛选尚未读取成功 · 旧范围数据已隐藏"; coverage.ToolTip = coverage.Text;
    }
    private void UpdateExportEnabled() => export.IsEnabled = !refreshBusy && (demo ? snapshot.Count > 0 && !snapshot.O("meta").B("loading") : usageSession.CanExport);
    private async Task<JsonObject> SendHost(JsonObject request)
    {
        if (demo)
        {
            if (request.S("action") == "settings") foreach (var item in request.O("patch")) state.O("settings")[item.Key] = item.Value?.DeepClone();
            if (request.S("action") == "mode") { state.O("settings")["mode"] = request.S("value"); ApplyHost(state.Copy()); }
            if (request.S("action") == "settings-dialog") SetStatus("演示 · 统一设置：" + request.S("page", "appearance"));
            if (request.S("action") == "monitor") { state = TaskMonitorDemo.Apply(state, request); ApplyHost(state.Copy()); }
            if (request.S("action") == "monitor-settings-dialog") new TaskMonitorSettingsWindow(this, () => state.Copy(), SendHost).Show();
            if (request.S("action") == "monitor-test") state["monitorResult"] = J.Obj(("ok", true), ("message", "演示模式：仅验证界面，不发送系统通知。"));
            return state.Copy();
        }
        var reply = await Ipc.Send(request); if (!closed) ApplyHost(reply); return reply;
    }
    private void ApplyHost(JsonObject value)
    {
        state = value.Copy(); bool previousApplying = applying; applying = true;
        try
        {
            var settings = state.O("settings");
            string themePreference = Theme.PreferenceSignature(settings, "main");
            if (!demo && appliedThemePreference != themePreference) { appliedThemePreference = themePreference; ApplyMainTheme(); }
            int nextRefresh = Math.Clamp(settings.I("refresh", 5), 0, 3600);
            bool intervalChanged = refreshSeconds != nextRefresh; refreshSeconds = nextRefresh;
            displayMode.ToolTip = "当前常驻方式：" + (settings.S("mode", "both") switch { "tray" => "仅托盘", "float" => "仅浮窗", _ => "托盘与浮窗" }) + "；切换不会关闭主面板";
            var quota = state.O("quota"); quotaText.Text = quota.S("detail", "账号额度暂不可用"); quotaText.ToolTip = quotaText.Text;
            var quotaUpdate = state.O("updates").O("quota");
            if (quotaUpdate.B("busy")) quotaText.Text += " · 更新中…";
            else if (quotaUpdate.S("error").Length > 0) quotaText.Text += " · 更新失败（可按刷新重试）";
            quotaText.ToolTip = quotaText.Text + (quotaUpdate.S("error").Length > 0 ? "\n" + quotaUpdate.S("error") : "");
            resetCards.Text = quota.S("resetLabel", "重置卡数量未知");
            UpdateAccountSummary();
            if (state.O("choices").Count > 0) budgetChoices = state.O("choices").Copy();
            UpdateBudget();
            monitor.Update(state);
            int unread = state.O("monitor").O("summary").I("unread");
            monitorTab.Content = unread > 0 ? "任务监控 · " + (unread > 99 ? "99+" : unread.ToString()) : "任务监控";
            if (intervalChanged && started && client.Running) _ = ConfigureInterval();
            var nextHome = state.S("home"); var nextCache = state.S("cache");
            if (currentHome.Length > 0 && (nextHome != currentHome || nextCache != currentCache))
            {
                model = "all"; task = "all"; stamp = ""; publishedStamp = "";
                models.SelectedValue = model; tasks.SelectedValue = task; SelectUsageIdentity();
                SaveFilters(); if (started && !closing) _ = RestartAndLoad();
            }
            refresh.IsEnabled = !refreshBusy && !state.B("busy");
            refresh.Content = refreshBusy || state.B("busy") ? "刷新中…" : "刷新";
            PresentStatus();
        }
        finally { applying = previousApplying; }
    }
    private async Task ConfigureInterval()
    {
        try { await client.Configure(refreshSeconds, lifetime.Token); }
        catch (Exception e) when (!closed) { SetStatus("刷新间隔同步失败：" + e.Message); }
    }
    private async Task RestartAndLoad() { await EnsureService(); await LoadUsage(); }
    private async Task EnsureService(bool force = false, bool throwOnError = false)
    {
        if (demo || closed) return;
        try
        {
            await serviceGate.WaitAsync(lifetime.Token);
            try
            {
                string home = state.S("home"), cache = state.S("cache");
                if (home.Length == 0 || cache.Length == 0) throw new IOException("未配置 Codex 数据目录");
                bool changed = force || !client.Running || currentHome != home || currentCache != cache;
                if (changed) { SetStatus("正在准备本机统计…"); await client.Start(home, cache, refreshSeconds, lifetime.Token, force || currentCache != cache); stamp = ""; }
                currentHome = home; currentCache = cache;
            }
            finally { serviceGate.Release(); }
        }
        catch (OperationCanceledException) { if (throwOnError) throw; }
        catch (Exception e) { SetStatus("启动失败 · 点击刷新重试：" + e.Message); refresh.IsEnabled = true; if (throwOnError) throw; }
    }
    private async Task Poll()
    {
        if (polling || closed || demo) return; polling = true;
        try
        {
            await SendHost(J.Obj(("action", "state")));
            if (refreshBusy) return;
            if (!client.Running) await EnsureService();
            if (!client.Running) return;
            var health = await client.Health(lifetime.Token); failures = 0;
            if (!health.B("ready")) { SetStatus("正在索引本机记录…"); return; }
            if (health.S("refresh_error").Length > 0) { usageSession.Invalidate(); UpdateExportEnabled(); SetStatus("本机扫描失败 · 请刷新重试：" + health.S("refresh_error")); return; }
            if (usageSession.ShouldRead(health.S("generated_at")) && page == "usage" && WindowState != WindowState.Minimized) await LoadUsage();
            else if (!usageSession.NeedsRead && health.S("generated_at") != publishedStamp) await NotifyChanged(generatedAt: health.S("generated_at"));
            if (page == "budget" && choicesStamp != health.S("generated_at")) await RefreshBudgetChoices(health.S("generated_at"));
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!closed) { failures++; SetStatus($"连接恢复中（{failures}）… {e.Message}"); if (failures >= 3) { failures = 0; await EnsureService(force: true); } } }
        finally { polling = false; }
    }
    private Task<bool> LoadUsage()
    {
        if (demo || closed) return Task.FromResult(demo);
        SelectUsageIdentity();
        if (usageLoad is { IsCompleted: false }) return usageLoad;
        return usageLoad = ReadUsage();
    }
    private async Task<bool> ReadUsage()
    {
        export.IsEnabled = false;
        try
        {
            do
            {
                var result = await usageSession.LoadAsync(async (identity, token) =>
                {
                    await EnsureService(throwOnError: true);
                    if (identity != CurrentIdentity) throw new IOException("数据范围已变化");
                    return await client.Usage(identity.Query, token);
                }, lifetime.Token);
                if (closed) return false;
                bool loading = result.O("meta").B("loading");
                // A selection may change between the completed HTTP task and this UI continuation.
                if (usageSession.Displayed != CurrentIdentity || (usageSession.NeedsRead && !loading)) continue;
                Render(result);
                if (loading) return false;
                if (!refreshBusy) await NotifyChanged();
                // State synchronization is also asynchronous: a manual refresh or filter change
                // arriving during that await must finish its own read before this task succeeds.
            } while (usageSession.NeedsRead && !closed);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception e)
        {
            if (!closed) SetStatus("当前筛选读取失败 · 将自动重试，也可按刷新重试：" + e.Message);
            return false;
        }
        finally { UpdateExportEnabled(); }
    }
    private async Task NotifyChanged(string? refreshID = null, string? error = null, string? generatedAt = null)
    {
        if (demo || closed) return;
        if (refreshID == null && error == null && (string.IsNullOrEmpty(generatedAt ?? stamp) || (generatedAt ?? stamp) == publishedStamp)) return;
        try
        {
            var request = J.Obj(("action", "data-changed"), ("success", error == null), ("error", error ?? ""));
            if (refreshID != null) request["refreshID"] = refreshID;
            await SendHost(request); if (error == null) publishedStamp = generatedAt ?? stamp;
        }
        catch (Exception e) { SetStatus("后台状态同步失败：" + e.Message); }
    }
    private void Render(JsonObject data)
    {
        snapshot = data.Copy(); var summary = snapshot.O("summary"); var meta = snapshot.O("meta");
        totalCard.Update(summary["total_tokens"]); inputCard.Update(summary["input_tokens"]); outputCard.Update(summary["output_tokens"]); cacheCard.Update(summary["cached_input_tokens"]);
        callsCard.Update(summary["requests"], $"次 · {UsageNumbers.Exact(summary["active_tasks"])} 个任务");
        stamp = meta.S("generated_at"); UpdateAccountSummary();
        bool loading = meta.B("loading"); string time = LocalSnapshotTime;
        if (!refreshBusy) SetStatus(loading ? "正在索引本机记录…" : $"● 本机 {time} 已更新 · {IntervalDescription}");
        refresh.Content = refreshBusy || state.B("busy") ? "刷新中…" : loading ? "初始化…" : "刷新";
        refresh.IsEnabled = !refreshBusy && !state.B("busy") && !loading; UpdateExportEnabled();
        var hints = new List<string> { $"本机 {UsageNumbers.Exact(meta["scanned_files"])} 个日志", "UTC+08:00" };
        int exclusions = meta.I("excluded_legacy_threads"); if (exclusions > 0) hints.Add($"{exclusions} 个旧任务未计入");
        if (meta.A("issues").Count > 0 || meta.O("coverage").A("cumulative_gaps").Count > 0) hints.Add("部分记录存在缺口 · 查看统计说明");
        coverage.Text = string.Join(" · ", hints); coverage.ToolTip = coverage.Text;
        trend.Update(snapshot.A("timeline")); FilterRows();
        var filters = snapshot.O("filters");
        var modelChoices = new List<Choice> { new("all", "全部模型") };
        modelChoices.AddRange(filters.A("models").Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).Select(x => new Choice(x, x)));
        var taskChoices = new List<Choice> { new("all", "全部任务") };
        taskChoices.AddRange(filters.A("tasks").Rows().Select(x => new Choice(x.S("id"), x.S("label", x.S("id")))));
        if (task != "all" && taskChoices.All(x => x.Id != task) && filters.O("selected_task").Count > 0) taskChoices.Insert(1, new Choice(task, filters.O("selected_task").S("label", task)));
        applying = true;
        try { UpdateChoices(models, modelChoices, model); UpdateChoices(tasks, taskChoices, task); period.SelectedValue = days; foreach (var entry in periodSegments) entry.Value.IsChecked = entry.Key == days; modelGroup.IsChecked = group == "model"; taskGroup.IsChecked = group == "task"; }
        finally { applying = false; }
        resetFilters.IsEnabled = model != "all" || task != "all";
    }
    private static void UpdateChoices(ComboBox picker, List<Choice> choices, string selected)
    {
        if (picker.ItemsSource is not IEnumerable<Choice> previous || !previous.SequenceEqual(choices)) picker.ItemsSource = choices;
        picker.SelectedValue = selected; picker.ToolTip = choices.FirstOrDefault(x => x.Id == selected)?.Label;
    }
    private void FilterRows()
    {
        if (table.Columns.Count == 0) return;
        var rows = TableSelection.Rows(snapshot).Select(row => new UsageRow(row)).ToList();
        table.ItemsSource = rows;
        // Replacing ItemsSource clears WPF's sort glyphs; restore them after the rows.
        foreach (var column in table.Columns) column.SortDirection = column.SortMemberPath == sortKey ? (sortAscending ? ListSortDirection.Ascending : ListSortDirection.Descending) : null;
        rowCount.Text = rows.Count == snapshot.A("groups").Count ? $"{rows.Count} 项" : $"{rows.Count} / {snapshot.A("groups").Count} 项";
    }
    private async Task FilterChanged()
    {
        if (applying || !IsLoaded) return;
        days = period.SelectedValue as string ?? days; model = models.SelectedValue as string ?? model; task = tasks.SelectedValue as string ?? task;
        foreach (var entry in periodSegments) entry.Value.IsChecked = entry.Key == days;
        modelGroup.IsChecked = group == "model"; taskGroup.IsChecked = group == "task";
        resetFilters.IsEnabled = model != "all" || task != "all";
        SaveFilters();
        if (demo) { SetStatus("演示数据 · 筛选控件预览"); return; }
        SetStatus("正在更新筛选…"); await LoadUsage();
    }
    private async Task ResetFilters()
    {
        applying = true; model = "all"; task = "all"; models.SelectedValue = "all"; tasks.SelectedValue = "all"; applying = false;
        await FilterChanged();
    }
    private void SaveFilters() => SaveSetting(J.Obj(("filterDays", days), ("filterModel", model), ("filterTask", task), ("filterGroup", group)));
    private void SaveSetting(JsonObject patch)
    {
        settingsQueue.Enqueue(patch); settingsTimer.Stop(); settingsTimer.Interval = TimeSpan.FromMilliseconds(200); settingsTimer.Start();
    }
    private async Task<bool> FlushSettings()
    {
        if (!settingsQueue.HasPending || demo) return true;
        try { await settingsQueue.FlushAsync(async patch => { await SendHost(J.Obj(("action", "settings"), ("patch", patch))); }); PresentStatus(); return true; }
        catch (Exception)
        {
            PresentStatus();
            if (!closing && !closed) { settingsTimer.Interval = TimeSpan.FromSeconds(5); settingsTimer.Start(); }
            return false;
        }
    }
    private void SelectPage(string selected, bool save = true)
    {
        page = selected == "budget" || selected == "budgets" ? "budget" : selected == "monitor" ? "monitor" : "usage";
        usageTab.IsChecked = page == "usage"; budgetTab.IsChecked = page == "budget"; monitorTab.IsChecked = page == "monitor";
        pageContent.Content = page == "usage" ? usageScroller : page == "budget" ? budget : monitor; trend.Dismiss();
        if (page == "usage") ResizeUsage();
        PresentStatus();
        _ = PublishViewing();
        if (save) SaveSetting(J.Obj(("mainPage", page)));
        if (page == "usage" && started) _ = LoadUsage();
        if (page == "budget" && started) _ = RefreshBudgetChoices(stamp);
    }
    private void UpdateBudget()
    {
        // The host also publishes quota and refresh status; neither should recreate budget controls.
        if (budget.StateMatches(state, budgetChoices)) return;
        var budgetState = state.Copy(); budgetState["choices"] = budgetChoices.DeepClone(); budget.Update(budgetState);
    }
    private async Task RefreshBudgetChoices(string generated)
    {
        if (demo || closed || choicesBusy) return; choicesBusy = true;
        try { await SendHost(J.Obj(("action", "budget-choices"))); choicesStamp = generated; }
        catch (Exception e) { SetStatus("预算筛选项读取失败：" + e.Message); }
        finally { choicesBusy = false; }
    }
    private async Task PublishViewing()
    {
        if (demo || closed || closing) return;
        bool active = IsActive && WindowState != WindowState.Minimized && page == "budget";
        string signature = $"{active}|{budget.SelectedId}|{budget.IsEditing}";
        if (signature != viewingSignature)
        {
            viewingSignature = signature;
            try { await Ipc.Send(J.Obj(("action", "viewing"), ("budgetID", budget.SelectedId), ("editing", budget.IsEditing), ("active", active), ("pid", Environment.ProcessId)), timeout: 2500); }
            catch (Exception) { viewingSignature = ""; }
        }
        bool monitorActive = IsActive && WindowState != WindowState.Minimized && page == "monitor" && monitor.IsViewingDetail;
        string next = $"{monitorActive}|{monitor.SelectedId}|{monitor.SelectedMessageId}";
        if (next != monitorViewingSignature)
        {
            monitorViewingSignature = next;
            try { await Ipc.Send(J.Obj(("action", "monitor-viewing"), ("taskID", monitor.SelectedId), ("messageID", monitor.SelectedMessageId), ("active", monitorActive), ("pid", Environment.ProcessId)), timeout: 2500); }
            catch (Exception) { monitorViewingSignature = ""; }
        }
    }
    private async Task ManualRefresh()
    {
        if (demo) { SetStatus("演示数据已更新"); return; }
        if (refreshBusy || state.B("busy")) return;
        refresh.IsEnabled = false; refresh.Content = "刷新中…";
        try { await SendHost(J.Obj(("action", "refresh"))); }
        catch (Exception e) { Error("刷新失败", e); refresh.IsEnabled = true; refresh.Content = "刷新"; }
    }
    private async Task RefreshService()
    {
        if (refreshBusy || closed) return; refreshBusy = true;
        string? error = null;
        try
        {
            refresh.IsEnabled = false; refresh.Content = "刷新中…"; export.IsEnabled = false; usageSession.Invalidate(); SetStatus("正在重新读取本机日志…");
            await EnsureService(throwOnError: true); await client.Refresh(lifetime.Token);
            usageSession.Invalidate();
            if (!await LoadUsage()) throw new IOException(usageSession.Error.Length > 0 ? usageSession.Error : "当前筛选尚未读取完成，请重试");
        }
        catch (OperationCanceledException) { if (!closed) error = "扫描耗时较长，仍在后台进行"; }
        catch (Exception e) { error = e.Message; }
        finally
        {
            refreshBusy = false;
            foreach (string id in refreshIDs.ToArray()) { refreshIDs.Remove(id); await NotifyChanged(id, error); }
            if (!closed) { refresh.IsEnabled = !state.B("busy"); refresh.Content = state.B("busy") ? "刷新中…" : "刷新"; UpdateExportEnabled(); SetStatus(error != null ? "本机更新失败 · 请重试：" + error : $"● 本机 {LocalSnapshotTime} 已更新 · {IntervalDescription}"); }
        }
    }
    public async Task<JsonObject> Handle(JsonObject request)
    {
        if (!Dispatcher.CheckAccess()) return await Dispatcher.InvokeAsync(() => Handle(request)).Task.Unwrap();
        switch (request.S("action"))
        {
            case "inspect":
                return J.Obj(("page", page), ("filters", J.Obj(("days", days), ("model", model), ("task", task), ("group", group))),
                    ("summary", snapshot.O("summary")), ("busy", refreshBusy), ("status", status.Text),
                    ("canExport", export.IsEnabled), ("queryPending", usageSession.NeedsRead), ("settingsPending", settingsQueue.HasPending),
                    ("frame", J.Obj(("width", ActualWidth), ("height", ActualHeight))), ("monitor", monitor.Inspect()));
            case "focus":
                bool backgroundTest = Environment.GetEnvironmentVariable("CODEX_USAGE_TEST_BACKGROUND") == "1";
                if (backgroundTest) { ShowActivated = false; Opacity = 0; WindowStartupLocation = WindowStartupLocation.Manual; Left = -12000; Top = -12000; }
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Show(); if (!backgroundTest) { Activate(); Topmost = true; Topmost = false; }
                string selected = request.S("page"); if (selected.Length > 0) SelectPage(selected);
                string id = request.S("budgetID", request.S("budgetId")); if (id.Length > 0) { SelectPage("budget"); budget.Select(id, request.B("edit")); }
                string monitorID = request.S("monitorID", request.S("monitorId")), messageID = request.S("messageID", request.S("messageId"));
                if (selected == "monitor" || monitorID.Length > 0 || messageID.Length > 0) { SelectPage("monitor"); monitor.Select(monitorID, messageID); }
                break;
            case "refresh":
                string refreshID = request.S("refreshID"); if (refreshID.Length > 0) refreshIDs.Add(refreshID);
                _ = RefreshService(); return J.Obj(("accepted", true));
            case "state":
            case "settings":
                var incoming = request["state"] as JsonObject;
                if (incoming != null) ApplyHost(incoming);
                else if (request.ContainsKey("home")) ApplyHost(request);
                else if (request.ContainsKey("settings")) { var merged = state.Copy(); merged["settings"] = request.O("settings").DeepClone(); ApplyHost(merged); }
                break;
            case "close":
                await PrepareClose(); return J.Obj(("closing", true));
        }
        return J.Obj(("ok", true), ("page", page));
    }
    private async Task Export(bool visible = true)
    {
        if (!export.IsEnabled) return;
        var dialog = new SaveFileDialog { Title = visible ? "导出当前显示行（含搜索及排序）" : "导出当前范围全部行（不受明细搜索影响）", FileName = $"codex-usage-{days}-{group}.csv", Filter = "CSV 文件 (*.csv)|*.csv", DefaultExt = ".csv", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        export.IsEnabled = false;
        try
        {
            MainExportSnapshot? captured = demo ? null : usageSession.CaptureExport();
            var selection = TableSelection;
            byte[] bytes = MainUsageSession.Csv(selection.ExportData(captured?.Data ?? snapshot, visible));
            string path = dialog.FileName, temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, lifetime.Token);
                if (captured != null) { SelectUsageIdentity(); usageSession.ValidateExport(captured); }
                if (visible && selection != TableSelection) throw new InvalidOperationException("搜索或排序已变化，请重新导出当前显示行。");
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            SetStatus("已导出 " + Path.GetFileName(path));
        }
        catch (Exception e) { Error("导出失败", e); }
        finally { UpdateExportEnabled(); }
    }
    private async Task OpenSettings(string page = "appearance")
    {
        try { ForegroundTransfer.GrantHost(state); await SendHost(J.Obj(("action", "settings-dialog"), ("page", page))); }
        catch (Exception error) { Error("无法打开设置", error); }
    }
    private void ShowDisplayModes()
    {
        var menu = new ContextMenu();
        var show = new MenuItem { Header = "查看悬浮窗" }; show.Click += async (_, _) =>
        { try { await SendHost(J.Obj(("action", "show-float"))); } catch (Exception error) { Error("无法显示浮窗", error); } };
        menu.Items.Add(show); menu.Items.Add(new Separator());
        foreach (var mode in new[] { ("tray", "仅系统托盘"), ("float", "仅悬浮窗"), ("both", "托盘与悬浮窗") })
        {
            var item = new MenuItem { Header = mode.Item2, IsCheckable = true, IsChecked = state.O("settings").S("mode", "both") == mode.Item1 };
            item.Click += async (_, _) => { try { await SendHost(J.Obj(("action", "mode"), ("value", mode.Item1))); } catch (Exception error) { Error("无法切换显示方式", error); } };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = displayMode; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true;
    }
    private void ShowExportOptions()
    {
        var menu = new ContextMenu();
        var visible = new MenuItem { Header = "当前显示行（含搜索及排序）", IsEnabled = export.IsEnabled };
        var all = new MenuItem { Header = "当前范围全部行", IsEnabled = export.IsEnabled };
        visible.Click += async (_, _) => await Export(); all.Click += async (_, _) => await Export(false);
        menu.Items.Add(visible); menu.Items.Add(all); menu.PlacementTarget = export; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true;
    }
    private void UpdateAccountSummary()
    {
        var quota = state.O("quota"); var windows = quota.A("windows").Rows().ToList();
        var week = windows.FirstOrDefault(row => row.I("duration_minutes") == 10080);
        var shortWindow = windows.Where(row => row.I("duration_minutes") != 10080).OrderBy(row => row.I("duration_minutes")).FirstOrDefault();
        void Set(TextBlock value, TextBlock note, JsonObject? window, string missing)
        {
            value.Text = window?.N("remaining") is double remaining ? Math.Floor(Math.Clamp(remaining, 0, 100)).ToString("0") + "%" : "—";
            note.Text = window is null ? missing : window.S("label") + " · " + (window.N("resets_at") is double reset ? J.Date(reset) + " 重置" : "重置时间未知");
            value.ToolTip = note.Text; note.ToolTip = note.Text; AutomationProperties.SetName(value, value.Text + " " + note.Text);
        }
        Set(weekQuota, weekReset, week, "周额度暂不可用"); Set(shortQuota, shortReset, shortWindow, "短周期额度暂不可用");
        var localUpdate = state.O("updates").O("local"); var quotaUpdate = state.O("updates").O("quota");
        string LocalTime() => localUpdate.N("updatedAt") is double updated ? J.Date(updated, "HH:mm:ss") : DateTimeOffset.TryParse(stamp, out var time) ? time.LocalDateTime.ToString("HH:mm:ss") : "—";
        string quotaTime = J.Date(quotaUpdate.N("updatedAt") ?? quota.N("updated_at"), "HH:mm:ss");
        updateTimes.Text = "本机 " + (localUpdate.B("busy") || refreshBusy ? "更新中…" : LocalTime()) + "\n账号 " + (quotaUpdate.B("busy") ? "更新中…" : quotaTime);
        updateTimes.ToolTip = updateTimes.Text + "\n" + IntervalDescription + "\n本机日志与账号额度各自更新，账号额度不受本机筛选影响。";
        quotaText.Text = quotaUpdate.S("error").Length > 0 ? "账号更新失败 · 上次记录" : quota.B("stale") ? "账号额度 · 上次记录" : "账号级 · 不受本机筛选影响";
        quotaText.ToolTip = quota.S("detail") + "\n账号更新：" + quotaTime + (quotaUpdate.S("error").Length > 0 ? "\n" + quotaUpdate.S("error") : "");
    }
    private void ApplyMainTheme()
    {
        bool dark = Theme.Resolve(state.O("settings"), "main");
        if (Theme.Dark != dark) { Theme.Apply(dark); trend.InvalidateVisual(); }
    }
    private void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs args)
    {
        if (!closed && !closing) _ = Dispatcher.BeginInvoke(new Action(ApplyMainTheme));
    }
    private void ShowCoverage()
    {
        var meta = snapshot.O("meta"); var gaps = meta.O("coverage").A("cumulative_gaps").Rows().ToList();
        string text = $"数据文件夹：{state.S("home")}\n\n输入包含缓存输入；输出包含推理。缓存不再重复加入总计。日界线使用 UTC+08:00。这些是日志记录的 Token，不代表订阅剩余额度或费用。\n\n扫描日志：{UsageNumbers.Exact(meta["scanned_files"])}\n未计入的旧任务：{UsageNumbers.Exact(meta["excluded_legacy_threads"])}\n累计计数不一致：{gaps.Count} 个任务\n\n旧日志可能缺少可归属的逐轮数据；无法可靠还原的记录不会按零值补齐。当前：{IntervalDescription}。手动刷新立即请求重新扫描，完成后更新窗口。统计只能读取 Codex 已写入磁盘的记录。";
        if (meta.A("issues").Count > 0) text += "\n\n记录问题：\n" + string.Join("\n", meta.A("issues").Take(8).Select(x => x?.ToString()));
        if (gaps.Count > 0) text += "\n\n不一致任务：\n" + string.Join("\n", gaps.Take(5).Select(x => $"{x.S("label", "未命名")}：差值 {x["difference"]}"));
        var dialog = new Window { Owner = this, Title = "统计说明与覆盖情况", Width = 660, Height = 520, MinWidth = 420, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.SetResourceReference(BackgroundProperty, "BackgroundBrush");
        var body = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Padding = new Thickness(20), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.Transparent };
        body.SetResourceReference(TextBox.ForegroundProperty, "ForegroundBrush"); dialog.Content = body; dialog.ShowDialog();
    }
    private void Error(string title, Exception error) { if (closed || closing) return; SetStatus(title + "：" + error.Message); MessageBox.Show(this, error.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning); }
    private void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.Q) { e.Handled = true; _ = Quit(); }
        if (e.Key == Key.H || e.Key == Key.W) { e.Handled = true; Close(); }
        if (e.Key == Key.R) { e.Handled = true; if (page == "monitor") _ = monitor.Check(); else _ = ManualRefresh(); }
        if (e.Key == Key.S) { e.Handled = true; _ = FlushSettings(); }
        if (e.Key == Key.OemComma) { e.Handled = true; _ = OpenSettings(); }
        if (e.Key == Key.E) { e.Handled = true; SelectPage("usage"); _ = Export(); }
        if (e.Key == Key.D1) { e.Handled = true; SelectPage("usage"); }
        if (e.Key == Key.D2) { e.Handled = true; SelectPage("budget"); }
        if (e.Key == Key.D3) { e.Handled = true; SelectPage("monitor"); }
    }
    private async Task Quit()
    {
        try
        {
            await PrepareClose();
            if (demo) { await CompleteClose(); return; }
            await Ipc.Send(J.Obj(("action", "quit")), timeout: 2500);
        }
        catch (Exception error) { ResumeAfterCloseFailure("尚未退出：" + error.Message); }
    }
    private void RestoreFrame(JsonObject frame)
    {
        if (demo || frame.Count == 0) return;
        double width = Math.Clamp(frame.N("width") ?? Width, MinWidth, Math.Max(MinWidth, SystemParameters.VirtualScreenWidth));
        double height = Math.Clamp(frame.N("height") ?? Height, MinHeight, Math.Max(MinHeight, SystemParameters.VirtualScreenHeight));
        double left = frame.N("left") ?? double.NaN, top = frame.N("top") ?? double.NaN;
        if (double.IsFinite(left) && double.IsFinite(top))
        {
            var saved = new Rect(left, top, width, height);
            var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var intersection = Rect.Intersect(saved, desktop);
            if (intersection.Width >= 100 && intersection.Height >= 60) { Left = Math.Clamp(left, desktop.Left - width + 150, desktop.Right - 150); Top = Math.Clamp(top, desktop.Top, Math.Max(desktop.Top, desktop.Bottom - 100)); WindowStartupLocation = WindowStartupLocation.Manual; }
        }
        Width = width; Height = height; if (frame.B("maximized")) WindowState = WindowState.Maximized;
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closed) return; e.Cancel = true; if (closing) return;
        try { await PrepareClose(); await CompleteClose(); }
        catch (Exception) { /* PrepareClose already restored the window and its retry feedback. */ }
    }
    private Task PrepareClose()
    {
        if (closing && preparingClose is not null) return preparingClose;
        return preparingClose = PrepareCloseCore();
    }
    private async Task PrepareCloseCore()
    {
        if (closed) return; closing = true; IsEnabled = false;
        if (!demo) SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        pollTimer.Stop(); settingsTimer.Stop(); trend.Dismiss();
        try
        {
            if (!demo) await budget.FlushDraft();
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            // Geometry is optional: opening an unreadable settings file for inspection
            // must not require resetting that file just to close the window.
            if (!demo && state.S("settingsError").Length == 0 && !bounds.IsEmpty && double.IsFinite(bounds.Left))
                settingsQueue.Enqueue(J.Obj(("mainWindow", J.Obj(("left", bounds.Left), ("top", bounds.Top), ("width", bounds.Width), ("height", bounds.Height), ("maximized", WindowState == WindowState.Maximized)))));
            if (!await FlushSettings()) throw new IOException(settingsQueue.Error);
        }
        catch (Exception error)
        {
            ResumeAfterCloseFailure("草稿或设置尚未保存，窗口保留以便重试：" + error.Message); throw;
        }
    }
    // IPC callers invoke this only after the successful close response was flushed.
    public Task CompleteClose() => completingClose ??= CompleteCloseCore();
    public void CancelPreparedClose()
    {
        if (closing && !closed && completingClose is null)
            ResumeAfterCloseFailure("关闭通信中断，已保存的内容已保留。窗口已恢复，可重试关闭。");
    }
    private async Task CompleteCloseCore()
    {
        await PrepareClose();
        lifetime.Cancel();
        try { await client.DisposeAsync(); } catch (Exception) { }
        closed = true; _ = Dispatcher.BeginInvoke(new Action(Close));
    }
    private void ResumeAfterCloseFailure(string message)
    {
        bool wasClosing = closing; closing = false; IsEnabled = true;
        if (!demo && wasClosing) { SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged; if (started) pollTimer.Start(); }
        if (settingsQueue.HasPending) { settingsTimer.Interval = TimeSpan.FromSeconds(5); settingsTimer.Start(); }
        SetStatus(message);
    }
}
