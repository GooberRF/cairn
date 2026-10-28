using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cairn.Lvl.Conversion;

namespace Cairn.Lvl.Ui.Dialogs;

/// <summary>
/// The Convert window's Red Faction II options (<see cref="LvlRf2Options"/>, REDUX's defaults): texture names, how
/// RF2's lights and coronas become Red Faction lights, and what happens to RF2-only brushes and objects.
/// </summary>
public sealed partial class LvlConvertWindow
{
    private readonly RadioButton _rf2Translate = new() { GroupName = "lvlRf2Textures", Content = "Translate the names to Red Faction textures" };
    private readonly RadioButton _rf2Keep = new() { GroupName = "lvlRf2Textures", Content = "Keep the Red Faction II names and textures", Margin = new Thickness(0, 2, 0, 0) };
    private readonly Expander _rf2More = new() { Margin = new Thickness(0, 8, 0, 0), IsExpanded = false };
    private readonly List<Action> _rf2Loaders = [];
    private readonly HashSet<TextBox> _invalidNumbers = [];
    private bool _rf2Loading;

    /// <summary>True while a number box holds something that is not a usable number (Convert is off).</summary>
    private bool HasInvalidNumbers => _invalidNumbers.Count > 0;

    partial void BuildRf2Options(StackPanel section, ref bool built)
    {
        built = true;
        section.Children.Add(SectionLabel("Red Faction II"));

        _rf2Translate.ToolTip = "Rename each texture to the Red Faction texture REDUX's tables match it with (names without a match stay)";
        _rf2Keep.ToolTip = "Keep the RF2 names; static meshes and group files get the textures from the level's .peg packs written beside them as .tga";
        AutomationProperties.SetName(_rf2Translate, "Translate the texture names");
        AutomationProperties.SetName(_rf2Keep, "Keep the Red Faction II textures");
        _rf2Translate.Checked += (_, _) => SetRf2(r => r with { Textures = LvlRf2TextureMode.Translate });
        _rf2Keep.Checked += (_, _) => SetRf2(r => r with { Textures = LvlRf2TextureMode.KeepOriginals });
        _rf2Loaders.Add(() => (_options.Rf2.Textures == LvlRf2TextureMode.KeepOriginals ? _rf2Keep : _rf2Translate).IsChecked = true);
        section.Children.Add(_rf2Translate);
        section.Children.Add(_rf2Keep);

        // the light, corona and object choices start folded: REDUX's defaults suit most levels, and the window stays short
        var lights = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        lights.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lights.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        lights.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var first = new StackPanel();
        var second = new StackPanel();
        Grid.SetColumn(second, 2);
        lights.Children.Add(first);
        lights.Children.Add(second);
        var more = new StackPanel();
        more.Children.Add(lights);
        _rf2More.Content = more;
        _rf2More.ToolTip = "How Red Faction II's lights and coronas become Red Faction lights, and what happens to its clutter, jump pads and RF2-only brushes";
        AutomationProperties.SetName(_rf2More, "Red Faction II lights, coronas, clutter and brushes");
        section.Children.Add(_rf2More);

        first.Children.Add(Number("Light intensity ×", "Multiplies every RF2 light's intensity (REDUX's default is 1)",
            () => _options.Rf2.LightScale, v => SetRf2(r => r with { LightScale = v!.Value })));
        first.Children.Add(Number("Light range ×", "Multiplies every RF2 light's range: RF2's range is where light falls to half, Red Faction's where it reaches nothing (REDUX's default is 3)",
            () => _options.Rf2.LightRangeScale, v => SetRf2(r => r with { LightRangeScale = v!.Value })));
        first.Children.Add(Check("Apply the level's lightmap multiplier", "Multiply every light's intensity by the level's lightmap multiplier, as REDUX does",
            () => _options.Rf2.ApplyLightmapMultiplier, v => SetRf2(r => r with { ApplyLightmapMultiplier = v })));
        first.Children.Add(Check("Fill lights where only baked light shows", "Add up to eight lights where the level's baked colours show light that no light or corona explains",
            () => _options.Rf2.FillLights, v => SetRf2(r => r with { FillLights = v })));
        first.Children.Add(Number("Jump pad strength ×", "Multiplies the push of jump pads: Red Faction II's gravity is stronger, so the same jump needs less push in Red Faction (default 0.8083)",
            () => _options.Rf2.JumpPadScale, v => SetRf2(r => r with { JumpPadScale = v!.Value })));

        second.Children.Add(Check("A light for each coloured corona", "RF2 bakes its coronas into the lighting; this adds a Red Faction light for each coloured one, as REDUX does",
            () => _options.Rf2.CoronasToLights, v => SetRf2(r => r with { CoronasToLights = v })));
        second.Children.Add(Number("Corona light range (blank: fitted)", "The range of the lights made from coronas; blank fits it to the level's baked colours",
            () => _options.Rf2.CoronaRange, v => SetRf2(r => r with { CoronaRange = v }), optional: true));
        second.Children.Add(Number("Corona intensity × (blank: fitted)", "The intensity scale of the lights made from coronas; blank fits it to the level's baked colours",
            () => _options.Rf2.CoronaScale, v => SetRf2(r => r with { CoronaScale = v }), optional: true));
        second.Children.Add(Check("Keep the coronas (as Alpine coronas)", "Keep each corona as an Alpine Faction corona, with RF2's halved cone angle doubled",
            () => _options.Rf2.KeepCoronas, v => SetRf2(r => r with { KeepCoronas = v })));
        second.Children.Add(Check("Clutter as Alpine static meshes", "Place each clutter object as an Alpine mesh named after its RF2 mesh (from the install's clutter table); convert those meshes too",
            () => _options.Rf2.ClutterAsMeshes, v => SetRf2(r => r with { ClutterAsMeshes = v })));
        second.Children.Add(Check("Geoable brushes without the detail flag", "Clear the detail flag of geoable detail brushes so Red Faction can geomod them (off by default, as in REDUX)",
            () => _options.Rf2.GeoableWithoutDetail, v => SetRf2(r => r with { GeoableWithoutDetail = v })));
        second.Children.Add(Check("Leave out invisible trigger brushes", "RF2's breakable invisible brushes that only trigger events have no use in Red Faction",
            () => _options.Rf2.DropInvisibleTriggerBrushes, v => SetRf2(r => r with { DropInvisibleTriggerBrushes = v })));

        var defaults = SmallButton("_Defaults", "Put the Red Faction II options back to REDUX's defaults", () =>
        {
            _options = _options with { Rf2 = new LvlRf2Options() };
            LoadRf2();
            Update();
        });
        defaults.HorizontalAlignment = HorizontalAlignment.Left;
        defaults.Margin = new Thickness(0, 6, 0, 0);
        more.Children.Add(defaults);
        LoadRf2();
    }

