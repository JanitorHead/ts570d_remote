using System.Windows;
using System.Windows.Media;

namespace TS570_Remote.Controls;

/// <summary>Attached properties consumed by the front-panel key and lamp templates.</summary>
public static class PanelFx
{
    /// <summary>Function engaged: keys light their amber halo, lamps switch on.</summary>
    public static readonly DependencyProperty IsLitProperty = DependencyProperty.RegisterAttached(
        "IsLit", typeof(bool), typeof(PanelFx), new FrameworkPropertyMetadata(false));

    /// <summary>Corner radius of the key cap; large values give round or pill keys.</summary>
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(CornerRadius), typeof(PanelFx), new FrameworkPropertyMetadata(new CornerRadius(4)));

    /// <summary>Legend color while lit (light keys need a darker amber than dark keys).</summary>
    public static readonly DependencyProperty LitForegroundProperty = DependencyProperty.RegisterAttached(
        "LitForeground", typeof(Brush), typeof(PanelFx),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x3A))));

    public static bool GetIsLit(DependencyObject d) => (bool)d.GetValue(IsLitProperty);
    public static void SetIsLit(DependencyObject d, bool value) => d.SetValue(IsLitProperty, value);

    public static CornerRadius GetCornerRadius(DependencyObject d) => (CornerRadius)d.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject d, CornerRadius value) => d.SetValue(CornerRadiusProperty, value);

    public static Brush GetLitForeground(DependencyObject d) => (Brush)d.GetValue(LitForegroundProperty);
    public static void SetLitForeground(DependencyObject d, Brush value) => d.SetValue(LitForegroundProperty, value);
}
