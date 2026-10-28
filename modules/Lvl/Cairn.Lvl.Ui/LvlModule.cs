using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Cairn.Assets;
using Cairn.Lvl.Model;
using Cairn.Lvl.Ui.Documents;
using Cairn.Lvl.Ui.Preview;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Ui.Mvvm;
using Cairn.Ui.Services;
using Cairn.Viewport;
using Cairn.Workspace;

namespace Cairn.Lvl.Ui;

/// <summary>
/// The levels and groups module: opens Red Faction levels (.rfl: RF1, Alpine Faction and Red Faction II) and editor
/// group files (.rfg) in read-only tabs with their facts and a 3D preview, previews them inside packfiles, and converts
/// them to glTF, OBJ, static meshes and (levels) group files: one at a time from a tab or an entry (File › Convert to…),
/// or in a batch from a packfile ("Convert levels…"). Meshes, glTF and OBJ files convert to group files too.
/// </summary>
public sealed partial class LvlModule : ModuleBase, IAssetPreviewProvider, IAssetConverter, IArchiveBatchConverter
{
    private readonly List<LvlDocument> _documents = [];
    private readonly List<MenuContribution> _menus = [];
    private readonly List<ShortcutInfo> _shortcuts = [];
    private ModuleSettings? _store;
    private LvlKind? _levelKind, _groupKind;

    public LvlModule()
    {
        Settings = new LvlSettings(() => _store);
        Textures = new TextureService(() => base.Shell?.Assets.Resolver);
        SettingsPages = [new LvlSettingsPage(Settings, () => base.Shell?.Dialogs)];
    }

    public override IReadOnlyList<ISettingsPage> SettingsPages { get; }

    public override string Id => "lvl";
    public override string DisplayName => "Levels & groups";

    /// <summary>The level kind (.rfl).</summary>
    public LvlKind LevelKind => _levelKind ??= new LvlKind(this, "lvl.level", "Level", [".rfl"], "Red Faction levels (*.rfl)|*.rfl", "Red Faction level");

    /// <summary>The group file kind (.rfg).</summary>
    public LvlKind GroupKind => _groupKind ??= new LvlKind(this, "lvl.group", "Group file", [".rfg"], "Red Faction group files (*.rfg)|*.rfg", "Red Faction editor group");

    /// <summary>The module's settings ("lvl." keys).</summary>
    public LvlSettings Settings { get; }

    /// <summary>The texture decoder and cache every level view shares.</summary>
    public TextureService Textures { get; }

    /// <summary>The shell (for documents and self-tests).</summary>
    public new IShellContext Shell => base.Shell;
    internal IShellContext ShellContext => base.Shell;

    /// <summary>The open level and group documents.</summary>
    public IReadOnlyList<LvlDocument> OpenDocuments => _documents;

    public override IReadOnlyList<IDocumentKind> DocumentKinds => [LevelKind, GroupKind];
    public override IReadOnlyList<MenuContribution> Menus => _menus;
    public override IReadOnlyList<ShortcutInfo> Shortcuts => _shortcuts;
    public override IReadOnlyList<HelpTopic> HelpTopics { get; } = [new HelpTopic("lvl.levels", "Levels and groups (.rfl, .rfg)", LvlHelp.Build)];

    private LvlDocument? Active => base.Shell.ActiveDocument as LvlDocument;
    private static bool IsLevel(IDocument? d) => d is LvlDocument;

