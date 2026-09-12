using System.Windows;
using System.Windows.Markup;

namespace CodexUsage;

// Popup content has a separate HWND. Supply the entire visual tree so it never
// falls back to the platform menu gutter, check box, or gray chrome.
internal static class ControlTheme
{
    public static void Initialize()
    {
        const string xaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Style x:Key="UsageSearchBox" TargetType="TextBox">
    <Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/><Setter Property="Background" Value="{DynamicResource PanelBrush}"/><Setter Property="BorderBrush" Value="{DynamicResource BorderBrush}"/><Setter Property="BorderThickness" Value="1"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/><Setter Property="Padding" Value="0"/><Setter Property="VerticalContentAlignment" Value="Center"/><Setter Property="MinHeight" Value="32"/>
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TextBox">
      <Border x:Name="SearchChrome" CornerRadius="16" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}">
        <Grid Margin="12,3,13,3"><Grid.ColumnDefinitions><ColumnDefinition Width="22"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
          <Path Data="M 11,5 A 4.5,4.5 0 1 1 2,5 A 4.5,4.5 0 1 1 11,5 M 9.5,8.5 L 13.5,12.5" Stroke="{DynamicResource SecondaryBrush}" StrokeThickness="1.4" StrokeStartLineCap="Round" StrokeEndLineCap="Round" Width="14" Height="14" HorizontalAlignment="Left" VerticalAlignment="Center" IsHitTestVisible="False"/>
          <ScrollViewer x:Name="PART_ContentHost" Grid.Column="1" VerticalAlignment="Center" HorizontalScrollBarVisibility="Hidden" VerticalScrollBarVisibility="Hidden"/>
          <TextBlock x:Name="Placeholder" Grid.Column="1" Text="搜索模型或任务" Foreground="{DynamicResource SecondaryBrush}" VerticalAlignment="Center" IsHitTestVisible="False" Visibility="Collapsed"/>
        </Grid>
      </Border>
      <ControlTemplate.Triggers>
        <Trigger Property="Text" Value=""><Setter TargetName="Placeholder" Property="Visibility" Value="Visible"/></Trigger>
        <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="SearchChrome" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger>
        <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="SearchChrome" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger>
        <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.42"/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style x:Key="PopupScrollThumb" TargetType="Thumb">
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Thumb"><Border x:Name="ThumbBody" Background="{DynamicResource BorderBrush}" CornerRadius="3" Margin="1,2"/><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="ThumbBody" Property="Background" Value="{DynamicResource SecondaryBrush}"/></Trigger><Trigger Property="IsDragging" Value="True"><Setter TargetName="ThumbBody" Property="Background" Value="{DynamicResource AccentBrush}"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style x:Key="PopupScrollBar" TargetType="ScrollBar">
    <Setter Property="Width" Value="8"/><Setter Property="MinWidth" Value="0"/><Setter Property="Background" Value="Transparent"/><Setter Property="Focusable" Value="False"/>
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ScrollBar"><Grid Background="Transparent"><Track x:Name="PART_Track" IsDirectionReversed="True"><Track.DecreaseRepeatButton><RepeatButton Command="ScrollBar.PageUpCommand" Focusable="False"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton><Track.Thumb><Thumb Style="{StaticResource PopupScrollThumb}" MinHeight="22"/></Track.Thumb><Track.IncreaseRepeatButton><RepeatButton Command="ScrollBar.PageDownCommand" Focusable="False"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton></Track></Grid></ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style x:Key="PopupScrollViewer" TargetType="ScrollViewer">
    <Setter Property="HorizontalScrollBarVisibility" Value="Disabled"/><Setter Property="VerticalScrollBarVisibility" Value="Auto"/><Setter Property="CanContentScroll" Value="True"/><Setter Property="Focusable" Value="False"/>
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ScrollViewer"><Grid><Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions><ScrollContentPresenter x:Name="PART_ScrollContentPresenter" Content="{TemplateBinding Content}" ContentTemplate="{TemplateBinding ContentTemplate}" CanContentScroll="{TemplateBinding CanContentScroll}" Margin="{TemplateBinding Padding}"/><ScrollBar x:Name="PART_VerticalScrollBar" Grid.Column="1" Style="{StaticResource PopupScrollBar}" Maximum="{TemplateBinding ScrollableHeight}" ViewportSize="{TemplateBinding ViewportHeight}" Value="{Binding VerticalOffset,RelativeSource={RelativeSource TemplatedParent},Mode=OneWay}" Visibility="{TemplateBinding ComputedVerticalScrollBarVisibility}"/></Grid></ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType="ToolTip">
    <Setter Property="Background" Value="{DynamicResource PopupBrush}"/><Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/><Setter Property="BorderBrush" Value="{DynamicResource BorderBrush}"/><Setter Property="Padding" Value="10,7"/><Setter Property="MaxWidth" Value="520"/>
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ToolTip"><Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="7" Padding="{TemplateBinding Padding}"><ContentPresenter RecognizesAccessKey="True" TextBlock.Foreground="{TemplateBinding Foreground}"/></Border></ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType="ContextMenu">
    <Setter Property="OverridesDefaultStyle" Value="True"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/><Setter Property="FontFamily" Value="Segoe UI, Microsoft YaHei UI"/><Setter Property="FontSize" Value="13"/>
    <Setter Property="Background" Value="{DynamicResource PopupBrush}"/><Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/><Setter Property="BorderBrush" Value="{DynamicResource BorderBrush}"/><Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="5"/><Setter Property="MinWidth" Value="156"/><Setter Property="MaxWidth" Value="500"/><Setter Property="MaxHeight" Value="480"/><Setter Property="SnapsToDevicePixels" Value="True"/><Setter Property="UseLayoutRounding" Value="True"/>
    <Setter Property="KeyboardNavigation.DirectionalNavigation" Value="Cycle"/><Setter Property="KeyboardNavigation.TabNavigation" Value="Cycle"/>
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ContextMenu"><Border x:Name="MenuChrome" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="10" Padding="{TemplateBinding Padding}"><ScrollViewer Style="{StaticResource PopupScrollViewer}"><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Cycle" KeyboardNavigation.TabNavigation="Cycle"/></ScrollViewer></Border></ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType="MenuItem">
    <Setter Property="OverridesDefaultStyle" Value="True"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/><Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/><Setter Property="FontFamily" Value="Segoe UI, Microsoft YaHei UI"/><Setter Property="FontSize" Value="13"/><Setter Property="Background" Value="Transparent"/><Setter Property="Padding" Value="10,7"/><Setter Property="MinHeight" Value="32"/><Setter Property="HorizontalContentAlignment" Value="Stretch"/>
    <Setter Property="HeaderTemplate"><Setter.Value><DataTemplate><TextBlock Text="{Binding}" TextTrimming="CharacterEllipsis" MaxWidth="420" ToolTip="{Binding}"/></DataTemplate></Setter.Value></Setter>
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="MenuItem"><Grid>
      <Border x:Name="ItemBody" CornerRadius="6" Background="{TemplateBinding Background}" BorderThickness="1" BorderBrush="Transparent" Padding="{TemplateBinding Padding}">
        <Grid><Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
          <ContentPresenter x:Name="Icon" Content="{TemplateBinding Icon}" VerticalAlignment="Center" Margin="0,0,8,0"/>
          <ContentPresenter Grid.Column="1" x:Name="Header" ContentSource="Header" RecognizesAccessKey="True" VerticalAlignment="Center" HorizontalAlignment="Stretch"/>
          <TextBlock x:Name="Gesture" Grid.Column="2" Text="{TemplateBinding InputGestureText}" Foreground="{DynamicResource SecondaryBrush}" FontSize="11" Margin="16,0,0,0"/>
          <Path x:Name="Check" Grid.Column="3" Data="M 0,4 L 3.5,7.5 L 10,0.5" Stroke="{DynamicResource AccentBrush}" StrokeThickness="1.7" StrokeStartLineCap="Round" StrokeEndLineCap="Round" Margin="13,0,1,0" Width="11" Height="9" VerticalAlignment="Center" Visibility="Collapsed"/>
          <Path x:Name="Arrow" Grid.Column="4" Data="M 0,0 L 4,4 L 0,8" Stroke="{DynamicResource SecondaryBrush}" StrokeThickness="1.4" StrokeStartLineCap="Round" StrokeEndLineCap="Round" Margin="16,0,1,0" Width="5" Height="9" VerticalAlignment="Center" Visibility="Collapsed"/>
        </Grid>
      </Border>
      <Popup x:Name="PART_Popup" AllowsTransparency="True" Focusable="False" IsOpen="{Binding IsSubmenuOpen,RelativeSource={RelativeSource TemplatedParent}}" Placement="Right" HorizontalOffset="2" VerticalOffset="-6" PopupAnimation="Fade">
        <Border x:Name="SubmenuChrome" Background="{DynamicResource PopupBrush}" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" CornerRadius="10" Padding="5" MinWidth="156" MaxWidth="500" MaxHeight="480" KeyboardNavigation.DirectionalNavigation="Cycle" KeyboardNavigation.TabNavigation="Cycle">
          <ScrollViewer Style="{StaticResource PopupScrollViewer}"><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Cycle" KeyboardNavigation.TabNavigation="Cycle"/></ScrollViewer>
        </Border>
      </Popup>
    </Grid><ControlTemplate.Triggers>
      <Trigger Property="Icon" Value="{x:Null}"><Setter TargetName="Icon" Property="Visibility" Value="Collapsed"/></Trigger>
      <Trigger Property="InputGestureText" Value=""><Setter TargetName="Gesture" Property="Visibility" Value="Collapsed"/></Trigger>
      <Trigger Property="IsChecked" Value="True"><Setter TargetName="Check" Property="Visibility" Value="Visible"/><Setter TargetName="ItemBody" Property="Background" Value="{DynamicResource SelectionBrush}"/></Trigger>
      <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="ItemBody" Property="Background" Value="{DynamicResource HoverBrush}"/></Trigger>
      <Trigger Property="IsSubmenuOpen" Value="True"><Setter TargetName="ItemBody" Property="Background" Value="{DynamicResource HoverBrush}"/></Trigger>
      <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="ItemBody" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger>
      <Trigger Property="HasItems" Value="True"><Setter TargetName="Arrow" Property="Visibility" Value="Visible"/></Trigger>
      <Trigger Property="IsEnabled" Value="False"><Setter TargetName="ItemBody" Property="Opacity" Value="0.42"/></Trigger>
    </ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType="Separator"><Setter Property="Focusable" Value="False"/><Setter Property="IsHitTestVisible" Value="False"/><Setter Property="Margin" Value="8,4"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Separator"><Border Height="1" Background="{DynamicResource BorderBrush}"/></ControlTemplate></Setter.Value></Setter></Style>
  <Style x:Key="{x:Static MenuItem.SeparatorStyleKey}" TargetType="Separator" BasedOn="{StaticResource {x:Type Separator}}"/>
  <Style TargetType="ComboBox">
    <Setter Property="OverridesDefaultStyle" Value="True"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/><Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/><Setter Property="Background" Value="{DynamicResource PanelBrush}"/><Setter Property="BorderBrush" Value="{DynamicResource BorderBrush}"/><Setter Property="BorderThickness" Value="1"/><Setter Property="MinHeight" Value="30"/><Setter Property="Padding" Value="9,4"/><Setter Property="HorizontalContentAlignment" Value="Stretch"/><Setter Property="ScrollViewer.HorizontalScrollBarVisibility" Value="Disabled"/><Setter Property="ScrollViewer.VerticalScrollBarVisibility" Value="Auto"/>
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBox"><Grid>
      <Border x:Name="Halo" Background="{TemplateBinding Background}" CornerRadius="7"/>
      <ToggleButton x:Name="Toggle" Focusable="False" FocusVisualStyle="{x:Null}" ClickMode="Press" IsChecked="{Binding IsDropDownOpen,RelativeSource={RelativeSource TemplatedParent},Mode=TwoWay}"><ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border Background="Transparent"/></ControlTemplate></ToggleButton.Template></ToggleButton>
      <Border x:Name="ComboChrome" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="7" IsHitTestVisible="False">
        <Grid><Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="26"/></Grid.ColumnDefinitions><ContentPresenter x:Name="Selection" Margin="{TemplateBinding Padding}" Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}" ContentTemplateSelector="{TemplateBinding ItemTemplateSelector}" ContentStringFormat="{TemplateBinding SelectionBoxItemStringFormat}" VerticalAlignment="Center" HorizontalAlignment="Stretch"/><Path Grid.Column="1" Data="M 0,0 L 3.5,3.5 L 7,0" Stroke="{DynamicResource SecondaryBrush}" StrokeThickness="1.4" StrokeStartLineCap="Round" StrokeEndLineCap="Round" Width="8" Height="5" HorizontalAlignment="Center" VerticalAlignment="Center"/></Grid>
      </Border>
      <TextBox x:Name="PART_EditableTextBox" Margin="9,2,27,2" BorderThickness="0" Padding="0" MinHeight="0" Visibility="Collapsed" Background="Transparent" Foreground="{TemplateBinding Foreground}" IsReadOnly="{TemplateBinding IsReadOnly}"/>
      <Popup x:Name="PART_Popup" AllowsTransparency="True" Focusable="False" IsOpen="{Binding IsDropDownOpen,RelativeSource={RelativeSource TemplatedParent}}" Placement="Bottom" VerticalOffset="4" PopupAnimation="Fade">
        <Border x:Name="DropDownChrome" Background="{DynamicResource PopupBrush}" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" CornerRadius="9" Padding="4" MinWidth="{Binding ActualWidth,RelativeSource={RelativeSource TemplatedParent}}" MaxWidth="640" MaxHeight="{Binding MaxDropDownHeight,RelativeSource={RelativeSource TemplatedParent}}"><ScrollViewer Style="{StaticResource PopupScrollViewer}"><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/></ScrollViewer></Border>
      </Popup>
    </Grid><ControlTemplate.Triggers>
      <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Halo" Property="Effect"><Setter.Value><DropShadowEffect Color="#39E9B7" BlurRadius="18" ShadowDepth="0" Opacity="0.26"/></Setter.Value></Setter></Trigger>
      <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="ComboChrome" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger>
      <Trigger Property="IsDropDownOpen" Value="True"><Setter TargetName="ComboChrome" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger>
      <Trigger Property="IsEditable" Value="True"><Setter TargetName="PART_EditableTextBox" Property="Visibility" Value="Visible"/><Setter TargetName="Selection" Property="Visibility" Value="Hidden"/></Trigger>
      <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.42"/><Setter TargetName="Halo" Property="Effect" Value="{x:Null}"/></Trigger>
    </ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType="ComboBoxItem">
    <Setter Property="OverridesDefaultStyle" Value="True"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/><Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/><Setter Property="HorizontalContentAlignment" Value="Stretch"/><Setter Property="Padding" Value="9,7"/><Setter Property="MinHeight" Value="32"/>
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBoxItem"><Border x:Name="ItemBody" Background="Transparent" BorderBrush="Transparent" BorderThickness="1" CornerRadius="5" Padding="{TemplateBinding Padding}"><Grid><Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions><ContentPresenter VerticalAlignment="Center" HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"/><Path x:Name="Check" Grid.Column="1" Data="M 0,4 L 3.5,7.5 L 10,0.5" Stroke="{DynamicResource AccentBrush}" StrokeThickness="1.7" StrokeStartLineCap="Round" StrokeEndLineCap="Round" Margin="13,0,1,0" Width="11" Height="9" VerticalAlignment="Center" Visibility="Hidden"/></Grid></Border><ControlTemplate.Triggers>
      <Trigger Property="IsSelected" Value="True"><Setter TargetName="ItemBody" Property="Background" Value="{DynamicResource SelectionBrush}"/><Setter TargetName="Check" Property="Visibility" Value="Visible"/></Trigger>
      <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="ItemBody" Property="Background" Value="{DynamicResource HoverBrush}"/></Trigger>
      <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="ItemBody" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger>
      <Trigger Property="IsEnabled" Value="False"><Setter TargetName="ItemBody" Property="Opacity" Value="0.42"/></Trigger>
    </ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style x:Key="SegmentButton" TargetType="ToggleButton">
    <Setter Property="FocusVisualStyle" Value="{x:Null}"/><Setter Property="Foreground" Value="{DynamicResource SecondaryBrush}"/><Setter Property="Background" Value="Transparent"/><Setter Property="BorderThickness" Value="0"/><Setter Property="Padding" Value="10,3"/><Setter Property="MinHeight" Value="24"/><Setter Property="Cursor" Value="Hand"/>
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ToggleButton"><Border x:Name="Body" Background="Transparent" CornerRadius="5" BorderBrush="Transparent" BorderThickness="1"><ContentPresenter Margin="{TemplateBinding Padding}" HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True"/></Border><ControlTemplate.Triggers><Trigger Property="IsChecked" Value="True"><Setter Property="Foreground" Value="{DynamicResource AccentBrush}"/><Setter Property="FontWeight" Value="SemiBold"/></Trigger><Trigger Property="IsMouseOver" Value="True"><Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Body" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.42"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType="CheckBox">
    <Setter Property="FocusVisualStyle" Value="{x:Null}"/><Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/><Setter Property="VerticalContentAlignment" Value="Center"/><Setter Property="MinHeight" Value="22"/>
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="CheckBox"><Grid Background="Transparent"><Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions><Grid Width="18" Height="18" VerticalAlignment="Center"><Border x:Name="FocusRing" Margin="-3" CornerRadius="6" BorderThickness="1" BorderBrush="Transparent"/><Border x:Name="Box" Background="{DynamicResource PanelBrush}" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" CornerRadius="4"/><Path x:Name="Check" Data="M 0,4 L 3.5,7.5 L 10,0.5" Stroke="{DynamicResource BackgroundBrush}" StrokeThickness="1.8" StrokeStartLineCap="Round" StrokeEndLineCap="Round" Width="11" Height="9" Visibility="Collapsed"/><Border x:Name="Mixed" Height="2" Width="8" Background="{DynamicResource BackgroundBrush}" Visibility="Collapsed"/></Grid><ContentPresenter Grid.Column="1" Margin="9,0,0,0" VerticalAlignment="Center" RecognizesAccessKey="True"/></Grid><ControlTemplate.Triggers>
      <Trigger Property="IsChecked" Value="True"><Setter TargetName="Check" Property="Visibility" Value="Visible"/><Setter TargetName="Box" Property="Background" Value="{DynamicResource AccentBrush}"/><Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger>
      <Trigger Property="IsChecked" Value="{x:Null}"><Setter TargetName="Mixed" Property="Visibility" Value="Visible"/><Setter TargetName="Box" Property="Background" Value="{DynamicResource AccentBrush}"/></Trigger>
      <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger>
      <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="FocusRing" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger>
      <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.42"/></Trigger>
    </ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
  </Style>
</ResourceDictionary>
""";
        Application.Current.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(xaml));
        // Focus remains visible through our solid accent ring, without the system's dotted adorner.
        foreach (var type in new[] { typeof(System.Windows.Controls.Button), typeof(System.Windows.Controls.Primitives.ToggleButton) })
        {
            var style = new Style(type, (Style)Application.Current.FindResource(type));
            style.Setters.Add(new Setter(System.Windows.Controls.Control.FocusVisualStyleProperty, null));
            Application.Current.Resources[type] = style;
        }
    }
}
