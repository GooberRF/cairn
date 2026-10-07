using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Cairn.Tbl.Assist;
using Cairn.Tbl.Linting;
using Cairn.Tbl.Model;
using Cairn.Tbl.Text;
using Cairn.Tbl.Ui.Documents;
using Cairn.Ui.Services;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Search;
using UiDocument = Cairn.Tbl.Ui.Documents.TblDocument;

namespace Cairn.Tbl.Ui.Editor;

/// <summary>
/// Everything a table editor does on top of AvalonEdit: highlighting by token class in both themes, folding of
/// sections, entries and block comments, squiggles with hover text, hover docs, completion (Ctrl+Space and after
/// <c>$</c>/<c>+</c>/a field's colon), quick fixes (Ctrl+. and the light bulb), toggle comment, go to line,
/// find/replace highlighting and clicks on file and reference names. Works for documents and for the read-only
/// packfile preview (no document: <see cref="SetReadOnlyModel"/>).
/// </summary>
public sealed class TblEditorController : IDisposable
{
    private readonly TextEditor _editor;
    private readonly TblModule _module;
    private readonly TblColorizer _colorizer = new();
    private readonly TblSquiggleRenderer _squiggles = new();
    private readonly ToolTip _hoverTip = new() { MaxWidth = 520, Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse };
    private UiDocument? _document;
    private TblModel? _model;
    private FoldingManager? _folding;
    private SearchPanel? _search;
    private CompletionWindow? _completion;
    private ThemeService? _theme;
    private bool _disposed;

    public TblEditorController(TextEditor editor, TblModule module, UiDocument? document)
    {
        _editor = editor;
        _module = module;
        _document = document;

        editor.Options.HighlightCurrentLine = true;
        editor.Options.ConvertTabsToSpaces = false;
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;
        editor.Options.AllowScrollBelowDocument = true;
        editor.TextArea.TextView.LineTransformers.Add(_colorizer);
        editor.TextArea.TextView.BackgroundRenderers.Add(_squiggles);
        // The folding manager binds to the editor's document when installed, so the document goes in first.
        if (document is not null) editor.Document = document.Text;
        _folding = FoldingManager.Install(editor.TextArea);
        _search = SearchPanel.Install(editor);

        editor.TextArea.TextView.MouseHover += OnMouseHover;
        editor.TextArea.TextView.MouseHoverStopped += OnMouseHoverStopped;
        editor.TextArea.TextView.PreviewMouseLeftButtonUp += OnTextViewMouseUp;
        editor.TextArea.TextView.MouseMove += OnTextViewMouseMove;

        _theme = module.ShellContext?.Theme;
        if (_theme is not null) _theme.ThemeChanged += OnThemeChanged;
        ApplyTheme();

        if (document is not null)
        {
            editor.WordWrap = module.Settings.WordWrap;
            editor.TextArea.Caret.PositionChanged += OnCaretMoved;
            editor.TextArea.TextEntered += OnTextEntered;
            editor.PreviewKeyDown += OnEditorKeyDown;
            document.ModelChanged += OnModelChanged;
            document.NavigateRequested += OnNavigateRequested;
            module.Settings.Changed += OnSettingsChanged;
            OnModelChanged(null, EventArgs.Empty);
        }
        else
        {
            editor.IsReadOnly = true;
        }
    }

    /// <summary>The light-bulb button beside the editor (shown when the caret is on a fixable problem).</summary>
    public Button? QuickFixButton
    {
        get => _quickFixButton;
        set
        {
            if (_quickFixButton is not null) _quickFixButton.Click -= OnQuickFixButtonClick;
            _quickFixButton = value;
            if (value is not null) value.Click += OnQuickFixButtonClick;
        }
    }
    private Button? _quickFixButton;

    /// <summary>The squiggles currently drawn (for tests).</summary>
    public IReadOnlyList<TblSquiggle> Squiggles => _squiggles.Markers;

    /// <summary>The fold regions currently installed (for tests).</summary>
    public IReadOnlyList<FoldingSection> Foldings => _folding is null ? [] : [.. _folding.AllFoldings];

    /// <summary>The highlighting spans in use (for tests).</summary>
    public TblModel? Model => _model;

