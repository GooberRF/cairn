using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using Cairn.Previews;
using Cairn.Tbl.Assist;
using Cairn.Tbl.Ui.Compare;
using Cairn.Tbl.Ui.Documents;
using Cairn.Tbl.Ui.Outline;
using Cairn.Tbl.Ui.References;
using Cairn.Ui.Modules;

namespace Cairn.Tbl.Ui.Navigation;

/// <summary>
/// The navigation half of one table document: its reference preview (right pane), outline (left pane), usages and
/// comparison (bottom pane), kept in step with the document's model and caret. Subscribes only to the document, so
/// everything goes away with it; <see cref="Dispose"/> unhooks and releases the views.
/// </summary>
public sealed class TblDocumentPanels : IDisposable
{
    private readonly TblDocument _doc;
    private readonly PreviewDebouncer _caretDebounce = new(TimeSpan.FromMilliseconds(250));
    private (TblSymbolKind Kind, string Name)? _shown;
    private readonly Func<Cairn.Tbl.Index.TblSource, bool> _shownByTab;
    private bool _disposed;

    internal TblDocumentPanels(TblDocument doc, IShellContext? shell, Func<string, string?> openText, Func<string?> gameDirectory, Func<Cairn.Tbl.Index.TblSource, bool>? shownByTab = null)
    {
        _doc = doc;
        _shownByTab = shownByTab ?? (_ => false);
        OpenText = openText;
        Reference = new TblReferencePane(shell) { OpenText = openText };
        Outline = new TblOutlinePanel();
        Usages = new TblUsagesPanel();
        Compare = new TblComparePanel(gameDirectory);
        Outline.NavigateRequested += (start, length) => _doc.NavigateTo(start, length);
        Compare.NavigateRequested += (start, length) => _doc.NavigateTo(start, length);
        doc.ModelChanged += OnModelChanged;
        doc.CaretChanged += OnCaretChanged;
        doc.TokenActivated += OnTokenActivated;
        Outline.Update(doc.Model);
        Outline.SetCaret(doc.CaretOffset);
    }

    /// <summary>The document.</summary>
    public TblDocument Document => _doc;
    /// <summary>The right-hand reference preview.</summary>
    public TblReferencePane Reference { get; }
    /// <summary>The left-pane outline.</summary>
    public TblOutlinePanel Outline { get; }
    /// <summary>The bottom usages list.</summary>
    public TblUsagesPanel Usages { get; }
    /// <summary>The bottom comparison with the stock table.</summary>
    public TblComparePanel Compare { get; }
    /// <summary>The current text of an open table by index key.</summary>
    public Func<string, string?> OpenText { get; }
    /// <summary>The packfile the table was opened from (references look there first), or null.</summary>
    public IAssetSiblings? Siblings { get => _siblings ?? _doc.Siblings; set => _siblings = value; }
    private IAssetSiblings? _siblings;

    /// <summary>A Ctrl+click on a token asks for its definition (raised for the module).</summary>
    public event Action<TblDocumentPanels, int>? DefinitionRequested;

    /// <summary>Shows what the symbol at <paramref name="offset"/> refers to in the reference pane now.</summary>
    public void PreviewAt(int offset, bool force = false)
    {
        if (_disposed) return;
        var model = _doc.Model;
        // The model lags the text (debounce; big tables parse off the UI thread): offsets would point into old text.
        if (!_doc.WhenModelCurrent.IsCompleted) return;
        var symbol = TblAssist.SymbolAt(model.Parsed, Math.Clamp(offset, 0, model.Text.Length));
        if (symbol is null || symbol.Kind is not (TblSymbolKind.File or TblSymbolKind.Ref) || symbol.Name.Length == 0) return;
        if (!force && _shown is { } s && s.Kind == symbol.Kind && string.Equals(s.Name, symbol.Name, StringComparison.OrdinalIgnoreCase)) return;
        _shown = (symbol.Kind, symbol.Name);
        if (symbol.Kind == TblSymbolKind.File) ShowFile(symbol);
        else ShowDefinition(symbol);
    }

