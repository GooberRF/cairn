using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Cairn.Rfa.Docs;
using Cairn.Rfa.Formats.V3d;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Ui.Mvvm;
using Cairn.Vfx.Animation;
using Cairn.Vfx.Docs;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Interchange;
using Cairn.Vfx.Ui.Dialogs;
using Cairn.Vfx.Ui.Documents;
using Cairn.Vfx.Ui.Library;
using Cairn.Vfx.Ui.Problems;

namespace Cairn.Vfx.Ui;

/// <summary>Creation, import/export, effects library, problems and help.</summary>
public sealed partial class VfxModule
{
    private IFileImporter[]? _importers;
    private VfxLibraryPanel? _library;

    /// <summary>Available before <c>Initialize</c>: the shell collects importers when it is constructed.</summary>
    public override IReadOnlyList<IFileImporter> Importers => _importers ??= [new VfxGltfImporter(this)];
    public override IReadOnlyList<PanelContribution> Panels =>
    [
        new("vfx.library", "Effects", PanelSide.Left, 50, _ => _library ??= new VfxLibraryPanel(Shell)),
        new(VfxProblemsPanel.PanelId, "Problems", PanelSide.Bottom, 10, d => d is VfxDocument doc ? VfxProblemsPanel.For(Shell, doc) : null),
        TimelinePanelContribution, // VfxModule.Timeline.cs
    ];
    public override IReadOnlyList<HelpTopic> HelpTopics =>
    [
        new("vfx.format", "VFX format reference", () => BuildReference(VfxFormatDocs.Reference)),
        new("vfx.accuracy", "Effect preview accuracy", BuildAccuracy),
    ];

    private void InitializeCreation()
    {
        InitializeAddCommands();
        if (_menus.Count > 0 && _menus[0].Item is MenuItem effect)
        {
            effect.Items.Insert(0, BuildAddMenu("_Add"));
            effect.Items.Insert(1, Item("New from _template", "Start a new effect from a ready-made animated example", null, BuildTemplateMenu()));
            effect.Items.Insert(2, new Separator());
        }
        var import = new MenuItem { Header = "_Effect" };
        import.Items.Add(Item("_glTF as effect...", "Import a glTF/GLB written by Cairn or REDUX as an effect", () => ImportGltf(null)));
        import.Items.Add(Item("Geometry from _mesh (V3M/V3C)...", "Convert a static mesh's submeshes into effect meshes", ImportV3d));
        import.Items.Add(Item("_Objects from another effect...", "Copy objects (with their materials) from another .vfx", ImportObjects));
        _menus.Add(new(MenuSlot.FileImport, 50, import));
        _menus.Add(new(MenuSlot.FileExport, 50, Item("Effect as _glTF...", "Export the current effect as glTF (.gltf + .bin) or GLB", ExportGltf), d => d is VfxDocument));
    }

    private static MenuItem Item(string header, string tip, Action? action, MenuItem? sub = null)
    {
        var item = sub ?? new MenuItem();
        item.Header = header; item.ToolTip = tip;
        System.Windows.Automation.AutomationProperties.SetName(item, header.Replace("_", "").TrimEnd('.'));
        if (action is not null) item.Command = new RelayCommand(action);
        return item;
    }

    private MenuItem BuildAddMenu(string header)
    {
        var add = Item(header, "Add an object or a material to the effect", null);
        foreach (var item in AddItems()) add.Items.Add(item);
        return add;
    }

    /// <summary>The Effect > Add entries, built fresh (shared by the menu, the toolbar arrow and the outliner menu).</summary>
    private IEnumerable<MenuItem> AddItems()
    {
        yield return Item("_Primitive...", "Add a plane, facing quad, facing rod, disc, ring, cylinder, cone, sphere or box", AddPrimitive);
        foreach (var (kind, key) in new[] { ("Particle system", "P_article system"), ("Dummy", "_Dummy"), ("Light", "_Light"), ("Spacewarp", "_Spacewarp") })
            yield return Item(key, $"Add a {kind.ToLowerInvariant()} at the origin that lasts as long as the effect", () => AddObject(kind));
        yield return Item("_Material", "Add a grey colour material", () => Edit("Add material", f => VfxCreation.AddMaterial(f, null, false, true).File, f => -1));
    }