    /// <summary>The editor.</summary>
    public TextEditor Editor => _editor;

    /// <summary>For the read-only preview: shows <paramref name="model"/> without a document.</summary>
    public void SetReadOnlyModel(TblModel model) => ShowModel(model);

    // ── Model, theme, settings ────────────────────────────────────────────────────────────────────────────────

    private void OnModelChanged(object? sender, EventArgs e)
    {
        if (_document is null) return;
        ShowModel(_document.Model);
        UpdateQuickFixButton();
    }

    private void ShowModel(TblModel model)
    {
        _model = model;
        // A model of older text is still right for nearly every line; the next one follows within the debounce.
        _colorizer.SetClasses(model.Classes);
        _squiggles.SetDiagnostics(_editor.TextArea.TextView, model.Diagnostics, _editor.Document.TextLength);
        if (_folding is not null && model.Text.Length == _editor.Document.TextLength)
        {
            _folding.UpdateFoldings(TblFolding.Build(model.Parsed, _editor.Document), -1);
        }
        _editor.TextArea.TextView.Redraw();
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyTheme();

    private void ApplyTheme()
    {
        var theme = _theme;
        _colorizer.Palette = new TblPalette(theme);
        if (theme is null) return;
        _squiggles.ErrorBrush = theme.Brush("Severity.Error", Brushes.Red);
        _squiggles.WarningBrush = theme.Brush("Severity.Warning", Brushes.Orange);
        _squiggles.InfoBrush = theme.Brush("Severity.Info", Brushes.SteelBlue);
        var view = _editor.TextArea.TextView;
        view.CurrentLineBackground = theme.Brush("Editor.CurrentLine", Brushes.Transparent);
        view.CurrentLineBorder = new Pen(theme.Brush("Editor.CurrentLineBorder", Brushes.Transparent), 1);
        _editor.TextArea.SelectionBrush = theme.Brush("App.Selection", Brushes.LightBlue);
        _editor.TextArea.SelectionBorder = new Pen(theme.Brush("App.Selection", Brushes.LightBlue), 1);
        _editor.TextArea.SelectionForeground = null;
        if (_search is not null) _search.MarkerBrush = theme.Brush("Editor.FrameHighlightBorder", Brushes.Yellow);
        if (_folding is not null)
        {
            var margin = _editor.TextArea.LeftMargins.OfType<FoldingMargin>().FirstOrDefault();
            if (margin is not null)
            {
                margin.FoldingMarkerBrush = theme.Brush("Editor.LineNumber", Brushes.Gray);
                margin.FoldingMarkerBackgroundBrush = theme.Brush("Editor.Background", Brushes.White);
                margin.SelectedFoldingMarkerBrush = theme.Brush("App.Text", Brushes.Black);
                margin.SelectedFoldingMarkerBackgroundBrush = theme.Brush("Editor.CurrentLine", Brushes.White);
            }
        }
        view.Redraw();
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => _editor.WordWrap = _module.Settings.WordWrap;

    // ── Caret, navigation ─────────────────────────────────────────────────────────────────────────────────────

    private void OnCaretMoved(object? sender, EventArgs e)
    {
        if (_document is null) return;
        _document.CaretOffset = _editor.TextArea.Caret.Offset;
        UpdateQuickFixButton();
    }

    private void OnNavigateRequested(object? sender, TextSpan span)
    {
        int start = Math.Clamp(span.Start, 0, _editor.Document.TextLength);
        int length = Math.Clamp(span.Length, 0, _editor.Document.TextLength - start);
        _editor.Select(start, length);
        _editor.TextArea.Caret.Offset = start + length;
        var location = _editor.Document.GetLocation(start);
        _editor.ScrollTo(location.Line, location.Column);
        _editor.TextArea.Focus();
    }

    /// <summary>Moves the caret to a 1-based line.</summary>
    public void GoToLine(int line)
    {
        line = Math.Clamp(line, 1, _editor.Document.LineCount);
        var docLine = _editor.Document.GetLineByNumber(line);
        _editor.Select(docLine.Offset, 0);
        _editor.TextArea.Caret.Offset = docLine.Offset;
        _editor.ScrollToLine(line);
        _editor.TextArea.Focus();
    }

    /// <summary>Asks for a line number and goes there.</summary>
    public void PromptGoToLine()
    {
        var box = new TextBox { MinWidth = 120, Margin = new Thickness(0, 6, 0, 10), Text = _editor.TextArea.Caret.Line.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        AutomationPropertiesHelper.Name(box, "Line number");
        var ok = new Button { Content = "Go", IsDefault = true, MinWidth = 72, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 72 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        var label = new TextBlock { Text = $"Line number (1 - {_editor.Document.LineCount}):" };
        label.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
        var panel = new StackPanel { Margin = new Thickness(14), Children = { label, box, buttons } };
        var window = new Window
        {
            Title = "Go to line", Content = panel, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Owner = Window.GetWindow(_editor),
        };
        window.SetResourceReference(Control.BackgroundProperty, "App.PaneBackground");
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        if (window.ShowDialog() == true && int.TryParse(box.Text.Trim(), out int line)) GoToLine(line);
    }

    /// <summary>Comments (<c>//</c>) or uncomments the lines of the selection.</summary>
    public void ToggleComment()
    {
        if (_editor.IsReadOnly) return;
        var document = _editor.Document;
        var area = _editor.TextArea;
        int startOffset = area.Selection.IsEmpty ? area.Caret.Offset : area.Selection.SurroundingSegment.Offset;
        int endOffset = area.Selection.IsEmpty ? area.Caret.Offset : Math.Max(startOffset, area.Selection.SurroundingSegment.EndOffset - 1);
        int first = document.GetLineByOffset(startOffset).LineNumber, last = document.GetLineByOffset(endOffset).LineNumber;
        var lines = Enumerable.Range(first, last - first + 1).Select(document.GetLineByNumber).ToList();
        var texts = lines.Select(l => document.GetText(l)).ToList();
        var nonBlank = texts.Where(t => t.Trim().Length > 0).ToList();
        if (nonBlank.Count == 0) return;
        bool allCommented = nonBlank.All(t => t.TrimStart().StartsWith("//", StringComparison.Ordinal));
        int indent = nonBlank.Min(t => t.Length - t.TrimStart().Length);
        document.BeginUpdate();
        try
        {
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                string text = texts[i];
                if (text.Trim().Length == 0) continue;
                if (allCommented)
                {
                    int at = text.IndexOf("//", StringComparison.Ordinal);
                    int remove = at + 2 < text.Length && text[at + 2] == ' ' ? 3 : 2;
                    document.Remove(lines[i].Offset + at, remove);
                }
                else
                {
                    document.Insert(lines[i].Offset + indent, "// ");
                }
            }
        }
        finally { document.EndUpdate(); }
    }

    // ── Find / replace ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Highlights every match of <paramref name="pattern"/> (null or empty clears).</summary>
    public void SetSearch(string? pattern, bool matchCase)
    {
        if (_search is null) return;
        _search.MatchCase = matchCase;
        _search.UseRegex = false;
        _search.SearchPattern = pattern ?? string.Empty;
        if (string.IsNullOrEmpty(pattern)) { if (!_search.IsClosed) _search.Close(); }
        else if (_search.IsClosed) _search.Open();
    }

    /// <summary>Selects the next (or previous) match.</summary>
    public void FindNext(bool forward)
    {
        if (_search is null || _search.IsClosed) return;
        if (forward) _search.FindNext(); else _search.FindPrevious();
    }

    /// <summary>Replaces the selected match (if the selection is one) and moves to the next.</summary>
    public void ReplaceNext(string pattern, string replacement, bool matchCase)
    {
        if (_editor.IsReadOnly || pattern.Length == 0) return;
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (string.Equals(_editor.SelectedText, pattern, comparison))
        {
            _editor.Document.Replace(_editor.SelectionStart, _editor.SelectionLength, replacement);
        }
        FindNext(forward: true);
    }

    /// <summary>Replaces every match as one undo step; returns the count.</summary>
    public int ReplaceAll(string pattern, string replacement, bool matchCase)
    {
        if (_editor.IsReadOnly || pattern.Length == 0) return 0;
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        string text = _editor.Document.Text;
        var offsets = new List<int>();
        for (int i = text.IndexOf(pattern, comparison); i >= 0; i = text.IndexOf(pattern, i + pattern.Length, comparison)) offsets.Add(i);
        if (offsets.Count == 0) return 0;
        _editor.Document.BeginUpdate();
        try { for (int k = offsets.Count - 1; k >= 0; k--) _editor.Document.Replace(offsets[k], pattern.Length, replacement); }
        finally { _editor.Document.EndUpdate(); }
        return offsets.Count;
    }

    // ── Hover ─────────────────────────────────────────────────────────────────────────────────────────────────

    private int? OffsetAt(MouseEventArgs e)
    {
        var view = _editor.TextArea.TextView;
        var position = view.GetPositionFloor(e.GetPosition(view) + view.ScrollOffset);
        if (position is null) return null;
        int offset = _editor.Document.GetOffset(position.Value.Location);
        return offset;
    }

    private void OnMouseHover(object sender, MouseEventArgs e)
    {
        if (OffsetAt(e) is not int offset) return;
        var content = BuildHover(offset);
        if (content is null) return;
        _hoverTip.PlacementTarget = _editor;
        _hoverTip.Content = content;
        _hoverTip.IsOpen = true;
        e.Handled = true;
    }

    private void OnMouseHoverStopped(object sender, MouseEventArgs e) => _hoverTip.IsOpen = false;

    /// <summary>The hover text at <paramref name="offset"/>: problems first, then the assist layer's description.</summary>
    public (IReadOnlyList<TblDiagnostic> Problems, TblHover? Hover) HoverAt(int offset)
    {
        var problems = _squiggles.MarkersAt(offset).SelectMany(m => m.Diagnostics).ToList();
        TblHover? hover = null;
        if (_model is { } model && model.Text.Length == _editor.Document.TextLength)
        {
            var resolver = _document?.Resolver ?? _module.ShellContext?.Assets.Resolver;
            hover = TblAssist.Hover(model.Parsed, offset, _module.Index, name => resolver?.Resolve(name));
        }
        return (problems, hover);
    }

    private FrameworkElement? BuildHover(int offset)
    {
        var (problems, hover) = HoverAt(offset);
        if (problems.Count == 0 && hover is null) return null;
        var panel = new StackPanel { MaxWidth = 500 };
        foreach (var d in problems)
        {
            var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            var code = new System.Windows.Documents.Run(d.Code + "  ") { FontWeight = FontWeights.SemiBold };
            code.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty,
                d.Severity == TblSeverity.Error ? "Severity.Error" : d.Severity == TblSeverity.Warning ? "Severity.Warning" : "Severity.Info");
            line.Inlines.Add(code);
            line.Inlines.Add(new System.Windows.Documents.Run(d.Message));
            if (!string.IsNullOrEmpty(d.Help)) line.Inlines.Add(new System.Windows.Documents.Run("\n" + d.Help) { FontStyle = FontStyles.Italic });
            panel.Children.Add(line);
        }
        if (hover is not null)
        {
            if (problems.Count > 0) panel.Children.Add(new Separator { Margin = new Thickness(0, 2, 0, 4) });
            panel.Children.Add(new TextBlock { Text = hover.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            foreach (string text in hover.Lines) panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        }
        return panel;
    }

    // ── Clicks on file and reference names ────────────────────────────────────────────────────────────────────

    /// <summary>The clickable token (file name or reference) at <paramref name="offset"/>, or null.</summary>
    public TblToken? TokenAt(int offset)
    {
        if (_model is not { } model || model.Text.Length != _editor.Document.TextLength) return null;
        if (model.ClassAt(offset) is not { } cs || cs.Class is not (TblTextClass.FileName or TblTextClass.RefName)) return null;
        return new TblToken(cs.Class, cs.Span, cs.Span.GetText(model.Text), TblAssist.SymbolAt(model.Parsed, offset));
    }

    private void OnTextViewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_document is null || OffsetAt(e) is not int offset || !_editor.TextArea.Selection.IsEmpty) return;
        if (TokenAt(offset) is { } token)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            _document.RaiseTokenActivated(token, ctrl);
        }
    }

    private void OnTextViewMouseMove(object sender, MouseEventArgs e)
    {
        bool hand = (Keyboard.Modifiers & ModifierKeys.Control) != 0 && OffsetAt(e) is int offset && TokenAt(offset) is not null;
        _editor.TextArea.TextView.Cursor = hand ? Cursors.Hand : null;
    }

    // ── Keys, completion ──────────────────────────────────────────────────────────────────────────────────────

    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (e.Key == Key.Space && mods == ModifierKeys.Control) { ShowCompletion(); e.Handled = true; }
        else if (e.Key == Key.OemPeriod && mods == ModifierKeys.Control) { ShowQuickFixes(); e.Handled = true; }
        else if (e.Key == Key.OemQuestion && mods == ModifierKeys.Control) { ToggleComment(); e.Handled = true; }
        else if (e.Key == Key.G && mods == ModifierKeys.Control) { PromptGoToLine(); e.Handled = true; }
    }

