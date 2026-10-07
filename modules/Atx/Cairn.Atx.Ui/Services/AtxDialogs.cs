using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using Cairn.Atx.Ui.Views.Dialogs;
using Cairn.Atx.Schema;
using Cairn.Workspace;
using Microsoft.Win32;

namespace Cairn.Atx.Ui.Services;

/// <summary>What to do about an image that lives outside every search location.</summary>
public enum OutsideFileChoice { Copy, Reference, Cancel }

/// <summary>
/// What to do when copying an image next to the .atx would land on a name a different file
/// already has.
/// </summary>
public enum ReplaceChoice
{
    /// <summary>Overwrite the files that are already there.</summary>
    Replace,

    /// <summary>Leave them alone; the frames will use the images already in the folder.</summary>
    KeepExisting,

    /// <summary>Do nothing at all.</summary>
    Cancel,
}

/// <summary>ATX-only pickers and prompts (owner = the main window); the generic ones are the shell's <see cref="IDialogService"/>.</summary>
public sealed class AtxDialogs
{
    private const string AtxFilter = "ATX animated textures (*.atx)|*.atx|All files (*.*)|*.*";

    private const string VppFilter = "Red Faction archives (*.vpp)|*.vpp|All files (*.*)|*.*";

    private const string VbmFilter = "Volition bitmaps (*.vbm)|*.vbm|All files (*.*)|*.*";

    public Window? Owner { get; set; }

