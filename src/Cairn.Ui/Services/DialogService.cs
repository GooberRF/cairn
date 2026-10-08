using System.Windows;
using Cairn.Assets;
using Cairn.Ui.Controls;
using Cairn.Workspace;
using Microsoft.Win32;

namespace Cairn.Ui.Services;

/// <summary>What to do about a document with unsaved changes.</summary>
public enum UnsavedChoice { Save, DontSave, Cancel }

/// <summary>
/// The generic file pickers and modal prompts, behind one interface so view-models stay testable.
/// The shell and the modules add their own windows by subclassing <see cref="DialogService"/> (or by
/// extending this interface in their own assemblies).
/// </summary>
public interface IDialogService
{
    /// <summary>The window modals centre on.</summary>
    Window? Owner { get; set; }

    /// <summary>
    /// Picks documents to open. Returns an empty array when cancelled.
    /// </summary>
    /// <param name="initialFolder">Where the dialog starts, or null.</param>
    /// <param name="filter">A Win32 filter built from the document kinds and importers.</param>
    string[] OpenDocuments(string? initialFolder, string filter);

    /// <summary>Picks where to save a document. Returns null when cancelled.</summary>
    /// <param name="initialFolder">Where the dialog starts, or null.</param>
    /// <param name="suggestedName">The file name offered.</param>
    /// <param name="extension">The default extension (lower case, with the dot).</param>
    /// <param name="filter">The document kind's Win32 filter; "All files" is appended.</param>
    string? SaveDocument(string? initialFolder, string suggestedName, string extension, string filter);

    /// <summary>Save / Don't Save / Cancel for one or more documents (all of them listed).</summary>
    UnsavedChoice AskUnsavedChanges(IReadOnlyList<string> documentNames);

    /// <summary>Confirms saving a file the game will misbehave with.</summary>
    bool ConfirmSaveWithErrors(string documentName, int errorCount);

    /// <summary>Confirms an action that cannot be undone by Ctrl+Z. Declined automatically in a diagnostic run.</summary>
    bool Confirm(string heading, string body, string confirmText);

    /// <summary>Reports a failure the user needs to know about. Collected instead of shown in a diagnostic run.</summary>
    void ShowError(string heading, string body, string? details = null);

    /// <summary>Reports an unhandled exception, listing what was rescued.</summary>
    void ShowCrash(string body, IReadOnlyList<string> recovered, string details);

    /// <summary>Picks one folder. Returns null when cancelled.</summary>
    string? PickFolder(string? initialFolder, string title);

    /// <summary>Picks files to read (any kind). Returns an empty array when cancelled.</summary>
    /// <param name="initialFolder">Where the dialog starts, or null.</param>
    /// <param name="title">The dialog title.</param>
    /// <param name="filter">A Win32 filter ("glTF (*.gltf;*.glb)|*.gltf;*.glb|All files (*.*)|*.*").</param>
    /// <param name="multiselect">True to allow several files.</param>
    string[] OpenFiles(string? initialFolder, string title, string filter, bool multiselect);

    /// <summary>Picks a file to write. Returns null when cancelled.</summary>
    /// <param name="initialFolder">Where the dialog starts, or null.</param>
    /// <param name="title">The dialog title.</param>
    /// <param name="filter">A Win32 filter.</param>
    /// <param name="suggestedName">The file name offered.</param>
    /// <param name="defaultExtension">".gltf", ".json"… added when the user types none.</param>
    string? SaveFile(string? initialFolder, string title, string filter, string suggestedName, string defaultExtension);

    /// <summary>
    /// Asks a question with several answers. Returns the index of the button pressed, or the index of
    /// the cancel button when the dialog is closed another way.
    /// </summary>
    /// <param name="heading">The question.</param>
    /// <param name="body">What each answer means.</param>
    /// <param name="buttons">Button captions ("_Replace"); the first is the default.</param>
    /// <param name="cancelIndex">The answer Esc and the title bar's close button give.</param>
    int Choose(string heading, string body, IReadOnlyList<string> buttons, int cancelIndex);
}

/// <summary>
/// The production <see cref="IDialogService"/>: Win32 file dialogs and <see cref="ChoiceDialog"/>.
/// Every member is virtual so the shell and modules can subclass it and add their own windows.
/// </summary>
public class DialogService : IDialogService
{
    /// <summary>The caption of every prompt.</summary>
    protected const string ProductName = "Cairn";