    private void OnTextEntered(object sender, TextCompositionEventArgs e)
    {
        if (_completion is not null || _document is null || !_module.Settings.CompletionAutoPopup || e.Text.Length != 1) return;
        char c = e.Text[0];
        int caret = _editor.TextArea.Caret.Offset;
        var line = _editor.Document.GetLineByOffset(caret);
        string before = _editor.Document.GetText(line.Offset, caret - line.Offset);
        bool trigger = c switch
        {
            '$' or '+' => before.Length == 1 || before[..^1].Trim().Length == 0,
            ' ' => before.TrimEnd().EndsWith(':') && before.TrimStart().Length > 1 && before.TrimStart()[0] is '$' or '+',
            _ => false,
        };
        if (trigger) ShowCompletion();
    }

    /// <summary>The completion at the caret for the current text (parsed now, so it never lags behind typing).</summary>
    public TblCompletion CompletionAtCaret()
    {
        string text = _editor.Document.Text;
        var schema = _document?.Model.Schema ?? _model?.Schema;
        var parsed = _model is { } m && m.Text == text ? m.Parsed : Cairn.Tbl.Model.TblDocument.Parse(text, schema);
        return TblAssist.Complete(parsed, _editor.TextArea.Caret.Offset, _module.Index);
    }

    /// <summary>Opens the completion list at the caret (Ctrl+Space).</summary>
    public CompletionWindow? ShowCompletion()
    {
        if (_editor.IsReadOnly) return null;
        var completion = CompletionAtCaret();
        if (completion.Items.IsDefaultOrEmpty) return null;
        _completion?.Close();
        var window = new CompletionWindow(_editor.TextArea)
        {
            StartOffset = Math.Clamp(completion.ReplaceSpan.Start, 0, _editor.Document.TextLength),
            EndOffset = Math.Clamp(completion.ReplaceSpan.End, 0, _editor.Document.TextLength),
            Width = 360,
            CloseWhenCaretAtBeginning = true,
        };
        if (_editor.TryFindResource("TblCompletionWindow") is Style windowStyle) window.Style = windowStyle;
        if (_editor.TryFindResource("TblCompletionListBox") is Style listStyle) window.CompletionList.ListBox.Style = listStyle;
        foreach (var item in completion.Items.OrderBy(i => i.Priority)) window.CompletionList.CompletionData.Add(new TblCompletionData(item));
        window.Closed += (_, _) => { if (_completion == window) _completion = null; };
        _completion = window;
        window.Show();
        // Pre-select what the user already typed.
        string typed = _editor.Document.GetText(window.StartOffset, Math.Max(0, _editor.TextArea.Caret.Offset - window.StartOffset));
        if (typed.Length > 0) window.CompletionList.SelectItem(typed);
        return window;
    }

