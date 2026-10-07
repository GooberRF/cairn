using Cairn.Ui.Mvvm;

namespace Cairn.Ui.Documents;

/// <summary>
/// One tab of a document's inspector (right pane). <see cref="Content"/> is a view-model; the
/// DocumentView picks its view through a DataTemplate keyed by the view-model's type. Phase 5 adds the
/// Bone and Key tabs by appending to <c>InspectorTabs</c> and declaring their
/// DataTemplates in <c>Views/DocumentView.xaml</c>; nothing else needs to change.
/// </summary>
public sealed class InspectorTab : ObservableObject
{
    private string _header;

    /// <param name="id">A stable id (also what <c>--tab</c> and settings refer to), e.g. "clip".</param>
    /// <param name="header">The tab caption.</param>
    /// <param name="content">The tab's view-model.</param>
    /// <param name="toolTip">What the tab shows.</param>
    public InspectorTab(string id, string header, object content, string toolTip)
    {
        Id = id;
        _header = header;
        Content = content;
        ToolTip = toolTip;
    }

    public string Id { get; }

    public string Header { get => _header; set => Set(ref _header, value); }

    public object Content { get; }

    public string ToolTip { get; }
}

/// <summary>
/// One tab of the bottom panel. The bottom panel belongs to the window and always shows the active
/// document, so a tab is a selector from the document to the view-model its view binds to (the
/// Problems tab selects <c>Problems</c>). Phase 5 adds Timeline and Table usage
/// by appending to <c>BottomTabs</c> with their selector and a DataTemplate in
/// <c>MainWindow.xaml</c>.
/// </summary>
public sealed class PanelTab : ObservableObject
{
    private readonly Func<IDocument?, object?> _select;
    private object? _content;
    private string _header;

    /// <param name="id">A stable id, e.g. "problems".</param>
    /// <param name="header">The caption (may change, e.g. with a count).</param>
    /// <param name="toolTip">What the tab shows.</param>
    /// <param name="select">Picks the content for the active document (null shows the empty state).</param>
    public PanelTab(string id, string header, string toolTip, Func<IDocument?, object?> select)
    {
        Id = id;
        _header = header;
        ToolTip = toolTip;
        _select = select ?? throw new ArgumentNullException(nameof(select));
    }

    public string Id { get; }

    public string Header { get => _header; set => Set(ref _header, value); }

    public string ToolTip { get; }

    /// <summary>What the tab says when it has no content (no document is open).</summary>
    public string EmptyText { get; init; } = "Open a document to see it here.";

    /// <summary>The view-model for the active document, or null.</summary>
    public object? Content { get => _content; private set => Set(ref _content, value); }

    /// <summary>Re-selects the content for a new active document.</summary>
    public void Update(IDocument? document) => Content = _select(document);
}
