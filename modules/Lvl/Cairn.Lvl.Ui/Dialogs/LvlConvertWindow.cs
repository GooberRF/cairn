using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Cairn.Lvl.Conversion;
using Cairn.Lvl.Model;
using Cairn.Ui.Services;

namespace Cairn.Lvl.Ui.Dialogs;

/// <summary>What the Convert window settled on.</summary>
/// <param name="Options">Target and conversion choices (the texture resolver is added by the converter).</param>
/// <param name="Place">Where the files go.</param>
/// <param name="Folder">The folder for <see cref="LvlOutputPlace.Folder"/> and <see cref="LvlOutputPlace.NextToSource"/>.</param>
/// <param name="Replace">True to replace files or entries of the same name (Cairn still asks first).</param>
public sealed record LvlConvertChoice(LvlConvertOptions Options, LvlOutputPlace Place, string? Folder, bool Replace)
{
    /// <summary>What meshes, glTF and OBJ files become in a group file (brush kind, level of detail, objects); levels ignore it.</summary>
    public RfgImportOptions Import { get; init; } = new();
}

/// <summary>
/// "Convert…" for levels and group files: the target (glTF, glTF binary, OBJ, static mesh, and group file when this
/// version writes them), the geometry (compiled or brushes), the face classes and objects to carry, each target's own
/// choices, Red Faction II options, and where the files go. The report is shown after the conversion.
/// </summary>
public sealed partial class LvlConvertWindow : Window
{
    private readonly IDialogService _dialogs;
    private readonly IReadOnlyList<string> _names;
    private readonly string? _packfile, _nextTo;
    private readonly bool _anyLevel, _rfgAvailable, _meshes, _anyGltf;
    private RfgImportOptions _import;
    private readonly Dictionary<LvlConvertTarget, RadioButton> _targets = [];
    private readonly RadioButton _compiled = new() { GroupName = "lvlGeometry", Content = "Compiled geometry (what the game draws)" };
    private readonly RadioButton _brushes = new() { GroupName = "lvlGeometry", Content = "Brushes (what the level editor works on)", Margin = new Thickness(0, 2, 0, 0) };
    private readonly CheckBox _movers = new() { Content = "Include the _movers", Margin = new Thickness(22, 4, 0, 0) };
    private readonly Dictionary<RfFaceClass, CheckBox> _faces = [];
    private readonly Dictionary<LvlObjectKinds, CheckBox> _objects = [];
    private readonly CheckBox _textures = new() { Content = "Write the _textures as PNG files", Margin = new Thickness(0, 2, 0, 2) };
    private readonly RadioButton _nodeTransforms = new() { GroupName = "lvlTransforms", Content = "Keep each brush's position and rotation on its node" };
    private readonly RadioButton _applyTransforms = new() { GroupName = "lvlTransforms", Content = "Move the vertices into world space", Margin = new Thickness(0, 2, 0, 0) };
    private readonly CheckBox _lightmapUvs = new() { Content = "Write the lightmap coordinates (TEXCOORD_1)", Margin = new Thickness(0, 4, 0, 0) };
    private readonly CheckBox _triangulate = new() { Content = "Write triangles instead of the faces' polygons" };
    private readonly CheckBox _metadata = new() { Content = "Write Cairn's comment lines (face and brush flags Cairn reads back)", Margin = new Thickness(0, 4, 0, 0) };
    private readonly RadioButton _perBrush = new() { GroupName = "lvlV3m", Content = "One submesh per brush, room or mover" };
    private readonly RadioButton _whole = new() { GroupName = "lvlV3m", Content = "One submesh for the whole level", Margin = new Thickness(0, 2, 0, 0) };
    private readonly RadioButton _intoPackfile = new() { GroupName = "lvlPlace", Margin = new Thickness(0, 2, 0, 2) };
    private readonly RadioButton _nextToSource = new() { GroupName = "lvlPlace", Margin = new Thickness(0, 2, 0, 2) };
    private readonly RadioButton _toFolder = new() { Content = "Folder:", GroupName = "lvlPlace", Margin = new Thickness(0, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _folder = new() { MinWidth = 300 };
    private readonly CheckBox _replace = new() { Content = "_Replace files or entries of the same name (Cairn asks first)", Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _plan = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _convert = new() { Content = "_Convert", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Convert with these choices" };
    private readonly StackPanel _geometrySection = new(), _objectsSection = new(), _gltfSection = new(), _objSection = new(), _v3mSection = new(), _texturesSection = new(), _rf2Section = new();
    private LvlConvertOptions _options;
    private bool _updating;

    /// <param name="dialogs">For the folder picker.</param>
    /// <param name="names">
    /// The sources' names: levels and group files (.rfl, .rfg), or meshes, glTF and OBJ files (.v3m, .v3c, .gltf, .glb,
    /// .obj), which only convert to group files.
    /// </param>
    /// <param name="packfile">The packfile the sources are entries of (and that can take the outputs), or null.</param>
    /// <param name="nextTo">The source's folder (or its packfile's), or null when it has none or it is the game directory.</param>
    /// <param name="settings">The defaults.</param>
    /// <param name="start">The target to start at (File › Convert to…), or null for the last one used.</param>
    /// <param name="rfgAvailable">True when this version writes group files.</param>
    /// <param name="anyRf2">True when a source is a Red Faction II level (its options are shown).</param>
    /// <param name="noNextToReason">Why "next to the source" is off when <paramref name="nextTo"/> is null.</param>
    public LvlConvertWindow(IDialogService dialogs, IReadOnlyList<string> names, string? packfile, string? nextTo, LvlSettings settings,
        LvlConvertTarget? start, bool rfgAvailable, bool anyRf2 = false, string? noNextToReason = null)
    {
        _dialogs = dialogs;
        _names = names;
        _packfile = packfile;
        _nextTo = nextTo;
        _meshes = names.Count > 0 && names.All(LvlModule.IsMeshSource);
        _anyGltf = _meshes && names.Any(LvlModule.IsGltfName);
        _anyLevel = !_meshes && names.Any(n => !Path.GetExtension(n).Equals(".rfg", StringComparison.OrdinalIgnoreCase));
        _rfgAvailable = rfgAvailable && (_anyLevel || _meshes);
        _import = settings.RfgImport;
        _options = settings.Options with { Target = start ?? settings.Target };
        // meshes, glTF and OBJ files make group files only; their objects are the glTF's (Cairn's nodes and lights)
        if (_meshes) _options = _options with { Target = LvlConvertTarget.Rfg, Objects = _import.Objects };
        if (_options.Target == LvlConvertTarget.Rfg && !_rfgAvailable) _options = _options with { Target = LvlConvertTarget.Glb };
        Title = names.Count == 1 ? "Convert " + names[0] + (_meshes ? " to a group file" : "")
            : string.Format(CultureInfo.CurrentCulture, _meshes ? "Convert {0:N0} files to group files" : "Convert {0:N0} levels and group files", names.Count);
        Width = _meshes && !_anyGltf ? 760 : 900; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "App.WindowBackground");
        SetResourceReference(ForegroundProperty, "App.Text");

        _intoPackfile.Content = new TextBlock { Text = packfile is null ? "Into the packfile (only for files opened from a packfile)" : $"Into {packfile} as new entries (one undo step)" };
        _intoPackfile.IsEnabled = packfile is not null;
        _nextToSource.Content = new TextBlock { Text = nextTo is null ? $"Next to the source ({noNextToReason ?? "it has no folder"})" : $"Next to the source ({nextTo})", TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 580 };
        _nextToSource.IsEnabled = nextTo is not null;
        _folder.Text = settings.Folder.Length > 0 ? settings.Folder : nextTo ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var place = settings.Place;
        if (place == LvlOutputPlace.IntoPackfile && packfile is null) place = nextTo is null ? LvlOutputPlace.Folder : LvlOutputPlace.NextToSource;
        if (place == LvlOutputPlace.NextToSource && nextTo is null) place = LvlOutputPlace.Folder;
        (place switch { LvlOutputPlace.IntoPackfile => _intoPackfile, LvlOutputPlace.NextToSource => _nextToSource, _ => _toFolder }).IsChecked = true;
        // never carried over: replacing is chosen here, and confirmed when a name is taken
        _replace.IsChecked = false;
        AutomationProperties.SetName(_folder, "Folder");
        AutomationProperties.SetName(_intoPackfile, "Into the packfile");
        AutomationProperties.SetName(_nextToSource, "Next to the source");
        AutomationProperties.SetName(_toFolder, "Into a folder");
        Content = Build(anyRf2);
        Load();
        foreach (var radio in _targets.Values) radio.Checked += (_, _) => Changed();
        foreach (var radio in new[] { _compiled, _brushes, _nodeTransforms, _applyTransforms, _perBrush, _whole }) radio.Checked += (_, _) => Changed();
        foreach (var box in _faces.Values.Concat(_objects.Values).Concat([_movers, _textures, _lightmapUvs, _triangulate, _metadata])) box.Click += (_, _) => Changed();
        foreach (var radio in new[] { _intoPackfile, _nextToSource, _toFolder }) radio.Checked += (_, _) => Update();
        _folder.TextChanged += (_, _) => { _toFolder.IsChecked = true; Update(); };
        _replace.Click += (_, _) => Update();
        Update();
    }

    /// <summary>The choice made with Convert, or null.</summary>
    public LvlConvertChoice? Result { get; private set; }

    /// <summary>The options the window describes now.</summary>
    public LvlConvertOptions Options => _options;

    /// <summary>Picks a target (self-tests and screenshots).</summary>
    public void Select(LvlConvertTarget target)
    {
        if (_targets.TryGetValue(target, out var radio) && radio.IsEnabled) radio.IsChecked = true;
    }

    /// <summary>The choice the window describes now, or null when the folder is not usable.</summary>
    public LvlConvertChoice? Current()
    {
        bool replace = _replace.IsChecked == true;
        var import = _import with { Objects = _meshes ? _options.Objects : _import.Objects };
        if (_intoPackfile.IsChecked == true && _packfile is not null) return new LvlConvertChoice(_options, LvlOutputPlace.IntoPackfile, null, replace) { Import = import };
        if (_nextToSource.IsChecked == true && _nextTo is not null) return new LvlConvertChoice(_options, LvlOutputPlace.NextToSource, _nextTo, replace) { Import = import };
        string folder = _folder.Text.Trim();
        if (folder.Length == 0) return null;
        try { folder = Path.GetFullPath(folder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        return new LvlConvertChoice(_options, LvlOutputPlace.Folder, folder, replace) { Import = import };
    }

    private UIElement Build(bool anyRf2)
    {
        // two columns: the format and its own choices on the left, the faces and objects on the right; where below.
        // The choices scroll when the window would not fit the screen; where and the buttons stay in view under them.
        var panel = new StackPanel { Margin = new Thickness(14, 14, 14, 0) };
        var footer = new StackPanel { Margin = new Thickness(14, 2, 14, 14) };
        var left = new StackPanel();
        var right = new StackPanel();
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(right, 2);
        columns.Children.Add(left);
        columns.Children.Add(right);
        panel.Children.Add(columns);
        void Label(Panel into, string text)
        {
            // for meshes the objects head the right column
            bool first = into.Children.Count == 0 && (into == left || into == right || (_meshes && into == _objectsSection));
            var t = new TextBlock { Text = text, Margin = new Thickness(0, first ? 0 : 12, 0, 4), FontWeight = FontWeights.SemiBold };
            t.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
            into.Children.Add(t);
        }

        Label(left, "Format");
        var targets = new List<(LvlConvertTarget Target, string Text, string Tip)>
        {
            (LvlConvertTarget.Gltf, "glTF (.gltf + .bin, textures beside it)", "glTF 2.0 with Cairn's and REDUX's extras: brushes, flags, groups and objects come back when Cairn reads it"),
            (LvlConvertTarget.Glb, "glTF binary (.glb, textures inside)", "The same glTF 2.0 in one file"),
            (LvlConvertTarget.Obj, "Wavefront OBJ (.obj + .mtl, textures beside it)", "Geometry and textures only; Cairn's comment lines keep the flags for Cairn, other tools drop them"),
            (LvlConvertTarget.V3m, "Static mesh (.v3m)", "A Red Faction static mesh, checked by the mesh linter"),
        };
        if (_meshes) targets.Clear();
        if (_rfgAvailable)
            targets.Add((LvlConvertTarget.Rfg, "Group file (.rfg)", _meshes
                ? "Brushes (and a glTF's objects) as an editor group file, for Alpine Faction's level editor"
                : "The level's brushes and objects as an editor group file, for Alpine Faction's level editor"));
        foreach (var (target, text, tip) in targets)
        {
            var radio = new RadioButton { GroupName = "lvlTarget", Content = text, ToolTip = tip, Margin = new Thickness(0, 2, 0, 2) };
            AutomationProperties.SetName(radio, text);
            _targets[target] = radio;
            left.Children.Add(radio);
        }

        Label(_geometrySection, "Geometry");
        _compiled.ToolTip = "The level as the game draws it, split into rooms, with the movers (glTF, OBJ and static meshes start here)";
        _brushes.ToolTip = "The editor's brushes, mover brushes included (group files start here)";
        _movers.ToolTip = "The movers are compiled apart from the rooms; leave them out for the static level only";
        _geometrySection.Children.Add(_compiled);
        _geometrySection.Children.Add(_brushes);
        _geometrySection.Children.Add(_movers);
        _geometrySection.Visibility = _anyLevel ? Visibility.Visible : Visibility.Collapsed;
        left.Children.Add(_geometrySection);

        if (_meshes) BuildMeshSection(left);
        else
        {
            Label(right, "Faces to include");
            var facesWrap = new UniformGrid { Columns = 3 };
            var classes = new List<RfFaceClass> { RfFaceClass.Normal };
            classes.AddRange(RfFaceClasses.All);
            foreach (var c in classes)
            {
                string text = c == RfFaceClass.Normal ? "Ordinary" : RfFaceClasses.DisplayName(c);
                var box = new CheckBox { Content = text, Margin = new Thickness(0, 2, 14, 2), ToolTip = c == RfFaceClass.Normal ? "Faces in none of the other classes" : $"Faces in the {text.ToLowerInvariant()} class (a face in an unticked class is left out)" };
                AutomationProperties.SetName(box, text + " faces");
                _faces[c] = box;
                facesWrap.Children.Add(box);
            }
            right.Children.Add(facesWrap);
        }

        Label(_objectsSection, "Objects to carry");
        var objectsWrap = new UniformGrid { Columns = 3 };
        foreach (var (kind, text) in ObjectKinds)
        {
            var box = new CheckBox { Content = text, Margin = new Thickness(0, 2, 14, 2), ToolTip = $"Carry the {text.ToLowerInvariant()} as objects" };
            AutomationProperties.SetName(box, text);
            _objects[kind] = box;
            objectsWrap.Children.Add(box);
        }
        _objectsSection.Children.Add(objectsWrap);
        var allNone = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        allNone.Children.Add(SmallButton("_All", "Carry every kind of object", () => SetObjects(LvlObjectKinds.All)));
        allNone.Children.Add(SmallButton("_None", "Carry no objects", () => SetObjects(LvlObjectKinds.None)));
        _objectsSection.Children.Add(allNone);
        right.Children.Add(_objectsSection);

        Label(_texturesSection, "Textures");
        _textures.ToolTip = "Decode each texture to PNG beside the output (inside a .glb); off writes the texture names only";
        _texturesSection.Children.Add(_textures);
        left.Children.Add(_texturesSection);

        Label(_gltfSection, "glTF");
        _gltfSection.Children.Add(_nodeTransforms);
        _gltfSection.Children.Add(_applyTransforms);
        _gltfSection.Children.Add(_lightmapUvs);
        left.Children.Add(_gltfSection);

        Label(_objSection, "OBJ");
        _objSection.Children.Add(_triangulate);
        _objSection.Children.Add(_metadata);
        left.Children.Add(_objSection);

        Label(_v3mSection, "Static mesh");
        _v3mSection.Children.Add(_perBrush);
        _v3mSection.Children.Add(_whole);
        left.Children.Add(_v3mSection);

        bool rf2Built = false;
        BuildRf2Options(_rf2Section, ref rf2Built);
        // the full width: its two columns of numbers need the room
        if (anyRf2 && rf2Built) panel.Children.Add(_rf2Section);

        Label(footer, "Where");
        footer.Children.Add(_intoPackfile);
        footer.Children.Add(_nextToSource);
        var browse = new Button { Content = "_Browse...", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2), ToolTip = "Pick the folder" };
        browse.SetResourceReference(StyleProperty, "PushButton");
        browse.Click += (_, _) => { if (_dialogs.PickFolder(_folder.Text, "Convert into") is { } picked) _folder.Text = picked; };
        var folderRow = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        DockPanel.SetDock(_toFolder, Dock.Left);
        DockPanel.SetDock(browse, Dock.Right);
        folderRow.Children.Add(_toFolder);
        folderRow.Children.Add(browse);
        folderRow.Children.Add(_folder);
        footer.Children.Add(folderRow);
        _replace.ToolTip = "Off: an output whose name is taken gets a free name. On: Cairn asks before replacing anything.";
        footer.Children.Add(_replace);
        _plan.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        footer.Children.Add(_plan);
        var hint = new TextBlock { Text = "What the conversion approximates, drops or splits is listed in the report when it is done.", TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        footer.Children.Add(hint);

        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, ToolTip = "Close without converting" };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        foreach (var b in new[] { _convert, cancel }) { b.SetResourceReference(StyleProperty, "DialogButton"); buttons.Children.Add(b); }
        _convert.Click += (_, _) => { Result = Current(); if (Result is not null) DialogResult = true; };
        footer.Children.Add(buttons);

        // The window sizes to its content up to most of the screen; past that only the choices scroll.
        MaxHeight = SystemParameters.WorkArea.Height * 0.9;
        var root = new DockPanel();
        // a line above where and the buttons while the choices scroll under it
        var footerBorder = new Border { Child = footer };
        footerBorder.SetResourceReference(Border.BorderBrushProperty, "App.SubtleBorder");
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        scroll.ScrollChanged += (_, _) => footerBorder.BorderThickness = new Thickness(0, scroll.ScrollableHeight > 0 ? 1 : 0, 0, 0);
        DockPanel.SetDock(footerBorder, Dock.Bottom);
        root.Children.Add(footerBorder);
        root.Children.Add(scroll);
        return root;
    }

    /// <summary>True when the window's choices are taller than the room they have and scroll (self-tests and screenshots).</summary>
    public bool ChoicesScroll => Content is DockPanel { Children.Count: 2 } root && root.Children[1] is ScrollViewer s && s.ScrollableHeight > 0;

    /// <summary>The Convert button (self-tests check it stays in view).</summary>
    public Button ConvertButton => _convert;

    /// <summary>
    /// Adds the Red Faction II options to <paramref name="section"/> (texture name translation, light and corona
    /// handling) and sets <paramref name="built"/>; the section is shown only for Red Faction II levels. The RF2
    /// level support implements it in its own file.
    /// </summary>
    partial void BuildRf2Options(StackPanel section, ref bool built);

    /// <summary>The object kinds with their check box text, in the order shown.</summary>
    internal static IReadOnlyList<(LvlObjectKinds Kind, string Text)> ObjectKinds { get; } =
    [
        (LvlObjectKinds.Lights, "Lights"), (LvlObjectKinds.Items, "Items"), (LvlObjectKinds.Clutter, "Clutter"), (LvlObjectKinds.Entities, "Entities"),
        (LvlObjectKinds.Triggers, "Triggers"), (LvlObjectKinds.Events, "Events"), (LvlObjectKinds.Spawns, "Spawn points"), (LvlObjectKinds.NavPoints, "Nav points"),
        (LvlObjectKinds.Regions, "Regions"), (LvlObjectKinds.Sounds, "Sounds"), (LvlObjectKinds.Effects, "Effects, decals"), (LvlObjectKinds.Cameras, "Cameras"),
        (LvlObjectKinds.AlpineMeshes, "Alpine meshes"), (LvlObjectKinds.Coronas, "Coronas"), (LvlObjectKinds.Keyframes, "Keyframes"),
    ];

    private Button SmallButton(string text, string tip, Action click)
    {
        var b = new Button { Content = text, ToolTip = tip, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 1, 8, 1) };
        b.SetResourceReference(StyleProperty, "DialogSmallButton");
        AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    private void SetObjects(LvlObjectKinds kinds)
    {
        _options = _options with { Objects = kinds };
        Load();
        Update();
    }

    /// <summary>Shows <see cref="_options"/> in the controls.</summary>
    private void Load()
    {
        _updating = true;
        if (_targets.TryGetValue(_options.Target, out var radio)) radio.IsChecked = true;
        var geometry = _options.ResolvedGeometry;
        _compiled.IsChecked = geometry == LvlGeometryChoice.Compiled;
        _brushes.IsChecked = geometry == LvlGeometryChoice.Brushes;
        _movers.IsChecked = _options.IncludeMovers;
        foreach (var (c, box) in _faces) box.IsChecked = _options.Keeps(c);
        foreach (var (k, box) in _objects) box.IsChecked = (_options.Objects & k) != 0;
        _textures.IsChecked = _options.ExportTextures;
        _nodeTransforms.IsChecked = _options.BrushTransforms == LvlBrushTransformMode.NodeTransform;
        _applyTransforms.IsChecked = _options.BrushTransforms == LvlBrushTransformMode.Apply;
        _lightmapUvs.IsChecked = _options.IncludeLightmapUvs;
        _triangulate.IsChecked = _options.ObjTriangulate;
        _metadata.IsChecked = _options.ObjMetadata;
        _perBrush.IsChecked = _options.V3mGrouping == LvlV3mGrouping.PerBrush;
        _whole.IsChecked = _options.V3mGrouping == LvlV3mGrouping.WholeLevel;
        _updating = false;
    }

    /// <summary>Reads the controls into <see cref="_options"/>.</summary>
    private void Changed()
    {
        if (_updating) return;
        var target = _targets.FirstOrDefault(p => p.Value.IsChecked == true).Key;
        bool targetChanged = target != _options.Target;
        var o = _options with { Target = target };
        // a new target starts at its own default geometry (compiled for meshes, brushes for group files)
        if (targetChanged) o = o with { Geometry = LvlGeometryChoice.Default };
        else
        {
            var chosen = _compiled.IsChecked == true ? LvlGeometryChoice.Compiled : LvlGeometryChoice.Brushes;
            o = o with { Geometry = chosen == (o with { Geometry = LvlGeometryChoice.Default }).ResolvedGeometry ? LvlGeometryChoice.Default : chosen };
        }
        o = o with { IncludeMovers = _movers.IsChecked == true };
        foreach (var (c, box) in _faces) o = o.WithFaceClass(c, box.IsChecked == true);
        var kinds = LvlObjectKinds.None;
        foreach (var (k, box) in _objects) if (box.IsChecked == true) kinds |= k;
        o = o with
        {
            Objects = kinds,
            ExportTextures = _textures.IsChecked == true,
            BrushTransforms = _applyTransforms.IsChecked == true ? LvlBrushTransformMode.Apply : LvlBrushTransformMode.NodeTransform,
            IncludeLightmapUvs = _lightmapUvs.IsChecked == true,
            ObjTriangulate = _triangulate.IsChecked == true,
            ObjMetadata = _metadata.IsChecked == true,
            V3mGrouping = _whole.IsChecked == true ? LvlV3mGrouping.WholeLevel : LvlV3mGrouping.PerBrush,
        };
        _options = o;
        if (targetChanged) Load();
        Update();
    }

    private void Update()
    {
        var target = _options.Target;
        bool gltf = target is LvlConvertTarget.Gltf or LvlConvertTarget.Glb;
        _gltfSection.Visibility = gltf ? Visibility.Visible : Visibility.Collapsed;
        _objSection.Visibility = target == LvlConvertTarget.Obj ? Visibility.Visible : Visibility.Collapsed;
        _v3mSection.Visibility = target == LvlConvertTarget.V3m ? Visibility.Visible : Visibility.Collapsed;
        // from meshes and OBJ files there are no objects to carry; a glTF may hold Cairn's object nodes and lights
        _objectsSection.Visibility = (_meshes ? _anyGltf : gltf || target == LvlConvertTarget.Rfg) ? Visibility.Visible : Visibility.Collapsed;
        _texturesSection.Visibility = target is LvlConvertTarget.V3m or LvlConvertTarget.Rfg ? Visibility.Collapsed : Visibility.Visible;
        _textures.Content = target == LvlConvertTarget.Glb ? "Put the _textures inside the .glb (as PNG)" : "Write the _textures as PNG files beside the output";
        _movers.IsEnabled = _compiled.IsChecked == true;
        var choice = Current();
        _convert.IsEnabled = choice is not null && !HasInvalidNumbers;
        if (choice is null) { _plan.Text = "Choose a folder."; return; }
        if (HasInvalidNumbers) { _plan.Text = "Correct the numbers marked in red (each must be a number above 0)."; return; }
        string ext = LvlConvertOptions.ExtensionOf(target);
        var outputs = _names.Select(n => Path.GetFileNameWithoutExtension(n) + ext).ToList();
        string where = choice.Place == LvlOutputPlace.IntoPackfile ? "into " + _packfile : "to " + choice.Folder;
        string side = target switch
        {
            LvlConvertTarget.Gltf => _options.ExportTextures ? " with its .bin and PNG textures" : " with its .bin",
            LvlConvertTarget.Obj => _options.ExportTextures ? " with its .mtl and PNG textures" : " with its .mtl",
            _ => "",
        };
        _plan.Text = outputs.Count == 1
            ? $"Writes {outputs[0]}{side} {where}."
            : string.Format(CultureInfo.CurrentCulture, "Writes {0:N0} files{1} {2}: {3} ... {4}.", outputs.Count, side, where, outputs[0], outputs[^1]);
    }
}
