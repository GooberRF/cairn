using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cairn.Formats.Audio;
using Cairn.Ui.Modules;
using Cairn.Ui.Services;

namespace Cairn.Snd.Ui;

/// <summary>Settings &gt; Sounds: what Convert starts with (format, Ogg quality, where the files go, loop points).</summary>
public sealed class SndSettingsPage : ISettingsPage
{
    private readonly SndSettings _settings;
    private readonly Func<IDialogService?> _dialogs;
    private readonly StackPanel _view = new() { Margin = new Thickness(16) };

    internal readonly ComboBox Format = new() { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
    internal readonly RadioButton IntoPackfile = new() { Content = "Into the packfile, as new entries (one undo step)", GroupName = "sndTarget", Margin = new Thickness(0, 2, 0, 2) };
    internal readonly RadioButton NextToSource = new() { Content = "Next to the source file (or its packfile)", GroupName = "sndTarget", Margin = new Thickness(0, 2, 0, 2) };
    internal readonly RadioButton ToFolder = new() { Content = "Into a folder:", GroupName = "sndTarget", Margin = new Thickness(0, 2, 0, 2) };
    internal readonly TextBox Folder = new() { MinWidth = 320 };
    // tenths: -1 (q-1) .. 10 (q10)
    internal readonly Slider Quality = new() { Minimum = -1, Maximum = 10, TickFrequency = 1, IsSnapToTickEnabled = true, SmallChange = 1, LargeChange = 1, Width = 220, TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight, VerticalAlignment = VerticalAlignment.Center };
    internal readonly TextBlock QualityText = new() { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    internal readonly CheckBox WriteLoop = new() { Content = "Keep _loop points (a 'smpl' chunk in a WAV, LOOPSTART/LOOPLENGTH comments in an Ogg)" };

    public SndSettingsPage(SndSettings settings, Func<IDialogService?> dialogs)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dialogs = dialogs;
        Format.Items.Add(new ComboBoxItem { Content = SoundConversion.DisplayName(SoundOutputFormat.Wav), Tag = SoundOutputFormat.Wav });
        Format.Items.Add(new ComboBoxItem { Content = SoundConversion.DisplayName(SoundOutputFormat.Ogg), Tag = SoundOutputFormat.Ogg, IsEnabled = OggVorbisWriter.IsAvailable });
        AutomationProperties.SetName(Format, "Format");
        AutomationProperties.SetName(Quality, "Ogg Vorbis quality");
        AutomationProperties.SetName(Folder, "Folder");
        Quality.ToolTip = "Ogg Vorbis quality: q-1 (smallest) to q10 (best); q5 suits game sounds";
        Quality.ValueChanged += (_, _) => QualityText.Text = SoundConversion.QualityText(SndSettings.Snap(Quality.Value / 10));
        QualityText.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");

        Section("Convert");
        Label("Format");
        _view.Children.Add(Format);
        Note(OggVorbisWriter.IsAvailable
            ? "WAV keeps the decoded sound exactly. Ogg Vorbis files are much smaller and lossy, made with the Xiph.Org encoder; the stock game loads WAV, Alpine Faction also loads Ogg."
            : "WAV keeps the decoded sound exactly. The Ogg Vorbis encoder (cairn-vorbis.dll) is missing from this installation.", 0);
        Label("Ogg Vorbis quality");
        _view.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { Quality, QualityText } });
        Note("Variable bitrate: mono, low-rate and quiet sounds take far less. q5 is the default; lower settings give smaller files that sound less clean.", 0);
        Label("Where the files go");
        _view.Children.Add(IntoPackfile);
        _view.Children.Add(NextToSource);
        var browse = new Button { Content = "_Browse...", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2), ToolTip = "Pick the folder" };
        browse.SetResourceReference(FrameworkElement.StyleProperty, "PushButton");
        browse.Click += (_, _) => { if (_dialogs()?.PickFolder(Folder.Text, "Convert sounds into") is { } picked) { Folder.Text = picked; ToFolder.IsChecked = true; } };
        _view.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { ToFolder, new Border { Width = 8 }, Folder, browse } });
        Note("A sound opened from a packfile (or a batch converted from the packfile's list) goes into the packfile; other sounds fall back to the next choice.", 0);
        Check(WriteLoop, "Looping sounds keep their loop for tools that read it; the game itself loops a sound because a table or level asks it to.");
        Note("Files and entries of the same name are only replaced when Replace is ticked in the Convert window, and Cairn asks first.", 0);
    }

    public string Title => "Sounds";

    public FrameworkElement View => _view;

    public void Load()
    {
        Format.SelectedIndex = _settings.Format == SoundOutputFormat.Ogg && OggVorbisWriter.IsAvailable ? 1 : 0;
        Quality.Value = Math.Round(_settings.Quality * 10);
        QualityText.Text = SoundConversion.QualityText(_settings.Quality);
        (_settings.Target switch { SoundTarget.Folder => ToFolder, SoundTarget.NextToSource => NextToSource, _ => IntoPackfile }).IsChecked = true;
        Folder.Text = _settings.Folder;
        WriteLoop.IsChecked = _settings.WriteLoop;
    }

    public void Commit()
    {
        if (Format.SelectedItem is ComboBoxItem { Tag: SoundOutputFormat format }) _settings.Format = format;
        _settings.Quality = SndSettings.Snap(Quality.Value / 10);
        _settings.Target = ToFolder.IsChecked == true ? SoundTarget.Folder : NextToSource.IsChecked == true ? SoundTarget.NextToSource : SoundTarget.IntoPackfile;
        _settings.Folder = Folder.Text.Trim();
        _settings.WriteLoop = WriteLoop.IsChecked == true;
    }

    private void Section(string text)
    {
        var t = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, _view.Children.Count == 0 ? 0 : 16, 0, 6) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
        _view.Children.Add(t);
    }

    private void Label(string text)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(0, 8, 0, 3) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
        _view.Children.Add(t);
    }

    private void Check(CheckBox box, string note)
    {
        box.Margin = new Thickness(0, 10, 0, 2);
        box.ToolTip = note;
        AutomationProperties.SetHelpText(box, note);
        _view.Children.Add(box);
        Note(note, 24);
    }

    private void Note(string text, double indent)
    {
        var note = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(indent, 2, 0, 6), MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left };
        note.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        _view.Children.Add(note);
    }
}
