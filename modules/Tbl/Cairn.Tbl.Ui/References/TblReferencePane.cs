using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Cairn.Assets;
using Cairn.Previews;
using Cairn.Tbl.Index;
using Cairn.Tbl.Model;
using Cairn.Tbl.Schema;
using Cairn.Tbl.Ui.Navigation;
using Cairn.Ui.Modules;

namespace Cairn.Tbl.Ui.References;

/// <summary>What the reference pane shows now.</summary>
public enum TblReferenceKind { Empty, File, Definition, MissingDefinition }

/// <summary>
/// The table view's right-hand pane: a preview of the file named under the caret (the shared asset preview) above its
/// details, or, for a name that refers to another table's entry, that entry's definition as read-only highlighted text
/// with "Go to definition". The split is remembered as <c>tbl.referenceSplit</c>. Callers debounce.
/// </summary>
public sealed class TblReferencePane : Grid, IDisposable
{
    /// <summary>Settings key (under "tbl.") of the preview's share of the height.</summary>
    public const string SplitKey = "referenceSplit";

    private readonly IShellContext? _shell;
    private readonly ModuleSettings? _settings;
    private readonly RowDefinition _previewRow;
    private readonly RowDefinition _detailsRow;
    private readonly ContentControl _content = new() { Focusable = false };
    private readonly DockPanel _definitionPanel = new();
    private readonly TextBlock _definitionHeader = new() { Margin = new Thickness(8, 4, 8, 4), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _title = new() { Margin = new Thickness(8, 5, 8, 5), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold };
    private readonly Button _goTo;
    private readonly Button _open;
    private readonly PreviewRowSizer _sizer;
    private CancellationTokenSource? _cts;
    private int _generation;
    private bool _disposed;
    private AssetLocation? _openable;
    private TblDefinition? _definition;

    /// <param name="shell">The shell (null in tests: no game data, no settings).</param>
    public TblReferencePane(IShellContext? shell)
    {
        _shell = shell;
        _settings = shell is null ? null : new ModuleSettings(shell.Settings, "tbl");
        Preview = new AssetPreviewPane(shell) { BusyLabel = "table reference preview" };
        Details = new AssetDetailsPane(shell) { BusyLabel = "table reference details" };
        Snippet = new TblSnippetView();
        AutomationProperties.SetName(this, "Reference preview");
        AutomationProperties.SetName(Preview, "Referenced file preview");
        AutomationProperties.SetName(Details, "Referenced file details");
        SetResourceReference(BackgroundProperty, "App.PaneBackground");

        _goTo = MakeButton("Go to definition", "Open the table that defines this name at its entry (F12)");
        _goTo.Click += (_, _) => { if (_definition is { } d) GoToDefinitionRequested?.Invoke(this, d); };
        _open = MakeButton("Open in Cairn", "Open the referenced file in its own tab (read-only when it is in a packfile)");
        _open.Click += (_, _) => { if (_openable is { } l) _shell?.OpenLocation(l); };

        var header = new DockPanel { LastChildFill = true };
        header.SetResourceReference(BackgroundProperty, "App.ChromeBackground");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 4, 2) };
        buttons.Children.Add(_goTo);
        buttons.Children.Add(_open);
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(_title);
        AutomationProperties.SetName(_title, "Reference");

        _definitionHeader.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        DockPanel.SetDock(_definitionHeader, Dock.Top);
        _definitionPanel.Children.Add(_definitionHeader);
        _definitionPanel.Children.Add(Snippet);

