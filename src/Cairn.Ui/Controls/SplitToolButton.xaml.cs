using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Cairn.Ui.Controls;

/// <summary>
/// A toolbar button with two ways in: clicking it runs the usual action, clicking the arrow beside
/// it offers that action and one other. Add Frames uses it — a plain click still opens the file
/// picker, exactly as it did before this control existed, and the arrow is where Add Frames from
/// VPP… lives.
///
/// Both halves are ordinary buttons, so both are tab stops and both work from the keyboard, and
/// both wear the <c>ToolButton</c> chrome rather than a template of their own.
/// </summary>
public partial class SplitToolButton : UserControl
{
    /// <summary>The glyph shown on the primary half, from Segoe MDL2 Assets.</summary>
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(SplitToolButton), new PropertyMetadata(""));

    /// <summary>Point size of the glyph.</summary>
    public static readonly DependencyProperty GlyphSizeProperty = DependencyProperty.Register(
        nameof(GlyphSize), typeof(double), typeof(SplitToolButton), new PropertyMetadata(12.0));

    /// <summary>What a plain click does.</summary>
    public static readonly DependencyProperty PrimaryCommandProperty = DependencyProperty.Register(
        nameof(PrimaryCommand), typeof(ICommand), typeof(SplitToolButton), new PropertyMetadata(null));

    /// <summary>What the arrow's second entry does.</summary>
    public static readonly DependencyProperty SecondaryCommandProperty = DependencyProperty.Register(
        nameof(SecondaryCommand), typeof(ICommand), typeof(SplitToolButton), new PropertyMetadata(null));

    /// <summary>The primary action's menu label, e.g. "_Browse…".</summary>
    public static readonly DependencyProperty PrimaryLabelProperty = DependencyProperty.Register(
        nameof(PrimaryLabel), typeof(string), typeof(SplitToolButton), new PropertyMetadata("_Browse…"));

    /// <summary>The second action's menu label, e.g. "From _VPP…".</summary>
    public static readonly DependencyProperty SecondaryLabelProperty = DependencyProperty.Register(
        nameof(SecondaryLabel), typeof(string), typeof(SplitToolButton), new PropertyMetadata("From _VPP…"));

    /// <summary>Tooltip for the primary half.</summary>
    public static readonly DependencyProperty PrimaryToolTipProperty = DependencyProperty.Register(
        nameof(PrimaryToolTip), typeof(string), typeof(SplitToolButton), new PropertyMetadata(string.Empty));

    /// <summary>Tooltip for the second entry, also used on the arrow.</summary>
    public static readonly DependencyProperty SecondaryToolTipProperty = DependencyProperty.Register(
        nameof(SecondaryToolTip), typeof(string), typeof(SplitToolButton), new PropertyMetadata(string.Empty));

    /// <summary>The arrow's own tooltip.</summary>
    public static readonly DependencyProperty MenuToolTipProperty = DependencyProperty.Register(
        nameof(MenuToolTip), typeof(string), typeof(SplitToolButton),
        new PropertyMetadata("More ways to add frames"));

    /// <summary>
    /// Whether both halves are usable. Bound separately from the command so a toolbar that is
    /// disabled because no document is open greys the whole control, not just the half whose
    /// command happens to say no.
    /// </summary>
    public static readonly DependencyProperty IsActionEnabledProperty = DependencyProperty.Register(
        nameof(IsActionEnabled), typeof(bool), typeof(SplitToolButton), new PropertyMetadata(true));

    public SplitToolButton() => InitializeComponent();

    /// <summary>
    /// Optional: builds the drop-down's items (called each time the arrow opens it) instead of the
    /// primary + secondary pair, for a button whose arrow offers more than one other action.
    /// </summary>
    public System.Func<System.Collections.Generic.IEnumerable<object>>? MenuItemsFactory { get; set; }

    /// <inheritdoc cref="GlyphProperty" />
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <inheritdoc cref="GlyphSizeProperty" />
    public double GlyphSize
    {
        get => (double)GetValue(GlyphSizeProperty);
        set => SetValue(GlyphSizeProperty, value);
    }

    /// <inheritdoc cref="PrimaryCommandProperty" />
    public ICommand? PrimaryCommand
    {
        get => (ICommand?)GetValue(PrimaryCommandProperty);
        set => SetValue(PrimaryCommandProperty, value);
    }

    /// <inheritdoc cref="SecondaryCommandProperty" />
    public ICommand? SecondaryCommand
    {
        get => (ICommand?)GetValue(SecondaryCommandProperty);
        set => SetValue(SecondaryCommandProperty, value);
    }

    /// <inheritdoc cref="PrimaryLabelProperty" />
    public string PrimaryLabel
    {
        get => (string)GetValue(PrimaryLabelProperty);
        set => SetValue(PrimaryLabelProperty, value);
    }

    /// <inheritdoc cref="SecondaryLabelProperty" />
    public string SecondaryLabel
    {
        get => (string)GetValue(SecondaryLabelProperty);
        set => SetValue(SecondaryLabelProperty, value);
    }

    /// <inheritdoc cref="PrimaryToolTipProperty" />
    public string PrimaryToolTip
    {
        get => (string)GetValue(PrimaryToolTipProperty);
        set => SetValue(PrimaryToolTipProperty, value);
    }

    /// <inheritdoc cref="SecondaryToolTipProperty" />
    public string SecondaryToolTip
    {
        get => (string)GetValue(SecondaryToolTipProperty);
        set => SetValue(SecondaryToolTipProperty, value);
    }

    /// <inheritdoc cref="MenuToolTipProperty" />
    public string MenuToolTip
    {
        get => (string)GetValue(MenuToolTipProperty);
        set => SetValue(MenuToolTipProperty, value);
    }

    /// <inheritdoc cref="IsActionEnabledProperty" />
    public bool IsActionEnabled
    {
        get => (bool)GetValue(IsActionEnabledProperty);
        set => SetValue(IsActionEnabledProperty, value);
    }

    /// <summary>
    /// Builds the drop-down fresh each time so its items read their commands' current enabled
    /// state, and so a menu left over from a document that has since closed can never be shown.
    ///
    /// The menu is assigned to the arrow button before it is opened. A <see cref="ContextMenu"/>
    /// that is merely told to open, without being anyone's <c>ContextMenu</c>, has no logical
    /// parent: it draws, but its items never reach a command, because the click it raises has
    /// nowhere to route to.
    /// </summary>
    private void OnArrowClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = Arrow,
            Placement = PlacementMode.Bottom,
            DataContext = DataContext,
        };
        if (MenuItemsFactory is { } factory)
            foreach (var item in factory()) menu.Items.Add(item);
        else
        {
            menu.Items.Add(NewItem(PrimaryLabel, PrimaryToolTip, PrimaryCommand));
            menu.Items.Add(NewItem(SecondaryLabel, SecondaryToolTip, SecondaryCommand));
        }
        Arrow.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private static MenuItem NewItem(string header, string tooltip, ICommand? command)
    {
        var item = new MenuItem { Header = header, Command = command };
        if (!string.IsNullOrEmpty(tooltip))
        {
            item.ToolTip = tooltip;
            ToolTipService.SetShowOnDisabled(item, true);
        }
        AutomationProperties.SetName(item, header.Replace("_", string.Empty, System.StringComparison.Ordinal));
        return item;
    }
}
