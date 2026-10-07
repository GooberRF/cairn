using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Cairn.Tbl.Ui.Editor;

namespace Cairn.Tbl.Ui.Documents;

/// <summary>
/// A table document's view: the external-change bars, the text editor (see <see cref="TblEditorController"/>) and a
/// right-hand pane (<see cref="RightPaneHost"/>) that stays collapsed until something is put in it.
/// </summary>
public partial class TblDocumentView : UserControl, IDisposable
{
    private TblDocument? _document;
    private GridLength _rightWidth = new(340);

    public TblDocumentView(TblDocument document)
    {
        _document = document;
        InitializeComponent();
        DataContext = document;
        Controller = new TblEditorController(Editor, document.Module, document);
        Controller.QuickFixButton = QuickFixButton;
        document.Editor = Editor;
        RightHost.ContentChanged += (_, _) => UpdateRightPane();
        RightSplitter.DragCompleted += (_, _) => { if (RightColumn.Width.Value > 0) _rightWidth = RightColumn.Width; };
        PropertyChangedEventManager.AddHandler(document, OnDocumentPropertyChanged, string.Empty);
        UpdateBars();
        WireFindBar();
        document.Module.NotifyViewCreated(document, this);
    }

    /// <summary>The right-hand area beside the editor, with a splitter; collapsed while its content is null.</summary>
    public ContentControl RightPaneHost => RightHost;

    /// <summary>The editor's behaviour (highlighting, squiggles, completion, quick fixes).</summary>
    public TblEditorController Controller { get; }

    // ── Find / replace bar ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Opens the find bar (and the replace row), seeded with the selection.</summary>
    public void OpenFind(bool replace)
    {
        FindBar.Visibility = Visibility.Visible;
        ReplaceRow.Visibility = replace && !Editor.IsReadOnly ? Visibility.Visible : Visibility.Collapsed;
        string selected = Editor.SelectedText;
        if (selected.Length > 0 && !selected.Contains('\n', StringComparison.Ordinal)) FindBox.Text = selected;
        FindBox.Focus();
        FindBox.SelectAll();
        UpdateSearch();
    }

    /// <summary>Closes the find bar and clears the match highlights.</summary>
    public void CloseFind()
    {
        FindBar.Visibility = Visibility.Collapsed;
        Controller.SetSearch(null, false);
        Editor.TextArea.Focus();
    }

    private void WireFindBar()
    {
        FindBox.TextChanged += (_, _) => UpdateSearch();
        MatchCaseBox.Click += (_, _) => UpdateSearch();
        FindNextButton.Click += (_, _) => Controller.FindNext(forward: true);
        FindPrevButton.Click += (_, _) => Controller.FindNext(forward: false);
        CloseFindButton.Click += (_, _) => CloseFind();
        ReplaceButton.Click += (_, _) => Controller.ReplaceNext(FindBox.Text, ReplaceBox.Text, MatchCaseBox.IsChecked == true);
        ReplaceAllButton.Click += (_, _) =>
        {
            int n = Controller.ReplaceAll(FindBox.Text, ReplaceBox.Text, MatchCaseBox.IsChecked == true);
            MatchStatus.Text = n == 1 ? "1 replaced" : $"{n} replaced";
        };
        FindBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) { Controller.FindNext(forward: (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) == 0); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.Escape) { CloseFind(); e.Handled = true; }
        };
        ReplaceBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) { Controller.ReplaceNext(FindBox.Text, ReplaceBox.Text, MatchCaseBox.IsChecked == true); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.Escape) { CloseFind(); e.Handled = true; }
        };
    }

    private void UpdateSearch()
    {
        string pattern = FindBox.Text;
        bool matchCase = MatchCaseBox.IsChecked == true;
        Controller.SetSearch(pattern, matchCase);
        if (pattern.Length == 0) { MatchStatus.Text = string.Empty; return; }
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        string text = Editor.Document.Text;
        int count = 0;
        for (int i = text.IndexOf(pattern, comparison); i >= 0 && count < 10000; i = text.IndexOf(pattern, i + pattern.Length, comparison)) count++;
        MatchStatus.Text = count == 0 ? "No matches" : count == 1 ? "1 match" : $"{count} matches";
    }

    private void UpdateRightPane()
    {
        bool show = RightHost.Content is not null;
        if (!show && RightColumn.Width.Value > 0) _rightWidth = RightColumn.Width;
        RightColumn.Width = show ? _rightWidth : new GridLength(0);
        SplitterColumn.Width = show ? GridLength.Auto : new GridLength(0);
        RightSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TblDocument.HasExternalChange) or nameof(TblDocument.IsMissingOnDisk) or "") UpdateBars();
    }

    private void UpdateBars()
    {
        if (_document is null) return;
        ExternalChangeBar.Visibility = _document.HasExternalChange ? Visibility.Visible : Visibility.Collapsed;
        MissingBar.Visibility = _document.IsMissingOnDisk ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Dispose()
    {
        if (_document is null) return;
        PropertyChangedEventManager.RemoveHandler(_document, OnDocumentPropertyChanged, string.Empty);
        Controller.Dispose();
        (RightHost.Content as IDisposable)?.Dispose();
        RightHost.Content = null;
        Editor.Document = new ICSharpCode.AvalonEdit.Document.TextDocument();
        DataContext = null;
        _document = null;
        GC.SuppressFinalize(this);
    }
}

/// <summary>A content host that reports when its content changes (without a property-descriptor subscription).</summary>
public sealed class TblPaneHost : ContentControl
{
    /// <summary>Raised after <see cref="ContentControl.Content"/> changed.</summary>
    public event EventHandler? ContentChanged;

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }
}
