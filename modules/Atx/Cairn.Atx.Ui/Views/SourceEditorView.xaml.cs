using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Cairn.Atx.Ui.Editor;
using Cairn.Atx.Ui.Services;
using Cairn.Atx.Ui.ViewModels;
using Cairn.Atx.Linting;
using Cairn.Atx.Parsing;
using Cairn.Atx.Schema;
using Cairn.Atx.Text;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Search;

namespace Cairn.Atx.Ui.Views;

/// <summary>
/// The source pane: AvalonEdit plus everything the design asks of it — syntax highlighting in both
/// themes, squiggles with hover tooltips, hover docs from <see cref="AtxSchema"/>, context-aware
/// completion, Ctrl+. quick fixes, find/replace, folding per frame block, Ctrl+/ comment toggle,
/// and the soft highlight that keeps the caret and the frames list in step.
/// </summary>
public partial class SourceEditorView : UserControl
{
    private readonly SquiggleRenderer _squiggles = new();
    private readonly BlockHighlightRenderer _blockHighlight = new();
    private readonly ToolTip _hoverTip = new() { MaxWidth = 460 };

    private DocumentViewModel? _document;
    private ThemeService? _theme;
    private FoldingManager? _folding;
    private SearchPanel? _search;
    private CompletionWindow? _completion;
    private bool _applyingDocument;

    public SourceEditorView()
    {
        InitializeComponent();

        Editor.Options.HighlightCurrentLine = true;
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.IndentationSize = 2;
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.Options.AllowScrollBelowDocument = true;

        Editor.TextArea.TextView.BackgroundRenderers.Add(_blockHighlight);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_squiggles);
        Editor.TextArea.Caret.PositionChanged += OnCaretMoved;
        Editor.TextArea.TextView.MouseHover += OnMouseHover;
        Editor.TextArea.TextView.MouseHoverStopped += (_, _) => _hoverTip.IsOpen = false;
        Editor.TextArea.TextEntered += OnTextEntered;
        Editor.PreviewKeyDown += OnEditorKeyDown;
        Editor.TextArea.GotKeyboardFocus += (_, _) => UpdateBlockHighlight();

        // AvalonEdit's own Ctrl+Z calls UndoStack.Undo() directly, and that throws while a gesture
        // group is open — a spinner's group stays open for 600 ms after the last step, and the
        // stepper buttons are not focusable, so the caret can still be in the editor when it does.
        // A CommandBinding of our own is not enough: the TextArea's default input handler installs
        // its bindings first and the first match wins. A preview handler runs before any of them.
        CommandManager.AddPreviewExecutedHandler(Editor.TextArea, OnEditorPreviewExecuted);