    /// <summary>Unfolds the light, corona and object choices (screenshots, self-tests).</summary>
    public void ShowRf2Details() => _rf2More.IsExpanded = true;

    /// <summary>Shows <see cref="LvlConvertOptions.Rf2"/> in the Red Faction II controls.</summary>
    private void LoadRf2()
    {
        _rf2Loading = true;
        foreach (var load in _rf2Loaders) load();
        _rf2Loading = false;
        Rf2Header();
    }

    private void SetRf2(Func<LvlRf2Options, LvlRf2Options> change)
    {
        if (_rf2Loading) return;
        _options = _options with { Rf2 = change(_options.Rf2) };
        Rf2Header();
        Update();
    }

    /// <summary>The folded section's header says whether its choices are still REDUX's defaults.</summary>
    private void Rf2Header()
    {
        var r = _options.Rf2 with { TexturePacks = null, ClutterClasses = null };
        bool defaults = r == new LvlRf2Options { Textures = r.Textures };
        _rf2More.Header = "Lights, coronas, clutter and brushes" + (defaults ? " (REDUX's defaults)" : " (changed)");
    }

    private CheckBox Check(string text, string tip, Func<bool> get, Action<bool> set)
    {
        var box = new CheckBox { Content = text, ToolTip = tip, Margin = new Thickness(0, 3, 0, 3) };
        AutomationProperties.SetName(box, text);
        box.Click += (_, _) => set(box.IsChecked == true);
        _rf2Loaders.Add(() => box.IsChecked = get());
        return box;
    }

    /// <summary>A labelled number box; <paramref name="optional"/> allows it to be empty (null). Anything else unusable marks it red.</summary>
    private DockPanel Number(string text, string tip, Func<float?> get, Action<float?> set, bool optional = false)
    {
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, ToolTip = tip, TextTrimming = TextTrimming.CharacterEllipsis };
        label.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
        var box = new TextBox { Width = 72, ToolTip = tip, Margin = new Thickness(8, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(box, text.TrimEnd(' ', '×'));
        void Mark(bool bad)
        {
            if (bad)
            {
                box.SetResourceReference(Control.BorderBrushProperty, "Severity.Error");
                _invalidNumbers.Add(box);
            }
            else
            {
                box.ClearValue(Control.BorderBrushProperty);
                _invalidNumbers.Remove(box);
            }
        }
        box.TextChanged += (_, _) =>
        {
            if (_rf2Loading) return;
            string s = box.Text.Trim();
            if (s.Length == 0 && optional) { Mark(false); set(null); return; }
            bool ok = (float.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out float v) || float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                && float.IsFinite(v) && v > 0 && v < 1e6f;
            Mark(!ok);
            if (ok) set(v);
            else Update();
        };
        _rf2Loaders.Add(() =>
        {
            box.Text = get() is { } value ? value.ToString("0.####", CultureInfo.CurrentCulture) : "";
            Mark(false);
        });
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2), LastChildFill = true };
        DockPanel.SetDock(box, Dock.Right);
        row.Children.Add(box);
        row.Children.Add(label);
        return row;
    }
}
