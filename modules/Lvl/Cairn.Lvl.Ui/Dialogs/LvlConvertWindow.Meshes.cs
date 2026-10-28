using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cairn.Lvl.Conversion;

namespace Cairn.Lvl.Ui.Dialogs;

/// <summary>The Convert window's choices for meshes, glTF and OBJ files made into group files.</summary>
public sealed partial class LvlConvertWindow
{
    /// <summary>The levels of detail offered for meshes (a submesh with fewer uses its last one).</summary>
    private const int MaxOfferedLod = 5;

    private readonly Dictionary<RfgBrushKind, RadioButton> _brushKinds = [];
    private readonly ComboBox _lod = new() { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 0) };

    /// <summary>What meshes, glTF and OBJ files become in the group file (self-tests and screenshots read it).</summary>
    public RfgImportOptions Import => _import;

    /// <summary>True when the window offers the level of detail (self-tests).</summary>
    public bool OffersLod => _lod.Items.Count > 0;

    /// <summary>Picks what meshes without stored brush flags become (self-tests and screenshots).</summary>
    public void Select(RfgBrushKind kind)
    {
        if (_brushKinds.TryGetValue(kind, out var radio)) radio.IsChecked = true;
    }

    /// <summary>
    /// The brush kind and, for .v3m / .v3c, glTF and OBJ sources, the level of detail. Brushes Cairn wrote (its glTF extras and OBJ
    /// comment lines) keep their stored flags whatever is chosen here.
    /// </summary>
    private void BuildMeshSection(StackPanel into)
    {
        into.Children.Add(SectionLabel("Brushes"));
        var kinds = new (RfgBrushKind Kind, string Text, string Tip)[]
        {
            (RfgBrushKind.Auto, "Solid when closed, else detail (recommended)",
                "A closed mesh whose faces point outward becomes a solid brush; anything else a detail brush, which the level build does not cut, so it cannot leak"),
            (RfgBrushKind.Solid, "Always solid brushes",
                "Every mesh becomes a solid brush; an open mesh can leave holes or leaks in the level build (the report warns)"),
            (RfgBrushKind.Detail, "Always detail brushes",
                "Every mesh becomes a detail brush: drawn and collided with, but not cut into the level's rooms"),
        };
        foreach (var (kind, text, tip) in kinds)
        {
            var radio = new RadioButton { GroupName = "lvlBrushKind", Content = text, ToolTip = tip, Margin = new Thickness(0, 2, 0, 2) };
            AutomationProperties.SetName(radio, text);
            radio.IsChecked = _import.BrushKind == kind;
            var picked = kind;
            radio.Checked += (_, _) => { _import = _import with { BrushKind = picked }; Update(); };
            _brushKinds[kind] = radio;
            into.Children.Add(radio);
        }
        var stored = new TextBlock
        {
            Text = "Brushes Cairn wrote to glTF or OBJ keep their own flags.",
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(22, 2, 0, 0),
        };
        stored.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        into.Children.Add(stored);

        // A mesh glTF (and a mesh OBJ written with every LOD) holds each submesh once per level of detail.
        if (!_names.Any(n => Path.GetExtension(n).ToLowerInvariant() is ".v3m" or ".v3c" or ".gltf" or ".glb" or ".obj")) return;
        into.Children.Add(SectionLabel("Level of detail"));
        for (int lod = 0; lod <= MaxOfferedLod; lod++)
            _lod.Items.Add(lod == 0 ? "LOD 0 (the most detailed)" : string.Format(CultureInfo.CurrentCulture, "LOD {0}", lod));
        _lod.SelectedIndex = Math.Clamp(_import.Lod, 0, MaxOfferedLod);
        _lod.ToolTip = "The level of detail each submesh is taken from (meshes, and glTF or OBJ files Cairn wrote from a mesh); a submesh with fewer uses its last one (the report says so)";
        AutomationProperties.SetName(_lod, "Level of detail");
        _lod.SelectionChanged += (_, _) => { _import = _import with { Lod = Math.Max(0, _lod.SelectedIndex) }; Update(); };
        into.Children.Add(_lod);
    }

    private static TextBlock SectionLabel(string text)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(0, 12, 0, 4), FontWeight = FontWeights.SemiBold };
        t.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
        return t;
    }
}
