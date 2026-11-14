using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Cairn.Formats.XForge;
using Cairn.Lvl.Conversion;
using Cairn.Lvl.Ui.Preview;

namespace Cairn.Lvl.Ui.Dialogs;

/// <summary>The Convert window's Red Faction for N-Gage options (<see cref="LvlXffOptions"/>): the scale and the texture copies.</summary>
public sealed partial class LvlConvertWindow
{
    /// <summary>True when a source is a Red Faction for N-Gage scene (its options are shown).</summary>
    private bool AnyXff => _names.Any(LvlSource.IsXffName);

    /// <summary>The N-Gage section: Red Faction metres per N-Gage unit, and whether the textures are copied beside a static mesh or group file.</summary>
    private StackPanel BuildXffOptions()
    {
        var section = new StackPanel();
        section.Children.Add(SectionLabel("Red Faction for N-Gage"));
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var scale = Number("Red Faction metres per N-Gage unit",
            $"How big the level becomes ({XffSpace.MinScale} to {XffSpace.MaxScale}): 2.35 (the default) matches the N-Gage player's size to Red Faction's; the preview uses the value last converted with",
            () => _options.Xff.Scale, v => SetXff(x => x with { Scale = v!.Value }),
            accepts: XffSpace.IsValidScale, rule: string.Format(CultureInfo.CurrentCulture, "the scale must be from {0} to {1}", XffSpace.MinScale, XffSpace.MaxScale));
        var copy = Check("Copy the textures beside a static mesh or group file",
            "Write the textures the level uses beside a .v3m or .rfg, each .pcx as the .tga the output names (Red Faction's editor and levels take .tga); glTF and OBJ get PNG copies instead",
            () => _options.Xff.CopyTextures, v => SetXff(x => x with { CopyTextures = v }));
        Grid.SetColumn(copy, 2);
        copy.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(scale);
        row.Children.Add(copy);
        section.Children.Add(row);
        var defaults = SmallButton("De_fault scale", "Put the scale back to 2.35 Red Faction metres per N-Gage unit", () =>
        {
            _options = _options with { Xff = _options.Xff with { Scale = new LvlXffOptions().Scale } };
            LoadRf2();
            Update();
        });
        defaults.HorizontalAlignment = HorizontalAlignment.Left;
        defaults.Margin = new Thickness(0, 6, 0, 0);
        section.Children.Add(defaults);
        LoadRf2();
        return section;
    }

    private void SetXff(Func<LvlXffOptions, LvlXffOptions> change)
    {
        if (_rf2Loading) return;
        _options = _options with { Xff = change(_options.Xff) };
        Update();
    }
}
