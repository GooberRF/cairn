using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cairn.Ui.Modules;

namespace Cairn.Vfx.Ui.Settings;

/// <summary>
/// Settings > Effects: the editing defaults otherwise only reachable from the Effect menu and the toolbars
/// (auto-key, local axes, pivot mode) and a reset for the remembered pane widths. Values live in <see cref="VfxGizmoPrefs"/>.
/// </summary>
public sealed class VfxSettingsPage : ISettingsPage
{
    private readonly CheckBox _autoKey, _local, _pivot, _resetPanes;
    private readonly ComboBox _mode;
    private static readonly string[] ModeNames = ["Loop", "One-shot", "Hold last frame"];

    /// <summary>The preview playback mode new and opened effects start in (setting "vfx.playbackMode"; Loop when unset).</summary>
    internal static Cairn.Vfx.Animation.VfxPlaybackMode DefaultMode =>
        (Cairn.Vfx.Animation.VfxPlaybackMode)Math.Clamp(VfxGizmoPrefs.Store?.Get("playbackMode", 0) ?? 0, 0, 2);
    private readonly StackPanel _view = new() { Margin = new Thickness(16) };

    public VfxSettingsPage()
    {
        Section("Editing");
        _autoKey = Box("Auto-key", "Moving, rotating or scaling a keyframed mesh adds or updates a key at the playhead. Also on the Effect menu, the viewport bar and the timeline toolbar.");
        _local = Box("Local axes", "Gizmos follow the selected object's own axes instead of the world axes.");
        _pivot = Box("Pivot mode", "Gizmo drags on keyframed meshes move or rotate the pivot and adjust the keys so the mesh stays in place.");
        Section("Playback");
        _mode = new ComboBox { ItemsSource = ModeNames, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 180, Margin = new Thickness(0, 3, 0, 3),
            ToolTip = "How newly opened or created effects play in the preview. Each effect can still be switched on its transport bar." };
        AutomationProperties.SetName(_mode, "Default playback mode");
        var label = new TextBlock { Text = "Default playback mode for opened effects" };
        label.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
        _view.Children.Add(label); _view.Children.Add(_mode);
        Section("Layout");
        _resetPanes = Box("Reset the object list and inspector widths", "Open effects again with the default pane widths (the widths are otherwise remembered when you drag the splitters).");
    }

    public string Title => "Effects";
    public FrameworkElement View => _view;

    public void Load()
    {
        _autoKey.IsChecked = VfxGizmoPrefs.AutoKey;
        _local.IsChecked = VfxGizmoPrefs.LocalSpace;
        _pivot.IsChecked = VfxGizmoPrefs.PivotMode;
        _resetPanes.IsChecked = false;
        _mode.SelectedIndex = (int)DefaultMode;
    }

    public void Commit()
    {
        VfxGizmoPrefs.AutoKey = _autoKey.IsChecked == true;
        VfxGizmoPrefs.LocalSpace = _local.IsChecked == true;
        VfxGizmoPrefs.PivotMode = _pivot.IsChecked == true;
        if (_mode.SelectedIndex >= 0) VfxGizmoPrefs.Store?.Set("playbackMode", _mode.SelectedIndex);
        if (_resetPanes.IsChecked == true && VfxGizmoPrefs.Store is { } store)
        {
            store.Set("outlinerWidth", 0.0);
            store.Set("inspectorWidth", 0.0);
        }
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