    public string[] OpenAtxFiles(string? initialFolder)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open ATX file",
            Filter = AtxFilter,
            Multiselect = true,
            CheckFileExists = true,
        };
        ApplyFolder(dialog, initialFolder);
        using (ModalScope.Enter()) return dialog.ShowDialog(Owner) == true ? dialog.FileNames : [];
    }

    public string? OpenVppFile(string? initialFolder)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a .vpp archive",
            Filter = VppFilter,
            Multiselect = false,
            CheckFileExists = true,
        };
        ApplyFolder(dialog, initialFolder);
        using (ModalScope.Enter()) return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? OpenVbmFile(string? initialFolder)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import a .vbm",
            Filter = VbmFilter,
            Multiselect = false,
            CheckFileExists = true,
        };
        ApplyFolder(dialog, initialFolder);
        using (ModalScope.Enter()) return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string[] OpenImageFiles(string? initialFolder, string title = "Add frames")
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = ImageFilter(),
            Multiselect = true,
            CheckFileExists = true,
        };
        ApplyFolder(dialog, initialFolder);
        using (ModalScope.Enter()) return dialog.ShowDialog(Owner) == true ? dialog.FileNames : [];
    }

    public string? OpenImageFile(string? initialFolder, string title, string? suggestedName = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = ImageFilter(),
            Multiselect = false,
            CheckFileExists = true,
            FileName = suggestedName ?? string.Empty,
        };
        ApplyFolder(dialog, initialFolder);
        using (ModalScope.Enter()) return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? SaveAtxFile(string? initialFolder, string suggestedName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save ATX file",
            Filter = AtxFilter,
            DefaultExt = ".atx",
            AddExtension = true,
            FileName = suggestedName,
            OverwritePrompt = true,
        };
        ApplyFolder(dialog, initialFolder);
        using (ModalScope.Enter()) return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public bool ConfirmSaveWithErrors(string documentName, int errorCount)
    {
        int choice = ChoiceDialog.Show(
            Owner,
            "Save with errors",
            $"{documentName} still has {errorCount} error{(errorCount == 1 ? "" : "s")}.",
            "The game will not load this file yet. You can save it anyway and fix the problems later.",
            [
                new ChoiceButton("Save _anyway", IsDefault: true),
                new ChoiceButton("Cancel", IsCancel: true),
            ]);
        return choice == 0;
    }

    /// <summary>Self-test hook: when set, answers the copy-or-reference prompt without showing it.</summary>
    internal Func<IReadOnlyList<string>, OutsideFileChoice>? OutsideFilesAnswer { get; set; }

    /// <summary>Self-test hook: when set, answers the replace-existing prompt without showing it.</summary>
    internal Func<IReadOnlyList<string>, ReplaceChoice>? ReplaceExistingAnswer { get; set; }

    public OutsideFileChoice AskOutsideFiles(IReadOnlyList<string> fileNames, string atxFolderName)
    {
        if (OutsideFilesAnswer is { } answer) return answer(fileNames);
        bool many = fileNames.Count > 1;
        int choice = ChoiceDialog.Show(
            Owner,
            "Files outside the search path",
            many
                ? $"{fileNames.Count} images are not in a folder the game will search."
                : $"'{fileNames[0]}' is not in a folder the game will search.",
            $"Frames always store just the file name, so the game has to find the image itself. "
            + $"Copying puts {(many ? "them" : "it")} next to the .atx in {atxFolderName}; referencing "
            + "leaves the file where it is and warns until you add its folder to Settings.",
            [
                new ChoiceButton("_Copy next to the .atx", IsDefault: true),
                new ChoiceButton("_Reference by name only"),
                new ChoiceButton("Cancel", IsCancel: true),
            ],
            fileNames);
        return choice switch
        {
            0 => OutsideFileChoice.Copy,
            1 => OutsideFileChoice.Reference,
            _ => OutsideFileChoice.Cancel,
        };
    }

    public ReplaceChoice AskReplaceExisting(IReadOnlyList<string> fileNames, string atxFolderName)
    {
        if (ReplaceExistingAnswer is { } answer) return answer(fileNames);
        bool many = fileNames.Count > 1;
        int choice = ChoiceDialog.Show(
            Owner,
            "Files with these names are already there",
            many
                ? $"{fileNames.Count} images with these names are already in {atxFolderName}, and "
                  + "they are not the same files."
                : $"'{fileNames[0]}' is already in {atxFolderName}, and it is not the same file.",
            "Replacing overwrites what is there now. Keeping means the frames will use the images "
            + "already in the folder rather than the ones you picked.",
            [
                new ChoiceButton("_Replace"),
                new ChoiceButton("_Keep existing", IsDefault: true),
                new ChoiceButton("Cancel", IsCancel: true),
            ],
            fileNames);
        return choice switch
        {
            0 => ReplaceChoice.Replace,
            1 => ReplaceChoice.KeepExisting,
            _ => ReplaceChoice.Cancel,
        };
    }

    /// <summary>
    /// The shell's dialog service: the generic prompts go through it so they look the same as every module's and,
    /// in unattended runs (self-tests, screenshots), are logged and answered safely instead of opening a window.
    /// </summary>
    public IDialogService? ShellDialogs { get; init; }

    public bool Confirm(string heading, string body, string confirmText) =>
        ShellDialogs is { } shell
            ? shell.Confirm(heading, body, confirmText)
            : ChoiceDialog.Show(Owner, "Cairn", heading, body,
                [new ChoiceButton(confirmText, IsDefault: true), new ChoiceButton("Cancel", IsCancel: true)]) == 0;

    public void ShowError(string heading, string body, string? details = null)
    {
        if (ShellDialogs is { } shell) { shell.ShowError(heading, body, details); return; }
        ChoiceDialog.Show(Owner, "Cairn", heading, body,
            [new ChoiceButton("Close", IsDefault: true, IsCancel: true)], null, details);
    }

    public string? PickFolder(string? initialFolder, string title)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
        };
        try
        {
            if (!string.IsNullOrWhiteSpace(initialFolder) && Directory.Exists(initialFolder))
                dialog.InitialDirectory = Path.GetFullPath(initialFolder);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { }
        using (ModalScope.Enter()) return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    public bool ShowAddSequence(ViewModels.DocumentViewModel document) =>
        AddSequenceDialog.Show(Owner, document);

    /// <summary>
    /// The archive browser. It has a second way out: an "Import as ATX…" link on a multi-frame VBM,
    /// which closes it and hands the entry straight to the import dialog. That happens here rather
    /// than inside the browser so the browser is fully shut before a second modal opens on top of
    /// where it was.
    /// </summary>
    /// <param name="document">The document the frames go into.</param>
    public bool ShowVppBrowser(ViewModels.DocumentViewModel document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var result = VppBrowserDialog.ShowAddFrames(Owner, document);
        if (result.ImportRequest is not { } request) return result.FramesAdded;
        document.Shell.ImportVbmCommand.Execute(
            ViewModels.VbmImportSource.FromArchive(request.ArchivePath, request.EntryName));
        return false;
    }

    public string? PickImageFromVpp(ViewModels.DocumentViewModel document) =>
        VppBrowserDialog.PickOne(Owner, document);

    public (string ArchivePath, string EntryName)? PickVbmFromVpp(
        ViewModels.DocumentViewModel document) =>
        VppBrowserDialog.PickVbm(Owner, document);

    public string? ShowVbmImport(
        AtxWorkspace shell, ViewModels.VbmImportSource source, byte[] bytes,
        Cairn.Formats.Imaging.VbmInfo info) =>
        VbmImportDialog.Show(Owner, shell, source, bytes, info);

    public bool ShowBulkTiming(ViewModels.DocumentViewModel document) =>
        BulkTimingDialog.Show(Owner, document);

    private static string ImageFilter()
    {
        var patterns = new List<string>();
        foreach (string ext in ImageProbe.ReadableExtensions) patterns.Add("*" + ext);
        string all = string.Join(";", patterns);
        return $"Texture images ({all})|{all}"
            + "|Targa (*.tga)|*.tga|DirectDraw Surface (*.dds)|*.dds"
            + "|PNG (*.png)|*.png|JPEG (*.jpg;*.jpeg)|*.jpg;*.jpeg"
            + "|Volition bitmap (*.vbm)|*.vbm|All files (*.*)|*.*";
    }

    private static void ApplyFolder(FileDialog dialog, string? folder)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                dialog.InitialDirectory = Path.GetFullPath(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { }
    }
}
