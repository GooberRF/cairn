using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cairn.Vpp.Ui.Documents;

namespace Cairn.Vpp.Ui.List;

/// <summary>
/// The left-pane "File types" panel of a packfile: the type filter's check boxes (categories and extensions with
/// their entry counts) shown as a list, sharing the same model as the toolbar's type filter.
/// </summary>
public sealed class VppTypesPanel : DockPanel
{
    private static readonly ConditionalWeakTable<VppDocument, VppTypesPanel> Panels = new();

    /// <summary>The cached panel of a document (one per document; panel content must be cheap to return).</summary>
    public static VppTypesPanel For(VppDocument doc) => Panels.GetValue(doc, d => new VppTypesPanel(d));

    private readonly VppDocument _doc;
    private readonly ItemsControl _options;
    private readonly TextBlock _summary = new() { Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
    private readonly Button _all;

    private VppTypesPanel(VppDocument doc)
    {
        _doc = doc;
        Margin = new Thickness(8, 6, 6, 6);
        AutomationProperties.SetName(this, "File types");

        _all = new Button
        {
            Content = "Show all types",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 4),
            ToolTip = "Clear the type filter so every entry is listed",
        };
        _all.SetResourceReference(StyleProperty, "LinkButton");
        AutomationProperties.SetName(_all, "Show all types");
        _all.Click += (_, _) => _doc.List.ShowOnlyTypes([]);
        SetDock(_all, Dock.Top);
        Children.Add(_all);

        _summary.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        SetDock(_summary, Dock.Top);
        Children.Add(_summary);

        // "Problems" group: entries the game will not find by name, filtered together with the type check boxes. Each
        // box is shown only while some entry has its problem, and the group only while a box is shown (UpdateProblems).
        ProblemsHeader = new TextBlock { Text = "Problems", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 1) };
        ProblemsHeader.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
        SetDock(ProblemsHeader, Dock.Top);
        Children.Add(ProblemsHeader);
        var option = doc.List.LongNameOption;
        LongNames = new CheckBox
        {
            DataContext = option,
            Margin = new Thickness(16, 1, 0, 6),
            ToolTip = $"List only textures, sounds and fonts whose names are longer than {Cairn.Vpp.Validation.VppValidator.MaxAssetNameLength} characters: the game cuts such names off and does not find the file",
        };
        LongNames.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding(nameof(VppProblemOption.IsChecked)));
        LongNames.SetBinding(ContentControl.ContentProperty, new System.Windows.Data.Binding(nameof(VppProblemOption.Text)));
        LongNames.SetResourceReference(Control.ForegroundProperty, "App.Text");
        AutomationProperties.SetName(LongNames, option.Label);
        SetDock(LongNames, Dock.Top);
        Children.Add(LongNames);

        var typesHeader = new TextBlock { Text = "Types", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 0) };
        typesHeader.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
        SetDock(typesHeader, Dock.Top);
        Children.Add(typesHeader);

        _options = new ItemsControl
        {
            ItemTemplate = Application.Current?.TryFindResource("Vpp.TypeOptionTemplate") as DataTemplate,
            Focusable = false,
        };
        AutomationProperties.SetName(_options, "File type filter");
        Children.Add(new ScrollViewer
        {
            Content = _options,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });

        doc.List.PropertyChanged += OnListChanged;
        option.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(VppProblemOption.Count) or nameof(VppProblemOption.HasMatches)) UpdateProblems(); };
        Refresh();
    }

    /// <summary>The "Problems" group header (collapsed while no problem check box is shown).</summary>
    internal TextBlock ProblemsHeader { get; }

    /// <summary>Shows each problem box only while some entry has its problem, and the header only while a box is shown.</summary>
    private void UpdateProblems()
    {
        bool longNames = _doc.List.LongNameOption.HasMatches;
        LongNames.Visibility = longNames ? Visibility.Visible : Visibility.Collapsed;
        ProblemsHeader.Visibility = longNames ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The options the check boxes are bound to now (self-tests tick them as a click would).</summary>
    internal IReadOnlyList<VppTypeOption> ShownOptions => _options.Items.Cast<VppTypeOption>().ToList();

    /// <summary>The "Names longer than 31 characters" check box of the Problems group (self-tests click it).</summary>
    internal CheckBox LongNames { get; }

    private void OnListChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VppFileList.TypeOptions) or nameof(VppFileList.FilterSummary) or nameof(VppFileList.TypeFilterLabel))
            Refresh();
    }

    private void Refresh()
    {
        var list = _doc.List;
        // TypeOptions is one list whose option objects are replaced on every snapshot change, so the check boxes are
        // re-bound whenever the shown objects are not the current ones (binding to stale options filters nothing).
        if (!_options.Items.Cast<object>().SequenceEqual(list.TypeOptions, ReferenceEqualityComparer.Instance))
        {
            _options.ItemsSource = null;
            _options.ItemsSource = list.TypeOptions;
        }
        UpdateProblems();
        bool typeFiltered = list.TypeOptions.Any(o => !o.IsCategory && o.IsChecked);
        _all.IsEnabled = typeFiltered;
        int total = list.AllRows.Count;
        _summary.Text = total == 0 ? "This packfile is empty."
            : list.IsFiltered ? list.FilterSummary + "."
            : $"{total:N0} {(total == 1 ? "entry" : "entries")}. Tick types to list only those.";
    }
}