    // ── Quick fixes ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The diagnostics with fixes at the caret (or on its line).</summary>
    public IReadOnlyList<(TblDiagnostic Diagnostic, TblQuickFix Fix)> FixesAtCaret()
    {
        if (_model is null) return [];
        int caret = _editor.TextArea.Caret.Offset;
        var line = _editor.Document.GetLineByOffset(Math.Clamp(caret, 0, _editor.Document.TextLength));
        var at = _model.Diagnostics.Where(d => d.Span.Start <= caret && caret <= d.Span.End).ToList();
        if (at.Count == 0) at = [.. _model.Diagnostics.Where(d => d.Span.Start <= line.EndOffset && d.Span.End >= line.Offset)];
        return [.. at.SelectMany(d => UiDocument.FixesFor(d).Select(f => (d, f)))];
    }

    private void UpdateQuickFixButton()
    {
        if (_quickFixButton is null) return;
        _quickFixButton.Visibility = _document is not null && !_editor.IsReadOnly && FixesAtCaret().Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnQuickFixButtonClick(object sender, RoutedEventArgs e) => ShowQuickFixes();

    /// <summary>Shows the quick fixes at the caret as a menu (Ctrl+.).</summary>
    public ContextMenu? ShowQuickFixes()
    {
        if (_document is null) return null;
        var fixes = FixesAtCaret();
        var menu = new ContextMenu { PlacementTarget = _editor.TextArea, Placement = System.Windows.Controls.Primitives.PlacementMode.RelativePoint };
        var caretRect = _editor.TextArea.Caret.CalculateCaretRectangle();
        var scroll = _editor.TextArea.TextView.ScrollOffset;
        menu.HorizontalOffset = caretRect.Left - scroll.X + 30;
        menu.VerticalOffset = caretRect.Bottom - scroll.Y;
        if (fixes.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No quick fixes here", IsEnabled = false });
        }
        foreach (var (diagnostic, fix) in fixes)
        {
            var item = new MenuItem { Header = fix.Title, ToolTip = diagnostic.Code + ": " + diagnostic.Message };
            var d = diagnostic; var f = fix;
            item.Click += (_, _) => { _document?.ApplyQuickFix(d, f); _editor.TextArea.Focus(); };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
        return menu;
    }

    // ── Lifetime ──────────────────────────────────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _completion?.Close();
        _hoverTip.IsOpen = false;
        if (_theme is not null) _theme.ThemeChanged -= OnThemeChanged;
        _theme = null;
        QuickFixButton = null;
        _editor.TextArea.TextView.MouseHover -= OnMouseHover;
        _editor.TextArea.TextView.MouseHoverStopped -= OnMouseHoverStopped;
        _editor.TextArea.TextView.PreviewMouseLeftButtonUp -= OnTextViewMouseUp;
        _editor.TextArea.TextView.MouseMove -= OnTextViewMouseMove;
        _editor.TextArea.Caret.PositionChanged -= OnCaretMoved;
        _editor.TextArea.TextEntered -= OnTextEntered;
        _editor.PreviewKeyDown -= OnEditorKeyDown;
        if (_document is not null)
        {
            _document.ModelChanged -= OnModelChanged;
            _document.NavigateRequested -= OnNavigateRequested;
            _module.Settings.Changed -= OnSettingsChanged;
            if (_document.Editor == _editor) _document.Editor = null;
        }
        _search?.Uninstall();
        _search = null;
        if (_folding is not null) FoldingManager.Uninstall(_folding);
        _folding = null;
        _document = null;
        _model = null;
    }
}

/// <summary>Sets <c>AutomationProperties.Name</c> from code.</summary>
internal static class AutomationPropertiesHelper
{
    public static void Name(DependencyObject element, string name) => System.Windows.Automation.AutomationProperties.SetName(element, name);
}

/// <summary>One completion entry for AvalonEdit's list.</summary>
public sealed class TblCompletionData(TblCompletionItem item) : ICompletionData
{
    public TblCompletionItem Item { get; } = item;
    public ImageSource? Image => null;
    public string Text => Item.Label;
    public object Content
    {
        get
        {
            var panel = new DockPanel();
            if (!string.IsNullOrEmpty(Item.Detail))
            {
                var detail = new TextBlock { Text = Item.Detail, Opacity = 0.65, Margin = new Thickness(12, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 };
                DockPanel.SetDock(detail, Dock.Right);
                panel.Children.Add(detail);
            }
            panel.Children.Add(new TextBlock { Text = Item.Label, TextTrimming = TextTrimming.CharacterEllipsis });
            return panel;
        }
    }
    public object? Description => string.IsNullOrEmpty(Item.Documentation) ? Item.Detail : Item.Documentation;
    public double Priority => -Item.Priority;
    public void Complete(ICSharpCode.AvalonEdit.Editing.TextArea textArea, ICSharpCode.AvalonEdit.Document.ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
        textArea.Document.Replace(completionSegment, Item.InsertText);
}