    private void ShowFile(TblSymbol symbol)
    {
        string? folder = _doc.FilePath is { } path ? Path.GetDirectoryName(path) : null;
        var index = _doc.Index;
        string name = symbol.Name, field = symbol.Field?.Marker ?? string.Empty, kind = symbol.IndexKind ?? string.Empty;
        // A field whose file the game loads under a suffixed name (HUD bitmaps: reticle.tga -> reticle_0.tga).
        string engineName = symbol.Field?.Schema is { NameSuffix.Length: > 0 } fieldSchema ? fieldSchema.EngineFileNames(name)[0] : name;
        Reference.ShowFile(engineName, Siblings, folder, () =>
        {
            var rows = new List<AssetDetailRow>();
            if (field.Length > 0) rows.Add(new("Table", "Field", field));
            if (kind.Length > 0) rows.Add(new("Table", "Expected", kind));
            var uses = index.FileUsages(name);
            int tables = uses.Select(u => u.Source.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            rows.Add(new("Table", "Used", $"{uses.Length:N0} {(uses.Length == 1 ? "time" : "times")} in {tables:N0} indexed {(tables == 1 ? "table" : "tables")}"));
            return rows;
        });
    }

    private void ShowDefinition(TblSymbol symbol)
    {
        string kind = symbol.IndexKind ?? "entry";
        // A packfile table that is open in a tab counts once, as the tab.
        var defs = symbol.IndexKind is null ? [] : _doc.Index.Define(symbol.IndexKind, symbol.Name)
            .Where(d => d.Source.Kind == Cairn.Tbl.Index.TblSourceKind.OpenDocument || !_shownByTab(d.Source)).ToImmutableArray();
        // Prefer this document's own definitions, then open tables, then the rest in index order.
        var def = defs.OrderBy(d => d.Source.Key.Equals(_doc.IndexKey, StringComparison.OrdinalIgnoreCase) ? 0 : d.Source.Kind == Cairn.Tbl.Index.TblSourceKind.OpenDocument ? 1 : 2).FirstOrDefault();
        Reference.ShowDefinition(kind, symbol.Name, def, Math.Max(0, defs.Length - 1), _doc.Index.Sources.Count);
    }

    private void OnModelChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        Outline.Update(_doc.Model);
        Outline.SetCaret(_doc.CaretOffset);
        Compare.Update(_doc.Model);
        // A caret preview skipped while the model lagged behind the text runs now (review finding 22).
        if (_doc.Editor is not null) PreviewAt(_doc.CaretOffset);
    }

    private void OnCaretChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        Outline.SetCaret(_doc.CaretOffset);
        _caretDebounce.Post(() => PreviewAt(_doc.CaretOffset));
    }

    private void OnTokenActivated(TblToken token, bool ctrl)
    {
        if (_disposed) return;
        if (ctrl) { DefinitionRequested?.Invoke(this, token.Span.Start); return; }
        _caretDebounce.Cancel();
        PreviewAt(token.Span.Start, force: true);
    }

    /// <summary>Selects the bottom-pane tab that hosts <paramref name="panel"/> (when the pane shows it).</summary>
    public static bool SelectTab(FrameworkElement panel)
    {
        DependencyObject? node = panel;
        while (node is not null and not TabItem) node = LogicalTreeHelper.GetParent(node) ?? (node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(node) : null);
        if (node is not TabItem tab) return false;
        tab.IsSelected = true;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _doc.ModelChanged -= OnModelChanged;
        _doc.CaretChanged -= OnCaretChanged;
        _doc.TokenActivated -= OnTokenActivated;
        _caretDebounce.Dispose();
        Usages.Cancel();
        Compare.Cancel();
        Reference.Dispose();
        DefinitionRequested = null;
    }
}
