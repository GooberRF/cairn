using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cairn.Formats.Imaging;
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
    /// <summary>Decode the PS2 MPEG-2 compressed backgrounds of PEG texture packs (previews, conversion, PNG export).</summary>
    public bool DecodeMpeg2 { get => store()?.Get("decodeMpeg2", true) ?? true; set => store()?.Set("decodeMpeg2", value); }
    /// <summary>Treat black as transparent in decoded MPEG-2 backgrounds (32-bit .tga with alpha); off = opaque 24-bit.</summary>
    public bool Mpeg2BlackTransparent { get => store()?.Get("mpeg2BlackTransparent", false) ?? false; set => store()?.Set("mpeg2BlackTransparent", value); }
    /// <summary>Pixels whose red, green and blue are all below this become transparent (0 to <see cref="PegBlackKey.MaxThreshold"/>).</summary>
    public int Mpeg2BlackThreshold
    {
        get => Math.Clamp(store()?.Get("mpeg2BlackThreshold", PegBlackKey.GameThreshold) ?? PegBlackKey.GameThreshold, 0, PegBlackKey.MaxThreshold);
        set => store()?.Set("mpeg2BlackThreshold", Math.Clamp(value, 0, PegBlackKey.MaxThreshold));
    }
    /// <summary>Play a sound as soon as it is selected in the entry list (the toolbar's autoplay toggle).</summary>
    public bool AutoPlaySounds { get => store()?.Get("autoPlaySounds", false) ?? false; set => store()?.Set("autoPlaySounds", value); }
    /// <summary>A half-transparent band up to twice the threshold.</summary>
    public bool Mpeg2SoftEdge { get => store()?.Get("mpeg2SoftEdge", false) ?? false; set => store()?.Set("mpeg2SoftEdge", value); }
    /// <summary>The key MPEG-2 backgrounds are decoded with (previews, conversion, PNG export), or null when black stays black.</summary>
    public PegBlackKey? Mpeg2BlackKey => Mpeg2BlackTransparent ? new PegBlackKey(Mpeg2BlackThreshold, Mpeg2SoftEdge) : null;

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
    private readonly CheckBox _backup, _confirmRemove, _decodeMpeg2;
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
        Section("PlayStation 2");
        _decodeMpeg2 = Box("Decode PS2 MPEG-2 backgrounds",
            "PEG texture packs store their full-screen backgrounds as MPEG-2 pictures. When on, they preview and convert to .tga like the other textures (24-bit, no alpha, unless black is treated as transparent); when off, they are listed and left out.");
        var black = new StackPanel { Margin = new Thickness(24, 0, 0, 0) };
        _view.Children.Add(black);
        _blackTransparent = Box("Treat black as transparent",
            "The PS2 could make the black of a background transparent when it decoded it (for overlays such as UI strips and portraits). When on, every pixel whose red, green and blue are all below the threshold becomes transparent, and decoded backgrounds become 32-bit .tga with alpha. Applies to previews, conversion and Extract as PNG; the convert dialog can change it per conversion.", black);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 2, 0, 2) };
        var thresholdLabel = new TextBlock { Text = "Threshold", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        thresholdLabel.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
        _threshold = new Slider { Minimum = 0, Maximum = PegBlackKey.MaxThreshold, Width = 200, IsSnapToTickEnabled = true, TickFrequency = 1, SmallChange = 1, LargeChange = 8, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = $"0 to {PegBlackKey.MaxThreshold}: a pixel becomes transparent when its red, green and blue are all below this. {PegBlackKey.GameThreshold} is the value the PS2 game uses." };
        AutomationProperties.SetName(_threshold, "Black threshold");
        _thresholdText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), MinWidth = 150 };
        _thresholdText.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
        _threshold.ValueChanged += (_, _) => _thresholdText.Text = ThresholdText((int)_threshold.Value);
        row.Children.Add(thresholdLabel);
        row.Children.Add(_threshold);
        row.Children.Add(_thresholdText);
        black.Children.Add(row);
        var soft = new StackPanel { Margin = new Thickness(24, 0, 0, 0) };
        black.Children.Add(soft);
        _softEdge = Box("Soft edge", "Pixels just above the threshold (up to twice it) become half transparent, as the PS2's second threshold would, so edges blend instead of breaking off.", soft);
        _decodeMpeg2.Click += (_, _) => UpdateBlack();
        _blackTransparent.Click += (_, _) => UpdateBlack();
        _blackPanel = black;
        _thresholdRow = row;
        _softPanel = soft;
    }

    private readonly StackPanel _thresholdRow, _softPanel;

    private readonly CheckBox _blackTransparent, _softEdge;
    private readonly Slider _threshold;
    private readonly TextBlock _thresholdText;
    private readonly StackPanel _blackPanel;

    /// <summary>"25 (as the PS2 game)".</summary>
    internal static string ThresholdText(int value) => value == PegBlackKey.GameThreshold ? $"{value} (the PS2 game's value)" : value == 0 ? "0 (nothing is transparent)" : value.ToString(System.Globalization.CultureInfo.CurrentCulture);

    private void UpdateBlack()
    {
        _blackPanel.IsEnabled = _decodeMpeg2.IsChecked == true;
        _blackPanel.Opacity = _blackPanel.IsEnabled ? 1 : 0.6;
        bool on = _blackTransparent.IsChecked == true;
        _thresholdRow.IsEnabled = _softPanel.IsEnabled = on;
        // the slider and the themed texts keep their colours when disabled: dim them
        _thresholdRow.Opacity = _softPanel.Opacity = on ? 1 : 0.5;
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
        _decodeMpeg2.IsChecked = _settings.DecodeMpeg2;
        _blackTransparent.IsChecked = _settings.Mpeg2BlackTransparent;
        _threshold.Value = _settings.Mpeg2BlackThreshold;
        _thresholdText.Text = ThresholdText(_settings.Mpeg2BlackThreshold);
        _softEdge.IsChecked = _settings.Mpeg2SoftEdge;
        UpdateBlack();
    }

    public void Commit()
    {
        _settings.KeepBackup = _backup.IsChecked == true;
        _settings.ConfirmRemove = _confirmRemove.IsChecked == true;
        _settings.DecodeMpeg2 = _decodeMpeg2.IsChecked == true;
        _settings.Mpeg2BlackTransparent = _blackTransparent.IsChecked == true;
        _settings.Mpeg2BlackThreshold = (int)_threshold.Value;
        _settings.Mpeg2SoftEdge = _softEdge.IsChecked == true;
        if (Work.VppWorkRoot.Validate(_workFolder.Text) is null) _settings.WorkFolderSetting = _workFolder.Text.Trim();
    }

    private void Section(string text)
    {
        var t = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, _view.Children.Count == 0 ? 0 : 14, 0, 6) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
        _view.Children.Add(t);
    }

    private CheckBox Box(string text, string tip, Panel? into = null)
    {
        into ??= _view;
        var c = new CheckBox { Content = text, ToolTip = tip, Margin = new Thickness(0, 3, 0, 3) };
        AutomationProperties.SetName(c, text);
        AutomationProperties.SetHelpText(c, tip);
        into.Children.Add(c);
        var note = new TextBlock { Text = tip, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(24, 0, 0, 4) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        into.Children.Add(note);
        return c;
    }
}
