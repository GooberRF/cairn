using System.Collections.ObjectModel;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Linting;

namespace Cairn.Rfa.Ui.ViewModels;

/// <summary>One offered repair, as a button in the Problems panel.</summary>
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
        ApplyCommand = new RelayCommand(() => _document.ApplyQuickFix(_fix, _diagnostic), () => _document.CanApplyQuickFix(_fix));
    }

    /// <summary>The button caption, e.g. "Set the ramps to fit".</summary>
    public string Title => _fix.Title;

    /// <summary>What pressing it does.</summary>
    public string ToolTip => _fix.Kind switch
    {
        QuickFixKind.Edit => "Apply this fix (one undo step)",
        QuickFixKind.PickPreviewMesh => "Choose another preview mesh",
        QuickFixKind.SaveAs => "Save the clip under another name",
        QuickFixKind.OpenSearchSettings => "Open Settings to set the game directory and search folders",
        QuickFixKind.ConformToSkeleton => "Open Clip › Conform to Skeleton with the mesh this problem names (or the preview mesh) picked",
        QuickFixKind.LocateFile => "Choose the texture to use from the files the game can find; the material's texture name changes (one undo step)",
        _ => "Apply this fix",
    };

    /// <summary>Carries the fix out.</summary>
    public RelayCommand ApplyCommand { get; }
}

/// <summary>One row of the Problems panel.</summary>
public sealed class DiagnosticViewModel
{
    private readonly DocumentViewModel _document;

    internal DiagnosticViewModel(DocumentViewModel document, Diagnostic diagnostic)
    {
        _document = document;
        Diagnostic = diagnostic;
        // Every kind Core produces has a handler in the clip and mesh documents (a read-only .v3m shows its
        // edits and Locate disabled). A kind added to Core later is not offered until the documents carry it
        // out: a button that does nothing is worse than no button.
        QuickFixes = [.. diagnostic.QuickFixes
            .Where(f => f.Kind is QuickFixKind.Edit or QuickFixKind.PickPreviewMesh or QuickFixKind.ConformToSkeleton
                or QuickFixKind.SaveAs or QuickFixKind.OpenSearchSettings or QuickFixKind.LocateFile)
            .Select(f => new QuickFixViewModel(document, diagnostic, f))];
        RevealCommand = new RelayCommand(() => _document.Reveal(Diagnostic.Location));
        LocationText = document.DescribeLocation(diagnostic.Location);
    }

    /// <summary>True when <paramref name="other"/> would produce an identical row (the row is then kept).</summary>
    internal bool Matches(Diagnostic other) =>
        Diagnostic.Code == other.Code
        && Diagnostic.Severity == other.Severity
        && Equals(Diagnostic.Location, other.Location)
        && string.Equals(Diagnostic.Message, other.Message, StringComparison.Ordinal)
        && string.Equals(Diagnostic.Help, other.Help, StringComparison.Ordinal)
        && Diagnostic.QuickFixes.Count == other.QuickFixes.Count;

    public Diagnostic Diagnostic { get; }

    public string Code => Diagnostic.Code;

    public DiagnosticSeverity Severity => Diagnostic.Severity;

    public string Message => Diagnostic.Message;

    public string Help => Diagnostic.Help;

    /// <summary>"Bone 3 (ult2-bdbn-spine01) · key at 12 f", "Header: ramp in".</summary>
    public string LocationText { get; }

    /// <summary>Accessible label combining severity, code and message.</summary>
    public string AutomationName => $"{Severity}: {Code}. {Message}";

    public IReadOnlyList<QuickFixViewModel> QuickFixes { get; }

    public bool HasQuickFixes => QuickFixes.Count > 0;

    /// <summary>Selects what the problem points at (bone, field, key time, mesh node).</summary>
    public RelayCommand RevealCommand { get; }
}

/// <summary>
/// The Problems panel: every diagnostic of the active document, filtered by severity, each with its
/// message, help text, location (click to select the bone or field) and inline quick-fix buttons.
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

    public bool IsEmpty => Items.Count == 0;

    public string EmptyText => _document.Diagnostics.Count == 0
        ? $"No problems — {_document.DisplayName} will load as intended."
        : "No problems match the current filter.";

    /// <summary>Rebuilds the list from the document's diagnostics, keeping unchanged rows (and the scroll position).</summary>
    public void Refresh()
    {
        var wanted = _document.Diagnostics.Where(d => Passes(d.Severity)).ToList();
        for (int i = 0; i < wanted.Count; i++)
        {
            if (i < Items.Count && Items[i].Matches(wanted[i])) continue;
            var row = new DiagnosticViewModel(_document, wanted[i]);
            if (i < Items.Count) Items[i] = row;
            else Items.Add(row);
        }
        while (Items.Count > wanted.Count) Items.RemoveAt(Items.Count - 1);
        RaiseAll(nameof(IsEmpty), nameof(EmptyText), nameof(ErrorCount), nameof(WarningCount), nameof(InfoCount));
    }

    private bool Passes(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => _showErrors,
        DiagnosticSeverity.Warning => _showWarnings,
        _ => _showInfo,
    };
}
