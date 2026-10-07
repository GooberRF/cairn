using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Ui.Mvvm;
using Cairn.Vfx.Animation;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Ui.Documents;
using Cairn.Workspace;

namespace Cairn.Vfx.Ui;

/// <summary>The effects module hosted by the Cairn shell.</summary>
public sealed partial class VfxModule : ModuleBase, IAssetPreviewProvider
{
    /// <inheritdoc/>
    public bool CanPreview(string fileName) => fileName.EndsWith(".vfx", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public FrameworkElement? CreatePreview(byte[] bytes, string fileName) => CreatePreview(bytes, fileName, null);

    /// <inheritdoc/>
    public FrameworkElement? CreatePreview(byte[] bytes, string fileName, IAssetSiblings? siblings)
    {
        if (!CanPreview(fileName)) return null;
        var assets = Shell?.Assets;
        // Textures beside the effect (same packfile) win over the game data's.
        return new Preview.VfxPreview(bytes, fileName, () => siblings.Layer(assets?.Resolver));
    }

    private readonly VfxKind _kind = new();
    private readonly List<MenuContribution> _menus = [];

    public override string Id => "vfx";
    public override string DisplayName => "Effects";
    public override IReadOnlyList<IDocumentKind> DocumentKinds => [_kind];
    public override IReadOnlyList<ISettingsPage> SettingsPages { get; } = [new Settings.VfxSettingsPage()];
    public override IReadOnlyList<MenuContribution> Menus => _menus;
    /// <summary>Module styles and templates (<c>Themes/VfxResources.xaml</c>), merged into the application resources.</summary>
    public override IReadOnlyList<ResourceDictionary> Resources { get; } =
        [new ResourceDictionary { Source = new Uri("pack://application:,,,/Cairn.Vfx.Ui;component/Themes/VfxResources.xaml") }];

    public override void Initialize(IShellContext shell)
    {
        base.Initialize(shell);
        _kind.Shell = shell;
        _menus.Add(new(MenuSlot.TopLevel, 0, BuildMenu(), d => d is VfxDocument));
        InitializeCreation(); // VfxModule.Creation.cs
        // diagnostic runs: binding errors go to the console so screenshot/self-test logs show them
        if (shell.IsDiagnosticRun && !System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.OfType<System.Diagnostics.ConsoleTraceListener>().Any())
        {
            System.Diagnostics.PresentationTraceSources.Refresh();
            System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = System.Diagnostics.SourceLevels.Warning;
            System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(new System.Diagnostics.ConsoleTraceListener());
        }
    }

    private VfxDocument? Active => Shell.ActiveDocument as VfxDocument;

    private MenuItem BuildMenu()
    {
        var menu = new MenuItem { Header = "_Effect", Name = "EffectMenu" };
        foreach (var (mode, label, tip) in new[]
        {
            (VfxPlaybackMode.Loop, "_Loop", "Restart from the first frame at the end"),
            (VfxPlaybackMode.OneShot, "_One-shot", "Play once; meshes disappear at the end"),
            (VfxPlaybackMode.HoldLastFrame, "_Hold last frame", "Play once and keep the last frame"),
        })
        {
            var item = new MenuItem { Header = label, ToolTip = tip, IsCheckable = true, Command = new RelayCommand(() => { if (Active is { } d) d.Mode = mode; }) };
            menu.SubmenuOpened += (_, _) => item.IsChecked = Active?.Mode == mode;
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "_Convert to current format", ToolTip = "Upgrade an older effect so it can be edited (undoable)", Command = new RelayCommand(() => Active?.ConvertCommand.Execute(null), () => Active?.IsOlderVersion == true) });
        AddEditingItems(menu);
        AddVertexItems(menu); // VfxModule.Vertex.cs
        AddTimelineItems(menu); // VfxModule.Timeline.cs
        return menu;
    }

    private IReadOnlyDictionary<string, string>? _pendingOptions;

    /// <summary>The shell applies options before files open: keep them and apply them to the first effect that becomes active.</summary>
    public override void ApplyDiagnosticOptions(IReadOnlyDictionary<string, string> options)
    {
        if (Active is { } doc) { ApplyOptions(doc, options); return; }
        _pendingOptions = options;
        Shell.ActiveDocumentChanged += OnFirstActive;
    }

    private void OnFirstActive(object? sender, EventArgs e)
    {
        if (Active is not { } doc || _pendingOptions is not { } options) return;
        Shell.ActiveDocumentChanged -= OnFirstActive;
        _pendingOptions = null;
        ApplyOptions(doc, options);
    }

    internal static void ApplyOptions(VfxDocument doc, IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("mode", out var mode))
            doc.Mode = mode.ToLowerInvariant() switch { "oneshot" => VfxPlaybackMode.OneShot, "hold" => VfxPlaybackMode.HoldLastFrame, _ => VfxPlaybackMode.Loop };
        if (options.TryGetValue("frame", out var frame) && float.TryParse(frame, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) doc.SeekFrame(f);
        if (options.TryGetValue("select", out var name))
        {
            int i = Enumerable.Range(0, doc.Current.Sections.Length).FirstOrDefault(i => VfxSections.NameOf(doc.Current.Sections[i]).Equals(name, StringComparison.OrdinalIgnoreCase), -1);
            doc.Selection.Select(i);
        }
        ApplyEditingOptions(doc, options); // --inspector
        ApplyTimelineOptions(doc, options); // --timeline expanded, --tool
        if (options.TryGetValue("camera", out var cam) && doc.View is VfxDocumentView view)
        {
            var parts = cam.Split(',').Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            if (parts.Length >= 2)
            {
                view.Dispatcher.InvokeAsync(() =>
                {
                    view.Viewport.Camera.SetView(parts[0], parts[1]);
                    if (parts.Length >= 3) view.Viewport.Camera.Distance = parts[2];
                    view.Viewport.Camera.NotifyChanged();
                    view.Viewport.Invalidate();
                }, System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }
    }
}

/// <summary>The .vfx document kind.</summary>
public sealed class VfxKind : IDocumentKind
{
    public IShellContext Shell { get; set; } = null!;
    public string Id => "vfx";
    public string DisplayName => "Effect";
    public IReadOnlyList<string> Extensions { get; } = [".vfx"];
    public string FileFilter => "Effects (*.vfx)|*.vfx";
    public bool CanCreateNew => true;
    public string AssociationDescription => "Red Faction effect";

    public IDocument? CreateNew() { var d = new VfxDocument(Shell, this, VfxBuilder.NewFile(), "Untitled.vfx", null) { Mode = Settings.VfxSettingsPage.DefaultMode }; d.MarkAsNew(); return d; }
    public IDocument Open(string path) => new VfxDocument(Shell, this, VfxReader.Read(AtomicFile.ReadAllBytes(path), Path.GetFileName(path)), Path.GetFileName(path), path) { Mode = Settings.VfxSettingsPage.DefaultMode };
    public IDocument OpenBytes(byte[] bytes, string displayName, string originText) => new VfxDocument(Shell, this, VfxReader.Read(bytes, displayName), displayName, null, originText);
    public IDocument Restore(RecoverySnapshot snapshot)
    {
        var d = new VfxDocument(Shell, this, VfxReader.Read(snapshot.Data, snapshot.DisplayName), snapshot.DisplayName, snapshot.OriginalPath);
        d.MarkAsNew();
        return d;
    }
}
