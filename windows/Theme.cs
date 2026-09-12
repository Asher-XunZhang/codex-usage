using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace CodexUsage;

internal static class Theme
{
    private static bool? systemDark;
    private static bool trackingSystem;
    internal static bool SystemDark
    {
        get
        {
            if (systemDark is bool cached) return cached;
            try { using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); return (systemDark = key?.GetValue("AppsUseLightTheme") is int value && value == 0).Value; }
            catch (Exception) { return (systemDark = false).Value; }
        }
    }
    internal static JsonObject Appearance(JsonObject settings)
    {
        if (settings["appearance"] is JsonObject appearance)
        {
            string global = appearance.S("theme", "system"); if (global is not ("system" or "light" or "dark")) global = "system";
            var overrides = new JsonObject();
            foreach (string surface in new[] { "main", "floating", "tray" }) { string value = appearance.O("overrides").S(surface, "inherit"); overrides[surface] = value is "inherit" or "system" or "light" or "dark" ? value : "inherit"; }
            return J.Obj(("theme", global), ("overrides", overrides));
        }
        // Preserve the legacy independently selected surfaces until the user chooses a new global appearance.
        string main = settings.S("mainTheme", "system"), floating = settings.O("floating").S("theme", "system");
        return J.Obj(("theme", main), ("overrides", J.Obj(("main", "inherit"), ("floating", floating == main ? "inherit" : floating), ("tray", floating == main ? "inherit" : floating))));
    }
    internal static bool Resolve(JsonObject settings, string surface)
    {
        string preference = Preference(settings, surface);
        return preference == "dark" || preference != "light" && SystemDark;
    }
    private static string Preference(JsonObject settings, string surface)
    {
        if (settings["appearance"] is not JsonObject appearance) return surface == "main" ? settings.S("mainTheme", "system") : settings.O("floating").S("theme", "system");
        string value = appearance.O("overrides").S(surface, "inherit");
        return value is "system" or "light" or "dark" ? value : appearance.S("theme", "system");
    }
    internal static string PreferenceSignature(JsonObject settings, string surface) => Preference(settings, surface) + ":" + Resolve(settings, surface);
    internal static void ValidateAppearance(JsonObject value)
    {
        if (value.S("theme") is not ("system" or "light" or "dark")) throw new ArgumentException("请选择跟随系统、浅色或深色。");
        foreach (string surface in new[] { "main", "floating", "tray" })
            if (value.O("overrides").S(surface, "inherit") is not ("inherit" or "system" or "light" or "dark")) throw new ArgumentException("界面外观选择无效。");
    }
    public static bool Dark { get; private set; }
    public static Brush Accent => Brush("AccentBrush");
    public static Brush Secondary => Brush("SecondaryBrush");
    public static Brush Foreground => Brush("ForegroundBrush");
    public static Brush Background => Brush("BackgroundBrush");
    public static Brush Panel => Brush("PanelBrush");
    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    public static SolidColorBrush Color(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
    public static void Apply(bool dark)
    {
        Dark = dark;
        ApplyTo(Application.Current.Resources, dark);
    }
    internal static void ApplyTo(ResourceDictionary resources, bool dark)
    {
        resources["BackgroundBrush"] = Color(dark ? "#17191B" : "#F5F5F2");
        resources["PanelBrush"] = Color(dark ? "#222628" : "#FFFFFF");
        resources["ForegroundBrush"] = Color(dark ? "#F5F7F6" : "#202823");
        resources["SecondaryBrush"] = Color(dark ? "#ADB6B2" : "#626C67");
        resources["AccentBrush"] = Color(dark ? "#57E6B2" : "#047857");
        resources["BorderBrush"] = Color(dark ? "#3C4340" : "#D6DDD7");
        resources["HoverBrush"] = Color(dark ? "#343D38" : "#E4EEE7");
        resources["SelectionBrush"] = Color(dark ? "#25483E" : "#C8EADD");
        resources["SegmentBrush"] = Color(dark ? "#303735" : "#E6EBE7");
        resources["SegmentSelectionBrush"] = Color(dark ? "#466257" : "#FFFFFF");
        resources["PopupBrush"] = Color(dark ? "#242A28" : "#FFFFFF");
    }
    public static void Initialize()
    {
        if (!trackingSystem) { trackingSystem = true; Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, _) => systemDark = null; }
        Apply(false);
        const string xaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Style TargetType="Window"><Setter Property="FontFamily" Value="Segoe UI, Microsoft YaHei UI"/><Setter Property="FontSize" Value="13"/><Setter Property="Background" Value="{DynamicResource BackgroundBrush}"/><Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/><Setter Property="UseLayoutRounding" Value="True"/></Style>
  <Style TargetType="TextBlock"><Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/><Setter Property="VerticalAlignment" Value="Center"/></Style>
  <Style TargetType="Control"><Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/><Setter Property="FontFamily" Value="Segoe UI, Microsoft YaHei UI"/></Style>
  <Style TargetType="Button">
    <Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/><Setter Property="Background" Value="{DynamicResource PanelBrush}"/><Setter Property="BorderBrush" Value="{DynamicResource BorderBrush}"/><Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="13,6"/><Setter Property="MinHeight" Value="30"/><Setter Property="Cursor" Value="Hand"/>
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button"><Grid><Border x:Name="Halo" CornerRadius="7" Background="{TemplateBinding Background}"/><Border x:Name="Body" CornerRadius="7" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"><ContentPresenter Margin="{TemplateBinding Padding}" HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True"/></Border></Grid><ControlTemplate.Triggers>
      <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Halo" Property="Effect"><Setter.Value><DropShadowEffect Color="#39E9B7" BlurRadius="19" ShadowDepth="0" Opacity="0.45"/></Setter.Value></Setter></Trigger>
      <Trigger Property="IsPressed" Value="True"><Setter TargetName="Body" Property="Background" Value="{DynamicResource SelectionBrush}"/></Trigger>
      <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Body" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger>
      <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.42"/><Setter TargetName="Halo" Property="Effect" Value="{x:Null}"/></Trigger>
    </ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType="ToggleButton"><Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/><Setter Property="Background" Value="{DynamicResource PanelBrush}"/><Setter Property="BorderBrush" Value="{DynamicResource BorderBrush}"/><Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="12,6"/><Setter Property="MinHeight" Value="30"/><Setter Property="Cursor" Value="Hand"/>
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ToggleButton"><Border x:Name="Body" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="6"><ContentPresenter Margin="{TemplateBinding Padding}" VerticalAlignment="Center" HorizontalAlignment="Center"/></Border><ControlTemplate.Triggers><Trigger Property="IsChecked" Value="True"><Setter TargetName="Body" Property="Background" Value="{DynamicResource SelectionBrush}"/><Setter Property="Foreground" Value="{DynamicResource AccentBrush}"/></Trigger><Trigger Property="IsPressed" Value="True"><Setter TargetName="Body" Property="Background" Value="{DynamicResource HoverBrush}"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Body" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.42"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
  <Style TargetType="TextBox"><Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/><Setter Property="Background" Value="{DynamicResource PanelBrush}"/><Setter Property="BorderBrush" Value="{DynamicResource BorderBrush}"/><Setter Property="Padding" Value="8,5"/><Setter Property="VerticalContentAlignment" Value="Center"/><Setter Property="MinHeight" Value="30"/></Style>
  <Style TargetType="DataGrid"><Setter Property="Background" Value="{DynamicResource PanelBrush}"/><Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/><Setter Property="BorderBrush" Value="{DynamicResource BorderBrush}"/><Setter Property="RowBackground" Value="{DynamicResource PanelBrush}"/><Setter Property="AlternatingRowBackground" Value="{DynamicResource BackgroundBrush}"/><Setter Property="HorizontalGridLinesBrush" Value="{DynamicResource BorderBrush}"/><Setter Property="GridLinesVisibility" Value="Horizontal"/><Setter Property="HeadersVisibility" Value="Column"/><Setter Property="RowHeight" Value="34"/><Setter Property="ColumnHeaderHeight" Value="35"/><Setter Property="EnableRowVirtualization" Value="True"/><Setter Property="EnableColumnVirtualization" Value="True"/></Style>
  <Style TargetType="DataGridColumnHeader"><Setter Property="Background" Value="{DynamicResource BackgroundBrush}"/><Setter Property="Foreground" Value="{DynamicResource SecondaryBrush}"/><Setter Property="Padding" Value="10,4"/><Setter Property="BorderThickness" Value="0,0,0,1"/><Setter Property="BorderBrush" Value="{DynamicResource BorderBrush}"/></Style>
  <Style TargetType="ListBox"><Setter Property="Background" Value="{DynamicResource PanelBrush}"/><Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/><Setter Property="BorderBrush" Value="{DynamicResource BorderBrush}"/></Style>
</ResourceDictionary>
""";
        Application.Current.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(xaml));
        ControlTheme.Initialize();
    }
}
