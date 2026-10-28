using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cairn.Ui.Modules;
using Cairn.Ui.Services;

namespace Cairn.Lvl.Ui;

/// <summary>Settings &gt; Levels &amp; groups: the 3D preview's size limit and lighting, and where Convert writes.</summary>
public sealed class LvlSettingsPage : ISettingsPage
{
    private readonly LvlSettings _settings;
    private readonly Func<IDialogService?> _dialogs;
    private readonly StackPanel _view = new() { Margin = new Thickness(16) };

    internal readonly TextBox TriangleLimit = new() { Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
    internal readonly ComboBox Lighting = new() { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
    internal readonly CheckBox BothSides = new() { Content = "Draw _both sides of each face" };
    internal readonly RadioButton IntoPackfile = new() { Content = "Into the packfile, as new entries (one undo step)", GroupName = "lvlPlace", Margin = new Thickness(0, 2, 0, 2) };
    internal readonly RadioButton NextToSource = new() { Content = "Next to the source file (or its packfile)", GroupName = "lvlPlace", Margin = new Thickness(0, 2, 0, 2) };
    internal readonly RadioButton ToFolder = new() { Content = "Into a folder:", GroupName = "lvlPlace", Margin = new Thickness(0, 2, 0, 2) };
    internal readonly TextBox Folder = new() { MinWidth = 320 };

    public LvlSettingsPage(LvlSettings settings, Func<IDialogService?> dialogs)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dialogs = dialogs;
        Lighting.Items.Add(new ComboBoxItem { Content = "Full bright", Tag = LvlLighting.FullBright });
        Lighting.Items.Add(new ComboBoxItem { Content = "Level ambient light", Tag = LvlLighting.LevelAmbient });
        Lighting.Items.Add(new ComboBoxItem { Content = "Shaded", Tag = LvlLighting.Shaded });
        AutomationProperties.SetName(TriangleLimit, "Triangle limit");
        AutomationProperties.SetName(Lighting, "Lighting");
        AutomationProperties.SetName(Folder, "Folder");
        TriangleLimit.ToolTip = "Levels with more triangles ask before their preview is built";

        Section("3D preview");
        Label("Ask before building a preview of more than this many triangles");
        _view.Children.Add(TriangleLimit);
        Note(string.Format(CultureInfo.CurrentCulture, "{0:N0} at first. A large level takes longer to build and uses more memory; stock levels stay well below it.", LvlSettings.DefaultTriangleLimit), 0);
        Label("Lighting");
        _view.Children.Add(Lighting);
        Note("Full bright draws each texture at its own colours, level ambient light at the level's ambient colour, and shaded adds a light from the camera's side. Lightmaps are not drawn.", 0);
        BothSides.Margin = new Thickness(0, 10, 0, 2);
        _view.Children.Add(BothSides);
        Note("The game draws the front of each face only, so a room seen from outside shows its inside.", 24);

        Section("Convert");
        Label("Where the files go");
        _view.Children.Add(IntoPackfile);
        _view.Children.Add(NextToSource);
        var browse = new Button { Content = "_Browse...", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2), ToolTip = "Pick the folder" };
        browse.SetResourceReference(FrameworkElement.StyleProperty, "PushButton");
        browse.Click += (_, _) => { if (_dialogs()?.PickFolder(Folder.Text, "Convert levels into") is { } picked) { Folder.Text = picked; ToFolder.IsChecked = true; } };
        _view.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { ToFolder, new Border { Width = 8 }, Folder, browse } });
        Note("A level opened from a packfile (or a batch converted from the packfile's list) goes into the packfile; other levels fall back to the next choice. The Convert window remembers the other choices.", 0);
    }

    public string Title => "Levels & groups";

    public FrameworkElement View => _view;

    public void Load()
    {
        TriangleLimit.Text = _settings.TriangleLimit.ToString(CultureInfo.CurrentCulture);
        Lighting.SelectedIndex = (int)_settings.Lighting;
        BothSides.IsChecked = _settings.BothSides;
        (_settings.Place switch { LvlOutputPlace.Folder => ToFolder, LvlOutputPlace.NextToSource => NextToSource, _ => IntoPackfile }).IsChecked = true;
        Folder.Text = _settings.Folder;
    }

    public void Commit()
    {
        if (int.TryParse(TriangleLimit.Text.Trim(), NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out int limit) && limit > 0)
            _settings.TriangleLimit = limit;
        if (Lighting.SelectedItem is ComboBoxItem { Tag: LvlLighting lighting }) _settings.Lighting = lighting;
        _settings.BothSides = BothSides.IsChecked == true;
        _settings.Place = ToFolder.IsChecked == true ? LvlOutputPlace.Folder : NextToSource.IsChecked == true ? LvlOutputPlace.NextToSource : LvlOutputPlace.IntoPackfile;
        _settings.Folder = Folder.Text.Trim();
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

    private void Note(string text, double indent)
    {
        var note = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(indent, 2, 0, 6), MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left };
        note.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        _view.Children.Add(note);
    }
}