    public override void Initialize(IShellContext shell)
    {
        base.Initialize(shell);
        _store = new ModuleSettings(shell.Settings, Id);
        Textures.WaitForIndex = () => shell.Assets.ArchivesIndexed;
        shell.Assets.Changed += (_, _) => Textures.Clear();
        RelayCommand Cmd(Action<LvlDocument> action) => new(() => { if (Active is { } d) action(d); }, () => Active is not null);
        var convert = Cmd(ConvertDocument);
        var frame = Cmd(d => ViewOf(d)?.Scene.Frame());
        var fly = Cmd(d => { if (ViewOf(d)?.Scene is { } scene) scene.FlyMode = !scene.FlyMode; });
        var menu = new MenuItem { Header = "_Level", Name = "LevelMenu" };
        void Add(string header, string tip, ICommand command, string gesture)
            => menu.Items.Add(new MenuItem { Header = header, ToolTip = tip, Command = command, InputGestureText = gesture });
        Add("_Convert...", RfgAvailable ? "Convert the level or group file to glTF, OBJ, a static mesh or (a level) a group file" : "Convert the level or group file to glTF, OBJ or a static mesh", convert, "Ctrl+Shift+E");
        menu.Items.Add(new Separator());
        Add("_Frame all", "Show the whole level in the preview", frame, "F");
        Add("F_ly camera", "Fly through the level: drag to look around, W A S D to move, Q and E for down and up", fly, "V");
        _menus.Add(new MenuContribution(MenuSlot.TopLevel, 62, menu, IsLevel));
        if (RfgAvailable) _menus.Add(new MenuContribution(MenuSlot.FileImport, 130, ImportMenuItem()));
        _shortcuts.Add(new("Level", "Convert the level or group file", Key.E, ModifierKeys.Control | ModifierKeys.Shift, convert, IsLevel));
    }

    internal LvlDocument Track(LvlDocument document)
    {
        _documents.Add(document);
        return document;
    }

    internal void Forget(LvlDocument document) => _documents.Remove(document);

    internal void RefreshCommands() => base.Shell.RefreshCommands();

    /// <summary>The view of <paramref name="document"/> (made on first use).</summary>
    internal static LvlDocumentView? ViewOf(LvlDocument document) => document.View as LvlDocumentView;

    // ── previews for other modules (IAssetPreviewProvider) ────────────────────────────────────────────────────

    public bool CanPreview(string fileName) => LvlSource.Handles(fileName);

    public FrameworkElement? CreatePreview(byte[] bytes, string fileName) => CreatePreview(bytes, fileName, null);

    /// <summary>The level's preview; textures are looked up among <paramref name="siblings"/> (the rest of its packfile) first.</summary>
    public FrameworkElement? CreatePreview(byte[] bytes, string fileName, IAssetSiblings? siblings)
    {
        if (!CanPreview(fileName)) return null;
        return new LvlPreview(bytes, fileName, siblings, base.Shell?.Assets.Resolver, Textures, base.Shell is null ? null : Settings);
    }

    /// <summary>
    /// Where a tab's level finds its textures: the document's resolver (its folder, the rest of its packfile for a work
    /// copy, the game data), with a Red Faction II level's .peg packs first (its own, then its install's shared ones). UI thread.
    /// </summary>
    internal AssetResolver ResolverFor(LvlDocument doc, LvlSource source)
    {
        if (source.Level is not { IsRf2: true }) return doc.Resolver;
        var siblings = AssetSiblings.For(base.Shell, doc.FilePath);
        var under = base.Shell.Assets.ResolverFor(doc.FilePath is { } path ? Path.GetDirectoryName(path) : doc.ArchivePath is { } archive ? Path.GetDirectoryName(archive) : null);
        return LvlTexturePacks.ResolverFor(source, siblings is null ? doc.FilePath : null, siblings, under, Rf2InstallOf(doc)) ?? doc.Resolver;
    }

    // ── diagnostic options ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Diagnostic runs: <c>--lvl-open name</c> opens a level or group file from the game data (or a path);
    /// <c>--lvl-geometry compiled|brushes</c>, <c>--lvl-hide class,class</c> (face classes: portal, detail, sky, invisible,
    /// holes, liquid, alpha, fullbright, scrolling, normal), <c>--lvl-lighting fullbright|ambient|shaded</c>,
    /// <c>--lvl-markers on|off</c>, <c>--lvl-both-sides on</c>, <c>--lvl-camera yaw,pitch[,zoom]</c>,
    /// <c>--lvl-fly x,y,z,yaw,pitch</c> or <c>--lvl-fly start</c> (fly camera at that eye, or the player's), <c>--lvl-pick fx,fy</c>
    /// (click at that fraction of the view) and <c>--lvl-build on</c> (build a preview above the size limit) apply to the
    /// first level tab once its preview is built; <c>--lvl-facts Section</c> then expands that group of the facts and
    /// scrolls it to the top.
    /// </summary>
    public override void ApplyDiagnosticOptions(IReadOnlyDictionary<string, string> options)
    {
        if (!options.Keys.Any(k => k.StartsWith("lvl-", StringComparison.OrdinalIgnoreCase))) return;
        var busy = BusyTracker.Begin("level diagnostic options");
        _ = ApplyDiagnosticOptionsAsync(options).ContinueWith(_ => busy.Dispose(), TaskScheduler.Default);
    }

