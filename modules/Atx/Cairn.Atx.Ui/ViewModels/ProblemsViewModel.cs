using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Cairn.Ui.Mvvm;
using Cairn.Atx.Linting;
using Cairn.Atx.Text;

namespace Cairn.Atx.Ui.ViewModels;

/// <summary>One offered repair, as a button in the problems panel or the Ctrl+. popup.</summary>
public sealed class QuickFixViewModel
{
    private readonly DocumentViewModel _document;
    private readonly Diagnostic _diagnostic;
    private readonly QuickFix _fix;

    internal QuickFixViewModel(DocumentViewModel document, Diagnostic diagnostic, QuickFix fix)
    {
        _document = document;
        _diagnostic = diagnostic;
        _fix = fix;
        // A fix that edits the file cannot work while the TOML is broken, and "Convert to standard
        // layout" only makes sense while the file is not already in it. Disabling beats a button
        // that looks live and does nothing when pressed.
        ApplyCommand = new RelayCommand(
            () => _document.ApplyQuickFix(_fix, _diagnostic),
            () => _fix.Kind switch
            {
                QuickFixKind.ConvertToStandardLayout => !_document.HasSyntaxErrors && !_document.IsCanonical,
                QuickFixKind.Edit => _document.CanEditValues,
                // Opening Settings changes nothing about the document, so it is always available.
                QuickFixKind.OpenSearchSettings => true,
                _ => !_document.HasSyntaxErrors,
            });
    }

    /// <summary>The button caption, e.g. "Set to 1".</summary>
    public string Title => _fix.Title;

    /// <summary>Carries the fix out.</summary>
    public RelayCommand ApplyCommand { get; }
}

/// <summary>One row of the problems panel.</summary>
public sealed class DiagnosticViewModel
{
    private readonly DocumentViewModel _document;

    /// <param name="document">The owning document.</param>
    /// <param name="diagnostic">The problem this row shows.</param>
    /// <param name="lines">
    /// A line map of the document, built once by the caller. Building one per row made refreshing
    /// the panel cost (diagnostics × document length), which a file with a few thousand
    /// unresolved frames turns into a visible stall on every keystroke.
    /// </param>
    internal DiagnosticViewModel(DocumentViewModel document, Diagnostic diagnostic, LineMap lines)
    {
        _document = document;
        Diagnostic = diagnostic;
        QuickFixes = [.. diagnostic.QuickFixes.Select(f => new QuickFixViewModel(document, diagnostic, f))];
        GoToCommand = new RelayCommand(GoTo);
        RevealCommand = new RelayCommand(Reveal);

        int line = lines.LineColumn(diagnostic.Span.Start).Line;
        LocationText = diagnostic.FrameIndex is { } frame
            ? $"Frame {frame} · line {line}"
            : $"Line {line}";
    }

    /// <summary>
    /// True when <paramref name="other"/> would produce an identical row, so the existing one can
    /// be kept and the panel's scroll position and selection survive the refresh.
    /// </summary>
    internal bool Matches(Diagnostic other) =>
        Diagnostic.Code == other.Code
        && Diagnostic.Severity == other.Severity
        && Diagnostic.Span.Start == other.Span.Start
        && Diagnostic.Span.Length == other.Span.Length
        && Diagnostic.FrameIndex == other.FrameIndex
        && string.Equals(Diagnostic.Message, other.Message, StringComparison.Ordinal)
        && string.Equals(Diagnostic.Help, other.Help, StringComparison.Ordinal)
        && Diagnostic.QuickFixes.Count == other.QuickFixes.Count
        && Diagnostic.QuickFixes.Zip(other.QuickFixes)
            .All(p => string.Equals(p.First.Title, p.Second.Title, StringComparison.Ordinal));

    /// <summary>The diagnostic this row shows.</summary>
    public Diagnostic Diagnostic { get; }

    public string Code => Diagnostic.Code;

    public DiagnosticSeverity Severity => Diagnostic.Severity;

    public string Message => Diagnostic.Message;

    public string Help => Diagnostic.Help;

    /// <summary>"Frame 3 · line 24", or just the line for a document-level problem.</summary>
    public string LocationText { get; }

