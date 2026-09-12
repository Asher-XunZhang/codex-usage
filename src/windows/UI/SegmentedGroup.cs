using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace CodexUsage;

// One continuous base and one animated selection surface shared by all segments.
// Existing ToggleButton click handlers remain the source of the application action.
internal sealed class SegmentedGroup : Border
{
    private readonly Panel buttons;
    private readonly Border selection = new() { CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
    private readonly TranslateTransform position = new();
    private Rect previous = Rect.Empty;
    private bool queued;

    public SegmentedGroup(Panel buttons)
    {
        this.buttons = buttons;
        CornerRadius = new CornerRadius(8); Padding = new Thickness(3);
        SetResourceReference(BackgroundProperty, "SegmentBrush");
        selection.SetResourceReference(BackgroundProperty, "SegmentSelectionBrush");
        selection.RenderTransform = position;
        var layers = new Grid(); layers.Children.Add(selection); layers.Children.Add(buttons); Child = layers;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Once);
        var style = new Style(typeof(Border));
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(EffectProperty, new DropShadowEffect { Color = Color.FromRgb(57, 233, 183), BlurRadius = 18, ShadowDepth = 0, Opacity = .28 }));
        style.Triggers.Add(hover); Style = style;
        foreach (var button in buttons.Children.OfType<ToggleButton>())
        {
            button.Style = (Style)Application.Current.FindResource("SegmentButton");
            button.Margin = new Thickness(0);
            button.Checked += (_, _) => QueueSelection();
            button.Unchecked += (_, _) => QueueSelection();
            button.Click += (_, _) => { button.IsChecked = true; QueueSelection(); };
            button.SizeChanged += (_, _) => QueueSelection();
        }
        Loaded += (_, _) => UpdateSelection(false);
        SizeChanged += (_, _) => QueueSelection();
        PreviewKeyDown += OnKey;
    }

    private void QueueSelection()
    {
        if (queued || !IsLoaded) return;
        queued = true;
        Dispatcher.BeginInvoke(() => { queued = false; UpdateSelection(true); }, DispatcherPriority.Loaded);
    }

    private void UpdateSelection(bool animate)
    {
        var selected = buttons.Children.OfType<ToggleButton>().FirstOrDefault(button => button.IsChecked == true);
        if (selected == null || selected.ActualWidth <= 0 || selected.ActualHeight <= 0) return;
        var point = selected.TranslatePoint(new Point(), buttons);
        var bounds = new Rect(point, new Size(selected.ActualWidth, selected.ActualHeight));
        if (bounds == previous) return;
        bool move = animate && !previous.IsEmpty && SystemParameters.ClientAreaAnimation;
        previous = bounds;
        var duration = new Duration(TimeSpan.FromMilliseconds(move ? 150 : 0));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        void Shift(Animatable target, DependencyProperty property, double value)
        {
            if (!move) { target.BeginAnimation(property, null); target.SetValue(property, value); return; }
            target.BeginAnimation(property, new DoubleAnimation(value, duration) { EasingFunction = ease });
        }
        Shift(position, TranslateTransform.XProperty, bounds.X);
        Shift(position, TranslateTransform.YProperty, bounds.Y);
        selection.Width = bounds.Width; selection.Height = bounds.Height;
        foreach (var button in buttons.Children.OfType<ToggleButton>()) button.IsTabStop = button == selected;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right or Key.Home or Key.End)) return;
        var options = buttons.Children.OfType<ToggleButton>().Where(button => button.IsEnabled).ToArray();
        if (options.Length == 0) return;
        int index = Array.FindIndex(options, button => button.IsKeyboardFocused);
        if (index < 0) index = Array.FindIndex(options, button => button.IsChecked == true);
        int next = e.Key == Key.Home ? 0 : e.Key == Key.End ? options.Length - 1 : (index + (e.Key == Key.Right ? 1 : -1) + options.Length) % options.Length;
        options[next].IsChecked = true;
        options[next].Focus();
        options[next].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        e.Handled = true;
    }
}