    private async Task ApplyDiagnosticOptionsAsync(IReadOnlyDictionary<string, string> options)
    {
        await Task.Yield();
        if (options.TryGetValue("lvl-open", out var name) && name.Length > 0)
        {
            if (File.Exists(name)) base.Shell.OpenFile(name);
            else
            {
                await base.Shell.Assets.ArchivesIndexed;
                var location = await Task.Run(() => base.Shell.Assets.Resolver.Resolve(name));
                if (location is null) base.Shell.ShowStatus("--lvl-open: " + name + " was not found in the game data");
                else base.Shell.OpenLocation(location);
            }
        }
        var doc = base.Shell.ActiveDocument as LvlDocument ?? _documents.FirstOrDefault();
        if (doc is null) return;
        base.Shell.Activate(doc);
        await doc.Loading;
        await Task.Delay(50);
        if (ViewOf(doc) is not { Scene: { } scene } view) return;
        await scene.Building;
        await ApplyViewOptionsAsync(scene, options);
        if (options.TryGetValue("lvl-facts", out var group) && group.Length > 0)
        {
            await view.Facts.Pending;
            if (!view.Facts.ShowGroup(group)) base.Shell.ShowStatus("--lvl-facts: the facts have no " + group + " group");
        }
    }

    /// <summary>Applies the <c>--lvl-*</c> view options to <paramref name="scene"/> (also used by self-tests).</summary>
    internal static async Task ApplyViewOptionsAsync(LvlSceneView scene, IReadOnlyDictionary<string, string> options)
    {
        static double[] Numbers(string text) => [.. text.Split(',').Select(p => double.TryParse(p.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN)];
        if (options.ContainsKey("lvl-build") && scene.IsWaitingForConfirmation)
        {
            scene.BuildAnyway();
            await scene.Building;
        }
        var settings = scene.SceneSettings;
        if (options.TryGetValue("lvl-geometry", out var geometry)) settings = settings with { Compiled = !geometry.Equals("brushes", StringComparison.OrdinalIgnoreCase) };
        if (options.TryGetValue("lvl-hide", out var hide))
        {
            foreach (var word in hide.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (ClassOf(word) is { } c) settings = settings.With(c, show: false);
        }
        scene.SetSceneSettings(settings);
        await scene.Building;
        if (options.TryGetValue("lvl-lighting", out var lighting))
            scene.Lighting = lighting.ToLowerInvariant() switch { "ambient" => LvlLighting.LevelAmbient, "shaded" => LvlLighting.Shaded, _ => LvlLighting.FullBright };
        if (options.TryGetValue("lvl-markers", out var markers)) scene.MarkerKinds = markers.Equals("off", StringComparison.OrdinalIgnoreCase) ? LvlMarkerKinds.None : LvlMarkerKinds.All;
        if (options.TryGetValue("lvl-both-sides", out var both)) scene.BothSides = !both.Equals("off", StringComparison.OrdinalIgnoreCase);
        scene.UpdateLayout();
        if (options.TryGetValue("lvl-camera", out var camera) && Numbers(camera) is { Length: >= 2 } angles && angles.All(double.IsFinite))
        {
            scene.Surface.Camera.Yaw = angles[0];
            scene.Surface.Camera.Pitch = angles[1];
            scene.Frame();
            if (angles.Length >= 3 && angles[2] > 0)
            {
                scene.Surface.Camera.Distance /= angles[2];
                scene.Surface.Camera.NotifyChanged();
            }
        }
        if (options.TryGetValue("lvl-fly", out var fly) && fly.Equals("start", StringComparison.OrdinalIgnoreCase) && scene.Source?.Level?.PlayerStart is { } start)
        {
            // the player's eye at the start, looking where the player looks
            var forward = start.Orientation.Forward;
            scene.FlyMode = true;
            scene.Fly.PlaceEye(start.Position + new Vector3(0, 1.5f, 0), Math.Atan2(-forward.X, -forward.Z) * 180 / Math.PI, Math.Asin(Math.Clamp(-forward.Y, -1, 1)) * 180 / Math.PI);
        }
        else if (options.TryGetValue("lvl-fly", out fly) && Numbers(fly) is { Length: >= 5 } f && f.All(double.IsFinite))
        {
            scene.FlyMode = true;
            scene.Fly.PlaceEye(new Vector3((float)f[0], (float)f[1], (float)f[2]), f[3], f[4]);
        }
        if (options.TryGetValue("lvl-pick", out var pick) && Numbers(pick) is { Length: >= 2 } p && p.All(double.IsFinite))
        {
            await Task.Delay(100);
            scene.PickAt(new Point(scene.Surface.ActualWidth * p[0], scene.Surface.ActualHeight * p[1]));
        }
        scene.Surface.Invalidate();
    }

    /// <summary>A face class by its option word ("sky", "liquid", "normal"), or null.</summary>
    internal static RfFaceClass? ClassOf(string word) => word.ToLowerInvariant() switch
    {
        "normal" or "ordinary" => RfFaceClass.Normal,
        "portal" => RfFaceClass.Portal,
        "detail" => RfFaceClass.Detail,
        "sky" => RfFaceClass.Sky,
        "invisible" => RfFaceClass.Invisible,
        "hole" or "holes" => RfFaceClass.Hole,
        "liquid" => RfFaceClass.LiquidSurface,
        "alpha" => RfFaceClass.Alpha,
        "fullbright" => RfFaceClass.FullBright,
        "scrolling" => RfFaceClass.Scrolling,
        _ => null,
    };
}

/// <summary>A level or group file kind (read-only tabs; Convert to… instead of Save).</summary>
public sealed class LvlKind(LvlModule module, string id, string displayName, IReadOnlyList<string> extensions, string filter, string association) : IDocumentKind
{
    public string Id => id;
    public string DisplayName => displayName;
    public IReadOnlyList<string> Extensions => extensions;
    public string FileFilter => filter;
    public bool CanCreateNew => false;
    public string AssociationDescription => association;

    public IDocument? CreateNew() => null;

    /// <summary>Levels run to hundreds of megabytes (the largest known is 337 MB), well above the usual document limit.</summary>
    public const long MaxLevelBytes = 1L << 30;

    public IDocument Open(string path) => Create(AtomicFile.ReadAllBytes(path, MaxLevelBytes), Path.GetFileName(path), Path.GetFullPath(path), null);

    public IDocument OpenBytes(byte[] bytes, string displayName, string originText) => Create(bytes, displayName, null, originText);

    /// <summary>A packfile entry of the game data: the document remembers the packfile.</summary>
    public IDocument OpenEntry(AssetLocation location, byte[] bytes, string originText)
    {
        var document = Create(bytes, location.ResolvedName, null, originText);
        document.ArchivePath = location.ArchivePath;
        return document;
    }

    /// <summary>Levels keep no recovery data (they are never edited); a snapshot is opened as it was.</summary>
    public IDocument Restore(RecoverySnapshot snapshot) => Create(snapshot.Data, snapshot.DisplayName, null, "Recovered");

    private LvlDocument Create(byte[] bytes, string name, string? path, string? origin)
    {
        // refuse what is plainly not a level or group file here; the rest is read after the tab opens
        LvlSignatures.Check(bytes, name);
        return module.Track(new LvlDocument(module, this, bytes, name, path, origin));
    }
}

/// <summary>The quick check before a tab opens (signature and size), so the shell reports an unreadable file at once.</summary>
internal static class LvlSignatures
{
    /// <exception cref="Cairn.Formats.AssetFormatException">The file is not a level or group file.</exception>
    public static void Check(byte[] bytes, string name)
    {
        uint magic = bytes.Length >= 4 ? BitConverter.ToUInt32(bytes, 0) : 0;
        if (LvlSource.IsGroupName(name))
        {
            if (magic != Reading.RfgReader.Magic) throw new Cairn.Formats.AssetFormatException($"'{name}' is not a Red Faction group file (no group file signature).");
        }
        else if (magic != Cairn.Formats.Rfl.RflReader.Magic)
        {
            throw new Cairn.Formats.AssetFormatException($"'{name}' is not a Red Faction level (no level signature).");
        }
    }
}