    private readonly List<ToolbarContribution> _creationToolbar = [];
    public override IReadOnlyList<ToolbarContribution> ToolbarItems => _creationToolbar;
    private static bool _outlinerHooked;

    private void InitializeAddCommands()
    {
        var button = new Cairn.Ui.Controls.SplitToolButton
        {
            Glyph = "",
            PrimaryCommand = new RelayCommand(AddPrimitive),
            PrimaryLabel = "_Primitive...",
            PrimaryToolTip = "Add a primitive (plane, quad, disc, cylinder, sphere, box...) to the effect",
            MenuToolTip = "Add another kind of object or a material",
            MenuItemsFactory = () => AddItems(),
        };
        System.Windows.Automation.AutomationProperties.SetName(button, "Add");
        _creationToolbar.Add(new ToolbarContribution(60, button, d => d is VfxDocument));

        // The outliner has no menu extension point: append "Add" after its own Opened handler fills the menu.
        if (_outlinerHooked) return;
        _outlinerHooked = true;
        var hooked = new System.Runtime.CompilerServices.ConditionalWeakTable<ContextMenu, object>();
        EventManager.RegisterClassHandler(typeof(Outliner.VfxOutliner), FrameworkElement.ContextMenuOpeningEvent, new ContextMenuEventHandler((s, args) =>
        {
            if (s is not FrameworkElement { ContextMenu: { } menu } || hooked.TryGetValue(menu, out _)) return;
            hooked.Add(menu, menu);
            menu.Opened += (_, _) =>
            {
                if (Active is null) return;
                if (menu.Items.Count > 0) menu.Items.Add(new Separator());
                menu.Items.Add(BuildAddMenu("_Add"));
            };
        }));
    }

    private MenuItem BuildTemplateMenu()
    {
        var menu = new MenuItem();
        foreach (var name in VfxCreation.TemplateNames)
            menu.Items.Add(Item(name, $"New effect: {name.ToLowerInvariant()}", () => OpenNew(VfxCreation.Template(name), $"{name.Replace(" ", "")}.vfx")));
        return menu;
    }

    internal void OpenNew(VfxFile file, string name)
    {
        var doc = new VfxDocument(Shell, _kind, file, name, null);
        doc.MarkAsNew();
        Shell.AddDocument(doc);
    }

    /// <summary>Applies an edit as one undo step and selects the section it reports.</summary>
    internal static bool Edit(VfxDocument doc, string label, Func<VfxFile, (VfxFile File, int Section)> edit)
    {
        int section = -1;
        bool changed = doc.Apply(label, f => { var r = edit(f); section = r.Section; return r.File; });
        if (changed && section >= 0) doc.Selection.Select(section);
        return changed;
    }

    private void Edit(string label, Func<VfxFile, VfxFile> edit, Func<VfxFile, int> select)
    {
        if (Active is not { } doc) return;
        if (doc.IsOlderVersion) { Shell.Dialogs.ShowError("Older effect", "Convert the effect to the current format first (Effect > Convert to current format)."); return; }
        Edit(doc, label, f => { var n = edit(f); return (n, select(n)); });
    }

    private void AddObject(string kind)
    {
        if (Active is { IsOlderVersion: false } doc) Edit(doc, $"Add {kind.ToLowerInvariant()}", f => VfxCreation.AddObject(f, kind));
        else Edit("", f => f, f => -1);
    }

    private void AddPrimitive()
    {
        if (Active is not { } doc) return;
        if (doc.IsOlderVersion) { Edit("", f => f, f => -1); return; }
        var form = VfxCreationDialogs.Primitive(doc.Current, Shell.MainWindow, out var read);
        if (form.ShowDialog() == true) Edit(doc, "Add primitive", f => VfxCreation.AddPrimitive(f, read()));
    }

