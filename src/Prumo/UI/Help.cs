using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Prumo.UI;

/// <summary>
/// Small "?" next to an option: hovering shows <see cref="Text"/> right away. Clicks are swallowed, so a "?"
/// inside a switch's label does not flip the switch.
/// </summary>
public sealed class Help : Border
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(Help), new PropertyMetadata("", (d, e) => ((Help)d)._body.Text = (string)e.NewValue));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    readonly TextBlock _body = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 340, FontSize = 12.5, LineHeight = 18 };

    public Help()
    {
        Width = Height = 16;
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        Margin = new Thickness(6, 0, 0, 0);
        VerticalAlignment = VerticalAlignment.Center;
        Cursor = Cursors.Help;
        Background = Brushes.Transparent;
        SetResourceReference(BorderBrushProperty, "MutedBrush");

        var q = new TextBlock { Text = "?", FontSize = 10.5, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        q.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Child = q;

        // The look comes from the ToolTip style in Theme.xaml (follows the theme).
        ToolTip = new ToolTip { Content = _body, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        ToolTipService.SetInitialShowDelay(this, 0);
        ToolTipService.SetBetweenShowDelay(this, 0);
        ToolTipService.SetShowDuration(this, 120_000);

        MouseEnter += (_, _) => { SetResourceReference(BorderBrushProperty, "Accent"); q.SetResourceReference(TextBlock.ForegroundProperty, "Accent"); };
        MouseLeave += (_, _) => { SetResourceReference(BorderBrushProperty, "MutedBrush"); q.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); };
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) => e.Handled = true;   // not a click on the switch around it

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => e.Handled = true;
}
