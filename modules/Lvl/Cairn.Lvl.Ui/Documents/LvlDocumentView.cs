using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cairn.Lvl.Ui.Preview;
using Cairn.Previews;
using Cairn.Ui.Modules;

namespace Cairn.Lvl.Ui.Documents;

/// <summary>A level or group tab: the facts on the left, the 3D preview on the right.</summary>
public sealed class LvlDocumentView : Grid, IDisposable
{
    private readonly LvlDocument _doc;
    private readonly LvlModule _module;
    private bool _disposed;

    internal LvlDocumentView(LvlDocument doc, LvlModule module)
    {
        _doc = doc;
        _module = module;
        SetResourceReference(BackgroundProperty, "App.PaneBackground");
        AutomationProperties.SetName(this, doc.DisplayName);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360), MinWidth = 180 });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 200 });

        Facts = new AssetDetailsPane(module.ShellContext) { BusyLabel = "level facts" };
        Facts.ShowMessage("Reading " + doc.DisplayName + "…");
        var factsBorder = new Border { Child = Facts, BorderThickness = new Thickness(0, 0, 1, 0) };
        factsBorder.SetResourceReference(Border.BorderBrushProperty, "App.SubtleBorder");
        Children.Add(factsBorder);

        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext, Focusable = false };
        splitter.SetResourceReference(StyleProperty, "VerticalSplitter");
        AutomationProperties.SetName(splitter, "Resize the facts");
        SetColumn(splitter, 1);
        Children.Add(splitter);

        Scene = new LvlSceneView(module.Textures, module.Settings, remember: !module.ShellContext.IsDiagnosticRun);
        var convert = PreviewUi.Button("Convert…", "Convert to glTF, OBJ or a static mesh (Ctrl+Shift+E)", (_, _) => module.ConvertDocument(_doc));
        AutomationProperties.SetName(convert, "Convert");
        Scene.AddToolbarElement(convert);
        Scene.ShowMessage("Reading " + doc.DisplayName + "…");
        SetColumn(Scene, 2);
        Children.Add(Scene);

        doc.Loaded += OnLoaded;
        if (doc.Source is not null || doc.LoadError is not null) OnLoaded(doc, EventArgs.Empty);
    }

    /// <summary>The facts pane.</summary>
    public AssetDetailsPane Facts { get; }

    /// <summary>The 3D preview.</summary>
    public LvlSceneView Scene { get; }

    private void OnLoaded(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (_doc.LoadError is { } error)
        {
            Scene.ShowMessage("Cannot read " + _doc.DisplayName + ".", error);
            Facts.ShowDetails(new AssetDetails(_doc.DisplayName, [new AssetDetailRow("File", "Problem", error, Flagged: true)], [error]));
            return;
        }
        if (_doc.Source is not { } source) return;
        // the document's resolver: its folder, the rest of its packfile for a work copy, then the game data (and a Red
        // Faction II level's .peg packs first; an N-Gage level's 16-bit textures made opaque)
        var resolver = _module.ViewResolverFor(_doc, source);
        var siblings = AssetSiblings.For(_module.ShellContext, _doc.FilePath);
        Func<string, Cairn.Formats.Rfl.AssetPresence>? presence;
        if (source.Level is { IsRf2: true })
        {
            // a Red Faction II level's textures are in its .peg packs and its install's shared ones; nothing else is checked
            string? install = _module.Rf2InstallOf(_doc);
            var packs = LvlTexturePacks.PacksFor(source.Name, siblings is null ? _doc.FilePath : null, siblings, install);
            presence = LvlFacts.Rf2Presence(packs, install is null ? [] : LvlTexturePacks.SharedPacks(install).Select(p => p.Name));
        }
        else presence = LvlFacts.Presence(siblings, _doc.Resolver);
        Scene.Show(source, resolver);
        Facts.Show(ct => LvlFacts.Build(source, presence, ct));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _doc.Loaded -= OnLoaded;
        Scene.Dispose();
        Facts.Dispose();
    }
}
