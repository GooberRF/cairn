using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cairn.Formats.Audio;
using Cairn.Ui.Services;

namespace Cairn.Snd.Ui.Dialogs;

/// <summary>What the Convert window settled on.</summary>
/// <param name="Target">Where the files go.</param>
/// <param name="Folder">The folder for <see cref="SoundTarget.Folder"/> and <see cref="SoundTarget.NextToSource"/>.</param>
/// <param name="Options">Format, Ogg quality and loop points.</param>
/// <param name="Replace">True to replace files or entries of the same name.</param>
public sealed record SndConvertChoice(SoundTarget Target, string? Folder, SoundConvertOptions Options, bool Replace);

/// <summary>
/// "Convert...": the format (WAV or Ogg Vorbis, with its quality), keeping loops, where the files go (into the packfile,
/// next to the source, or a folder), what happens to taken names, the names that result, and what the conversion
/// approximates.
/// </summary>
public sealed class SndConvertWindow : Window
{
    private readonly IDialogService _dialogs;
    private readonly IReadOnlyList<string> _names;
    private readonly string? _packfile, _nextTo;
    private readonly Func<SoundConvertOptions, IReadOnlyList<string>>? _notes;
    private readonly RadioButton _wav = new() { Content = SoundConversion.DisplayName(SoundOutputFormat.Wav), GroupName = "sndFormat", IsChecked = true };
    private readonly RadioButton _ogg = new() { Content = SoundConversion.DisplayName(SoundOutputFormat.Ogg), GroupName = "sndFormat", Margin = new Thickness(0, 4, 0, 0) };
    // the slider counts tenths: -1 (q-1) .. 10 (q10)
    private readonly Slider _quality = new() { Minimum = -1, Maximum = 10, TickFrequency = 1, IsSnapToTickEnabled = true, SmallChange = 1, LargeChange = 1, Width = 220, TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _qualityText = new() { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _qualityRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(22, 4, 0, 0) };
    private readonly StackPanel _notesPanel = new();
    private readonly CheckBox _loop = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly RadioButton _intoPackfile = new() { GroupName = "sndTarget", Margin = new Thickness(0, 2, 0, 2) };
    private readonly RadioButton _nextToSource = new() { GroupName = "sndTarget", Margin = new Thickness(0, 2, 0, 2) };
    private readonly RadioButton _toFolder = new() { Content = "Folder:", GroupName = "sndTarget", Margin = new Thickness(0, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _folder = new() { MinWidth = 300 };
    private readonly CheckBox _replace = new() { Content = "_Replace files or entries of the same name (else a free name is chosen)", Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _plan = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _convert = new() { Content = "_Convert", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Convert with these choices" };

    /// <param name="dialogs">For the folder picker.</param>
    /// <param name="names">The sounds' names.</param>
    /// <param name="packfile">The packfile the sounds are entries of, or null.</param>
    /// <param name="nextTo">The source's folder (or its packfile's), or null when it has none.</param>
    /// <param name="settings">The defaults.</param>
    /// <param name="notes">What converting the (single) sound with given options approximates, or null for a batch.</param>
    /// <param name="sameFormat">Why the (single) sound gains nothing from a format (it is in that format already), or null when it does.</param>
    /// <param name="noNextToReason">Why "next to the source" is off when <paramref name="nextTo"/> is null ("it has no folder").</param>
    public SndConvertWindow(IDialogService dialogs, IReadOnlyList<string> names, string? packfile, string? nextTo, SndSettings settings, Func<SoundConvertOptions, IReadOnlyList<string>>? notes,
        Func<SoundOutputFormat, string?>? sameFormat = null, string? noNextToReason = null)
    {
        _dialogs = dialogs;
        _names = names;
        _packfile = packfile;
        _nextTo = nextTo;
        _notes = notes;
        Title = names.Count == 1 ? "Convert " + names[0] : string.Format(CultureInfo.CurrentCulture, "Convert {0:N0} sounds", names.Count);
        Width = 600; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "App.WindowBackground");
        SetResourceReference(ForegroundProperty, "App.Text");

        // plain text blocks: names and folders may hold underscores (no access keys there)
        _intoPackfile.Content = new TextBlock { Text = packfile is null ? "Into the packfile (only for sounds opened from a packfile)" : $"Into {packfile} as new entries (one undo step)" };
        _intoPackfile.IsEnabled = packfile is not null;
        _nextToSource.Content = new TextBlock { Text = nextTo is null ? $"Next to the source ({noNextToReason ?? "it has no folder"})" : $"Next to the source ({nextTo})", TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 540 };
        _nextToSource.IsEnabled = nextTo is not null;
        _folder.Text = settings.Folder.Length > 0 ? settings.Folder : nextTo ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        _loop.IsChecked = settings.WriteLoop;
        // never carried over from an earlier conversion: replacing is chosen here, and confirmed when a name is taken
        _replace.IsChecked = false;
        _quality.Value = Math.Round(settings.Quality * 10);
        bool oggAvailable = OggVorbisWriter.IsAvailable;
        string? wavSame = sameFormat?.Invoke(SoundOutputFormat.Wav), oggSame = sameFormat?.Invoke(SoundOutputFormat.Ogg);
        _wav.IsEnabled = wavSame is null;
        _ogg.IsEnabled = oggAvailable && oggSame is null;
        bool preferOgg = settings.Format == SoundOutputFormat.Ogg || !_wav.IsEnabled;
        (preferOgg && _ogg.IsEnabled ? _ogg : _wav.IsEnabled ? _wav : _ogg).IsChecked = true;
        var target = settings.Target;
        if (target == SoundTarget.IntoPackfile && packfile is null) target = nextTo is null ? SoundTarget.Folder : SoundTarget.NextToSource;
        if (target == SoundTarget.NextToSource && nextTo is null) target = SoundTarget.Folder;
        (target switch { SoundTarget.IntoPackfile => _intoPackfile, SoundTarget.NextToSource => _nextToSource, _ => _toFolder }).IsChecked = true;
        _wav.ToolTip = wavSame is not null ? $"Not offered: {wavSame}" : "16-bit PCM WAVE: the decoded sound exactly, which the game loads";
        _ogg.ToolTip = oggSame is not null ? $"Not offered: {oggSame}"
            : oggAvailable
            ? "Ogg Vorbis (the Xiph.Org encoder): much smaller files, close to the original but not identical"
            : "The Ogg Vorbis encoder (cairn-vorbis.dll) is missing from this installation";
        if (wavSame is not null) _wav.Content = $"{SoundConversion.DisplayName(SoundOutputFormat.Wav)}: not offered, {wavSame}";
        if (oggSame is not null) _ogg.Content = $"{SoundConversion.DisplayName(SoundOutputFormat.Ogg)}: not offered, {oggSame}";
        _replace.Content = notes is null
            ? "_Replace files or entries of the same name (Cairn asks first; else those sounds are skipped)"
            : "_Replace a file or entry of the same name (Cairn asks first; else a free name is chosen)";
        _replace.ToolTip = notes is null
            ? "Off: a sound whose converted name is taken is left out and listed in the report. On: Cairn asks before replacing anything."
            : "Off: a converted file whose name is taken gets a free name. On: Cairn asks before replacing anything.";
        ToolTipService.SetShowOnDisabled(_ogg, true);
        ToolTipService.SetShowOnDisabled(_wav, true);
        _quality.ToolTip = "Ogg Vorbis quality: q-1 (smallest) to q10 (best); q5 suits game sounds";
        AutomationProperties.SetName(_quality, "Ogg Vorbis quality");
        AutomationProperties.SetName(_folder, "Folder");
        AutomationProperties.SetName(_intoPackfile, "Into the packfile");
        AutomationProperties.SetName(_nextToSource, "Next to the source");
        AutomationProperties.SetName(_toFolder, "Into a folder");
        Content = Build(notes is null);
        _folder.TextChanged += (_, _) => { _toFolder.IsChecked = true; Update(); };
        foreach (var radio in new[] { _intoPackfile, _nextToSource, _toFolder, _wav, _ogg }) radio.Checked += (_, _) => Update();
        _replace.Click += (_, _) => Update();
        _loop.Click += (_, _) => Update();
        _quality.ValueChanged += (_, _) => Update();
        Update();
    }

    /// <summary>The choice made with Convert, or null.</summary>
    public SndConvertChoice? Result { get; private set; }

    /// <summary>The format chosen now.</summary>
    public SoundOutputFormat Format => _ogg.IsChecked == true ? SoundOutputFormat.Ogg : SoundOutputFormat.Wav;

    /// <summary>The Ogg Vorbis quality chosen now (-0.1 to 1.0).</summary>
    public float Quality => SndSettings.Snap(_quality.Value / 10);

    /// <summary>Picks a format and quality (self-tests and screenshots).</summary>
    public void Select(SoundOutputFormat format, float? quality = null)
    {
        if (quality is { } q) _quality.Value = Math.Round(SndSettings.Snap(q) * 10);
        (format == SoundOutputFormat.Ogg && _ogg.IsEnabled ? _ogg : _wav).IsChecked = true;
    }

    /// <summary>The choice the window describes now (also for tests), or null when the folder is not usable.</summary>
    public SndConvertChoice? Current()
    {
        var options = new SoundConvertOptions { Format = Format, WriteLoop = _loop.IsChecked == true, Quality = Quality };
        bool replace = _replace.IsChecked == true;
        if (_intoPackfile.IsChecked == true && _packfile is not null) return new SndConvertChoice(SoundTarget.IntoPackfile, null, options, replace);
        if (_nextToSource.IsChecked == true && _nextTo is not null) return new SndConvertChoice(SoundTarget.NextToSource, _nextTo, options, replace);
        string folder = _folder.Text.Trim();
        if (folder.Length == 0) return null;
        try { folder = Path.GetFullPath(folder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        return new SndConvertChoice(SoundTarget.Folder, folder, options, replace);
    }

    private UIElement Build(bool batch)
    {
        var panel = new StackPanel { Margin = new Thickness(14) };
        void Label(string text)
        {
            var t = new TextBlock { Text = text, Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : 12, 0, 4), FontWeight = FontWeights.SemiBold };
            t.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
            panel.Children.Add(t);
        }
        TextBlock Hint(string text)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 2, 0, 2) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
            return t;
        }
        Label("Format");
        panel.Children.Add(_wav);
        panel.Children.Add(_ogg);
        var qualityLabel = new TextBlock { Text = "Quality", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        qualityLabel.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
        _qualityText.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        _qualityRow.Children.Add(qualityLabel);
        _qualityRow.Children.Add(_quality);
        _qualityRow.Children.Add(_qualityText);
        panel.Children.Add(_qualityRow);
        if (!_ogg.IsEnabled) panel.Children.Add(Hint("The Ogg Vorbis encoder (cairn-vorbis.dll) is missing from this installation; reinstall Cairn to convert to Ogg."));
        panel.Children.Add(_loop);
        Label("Where");
        panel.Children.Add(_intoPackfile);
        panel.Children.Add(_nextToSource);
        var browse = new Button { Content = "_Browse...", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2), ToolTip = "Pick the folder" };
        browse.SetResourceReference(StyleProperty, "PushButton");
        browse.Click += (_, _) => { if (_dialogs.PickFolder(_folder.Text, "Convert sounds into") is { } picked) _folder.Text = picked; };
        var folderRow = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        DockPanel.SetDock(_toFolder, Dock.Left);
        DockPanel.SetDock(browse, Dock.Right);
        folderRow.Children.Add(_toFolder);
        folderRow.Children.Add(browse);
        folderRow.Children.Add(_folder);
        panel.Children.Add(folderRow);
        panel.Children.Add(_replace);
        _plan.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        panel.Children.Add(_plan);
        if (batch) panel.Children.Add(Hint("What each conversion approximates (lossy sources, loop points, PS2 pitch) is listed when it is done."));
        else panel.Children.Add(_notesPanel);

        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, ToolTip = "Close without converting" };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        foreach (var b in new[] { _convert, cancel }) { b.SetResourceReference(StyleProperty, "DialogButton"); buttons.Children.Add(b); }
        _convert.Click += (_, _) => { Result = Current(); if (Result is not null) DialogResult = true; };
        panel.Children.Add(buttons);
        return panel;
    }

    private void Update()
    {
        bool ogg = Format == SoundOutputFormat.Ogg;
        _qualityRow.IsEnabled = ogg;
        _qualityRow.Opacity = ogg ? 1 : 0.55;
        _qualityText.Text = SoundConversion.QualityText(Quality);
        _loop.Content = ogg ? "Keep _loop points (LOOPSTART/LOOPLENGTH comments)" : "Keep _loop points (a 'smpl' chunk)";
        _loop.ToolTip = ogg
            ? "Players and tools that read LOOPSTART/LOOPLENGTH comments loop the sound; others play it once"
            : "Players and tools that read the 'smpl' chunk loop the sound; others play it once";
        UpdateNotes();
        var choice = Current();
        if (!_wav.IsEnabled && !_ogg.IsEnabled) { _convert.IsEnabled = false; _plan.Text = "Nothing to convert to: the sound is in the only format available already."; return; }
        _convert.IsEnabled = choice is not null;
        if (choice is null) { _plan.Text = "Choose a folder."; return; }
        var outputs = _names.Select(n => SoundConversion.OutputName(n, choice.Options.Format)).ToList();
        string where = choice.Target == SoundTarget.IntoPackfile ? "into " + _packfile : "to " + choice.Folder;
        _plan.Text = outputs.Count == 1
            ? $"Writes {outputs[0]} {where}."
            : string.Format(CultureInfo.CurrentCulture, "Writes {0:N0} files {1}: {2} ... {3}.", outputs.Count, where, outputs[0], outputs[^1]);
    }

    /// <summary>The single sound's notes for the options chosen now.</summary>
    private void UpdateNotes()
    {
        if (_notes is null) return;
        _notesPanel.Children.Clear();
        var notes = _notes(new SoundConvertOptions { Format = Format, WriteLoop = _loop.IsChecked == true, Quality = Quality });
        if (notes.Count == 0) return;
        var label = new TextBlock { Text = "What the conversion changes", Margin = new Thickness(0, 12, 0, 4), FontWeight = FontWeights.SemiBold };
        label.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
        _notesPanel.Children.Add(label);
        foreach (var note in notes)
        {
            var t = new TextBlock { Text = "• " + note, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 2, 0, 2) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
            _notesPanel.Children.Add(t);
        }
    }
}
