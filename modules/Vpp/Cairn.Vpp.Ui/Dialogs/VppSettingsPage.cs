using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cairn.Ui.Modules;

namespace Cairn.Vpp.Ui.Dialogs;

/// <summary>The packfile module's settings ("vpp." keys), read by documents when they need them.</summary>
public sealed class VppSettings(Func<ModuleSettings?> store)
{
    /// <summary>Keep the previous packfile as name.vpp.bak when saving over it.</summary>
    public bool KeepBackup { get => store()?.Get("keepBackup", false) ?? false; set => store()?.Set("keepBackup", value); }
    /// <summary>Ask before removing entries (Remove is undoable either way).</summary>
    public bool ConfirmRemove { get => store()?.Get("confirmRemove", false) ?? false; set => store()?.Set("confirmRemove", value); }
    /// <summary>The folder work copies go in; empty = %LOCALAPPDATA%\Cairn\work.</summary>
    public string WorkFolderSetting { get => store()?.Get("workFolder", string.Empty) ?? string.Empty; set => store()?.Set("workFolder", value ?? string.Empty); }

    /// <summary>
    /// The folder that holds one sub-folder of work copies per open packfile: Cairn's own folder, never the chosen
    /// folder itself (a chosen location gets a "Cairn work" folder inside it; see <see cref="Work.VppWorkRoot"/>).
    /// </summary>
    public string WorkRoot => Work.VppWorkRoot.Resolve(WorkFolderSetting);
}

/// <summary>Settings > Packfiles.</summary>
public sealed class VppSettingsPage : ISettingsPage
{
    private readonly VppSettings _settings;
    private readonly CheckBox _backup, _confirmRemove;
    private readonly TextBox _workFolder;
    private readonly StackPanel _view = new() { Margin = new Thickness(16) };

    public VppSettingsPage(VppSettings settings)
    {
        _settings = settings;
        Section("Saving");
        _backup = Box("Keep a .bak copy when saving over a packfile", "The previous file is kept next to it as name.vpp.bak. Saving never writes over a packfile in place either way.");
        Section("Editing");
        _confirmRemove = Box("Ask before removing entries", "Remove can always be undone with Ctrl+Z until the packfile is saved.");
        Section("Work copies");
        var label = new TextBlock { Text = "Folder for work copies (empty = the default under your local application data)", TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
        _view.Children.Add(label);
        _workFolder = new TextBox { Margin = new Thickness(0, 3, 0, 3), MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "Entries opened in another program are copied here; each packfile gets its own sub-folder, deleted when the packfile closes." };
        AutomationProperties.SetName(_workFolder, "Work copy folder");
        _view.Children.Add(_workFolder);
        _workNote = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 0, 0, 4) };
        _workNote.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        _view.Children.Add(_workNote);
        _workFolder.TextChanged += (_, _) => UpdateWorkNote();
    }

    private readonly TextBlock _workNote;

    /// <summary>Says where the copies will go, or why the text is not accepted (then the previous setting is kept).</summary>
    private void UpdateWorkNote()
    {
        string text = _workFolder.Text.Trim();
        _workNote.Text = Work.VppWorkRoot.Validate(text) is { } error
            ? error + " The previous setting is kept."
            : $"Work copies go in {Work.VppWorkRoot.Resolve(text)}. Cairn only ever deletes the folders it created there.";
    }

    public string Title => "Packfiles";
    public FrameworkElement View => _view;

    public void Load()
    {
        _backup.IsChecked = _settings.KeepBackup;
        _confirmRemove.IsChecked = _settings.ConfirmRemove;
        _workFolder.Text = _settings.WorkFolderSetting;
    }

    public void Commit()
    {
        _settings.KeepBackup = _backup.IsChecked == true;
        _settings.ConfirmRemove = _confirmRemove.IsChecked == true;
        if (Work.VppWorkRoot.Validate(_workFolder.Text) is null) _settings.WorkFolderSetting = _workFolder.Text.Trim();
    }

    private void Section(string text)
    {
        var t = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, _view.Children.Count == 0 ? 0 : 14, 0, 6) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
        _view.Children.Add(t);
    }

    private CheckBox Box(string text, string tip)
    {
        var c = new CheckBox { Content = text, ToolTip = tip, Margin = new Thickness(0, 3, 0, 3) };
        AutomationProperties.SetName(c, text);
        AutomationProperties.SetHelpText(c, tip);
        _view.Children.Add(c);
        var note = new TextBlock { Text = tip, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(24, 0, 0, 4) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        _view.Children.Add(note);
        return c;
    }
}