    public Window? Owner { get; set; }

    /// <summary>
    /// For an unattended diagnostic run (<c>--screenshot</c>): error messages are collected here instead of
    /// shown as a modal dialog nobody would close (the runner logs them). Null shows them normally.
    /// </summary>
    public List<string>? CollectErrors { get; init; }

    /// <summary>
    /// Unattended runs only: answers <see cref="Choose"/> (heading, buttons -> index) so a self-test can drive a
    /// prompt's path without a window. Null in an unattended run takes the cancel answer.
    /// </summary>
    public Func<string, IReadOnlyList<string>, int>? NonInteractiveChoice { get; set; }

    /// <summary>
    /// The settings whose game directory no save or export dialog starts in (set by the shell). Null leaves the
    /// folder a caller gives unchanged.
    /// </summary>
    public AppSettings? SaveFolderSettings { get; set; }

    /// <summary>
    /// Where a save or export dialog starts: <paramref name="preferred"/> when it exists and is not the game directory
    /// or inside it (a loose file there changes what the game loads); otherwise the last folder saved to under the
    /// same rule; otherwise the Documents folder. With no <paramref name="settings"/>, <paramref name="preferred"/>.
    /// </summary>
    public static string? SafeSaveFolder(string? preferred, AppSettings? settings)
    {
        if (settings is null) return preferred;
        if (Usable(preferred, settings.GameDirectory)) return preferred;
        if (Usable(settings.LastSaveFolder, settings.GameDirectory)) return settings.LastSaveFolder;
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        static bool Usable(string? folder, string? game)
        {
            if (string.IsNullOrWhiteSpace(folder) || GameDirectoryLocator.IsInGameDirectory(folder, game)) return false;
            try { return Directory.Exists(folder); }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return false; }
        }
    }

    /// <summary>True (and the prompt logged) when no window may be shown: every prompt then takes a safe answer.</summary>
    protected bool Unattended(string what)
    {
        if (CollectErrors is not { } collected) return false;
        lock (collected) collected.Add($"(no window) {what}");
        return true;
    }

    public virtual string[] OpenDocuments(string? initialFolder, string filter)
    {
        if (Unattended("Open: cancelled")) return [];
        var dialog = new OpenFileDialog
        {
            Title = "Open",
            Filter = filter,
            Multiselect = true,
            CheckFileExists = true,
        };
        ApplyFolder(dialog, initialFolder);
        using (ModalScope.Enter()) return dialog.ShowDialog(Owner) == true ? dialog.FileNames : [];
    }

    public virtual string? SaveDocument(string? initialFolder, string suggestedName, string extension, string filter)
    {
        if (Unattended($"Save as {suggestedName}: cancelled")) return null;
        var dialog = new SaveFileDialog
        {
            Title = "Save as",
            Filter = filter + "|All files (*.*)|*.*",
            DefaultExt = extension,
            AddExtension = true,
            FileName = suggestedName,
            OverwritePrompt = true,
        };
        ApplyFolder(dialog, SafeSaveFolder(initialFolder, SaveFolderSettings));
        using (ModalScope.Enter()) return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public virtual UnsavedChoice AskUnsavedChanges(IReadOnlyList<string> documentNames)
    {
        // Unattended: discard the in-memory edits (nothing on disk changes) so a close never waits for an answer.
        if (Unattended($"Save changes to {string.Join(", ", documentNames)}? -> Don't Save")) return UnsavedChoice.DontSave;
        bool many = documentNames.Count > 1;
        int choice = ChoiceDialog.Show(
            Owner,
            ProductName,
            many ? $"Save changes to {documentNames.Count} files?" : $"Save changes to {documentNames[0]}?",
            "Your changes will be lost if you don't save them.",
            [
                new ChoiceButton("_Save", IsDefault: true),
                new ChoiceButton("Do_n't Save"),
                new ChoiceButton("Cancel", IsCancel: true),
            ],
            many ? documentNames : null);
        return choice switch
        {
            0 => UnsavedChoice.Save,
            1 => UnsavedChoice.DontSave,
            _ => UnsavedChoice.Cancel,
        };
    }

    public virtual bool ConfirmSaveWithErrors(string documentName, int errorCount)
    {
        if (Unattended($"Save {documentName} with {errorCount} errors? -> Cancel")) return false;
        int choice = ChoiceDialog.Show(
            Owner,
            "Save with errors",
            $"{documentName} still has {errorCount} error{(errorCount == 1 ? "" : "s")}.",
            "The game will misbehave with this file as it is. You can save it anyway and fix the problems later.",
            [
                new ChoiceButton("Save _anyway", IsDefault: true),
                new ChoiceButton("Cancel", IsCancel: true),
            ]);
        return choice == 0;
    }

    public virtual bool Confirm(string heading, string body, string confirmText)
    {
        if (CollectErrors is { } collected)
        {
            // Unattended: nobody can answer, so the safe answer (Cancel) is taken and logged.
            lock (collected) collected.Add($"(declined) {heading} {body}");
            return false;
        }
        return ChoiceDialog.Show(Owner, ProductName, heading, body,
            [new ChoiceButton(confirmText, IsDefault: true), new ChoiceButton("Cancel", IsCancel: true)]) == 0;
    }

    public virtual void ShowError(string heading, string body, string? details = null)
    {
        if (CollectErrors is { } collected)
        {
            lock (collected) collected.Add($"{heading} {body}" + (details is null ? string.Empty : $" ({details})"));
            return;
        }
        ChoiceDialog.Show(Owner, ProductName, heading, body,
            [new ChoiceButton("Close", IsDefault: true, IsCancel: true)], null, details);
    }

    public virtual void ShowCrash(string body, IReadOnlyList<string> recovered, string details)
    {
        if (Unattended($"Something went wrong. {body} ({details})")) return;
        ChoiceDialog.Show(
            Owner,
            ProductName,
            "Something went wrong.",
            body,
            [new ChoiceButton("Close", IsDefault: true, IsCancel: true)],
            recovered.Count > 0 ? recovered : null,
            details);
    }

    public virtual string? PickFolder(string? initialFolder, string title)
    {
        if (Unattended($"{title}: cancelled")) return null;
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        try
        {
            if (!string.IsNullOrWhiteSpace(initialFolder) && Directory.Exists(initialFolder))
                dialog.InitialDirectory = Path.GetFullPath(initialFolder);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { }
        using (ModalScope.Enter()) return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    public virtual string[] OpenFiles(string? initialFolder, string title, string filter, bool multiselect)
    {
        if (Unattended($"{title}: cancelled")) return [];
        var dialog = new OpenFileDialog { Title = title, Filter = filter, Multiselect = multiselect, CheckFileExists = true };
        ApplyFolder(dialog, initialFolder);
        using (ModalScope.Enter()) return dialog.ShowDialog(Owner) == true ? dialog.FileNames : [];
    }

    public virtual string? SaveFile(string? initialFolder, string title, string filter, string suggestedName, string defaultExtension)
    {
        if (Unattended($"{title} {suggestedName}: cancelled")) return null;
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            DefaultExt = defaultExtension,
            AddExtension = true,
            FileName = suggestedName,
            OverwritePrompt = true,
        };
        ApplyFolder(dialog, SafeSaveFolder(initialFolder, SaveFolderSettings));
        using (ModalScope.Enter()) return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public virtual int Choose(string heading, string body, IReadOnlyList<string> buttons, int cancelIndex)
    {
        ArgumentNullException.ThrowIfNull(buttons);
        if (buttons.Count == 0) throw new ArgumentException("A choice needs at least one button.", nameof(buttons));
        if (CollectErrors is not null)
        {
            int answer = NonInteractiveChoice?.Invoke(heading, buttons) ?? cancelIndex;
            if (answer < 0 || answer >= buttons.Count) answer = cancelIndex;
            Unattended($"{heading} -> {(answer >= 0 && answer < buttons.Count ? buttons[answer] : "cancel")}");
            return answer;
        }
        var list = buttons.Select((b, i) => new ChoiceButton(b, IsDefault: i == 0, IsCancel: i == cancelIndex)).ToList();
        // Asked from inside another dialog (a batch's collision prompt), it belongs on top of that dialog.
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsLoaded) ?? Owner;
        int choice = ChoiceDialog.Show(owner, ProductName, heading, body, list);
        return choice < 0 || choice >= buttons.Count ? cancelIndex : choice;
    }

    /// <summary>Starts a file dialog in <paramref name="folder"/> when it exists.</summary>
    protected static void ApplyFolder(FileDialog dialog, string? folder)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                dialog.InitialDirectory = Path.GetFullPath(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { }
    }
}