        FindButton.Click += (_, _) => OpenSearch(replace: false);
        QuickFixButton.Click += (_, _) => ShowQuickFixes();
        WireFindBar();

        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => Install();
        Unloaded += (_, _) => Detach();
    }

    private void Install()
    {
        _search ??= SearchPanel.Install(Editor);
        _folding ??= FoldingManager.Install(Editor.TextArea);
        if (_theme is null && AtxModule.Workspace is { } app)
        {
            _theme = app.Theme;
            _theme.ThemeChanged += OnThemeChanged;
            ApplyTheme();
        }
        // Each document owns one view whose DataContext is set once, so switching tabs unloads and
        // reloads this view without DataContextChanged firing again. Re-resolve the document here
        // or everything driven by it — squiggles, folding, hover docs, completion, Reveal — would
        // stay dead for the rest of the tab's life.
        if (_document is null) Subscribe(DataContext as DocumentViewModel);
        RefreshAll();
    }

    private void Detach()
    {
        if (_theme is not null) _theme.ThemeChanged -= OnThemeChanged;
        _theme = null;
        Unsubscribe(_document);
        _document = null;
    }

    // ── Document wiring ───────────────────────────────────────────────────────

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Unsubscribe(e.OldValue as DocumentViewModel);
        Subscribe(e.NewValue as DocumentViewModel);
    }

    private void Subscribe(DocumentViewModel? document)
    {
        _document = document;

        if (_document is null)
        {
            Editor.Document = new ICSharpCode.AvalonEdit.Document.TextDocument();
            return;
        }

        _applyingDocument = true;
        try { Editor.Document = _document.Document; }
        finally { _applyingDocument = false; }

        _document.Refreshed += OnRefreshed;
        _document.DiagnosticsChanged += OnRefreshed;
        _document.RevealRequested += OnRevealRequested;
        _document.SelectRequested += OnSelectRequested;
        RefreshAll();
    }

    private void Unsubscribe(DocumentViewModel? document)
    {
        if (document is null) return;
        document.Refreshed -= OnRefreshed;
        document.DiagnosticsChanged -= OnRefreshed;
        document.RevealRequested -= OnRevealRequested;
        document.SelectRequested -= OnSelectRequested;
    }

    /// <summary>
    /// Takes Undo and Redo away from AvalonEdit and hands them to the document, which closes any
    /// open gesture group first. Everything else the editor binds is left alone.
    /// </summary>
    private void OnEditorPreviewExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (_document is null) return;
        if (e.Command == ApplicationCommands.Undo)
        {
            _document.Undo();
            e.Handled = true;
        }
        else if (e.Command == ApplicationCommands.Redo)
        {
            _document.Redo();
            e.Handled = true;
        }
    }

    private void OnRefreshed(object? sender, EventArgs e) => RefreshAll();

    private void RefreshAll()
    {
        if (_document is null) return;
        UpdateSquiggles();
        UpdateBlockHighlight();
        if (_folding is not null) FrameFolding.Update(_folding, _document);
    }

    // ── Theme ─────────────────────────────────────────────────────────────────

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyTheme();

    private void ApplyTheme()
    {
        if (_theme is null) return;
        Editor.SyntaxHighlighting = AtxHighlighting.Build(_theme);

        _squiggles.ErrorBrush = _theme.Brush("Severity.Error", Brushes.Red);
        _squiggles.WarningBrush = _theme.Brush("Severity.Warning", Brushes.Orange);
        _squiggles.InfoBrush = _theme.Brush("Severity.Info", Brushes.SteelBlue);
        _blockHighlight.Background = _theme.Brush("Editor.FrameHighlight", Brushes.Transparent);
        _blockHighlight.BorderBrush = _theme.Brush("Editor.FrameHighlightBorder", Brushes.Transparent);

        var view = Editor.TextArea.TextView;
        view.CurrentLineBackground = _theme.Brush("Editor.CurrentLine", Brushes.Transparent);
        view.CurrentLineBorder = new Pen(_theme.Brush("Editor.CurrentLineBorder", Brushes.Transparent), 1);
        Editor.TextArea.SelectionBrush = _theme.Brush("App.Selection", Brushes.LightBlue);
        Editor.TextArea.SelectionBorder = new Pen(_theme.Brush("App.Selection", Brushes.LightBlue), 1);
        Editor.TextArea.SelectionForeground = null;

        // The stock panel is invisible (see Themes/Controls.xaml) but still draws the match
        // highlights, so its marker brush is the one colour of it that matters.
        if (_search is not null)
        {
            _search.MarkerBrush = _theme.Brush("Editor.FrameHighlightBorder", Brushes.Yellow);
        }

        view.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Background);
    }

    // ── Squiggles and the block highlight ─────────────────────────────────────

    private void UpdateSquiggles()
    {
        if (_document is null) return;
        int length = Editor.Document.TextLength;
        var markers = _document.Diagnostics
            .GroupBy(d => (d.Span.Start, d.Span.Length))
            .Select(g =>
            {
                int start = Math.Clamp(g.Key.Start, 0, length);
                int span = Math.Clamp(g.Key.Length, 0, length - start);
                if (span == 0) span = Math.Min(1, length - start);
                return new SquiggleMarker(start, span, g.Max(d => d.Severity), [.. g]);
            })
            .Where(m => m.Length > 0)
            .ToList();
        _squiggles.SetMarkers(Editor.TextArea.TextView, markers);
    }

    private void UpdateBlockHighlight()
    {
        if (_document?.SelectedBlockSpan is { } span)
        {
            int length = Editor.Document.TextLength;
            int start = Math.Clamp(span.Start, 0, length);
            _blockHighlight.SetSegment(
                Editor.TextArea.TextView, start, Math.Clamp(span.Length, 0, length - start));
        }
        else
        {
            _blockHighlight.SetSegment(Editor.TextArea.TextView, 0, 0);
        }
    }

    // ── Caret, reveal and selection ───────────────────────────────────────────

    private void OnCaretMoved(object? sender, EventArgs e)
    {
        if (_document is null || _applyingDocument) return;
        var caret = Editor.TextArea.Caret;
        _document.OnCaretMoved(caret.Offset, caret.Line, caret.Column, Editor.TextArea.IsKeyboardFocusWithin);
        _document.Shell.OnCaretMoved();
        UpdateBlockHighlight();
    }

    /// <summary>Scrolls a span into view without taking focus, for list and problems selection.</summary>
    private void OnRevealRequested(object? sender, TextSpan span)
    {
        int length = Editor.Document.TextLength;
        int start = Math.Clamp(span.Start, 0, length);
        var location = Editor.Document.GetLocation(start);
        Editor.ScrollTo(location.Line, location.Column);
        UpdateBlockHighlight();
    }

    /// <summary>Selects a span and focuses the editor, for a click in the problems panel.</summary>
    private void OnSelectRequested(object? sender, TextSpan span)
    {
        int length = Editor.Document.TextLength;
        int start = Math.Clamp(span.Start, 0, length);
        int count = Math.Clamp(span.Length, 0, length - start);
        Editor.Select(start, count);
        var location = Editor.Document.GetLocation(start);
        Editor.ScrollTo(location.Line, location.Column);
        Editor.TextArea.Focus();
    }

    // ── Hover tooltips ────────────────────────────────────────────────────────

    private void OnMouseHover(object sender, MouseEventArgs e)
    {
        if (_document is null) return;
        var position = Editor.TextArea.TextView.GetPositionFloor(
            e.GetPosition(Editor.TextArea.TextView) + Editor.TextArea.TextView.ScrollOffset);
        if (position is null) return;

        int offset = Editor.Document.GetOffset(position.Value.Location);
        var content = BuildTooltip(offset);
        if (content is null) return;

        _hoverTip.PlacementTarget = this;
        _hoverTip.Content = content;
        _hoverTip.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>
    /// Diagnostics under the pointer win, because that is what the user is asking about; otherwise
    /// a key name shows its schema documentation.
    /// </summary>
    private object? BuildTooltip(int offset)
    {
        var markers = _squiggles.MarkersAt(offset);
        if (markers.Count > 0)
        {
            var panel = new StackPanel { MaxWidth = 440 };
            foreach (var diagnostic in markers.SelectMany(m => m.Diagnostics).Distinct())
            {
                panel.Children.Add(new TextBlock
                {
                    Text = diagnostic.Message,
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : 8, 0, 0),
                });
                panel.Children.Add(new TextBlock
                {
                    Text = diagnostic.Help,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.85,
                    Margin = new Thickness(0, 2, 0, 0),
                });
                if (diagnostic.QuickFixes.Count > 0)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = "Ctrl+. : " + string.Join(" · ", diagnostic.QuickFixes.Select(f => f.Title)),
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.7,
                        Margin = new Thickness(0, 4, 0, 0),
                    });
                }
            }
            return panel;
        }

        var info = KeyAt(offset);
        if (info is null) return null;
        return new TextBlock
        {
            Text = AtxCompletion.Describe(info),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 440,
        };
    }

    /// <summary>The schema key whose name token sits under <paramref name="offset"/>, if any.</summary>
    private AtxKeyInfo? KeyAt(int offset)
    {
        if (_document is null) return null;
        var map = _document.Parse.SyntaxMap;
        var block = map.BlockAt(offset);
        var scope = block?.Kind == AtxBlockKind.Frame ? AtxKeyScope.Frame : AtxKeyScope.Header;

        var entries = scope == AtxKeyScope.Frame && block is { FrameIndex: >= 0 }
            && block.FrameIndex < map.FrameKeys.Count
            ? map.FrameKeys[block.FrameIndex]
            : map.HeaderKeys;

        foreach (var entry in entries.Values)
        {
            if (entry.KeySpan.ContainsInclusive(offset)) return AtxSchema.FindKey(scope, entry.Key);
        }
        return null;
    }

    // ── Keyboard, completion and quick fixes ──────────────────────────────────

    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key is Key.OemPeriod or Key.Decimal)
        {
            ShowQuickFixes();
            e.Handled = true;
        }
        else if (ctrl && e.Key is Key.OemQuestion or Key.Oem2)
        {
            CommentToggle.Toggle(Editor);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Space)
        {
            ShowCompletion();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && FindBar.Visibility == Visibility.Visible && _completion is null)
        {
            CloseSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.F3)
        {
            if (FindBar.Visibility != Visibility.Visible) OpenSearch(replace: false);
            else FindNext(forward: !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
        }
    }

    private void OnTextEntered(object? sender, TextCompositionEventArgs e)
    {
        if (_completion is not null) return;
        // Typing a letter at the start of a key, or the first character of a value, is enough of a
        // hint to offer completion; anything else would be noise.
        if (e.Text.Length != 1) return;
        char c = e.Text[0];
        if (!char.IsLetterOrDigit(c) && c is not ('_' or '"' or '=')) return;
        ShowCompletion();
    }

    private void ShowCompletion()
    {
        if (_document is null) return;
        // Ctrl+Space with a list already up would stack a second window on the first, and only the
        // top one ever closes.
        if (_completion is not null) return;
        _document.EnsureParsed();
        var items = AtxCompletion.Suggest(_document, Editor.CaretOffset, out int filterStart);
        if (items.Count == 0) return;

        var window = new CompletionWindow(Editor.TextArea)
        {
            StartOffset = Math.Clamp(filterStart, 0, Editor.CaretOffset),
            EndOffset = Editor.CaretOffset,
            CloseWhenCaretAtBeginning = true,
            Width = 320,
            MaxHeight = 260,
        };
        foreach (var item in items) window.CompletionList.CompletionData.Add(item);
        window.Closed += (_, _) => _completion = null;
        _completion = window;

        // AvalonEdit's completion window has no theme of its own, and the Fluent theme dresses it as
        // an ordinary window — chrome included, which showed as a 1px light line across the top of a
        // dark popup. Both the window and its list get explicit templates, applied before Show so
        // the stock ones never appear even for a frame.
        if (_theme is not null)
        {
            var background = _theme.Brush("App.PaneBackground", Brushes.White);
            var foreground = _theme.Brush("App.Text", Brushes.Black);
            // A borderless WPF window still draws a system resize frame unless resizing is off,
            // and that frame is what showed as a light band across the top of a dark popup.
            window.ResizeMode = ResizeMode.NoResize;
            if (TryFindResource("CompletionWindow") is Style windowStyle) window.Style = windowStyle;
            window.Background = background;
            window.Foreground = foreground;
            window.CompletionList.Background = background;
            window.CompletionList.Foreground = foreground;
        }

        window.Show();

        if (_theme is not null)
        {
            // The list box only exists once the completion list's template is applied.
            window.Dispatcher.BeginInvoke(new Action(() =>
            {
                var list = window.CompletionList.ListBox;
                if (list is null) return;
                if (TryFindResource("CompletionListBox") is Style listStyle) list.Style = listStyle;
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>Ctrl+. : the quick fixes for the diagnostics at the caret, as a menu.</summary>
    private void ShowQuickFixes()
    {
        if (_document is null) return;
        // Build the menu from diagnostics that describe the text as it is now, not as it was
        // before the last keystroke — a fix carries the spans of the parse that produced it.
        _document.EnsureParsed();
        int offset = Editor.CaretOffset;
        var fixes = _document.Diagnostics
            .Where(d => d.Span.ContainsInclusive(offset) && d.QuickFixes.Count > 0)
            .SelectMany(d => d.QuickFixes.Select(f => (Diagnostic: d, Fix: f)))
            .ToList();

        var menu = new ContextMenu
        {
            PlacementTarget = Editor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        if (fixes.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No quick fixes here", IsEnabled = false });
        }
        else
        {
            foreach (var (diagnostic, fix) in fixes)
            {
                var item = new MenuItem { Header = fix.Title, ToolTip = diagnostic.Message };
                var captured = (diagnostic, fix);
                item.Click += (_, _) => _document.ApplyQuickFix(captured.fix, captured.diagnostic);
                menu.Items.Add(item);
            }
        }
        menu.IsOpen = true;
    }

    /// <summary>Focuses the text area, for View &gt; Focus source.</summary>
    public void FocusEditor() => Editor.TextArea.Focus();

    /// <summary>Diagnostics (<c>--find</c>): the find bar open with <paramref name="text"/> as its pattern.</summary>
    internal void OpenSearchFor(string text)
    {
        OpenSearch(replace: false);
        FindBox.Text = text;
        OnPatternChanged();
    }

    /// <summary>Diagnostics (<c>--completion</c>): the completion list at the start of the first key line of the first table.</summary>
    internal Window? ShowCompletionForCapture()
    {
        string text = Editor.Text;
        int table = text.IndexOf("\n[", StringComparison.Ordinal);
        int line = table < 0 ? -1 : text.IndexOf('\n', table + 1);
        Editor.CaretOffset = line < 0 ? 0 : line + 1;
        Editor.TextArea.Focus();
        ShowCompletion();
        return _completion;
    }

    /// <summary>Comments or uncomments the selected lines, for Edit &gt; Toggle Comment.</summary>
    public void ToggleComment() => CommentToggle.Toggle(Editor);

    // ── Find and replace ──────────────────────────────────────────────────────

    private void WireFindBar()
    {
        FindBox.TextChanged += (_, _) => OnPatternChanged();
        FindBox.PreviewKeyDown += OnFindBoxKeyDown;
        ReplaceBox.PreviewKeyDown += OnReplaceBoxKeyDown;

        NextButton.Click += (_, _) => FindNext(forward: true);
        PreviousButton.Click += (_, _) => FindNext(forward: false);
        CloseFindButton.Click += (_, _) => CloseSearch();
        ReplaceButton.Click += (_, _) => ReplaceCurrent();
        ReplaceAllButton.Click += (_, _) => ReplaceAll();

        MatchCaseToggle.Checked += (_, _) => OnPatternChanged();
        MatchCaseToggle.Unchecked += (_, _) => OnPatternChanged();
        WholeWordToggle.Checked += (_, _) => OnPatternChanged();
        WholeWordToggle.Unchecked += (_, _) => OnPatternChanged();
        RegexToggle.Checked += (_, _) => OnPatternChanged();
        RegexToggle.Unchecked += (_, _) => OnPatternChanged();
        ReplaceToggle.Checked += (_, _) => ShowReplaceRow(true);
        ReplaceToggle.Unchecked += (_, _) => ShowReplaceRow(false);
    }

    /// <summary>
    /// Opens the find bar, optionally with the replace row showing. Ctrl+F and Ctrl+H both land
    /// here; whichever one the user pressed, the selected word is offered as the search term,
    /// because that is nearly always what they meant.
    /// </summary>
    /// <param name="replace">True to show the replace row (Ctrl+H).</param>
    public void OpenSearch(bool replace)
    {
        FindBar.Visibility = Visibility.Visible;
        if (replace) ReplaceToggle.IsChecked = true;

        string selected = Editor.SelectedText;
        if (!string.IsNullOrEmpty(selected)
            && !selected.Contains('\n', StringComparison.Ordinal))
        {
            FindBox.Text = selected;
        }

        OnPatternChanged();
        FindBox.Focus();
        FindBox.SelectAll();
    }

    /// <summary>Closes the find bar and clears the match highlighting.</summary>
    public void CloseSearch()
    {
        FindBar.Visibility = Visibility.Collapsed;
        if (_search is not null) _search.SearchPattern = string.Empty;
        Editor.TextArea.Focus();
    }

    private void ShowReplaceRow(bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ReplaceRow.Visibility = visibility;
        ReplaceButtons.Visibility = visibility;
        if (visible && FindBar.Visibility == Visibility.Visible) ReplaceBox.Focus();
    }

    private void OnFindBoxKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                FindNext(forward: !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                e.Handled = true;
                break;
            case Key.Escape:
                CloseSearch();
                e.Handled = true;
                break;
        }
    }

    private void OnReplaceBoxKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                ReplaceCurrent();
                e.Handled = true;
                break;
            case Key.Escape:
                CloseSearch();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Keeps the stock search panel's pattern in step with the bar. The panel is invisible but it
    /// is what paints every match, so this is how the highlighting stays live as the user types.
    /// </summary>
    private void OnPatternChanged()
    {
        string pattern = FindBox.Text;
        if (_search is not null)
        {
            _search.MatchCase = MatchCaseToggle.IsChecked == true;
            _search.WholeWords = WholeWordToggle.IsChecked == true;
            _search.UseRegex = RegexToggle.IsChecked == true;
            try
            {
                _search.Open();
                _search.SearchPattern = pattern;
            }
            catch (ArgumentException)
            {
                // A half-typed regular expression; the status line below says so.
            }
        }
        UpdateMatchStatus();
    }

    /// <summary>The compiled pattern, or null when the box is empty or the regex is not valid yet.</summary>
    private Regex? BuildRegex()
    {
        string pattern = FindBox.Text;
        if (string.IsNullOrEmpty(pattern)) return null;

        var options = RegexOptions.CultureInvariant | RegexOptions.Multiline;
        if (MatchCaseToggle.IsChecked != true) options |= RegexOptions.IgnoreCase;

        string expression = RegexToggle.IsChecked == true ? pattern : Regex.Escape(pattern);
        if (WholeWordToggle.IsChecked == true) expression = @"\b(?:" + expression + @")\b";

        try { return new Regex(expression, options, TimeSpan.FromSeconds(2)); }
        catch (ArgumentException) { return null; }
    }

    private void UpdateMatchStatus()
    {
        if (string.IsNullOrEmpty(FindBox.Text))
        {
            MatchStatus.Text = string.Empty;
            return;
        }
        var regex = BuildRegex();
        if (regex is null)
        {
            MatchStatus.Text = "Bad pattern";
            MatchStatus.SetResourceReference(TextBlock.ForegroundProperty, "Severity.Error");
            return;
        }
        MatchStatus.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        int count;
        try { count = regex.Matches(Editor.Document.Text).Count; }
        catch (RegexMatchTimeoutException) { MatchStatus.Text = "Too slow"; return; }
        MatchStatus.Text = count switch
        {
            0 => "No matches",
            1 => "1 match",
            _ => count.ToString(CultureInfo.CurrentCulture) + " matches",
        };
    }

    /// <summary>Selects the next (or previous) match, wrapping around the end of the file.</summary>
    private void FindNext(bool forward)
    {
        var regex = BuildRegex();
        if (regex is null) return;
        string text = Editor.Document.Text;
        if (text.Length == 0) return;

        Match? found = null;
        try
        {
            if (forward)
            {
                int from = Math.Clamp(Editor.SelectionStart + Math.Max(1, Editor.SelectionLength), 0, text.Length);
                var match = regex.Match(text, from);
                found = match.Success ? match : regex.Match(text);
            }
            else
            {
                int before = Math.Clamp(Editor.SelectionStart, 0, text.Length);
                foreach (Match candidate in regex.Matches(text))
                {
                    if (candidate.Index < before) found = candidate;
                    else break;
                }
                if (found is null)
                {
                    var all = regex.Matches(text);
                    if (all.Count > 0) found = all[^1];
                }
            }
        }
        catch (RegexMatchTimeoutException) { return; }

        if (found is not { Success: true }) return;
        Editor.Select(found.Index, found.Length);
        var location = Editor.Document.GetLocation(found.Index);
        Editor.ScrollTo(location.Line, location.Column);
    }

    /// <summary>Replaces the current selection when it is a match, then moves on.</summary>
    private void ReplaceCurrent()
    {
        var regex = BuildRegex();
        if (regex is null) return;

        string selected = Editor.SelectedText;
        Match? match = null;
        if (!string.IsNullOrEmpty(selected))
        {
            try
            {
                var candidate = regex.Match(selected);
                if (candidate.Success && candidate.Index == 0 && candidate.Length == selected.Length)
                    match = candidate;
            }
            catch (RegexMatchTimeoutException) { return; }
        }

        if (match is null)
        {
            // Nothing suitable selected: find one first, so the button always does something.
            FindNext(forward: true);
            return;
        }

        string replacement = RegexToggle.IsChecked == true
            ? match.Result(ReplaceBox.Text)
            : ReplaceBox.Text;
        Editor.Document.Replace(Editor.SelectionStart, Editor.SelectionLength, replacement);
        FindNext(forward: true);
        UpdateMatchStatus();
    }

    /// <summary>
    /// Replaces every match. The whole sweep sits in one <c>BeginUpdate</c>/<c>EndUpdate</c> group
    /// and runs back to front, so earlier offsets stay valid and one Ctrl+Z takes it all back.
    /// </summary>
    private void ReplaceAll()
    {
        var regex = BuildRegex();
        if (regex is null) return;

        List<Match> matches;
        try { matches = [.. regex.Matches(Editor.Document.Text).Cast<Match>()]; }
        catch (RegexMatchTimeoutException) { return; }
        if (matches.Count == 0) { MatchStatus.Text = "No matches"; return; }

        bool useGroups = RegexToggle.IsChecked == true;
        string replacement = ReplaceBox.Text;

        Editor.Document.BeginUpdate();
        try
        {
            for (int i = matches.Count - 1; i >= 0; i--)
            {
                var match = matches[i];
                Editor.Document.Replace(
                    match.Index, match.Length, useGroups ? match.Result(replacement) : replacement);
            }
        }
        finally { Editor.Document.EndUpdate(); }

        MatchStatus.Text = matches.Count == 1
            ? "1 replaced"
            : matches.Count.ToString(CultureInfo.CurrentCulture) + " replaced";
    }
}
