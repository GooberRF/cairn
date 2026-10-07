using System.ComponentModel;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Ui.Views.Dialogs.ClipTools;

namespace Cairn.Rfa.Ui.ViewModels.ClipTools;

/// <summary>One Clip menu tool: its id (diagnostics), and how to build its dialog's view-model.</summary>
/// <param name="Id">"trim", "reduce"… (also the <c>--dialog</c> name).</param>
/// <param name="Create">Builds the view-model for a document.</param>
public sealed record ClipToolInfo(string Id, Func<ClipDocumentViewModel, ClipDialogViewModel> Create);

/// <summary>
/// The Clip menu's tool commands (phase 5), exposed on <see cref="RfaWorkspace.ClipTools"/>: each opens
/// its dialog for the active clip document. Enabled for an editable clip document.
/// </summary>
public sealed class ClipToolCommands
{
    private readonly RfaWorkspace _shell;
    private readonly List<RelayCommand> _all = [];

    /// <summary>Every tool, in menu order.</summary>
    public static IReadOnlyList<ClipToolInfo> Tools { get; } =
    [
        new("trim", d => new TrimToolViewModel(d)),
        new("shift", d => new ShiftToolViewModel(d)),
        new("retime", d => new RetimeToolViewModel(d)),
        new("reverse", d => new ReverseToolViewModel(d)),
        new("range", d => new RecomputeRangeToolViewModel(d)),
        new("loop", d => new LoopToolViewModel(d)),
        new("resample", d => new ResampleToolViewModel(d)),
        new("reduce", d => new ReduceToolViewModel(d)),
        new("mirror", d => new MirrorToolViewModel(d)),
        new("offset", d => new OffsetToolViewModel(d)),
        new("root", d => new RootMotionToolViewModel(d)),
        new("lengths", d => new BoneLengthsToolViewModel(d)),
        new("conform", d => new ConformToolViewModel(d)),
        new("weights", d => new WeightsToolViewModel(d)),
        new("normalize", d => new NormalizeToolViewModel(d)),
        new("compare", d => new CompareDialogViewModel(d)),
    ];

    public ClipToolCommands(RfaWorkspace shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        TrimCommand = Make("trim");
        ShiftCommand = Make("shift");
        RetimeCommand = Make("retime");
        ReverseCommand = Make("reverse");
        RecomputeRangeCommand = Make("range");
        LoopCommand = Make("loop");
        ResampleCommand = Make("resample");
        ReduceCommand = Make("reduce");
        MirrorCommand = Make("mirror");
        OffsetCommand = Make("offset");
        RootMotionCommand = Make("root");
        BoneLengthsCommand = Make("lengths");
        ConformCommand = Make("conform");
        WeightsCommand = Make("weights");
        NormalizeCommand = Make("normalize");
        CompareCommand = Make("compare", editable: false);
        ClearCompareCommand = new RelayCommand(
            () => (_shell.ActiveDocument as ClipDocumentViewModel)?.Compare.Clear(),
            () => _shell.ActiveDocument is ClipDocumentViewModel { Compare.IsActive: true });
        _all.Add(ClearCompareCommand);
        _shell.PropertyChanged += OnShellPropertyChanged;
    }

    public RelayCommand TrimCommand { get; }
    public RelayCommand ShiftCommand { get; }
    public RelayCommand RetimeCommand { get; }
    public RelayCommand ReverseCommand { get; }
    public RelayCommand RecomputeRangeCommand { get; }
    public RelayCommand LoopCommand { get; }
    public RelayCommand ResampleCommand { get; }
    public RelayCommand ReduceCommand { get; }
    public RelayCommand MirrorCommand { get; }
    public RelayCommand OffsetCommand { get; }
    public RelayCommand RootMotionCommand { get; }
    public RelayCommand BoneLengthsCommand { get; }
    public RelayCommand ConformCommand { get; }
    public RelayCommand WeightsCommand { get; }
    public RelayCommand NormalizeCommand { get; }
    public RelayCommand CompareCommand { get; }

    /// <summary>Clip › Clear Comparison.</summary>
    public RelayCommand ClearCompareCommand { get; }

    /// <summary>The tool registered under <paramref name="id"/>.</summary>
    public static ClipToolInfo Find(string id) =>
        Tools.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"No clip tool '{id}'.", nameof(id));

    /// <summary>Re-queries every command (the active document or its state changed).</summary>
    public void Refresh()
    {
        foreach (var c in _all) c.RaiseCanExecuteChanged();
    }

    private RelayCommand Make(string id, bool editable = true)
    {
        var info = Find(id);
        var command = new RelayCommand(
            () => Open(info),
            () => _shell.ActiveDocument is ClipDocumentViewModel d && (!editable || !d.IsReadOnly));
        _all.Add(command);
        return command;
    }

    private void Open(ClipToolInfo info)
    {
        if (_shell.ActiveDocument is not ClipDocumentViewModel document) return;
        _shell.CommitPendingEdits();
        var model = info.Create(document);
        ClipToolWindow.ShowModal(_shell.Dialogs.Owner, model);
        Refresh();
    }

    /// <summary>
    /// Opens Conform to Skeleton for <paramref name="document"/> with <paramref name="meshName"/> picked
    /// (the Problems panel's "Conform to skeleton…" quick fix on RFA002, RFA007 and RFA013).
    /// </summary>
    public void OpenConform(ClipDocumentViewModel document, string? meshName)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.IsReadOnly) return;
        _shell.CommitPendingEdits();
        ClipToolWindow.ShowModal(_shell.Dialogs.Owner, new ConformToolViewModel(document, meshName));
        Refresh();
    }

    private ClipCompareViewModel? _watchedCompare;

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(RfaWorkspace.ActiveDocument) or nameof(RfaWorkspace.IsClipDocument) or nameof(RfaWorkspace.HasDocument)))
            return;
        // Clear Comparison follows the active document's comparison, which the viewport chip can also clear.
        var compare = (_shell.ActiveDocument as ClipDocumentViewModel)?.Compare;
        if (!ReferenceEquals(compare, _watchedCompare))
        {
            if (_watchedCompare is not null) _watchedCompare.PropertyChanged -= OnCompareChanged;
            _watchedCompare = compare;
            if (compare is not null) compare.PropertyChanged += OnCompareChanged;
        }
        Refresh();
    }

    private void OnCompareChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClipCompareViewModel.IsActive)) ClearCompareCommand.RaiseCanExecuteChanged();
    }
}
