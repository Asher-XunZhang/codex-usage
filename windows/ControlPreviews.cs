using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexUsage;

// Exercises real popup presentation sources rather than only drawing closed controls.
// Synthetic content only; no host, backend, settings, or account access.
internal static class ControlPreviews
{
    public static async Task<JsonObject> Render(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new JsonArray();
        foreach (bool dark in new[] { false, true })
        {
            Theme.Apply(dark); string name = dark ? "dark" : "light";
            var content = new StackPanel { Margin = new Thickness(24) };
            var owner = new Window { Title = "控件验收", Width = 670, Height = 300, Content = content, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 60, Top = 60 };
            var segments = new StackPanel { Orientation = Orientation.Horizontal };
            var choices = new[] { "今天", "7 天", "30 天", "90 天", "全部" }.Select(label => new ToggleButton { Content = label, MinWidth = 58 }).ToArray();
            foreach (var button in choices)
            {
                segments.Children.Add(button);
                button.Click += (_, _) => { foreach (var item in choices) item.IsChecked = item == button; };
            }
            choices[0].IsChecked = true;
            var group = new SegmentedGroup(segments) { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 20) };
            content.Children.Add(group);
            var picker = new ComboBox { Width = 420, HorizontalAlignment = HorizontalAlignment.Left, MaxDropDownHeight = 205 };
            foreach (var label in new[] { "全部任务", "Windows 适配 / 完整中文任务名称与预算范围", "一个非常长的中文任务名称：检查下拉内容截断与鼠标悬浮显示完整文本，而不是挤出窗口边界" }.Concat(Enumerable.Range(4, 26).Select(i => "任务 " + i + " · 持续完善本机用量分析")))
                picker.Items.Add(new ComboBoxItem { Content = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 535 }, ToolTip = label, IsEnabled = picker.Items.Count != 5 });
            picker.SelectedIndex = 1; content.Children.Add(picker);
            var launcher = new Button { Content = "设置与菜单", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 20, 0, 0) }; content.Children.Add(launcher);
            var checkBox = new CheckBox { Content = "预算提醒 · 达到阈值时通知", IsChecked = true, Margin = new Thickness(0, 16, 0, 0) }; content.Children.Add(checkBox);
            var menu = new ContextMenu { PlacementTarget = launcher, Placement = PlacementMode.Bottom };
            var inactive = new MenuItem { Header = "今日 Token 42.7K · 周余 75%", IsEnabled = false }; menu.Items.Add(inactive); menu.Items.Add(new Separator());
            var selected = new MenuItem { Header = "状态栏与胶囊", IsCheckable = true, IsChecked = true }; menu.Items.Add(selected);
            var submenu = new MenuItem { Header = "主面板外观" }; menu.Items.Add(submenu);
            foreach (var label in new[] { "跟随系统", "浅色", "深色" }) submenu.Items.Add(new MenuItem { Header = label, IsCheckable = true, IsChecked = label == (dark ? "深色" : "浅色") });
            menu.Items.Add(new MenuItem { Header = "一段非常长的中文菜单选项，用于验证有限屏幕空间下的截断和完整提示文本，以及右侧符号仍然可见", ToolTip = "完整菜单提示" });
            menu.Items.Add(new Separator());
            bool invoked = false; var command = new MenuItem { Header = "刷新", InputGestureText = "Enter" }; command.Click += (_, _) => invoked = true; menu.Items.Add(command);
            menu.Items.Add(new MenuItem { Header = "退出", InputGestureText = "Ctrl+Q" });
            try
            {
                owner.Show(); await Settle();
                Check(choices.All(button => button.FocusVisualStyle == null && button.Margin == new Thickness(0) && button.BorderThickness == new Thickness(0)), "segments have one borderless base");
                choices[0].Focus();
                group.RaiseEvent(KeyEvent(owner, Key.Right, Keyboard.PreviewKeyDownEvent)); await Settle();
                Check(choices[1].IsChecked == true && choices.Count(button => button.IsChecked == true) == 1, "segment arrow key selects next");
                choices[2].IsEnabled = false; group.RaiseEvent(KeyEvent(owner, Key.Right, Keyboard.PreviewKeyDownEvent)); await Settle();
                Check(choices[3].IsChecked == true && !choices[2].IsKeyboardFocused, "segment keyboard skips disabled choice");
                choices[2].IsEnabled = true;
                Save(owner, Path.Combine(directory, "controls-" + name + ".png"));

                picker.IsDropDownOpen = true; await Settle();
                var drop = (Popup)picker.Template.FindName("PART_Popup", picker);
                Check(drop.IsOpen && PresentationSource.FromVisual(drop.Child) != null, "combobox uses a real popup HWND");
                var dropdown = (FrameworkElement)drop.Child;
                Check(dropdown.ActualWidth >= picker.ActualWidth && dropdown.ActualHeight <= picker.MaxDropDownHeight + 1, "dropdown width and scroll limit");
                Save(dropdown, Path.Combine(directory, "dropdown-" + name + ".png"));
                var scroll = Descendants<ScrollViewer>(dropdown).First();
                Check(scroll.ScrollableHeight > 0, "long dropdown scrolls");
                scroll.ScrollToEnd(); await Settle();
                Check(scroll.VerticalOffset > 0, "dropdown scroll navigation");
                Save(dropdown, Path.Combine(directory, "dropdown-scrolled-" + name + ".png"));
                picker.RaiseEvent(KeyEvent(owner, Key.Escape)); await Settle();
                Check(!picker.IsDropDownOpen, "escape dismisses dropdown");
                picker.SelectedIndex = 0; picker.Focus(); picker.IsDropDownOpen = true; await Settle();
                picker.RaiseEvent(KeyEvent(owner, Key.Down)); picker.RaiseEvent(KeyEvent(owner, Key.Enter)); await Settle();
                Check(picker.SelectedIndex == 1 && !picker.IsDropDownOpen, "dropdown arrow and Enter select an item");
                Check(checkBox.FocusVisualStyle == null && checkBox.Template.FindName("Check", checkBox) is System.Windows.Shapes.Path, "checkbox uses themed selection and solid focus");

                menu.IsOpen = true; await Settle();
                Check(PresentationSource.FromVisual(menu) != null, "context menu uses a real popup HWND");
                Check(menu.Template.FindName("MenuChrome", menu) is Border && selected.Template.FindName("Check", selected) is System.Windows.Shapes.Path, "custom menu and trailing check templates");
                Check(selected.FocusVisualStyle == null && !inactive.IsEnabled, "menu selected and disabled states");
                Check(((System.Windows.Shapes.Path)selected.Template.FindName("Check", selected)).Visibility == Visibility.Visible
                    && ((System.Windows.Shapes.Path)inactive.Template.FindName("Check", inactive)).Visibility == Visibility.Collapsed, "only checked menus show a trailing check");
                Save(menu, Path.Combine(directory, "menu-" + name + ".png"));
                submenu.Focus(); submenu.IsSubmenuOpen = true; await Settle();
                var sub = (Popup)submenu.Template.FindName("PART_Popup", submenu);
                Check(sub.IsOpen && PresentationSource.FromVisual(sub.Child) != null, "submenu opens in a themed popup HWND");
                Save((FrameworkElement)sub.Child, Path.Combine(directory, "submenu-" + name + ".png"));
                submenu.IsSubmenuOpen = false; command.Focus(); command.RaiseEvent(KeyEvent(owner, Key.Enter)); await Settle();
                Check(invoked, "menu Enter invokes command");
                menu.IsOpen = false;

                var longMenu = new ContextMenu { PlacementTarget = launcher, Placement = PlacementMode.Bottom, MaxHeight = 190 };
                foreach (int i in Enumerable.Range(1, 30)) longMenu.Items.Add(new MenuItem { Header = "预算选项 " + i });
                try
                {
                    longMenu.IsOpen = true; await Settle(); var menuScroll = Descendants<ScrollViewer>(longMenu).First();
                    Check(menuScroll.ScrollableHeight > 0, "long context menu scrolls"); menuScroll.ScrollToEnd(); await Settle();
                    Save(longMenu, Path.Combine(directory, "menu-scrolled-" + name + ".png"));
                }
                finally { longMenu.IsOpen = false; }
            }
            finally { menu.IsOpen = false; picker.IsDropDownOpen = false; owner.Close(); }
        }
        foreach (string check in new[] { "segments-continuous", "segments-keyboard", "combo-popup", "combo-scroll", "combo-escape", "combo-keyboard", "menu-popup", "menu-check-disabled", "submenu-popup", "menu-enter", "menu-scroll", "checkbox-themed" }) checks.Add(check);
        return new JsonObject { ["success"] = true, ["version"] = 1, ["themes"] = new JsonArray("light", "dark"), ["checks"] = checks, ["images"] = new JsonArray(Directory.GetFiles(directory, "*.png").Select(path => (JsonNode?)JsonValue.Create(Path.GetFileName(path))).ToArray()) };
    }

    private static KeyEventArgs KeyEvent(Visual source, Key key, RoutedEvent? routedEvent = null) => new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(source), Environment.TickCount, key) { RoutedEvent = routedEvent ?? Keyboard.KeyDownEvent };
    private static async Task Settle() { await Task.Delay(180); await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
    private static void Check(bool condition, string text) { if (!condition) throw new InvalidOperationException("Control preview: " + text); }
    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) { var child = VisualTreeHelper.GetChild(node, i); if (child is T match) yield return match; foreach (var descendant in Descendants<T>(child)) yield return descendant; }
    }
    private static void Save(FrameworkElement view, string path)
    {
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(view.ActualWidth)), Math.Max(1, (int)Math.Ceiling(view.ActualHeight)), 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
}
