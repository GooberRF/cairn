using System.Windows;
using System.Windows.Controls;

namespace Cairn.Viewport;

/// <summary>The transport under the viewport; everything is bound to a <c>PlaybackViewModel</c>.</summary>
public partial class TransportBar : UserControl
{
    /// <summary>Module-specific content shown after the speed box (null = nothing).</summary>
    public static readonly DependencyProperty ExtraContentProperty = DependencyProperty.Register(
        nameof(ExtraContent), typeof(object), typeof(TransportBar), new PropertyMetadata(null));

    public TransportBar() => InitializeComponent();

    /// <summary>Module-specific content shown after the speed box (null = nothing).</summary>
    public object? ExtraContent
    {
        get => GetValue(ExtraContentProperty);
        set => SetValue(ExtraContentProperty, value);
    }
}