        double split = Math.Clamp(_settings?.Get(SplitKey, 0.6) ?? 0.6, 0.1, 0.9);
        _previewRow = new RowDefinition { Height = new GridLength(split, GridUnitType.Star), MinHeight = 60 };
        _detailsRow = new RowDefinition { Height = new GridLength(1 - split, GridUnitType.Star), MinHeight = 60 };
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(_previewRow);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(_detailsRow);
        var splitter = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext, ToolTip = "Drag to share the space between the preview and the details" };
        splitter.SetResourceReference(StyleProperty, "HorizontalSplitter");
        AutomationProperties.SetName(splitter, "Preview and details splitter");
        splitter.DragCompleted += OnSplitterMoved;
        SetRow(header, 0);
        SetRow(_content, 1);
        SetRow(splitter, 2);
        SetRow(Details, 3);
        Children.Add(header);
        Children.Add(_content);
        Children.Add(splitter);
        Children.Add(Details);
        _sizer = new PreviewRowSizer(Preview, _previewRow, splitter);
        if (shell is not null) shell.Theme.ThemeChanged += OnThemeChanged;
        Clear();
    }

    /// <summary>The shared file preview.</summary>
    public AssetPreviewPane Preview { get; }
    /// <summary>The details below the preview.</summary>
    public AssetDetailsPane Details { get; }
    /// <summary>The definition text of a referenced entry.</summary>
    public TblSnippetView Snippet { get; }
    /// <summary>What is shown now.</summary>
    public TblReferenceKind Kind { get; private set; }
    /// <summary>The name shown (file or entry name), or null.</summary>
    public string? ShownName { get; private set; }
    /// <summary>The definition shown, or null.</summary>
    public TblDefinition? Definition => _definition;
    /// <summary>The last file lookup (details callback), or null.</summary>
    public AssetLookupResult? Lookup { get; private set; }
    /// <summary>The definition header line ("Defined in ammo.tbl (tables.vpp), line 14").</summary>
    public string DefinitionHeader => _definitionHeader.Text;
    /// <summary>True when "Open in Cairn" is offered.</summary>
    public bool CanOpen => _open.IsEnabled;
    /// <summary>Completes when the definition text is shown (tests).</summary>
    public Task Pending { get; private set; } = Task.CompletedTask;
    /// <summary>The current text of an open table by source key (file path), so unsaved edits show; set by the owner.</summary>
    public Func<string, string?>? OpenText { get; set; }

    /// <summary>"Go to definition" was clicked.</summary>
    public event EventHandler<TblDefinition>? GoToDefinitionRequested;

    /// <summary>Shows nothing but a hint.</summary>
    public void Clear(string message = "Click a file name or a name from another table (or move the caret onto one) to preview it here.")
    {
        if (_disposed) return;
        Begin(TblReferenceKind.Empty, null, "Reference preview");
        Preview.Clear(message);
        _content.Content = Preview;
        Details.ShowMessage("Details of the referenced file appear here.");
    }

    /// <summary>
    /// Previews the file <paramref name="name"/> as the engine would load it (name mapping and texture supersede inside the
    /// shared lookup): <paramref name="siblings"/> (the packfile the table came from) first, then the shell's game data
    /// with <paramref name="documentFolder"/> (the table's own folder) among the search places.
    /// </summary>
    /// <param name="name">The file name as written in the table.</param>
    /// <param name="siblings">The packfile the table came from, or null.</param>
    /// <param name="documentFolder">The table's folder, or null.</param>
    /// <param name="extra">Table facts appended to the details (field, file kind, uses), built off the UI thread.</param>
    public void ShowFile(string name, IAssetSiblings? siblings, string? documentFolder, Func<IEnumerable<AssetDetailRow>>? extra = null)
    {
        if (_disposed) return;
        int generation = Begin(TblReferenceKind.File, name, name);
        _content.Content = Preview;
        Preview.ShowAsset(name, siblings, documentFolder);
        var resolver = AssetLookup.ResolverFor(_shell, siblings, documentFolder);
        // The type facts (dimensions, frames, mesh contents...) come from whichever module describes files.
        var facts = _shell?.Modules.OfType<IAssetFactsProvider>().FirstOrDefault();
        Details.Show(ct =>
        {
            var lookup = AssetLookup.Resolve(resolver, name, siblings, ct);
            var rows = new List<AssetDetailRow>();
            if (facts is not null && lookup.Location is { } location)
            {
                try { rows.AddRange(facts.Describe(location.ResolvedName, location.Open, lookup.Size).Select(f => new AssetDetailRow(f.Section, f.Label, f.Value, f.Flagged))); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { rows.Add(new("Contents", "Error", ex.Message, true)); }
            }
            ct.ThrowIfCancellationRequested();
            rows.AddRange(extra?.Invoke() ?? []);
            Dispatcher.BeginInvoke(() =>
            {
                if (_disposed || generation != _generation) return;
                Lookup = lookup;
                _openable = lookup.Location is { } l && IsOpenable(l.ResolvedName) ? l : null;
                _open.IsEnabled = _openable is not null;
                _open.Visibility = _openable is not null ? Visibility.Visible : Visibility.Collapsed;
                if (lookup.Location is { } found && !string.Equals(found.ResolvedName, name, StringComparison.OrdinalIgnoreCase))
                    _title.Text = $"{name}  (as {found.ResolvedName})";
            });
            return AssetDetails.ForLookup(lookup, rows);
        });
    }

    /// <summary>
    /// Shows the definition of <paramref name="name"/> (an entry of kind <paramref name="kind"/> in another table):
    /// its text, read off the UI thread, highlighted. <paramref name="definition"/> null = not defined anywhere indexed.
    /// </summary>
    /// <param name="kind">The entry kind (<c>ammo</c>).</param>
    /// <param name="name">The name as written.</param>
    /// <param name="definition">The definition to show.</param>
    /// <param name="otherCount">Further definitions of the same name (shown as a note).</param>
    /// <param name="searched">How many tables were searched (for the "not defined" message).</param>
    public void ShowDefinition(string kind, string name, TblDefinition? definition, int otherCount = 0, int searched = 0)
    {
        if (_disposed) return;
        if (definition is null)
        {
            Begin(TblReferenceKind.MissingDefinition, name, name);
            Preview.Clear($"'{name}' is not defined in any indexed table" + (searched > 0 ? $" ({searched} tables searched)." : "."));
            _content.Content = Preview;
            Details.ShowDetails(new AssetDetails(name, [new("Reference", "Kind", kind), new("Reference", "Defined in", "not found", Flagged: true)],
                [$"No table defines the {kind} '{name}'."]));
            return;
        }
        int generation = Begin(TblReferenceKind.Definition, name, $"{name}  ({definition.Source.FileName})");
        _definition = definition;
        _goTo.Visibility = Visibility.Visible;
        _goTo.IsEnabled = true;
        _definitionHeader.Text = string.Format(CultureInfo.CurrentCulture, "{0} '{1}', defined in {2} ({3}), line {4}{5}", Capital(kind), name,
            definition.Source.FileName, definition.Source.DisplayLocation, definition.Line,
            otherCount > 0 ? $"; {otherCount} more definition{(otherCount == 1 ? "" : "s")} elsewhere" : "");
        _content.Content = _definitionPanel;
        Snippet.Show("Reading " + definition.Source.FileName + "...", [], 0);
        // An open table's text is read here, on the UI thread (the editor's document belongs to it); files on the pool.
        string? current = OpenText?.Invoke(definition.Source.Key);
        Func<string, string?>? openText = current is null ? null : _ => current;
        var cts = _cts = new CancellationTokenSource();
        var rows = new List<AssetDetailRow>
        {
            new("Definition", "Kind", kind),
            new("Definition", "Table", definition.Source.FileName),
            new("Definition", "Location", definition.Source.DisplayLocation),
            new("Definition", "Line", definition.Line.ToString(CultureInfo.CurrentCulture)),
        };
        if (definition.Section.Length > 0) rows.Add(new("Definition", "Section", "#" + definition.Section));
        if (definition.Source.Location is { IsArchived: true } loc) rows.Add(new("Definition", "Packfile", loc.ArchivePath!));
        else if (definition.Source.Kind != TblSourceKind.OpenDocument) rows.Add(new("Definition", "Path", definition.Source.Key));
        else if (Path.IsPathRooted(definition.Source.Key)) rows.Add(new("Definition", "Path", definition.Source.Key));
        Details.ShowDetails(new AssetDetails(name, rows, []));
        Pending = LoadDefinitionAsync(definition, openText, generation, cts.Token);
    }

    private async Task LoadDefinitionAsync(TblDefinition def, Func<string, string?>? openText, int generation, CancellationToken ct)
    {
        (string Snippet, int Offset, System.Collections.Immutable.ImmutableArray<TblClassifiedSpan> Classes)? result;
        try
        {
            result = await Task.Run(() =>
            {
                string? text = TblSourceText.Read(def.Source, openText);
                if (text is null) return ((string, int, System.Collections.Immutable.ImmutableArray<TblClassifiedSpan>)?)null;
                ct.ThrowIfCancellationRequested();
                var doc = TblDocument.Parse(text, TblSchemaSet.Default.Find(def.Source.FileName));
                var classes = TblClassifier.Classify(doc);
                int end = def.EntrySpan.Length > 0 ? def.EntrySpan.End : -1;
                var span = def.EntrySpan.Length > 0 && def.EntrySpan.Start <= def.Span.Start ? def.EntrySpan : def.Span;
                var (snippet, offset) = TblSourceText.Snippet(text, span, 60, end);
                return (snippet, offset, classes);
            }, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (_disposed || generation != _generation) return;
        if (result is { } r) Snippet.Show(r.Snippet, r.Classes, r.Offset);
        else Snippet.Show($"{def.Source.FileName} could not be read.", [], 0);
    }

    private int Begin(TblReferenceKind kind, string? name, string title)
    {
        _cts?.Cancel();
        _cts = null;
        Kind = kind;
        ShownName = name;
        _title.Text = title;
        _title.ToolTip = title;
        _definition = null;
        _openable = null;
        Lookup = null;
        _goTo.Visibility = Visibility.Collapsed;
        _open.Visibility = Visibility.Collapsed;
        _open.IsEnabled = false;
        Pending = Task.CompletedTask;
        return ++_generation;
    }

    /// <summary>True when some module opens files named like <paramref name="fileName"/>.</summary>
    private bool IsOpenable(string fileName)
    {
        if (_shell is null) return false;
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext.Length > 0 && _shell.Modules.Any(m => m.DocumentKinds.Any(k => k.Extensions.Contains(ext)));
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.CurrentCulture) + s[1..];

    private static Button MakeButton(string text, string tip)
    {
        var b = new Button { Content = text, ToolTip = tip, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(8, 1, 8, 1), Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(b, text);
        return b;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (!_disposed && Kind == TblReferenceKind.Definition) Snippet.Render();
    }

    private void OnSplitterMoved(object sender, DragCompletedEventArgs e)
    {
        double total = _previewRow.ActualHeight + _detailsRow.ActualHeight;
        if (total <= 0) return;
        double split = Math.Clamp(_previewRow.ActualHeight / total, 0.1, 0.9);
        _previewRow.Height = new GridLength(split, GridUnitType.Star);
        _detailsRow.Height = new GridLength(1 - split, GridUnitType.Star);
        _settings?.Set(SplitKey, split);
    }

    /// <summary>Stops loads and playback, releases the views and unhooks from the shell.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        if (_shell is not null) _shell.Theme.ThemeChanged -= OnThemeChanged;
        _sizer.Detach();
        Preview.Dispose();
        Details.Dispose();
        _content.Content = null;
        GoToDefinitionRequested = null;
        OpenText = null;
    }
}