    /// <summary>Accessible label combining severity, code and message.</summary>
    public string AutomationName => $"{Severity}: {Code}. {Message}";

    /// <summary>Repairs offered for this problem.</summary>
    public IReadOnlyList<QuickFixViewModel> QuickFixes { get; }

    /// <summary>True when the row shows quick-fix buttons.</summary>
    public bool HasQuickFixes => QuickFixes.Count > 0;

    /// <summary>Selects the problem in the source editor and in the GUI, and focuses the editor.</summary>
    public RelayCommand GoToCommand { get; }

    /// <summary>
    /// Selects the frame and scrolls the source to the problem without taking focus, so arrowing
    /// through the list keeps the keyboard where the user put it.
    /// </summary>
    public RelayCommand RevealCommand { get; }

    private void GoTo()
    {
        Reveal();
        _document.SelectInSource(Diagnostic.Span);
    }

    private void Reveal()
    {
        if (Diagnostic.FrameIndex is { } frame) _document.Frames.SelectFromEditor(frame);
        _document.Reveal(Diagnostic.Span);
    }
}

/// <summary>
/// The Problems panel: every diagnostic for the document, filtered by severity, each with its
/// message, wrapped help text, location and inline quick-fix buttons.
/// </summary>
public sealed class ProblemsViewModel : ObservableObject
{
    private readonly DocumentViewModel _document;
    private bool _showErrors = true;
    private bool _showWarnings = true;
    private bool _showInfo = true;

    public ProblemsViewModel(DocumentViewModel document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
    }

    /// <summary>The visible rows, after the severity filter.</summary>
    public ObservableCollection<DiagnosticViewModel> Items { get; } = [];

    public bool ShowErrors
    {
        get => _showErrors;
        set { if (Set(ref _showErrors, value)) Refresh(); }
    }

    public bool ShowWarnings
    {
        get => _showWarnings;
        set { if (Set(ref _showWarnings, value)) Refresh(); }
    }

    public bool ShowInfo
    {
        get => _showInfo;
        set { if (Set(ref _showInfo, value)) Refresh(); }
    }

    public int ErrorCount => _document.ErrorCount;

    public int WarningCount => _document.WarningCount;

    public int InfoCount => _document.InfoCount;

    /// <summary>The panel header, e.g. "Problems (3)".</summary>
    public string HeaderText
    {
        get
        {
            int total = _document.Diagnostics.Count;
            return total == 0 ? "PROBLEMS" : $"PROBLEMS ({total})";
        }
    }

    /// <summary>True when nothing is listed, so the empty state shows.</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>What to say when the list is empty.</summary>
    public string EmptyText => _document.Diagnostics.Count == 0
        ? "No problems — this ATX will load cleanly."
        : "No problems match the current filter.";

    /// <summary>Rebuilds the list from the document's current diagnostics.</summary>
    public void Refresh()
    {
        var wanted = new List<Diagnostic>(_document.Diagnostics.Count);
        foreach (var diagnostic in _document.Diagnostics)
        {
            if (Passes(diagnostic.Severity)) wanted.Add(diagnostic);
        }

        // Reconcile in place rather than clearing: this runs on every debounced keystroke and
        // again when each asset pass lands, and a full rebuild would throw away the panel's
        // scroll position and selection every time. One line map serves the whole refresh.
        var lines = new LineMap(_document.Parse.Text);
        for (int i = 0; i < wanted.Count; i++)
        {
            if (i < Items.Count && Items[i].Matches(wanted[i])) continue;
            var row = new DiagnosticViewModel(_document, wanted[i], lines);
            if (i < Items.Count) Items[i] = row;
            else Items.Add(row);
        }
        while (Items.Count > wanted.Count) Items.RemoveAt(Items.Count - 1);

        RaiseAll(nameof(IsEmpty), nameof(EmptyText), nameof(HeaderText),
            nameof(ErrorCount), nameof(WarningCount), nameof(InfoCount));
    }

    private bool Passes(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => _showErrors,
        DiagnosticSeverity.Warning => _showWarnings,
        _ => _showInfo,
    };
}
