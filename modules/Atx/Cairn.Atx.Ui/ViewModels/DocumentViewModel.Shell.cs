using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using System.Windows;
using Cairn.Atx.Ui.Views;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;

namespace Cairn.Atx.Ui.ViewModels;

/// <summary>The shell's document contract on top of ATX's document (history = AvalonEdit's undo stack).</summary>
public sealed partial class DocumentViewModel
{
    private static readonly HashSet<string> StatusSources = new(StringComparer.Ordinal)
    {
        nameof(ErrorCount), nameof(WarningCount), nameof(FrameCount), nameof(LoopDurationMs),
        nameof(CaretLine), nameof(CaretColumn), nameof(LineEnding), nameof(LineEndingLabel), nameof(Diagnostics),
    };

    private FrameworkElement? _view;
    private bool _statusHooked;

    /// <inheritdoc/>
    public IDocumentKind Kind => AtxModule.DocumentKind;

    /// <inheritdoc/>
    public bool IsReadOnly => OriginText is not null && FilePath is null;

    /// <summary>Where a document opened from bytes came from (e.g. "x.atx in textures.vpp"), or null.</summary>
    public string? OriginText { get; internal set; }

    /// <inheritdoc/>
    public string? UndoLabel => null;

    /// <inheritdoc/>
    public string? RedoLabel => null;

    /// <inheritdoc/>
    public void CommitPendingEdits() => _main.CommitPendingEdits();

    /// <inheritdoc/>
    public bool ConfirmSave() =>
        _main.Shell.IsDiagnosticRun || ErrorCount == 0 || _main.Dialogs.ConfirmSaveWithErrors(DisplayName, ErrorCount);

    /// <inheritdoc/>
    public void SaveTo(string path)
    {
        SuspendFileWatch(true);
        try { AtxTextFiles.SaveDocument(path, TextForSave, LineEnding); }
        finally { SuspendFileWatch(false); }
        MarkSaved(path);
    }

    /// <inheritdoc/>
    public byte[]? CaptureRecovery() => IsDirty ? System.Text.Encoding.UTF8.GetBytes(Document.Text) : null;

    /// <inheritdoc/>
    public IReadOnlyList<StatusItem> StatusItems
    {
        get
        {
            HookStatus();
            var problems = _main.ToggleProblemsCommand;
            var items = new List<StatusItem>
            {
                new($"{ErrorCount} error{(ErrorCount == 1 ? "" : "s")}", "Show or hide the problems panel", problems),
                new($"{WarningCount} warning{(WarningCount == 1 ? "" : "s")}", "Show or hide the problems panel", problems),
            };
            if (Model is not null)
            {
                items.Add(new($"{FrameCount} frame{(FrameCount == 1 ? "" : "s")}"));
                items.Add(new($"Loop {FormatDuration(LoopDurationMs)}"));
            }
            items.Add(new($"Ln {CaretLine}, Col {CaretColumn}"));
            items.Add(new(LineEndingLabel));
            return items;
        }
    }

    /// <inheritdoc/>
    string? IDocument.TabToolTip => TabToolTip;

    /// <inheritdoc/>
    public FrameworkElement View
    {
        get
        {
            HookStatus();
            return _view ??= new DocumentView { DataContext = this };
        }
    }

    /// <inheritdoc/>
    public void OnActivated() => _main.RefreshCommands();

    /// <inheritdoc/>
    public void OnDeactivated()
    {
        if (Preview.IsPlaying) Preview.PlayPauseCommand.Execute(null);
    }

    private void HookStatus()
    {
        if (_statusHooked) return;
        _statusHooked = true;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null || StatusSources.Contains(e.PropertyName)) Raise(nameof(StatusItems));
        };
    }
}