    internal async void ImportGltf(string? path)
    {
        path ??= Shell.Dialogs.OpenFiles(null, "Import glTF as effect", "glTF (*.gltf;*.glb)|*.gltf;*.glb|All files (*.*)|*.*", false).FirstOrDefault();
        if (path is null) return;
        VfxGltfImportResult result;
        using var cancel = new CancellationTokenSource();
        // A small non-modal window with Cancel while the import runs (a big glTF can take a while).
        var stop = new System.Windows.Controls.Button { Content = "Cancel", IsCancel = true, MinWidth = 80, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new System.Windows.Thickness(0, 12, 0, 0) };
        var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(16) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = $"Importing {System.IO.Path.GetFileName(path)}..." });
        panel.Children.Add(new System.Windows.Controls.ProgressBar { IsIndeterminate = true, Height = 6, Margin = new System.Windows.Thickness(0, 10, 0, 0) });
        panel.Children.Add(stop);
        var progress = new System.Windows.Window
        {
            Title = "Import glTF", Content = panel, SizeToContent = System.Windows.SizeToContent.WidthAndHeight, MinWidth = 320,
            ResizeMode = System.Windows.ResizeMode.NoResize, ShowInTaskbar = false, Owner = Shell.MainWindow,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
        };
        progress.SetResourceReference(System.Windows.Window.BackgroundProperty, System.Windows.SystemColors.ControlBrushKey);
        stop.Click += (_, _) => { cancel.Cancel(); stop.IsEnabled = false; };
        progress.Closing += (_, _) => cancel.Cancel();
        var work = Task.Run(() => VfxGltfImport.Import(path, null, cancel.Token), cancel.Token);
        // only shown when the import is not almost instant
        if (await Task.WhenAny(work, Task.Delay(300)) != work && !Shell.IsDiagnosticRun) progress.Show();
        try { result = await work; }
        catch (OperationCanceledException) { Shell.ShowStatus("glTF import cancelled"); return; }
        catch (Exception ex) { Shell.Dialogs.ShowError("glTF import failed", ex.Message, ex.ToString()); return; }
        finally { if (progress.IsVisible) progress.Close(); }
        var target = Active is { IsOlderVersion: false } a ? a : null;
        var form = VfxCreationDialogs.GltfImport(path, result.File, result.Messages, target is not null, Shell.MainWindow, out var add);
        if (form.ShowDialog() != true) return;
        if (add() && target is not null) Transplant(target, "Import glTF", f => VfxTransplant.CopyAll(result.File, f));
        else OpenNew(result.File, Path.GetFileNameWithoutExtension(path) + ".vfx");
    }

    private void Transplant(VfxDocument doc, string label, Func<VfxFile, VfxTransplantResult> copy)
    {
        IReadOnlyList<string> warnings = [];
        Edit(doc, label, f => { var r = copy(f); warnings = r.Warnings; return (r.File, r.Sections.Count > 0 ? r.Sections[0].TargetIndex : -1); });
        Shell.ShowStatus(warnings.Count == 0 ? $"{label}: done" : $"{label}: {string.Join("; ", warnings.Take(3))}");
    }

    private VfxPickedAsset? PickAsset(string title, string filter, string[] extensions) =>
        VfxAssetPicker.Pick(Shell, title, filter, extensions);

    private void ImportV3d()
    {
        if (PickAsset("Geometry from mesh", "Meshes (*.v3m;*.v3c)|*.v3m;*.v3c", [".v3m", ".v3c"]) is not { } picked) return;
        V3dFile mesh;
        try { mesh = V3dReader.Read(picked.Bytes, picked.Name); }
        catch (Exception ex) { Shell.Dialogs.ShowError("Cannot read mesh", ex.Message); return; }
        var target = Active is { IsOlderVersion: false } a ? a : null;
        var form = VfxCreationDialogs.FromV3d(mesh, target is not null, Shell.MainWindow, out var read);
        if (form.ShowDialog() != true) return;
        var (options, add) = read();
        if (add && target is not null)
            Edit(target, "Geometry from mesh", f => { var r = VfxFromV3d.Add(f, mesh, options); return (r.File, r.MeshSections.IsEmpty ? -1 : r.MeshSections[0]); });
        else OpenNew(VfxFromV3d.CreateFile(mesh, options), Path.GetFileNameWithoutExtension(picked.Name) + ".vfx");
    }

    private void ImportObjects()
    {
        if (Active is not { IsOlderVersion: false } doc) { Edit("", f => f, f => -1); return; }
        if (PickAsset("Objects from another effect", "Effects (*.vfx)|*.vfx", [".vfx"]) is not { } picked) return;
        VfxFile source;
        try { source = VfxReader.Read(picked.Bytes, picked.Name); }
        catch (Exception ex) { Shell.Dialogs.ShowError("Cannot read effect", ex.Message); return; }
        var form = VfxCreationDialogs.Transplant(source, picked.Name, Shell.MainWindow, out var read);
        if (form.ShowDialog() != true) return;
        var (sections, offset) = read();
        Transplant(doc, "Objects from effect", f => VfxTransplant.Copy(source, f, sections, offset));
    }

    private void ExportGltf()
    {
        if (Active is not { } doc) return;
        var stem = Path.GetFileNameWithoutExtension(doc.DisplayName);
        var form = VfxCreationDialogs.GltfExport(stem, Shell.MainWindow, out var glb);
        if (form.ShowDialog() != true) return;
        bool binary = glb();
        var path = Shell.Dialogs.SaveFile(doc.FilePath is null ? null : Path.GetDirectoryName(doc.FilePath), "Export glTF",
            binary ? "GLB (*.glb)|*.glb" : "glTF (*.gltf)|*.gltf", stem + (binary ? ".glb" : ".gltf"), binary ? ".glb" : ".gltf");
        if (path is null) return;
        try { VfxGltfExport.Save(doc.Current, path, binary); }
        catch (Exception ex) { Shell.Dialogs.ShowError("glTF export failed", ex.Message, ex.ToString()); return; }
        Shell.ShowStatus($"Exported {Path.GetFileName(path)}");
        if (!Shell.IsDiagnosticRun && Shell.Dialogs.Confirm("Export finished", $"Wrote {path}" + (binary ? "" : " and its .bin"), "Open folder"))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    private static FlowDocument BuildReference(FormatReference reference)
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph(new Run(reference.Title)) { FontSize = 20, FontWeight = FontWeights.SemiBold });
        doc.Blocks.Add(new Paragraph(new Run(reference.Summary)));
        foreach (var section in reference.Sections)
        {
            doc.Blocks.Add(new Paragraph(new Run(section.Title)) { FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4) });
            doc.Blocks.Add(new Paragraph(new Run(section.Summary)));
            var list = new List();
            foreach (var f in section.Fields)
            {
                var p = new Paragraph();
                p.Inlines.Add(new Bold(new Run($"{f.Name} ({f.Type}{(f.Units is null ? "" : ", " + f.Units)})")));
                p.Inlines.Add(new Run($": {f.Summary} {f.EngineUse}{(f.Limits is null ? "" : " Limits: " + f.Limits)}{(f.ReadByGame == false ? " Not read by the game." : "")}"));
                list.ListItems.Add(new ListItem(p));
            }
            doc.Blocks.Add(list);
        }
        foreach (var topic in VfxFormatDocs.Topics)
        {
            doc.Blocks.Add(new Paragraph(new Run(topic.Title)) { FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4) });
            doc.Blocks.Add(new Paragraph(new Run(topic.Text)));
        }
        return doc;
    }

    private static FlowDocument BuildAccuracy()
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph(new Run("Effect preview accuracy")) { FontSize = 20, FontWeight = FontWeights.SemiBold });
        doc.Blocks.Add(new Paragraph(new Run("The preview follows the game closely for geometry, timing and textures. These behaviours are approximated, so check them in the game:")));
        var list = new List();
        foreach (var note in VfxEngineNotes.All) list.ListItems.Add(new ListItem(new Paragraph(new Run(note.Text))));
        doc.Blocks.Add(list);
        return doc;
    }
}

/// <summary>File &gt; Open / drag-drop of glTF files that carry a VFX root (<c>rf_type</c> "vfx").</summary>
public sealed class VfxGltfImporter(VfxModule module) : IFileImporter
{
    public string DisplayName => "glTF as effect";
    public IReadOnlyList<string> Extensions { get; } = [".gltf", ".glb"];
    public string FileFilter => "glTF effect (*.gltf;*.glb)|*.gltf;*.glb";
    /// <summary>100 for a file whose nodes carry a <c>vfx*</c> rf_type; 10 for a plain glTF (the same as RFA's, so the shell asks).</summary>
    public int Probe(string path)
    {
        try { var p = VfxGltfImport.Probe(path); return p >= 1f ? 100 : p > 0f ? 10 : 0; }
        catch { return 0; }
    }
    public void Import(string path) => module.ImportGltf(path);
}
