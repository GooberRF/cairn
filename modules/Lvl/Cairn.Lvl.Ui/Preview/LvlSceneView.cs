using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Cairn.Assets;
using Cairn.Lvl.Conversion;
using Cairn.Lvl.Model;
using Cairn.Previews;
using Cairn.Ui.Services;
using Cairn.Viewport;

namespace Cairn.Lvl.Ui.Preview;

/// <summary>
/// The 3D preview of a level or group file: a toolbar (compiled geometry or brushes, face classes, object markers,
/// lighting, orbit or fly camera, frame), the viewport with one frozen mesh per texture (built off the UI thread, then
/// textured as the textures decode), object markers, and the picked face's or object's details. Shared by the
/// document tab and the packfile's preview; it never touches a document, the undo history or (unless given) the
/// settings.
/// </summary>
public sealed class LvlSceneView : DockPanel, IDisposable
{
    private readonly ViewportSurface _surface = new();
    private readonly FlyCameraController _fly;
    private readonly LvlMarkerLayer _markerLayer;
    private readonly LvlMarkerMeshes _markerMeshes;
    private readonly LvlSelectionLayer _selectionLayer;
    private readonly Model3DGroup _opaque = new(), _transparent = new();
    private readonly WireframeLines _edges = new();
    private readonly StatisticsPanel _statsPanel = new();
    private readonly ViewportDisplaySettingsBase _display = new("lvl.viewport") { ShowGrid = true };
    private readonly TextureService _textures;
    private readonly LvlSettings? _settings;
    private readonly bool _remember;
    private readonly Grid _stage = new();
    private readonly Border _messageBox = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(14, 10, 14, 10), CornerRadius = new CornerRadius(4), MaxWidth = 460 };
    private readonly StackPanel _messagePanel = new();
    private readonly Border _infoBox = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8), Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(4), MaxWidth = 560, Visibility = Visibility.Collapsed };
    private readonly TextBox _infoText = new() { IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, TextWrapping = TextWrapping.Wrap };
    private readonly ToggleButton _compiled, _brushes, _flyToggle;
    private readonly Button _faces, _objects, _lighting, _view;
    private readonly TextBlock _stats;
    private readonly WrapPanel _toolbarPanel;
    private readonly Dictionary<string, TextureImage?> _textureCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(LvlPreviewBatch Batch, GeometryModel3D Model)> _models = [];
    private LvlSource? _source;
    private AssetResolver? _resolver;
    private IReadOnlyList<LvlMarker> _markers = [];
    private LvlSceneSettings _sceneSettings;
    private LvlLighting _lightingMode;
    private LvlEdges _edgesMode;
    private LvlPreviewScene? _edgesFor;
    private bool _bothSides, _showStatistics, _framed, _largeConfirmed, _disposed, _updatingToggles;
    private CancellationTokenSource? _buildCts;
    private int _generation, _pendingTextures;
    private Color _ambient = Colors.White;

    /// <param name="textures">The texture decoder and cache to use.</param>
    /// <param name="settings">The module's settings (the toggles' starting values and the size limit), or null for the defaults.</param>
    /// <param name="remember">True to write toggle changes back to <paramref name="settings"/> (a document tab outside diagnostic runs).</param>
    public LvlSceneView(TextureService textures, LvlSettings? settings, bool remember)
    {
        _textures = textures ?? throw new ArgumentNullException(nameof(textures));
        _settings = settings;
        _remember = remember && settings is not null;
        _sceneSettings = settings is null ? LvlSceneSettings.Default
            : LvlSceneSettings.Default with { HiddenClasses = settings.HiddenFaceClasses, ShowNormalFaces = settings.ShowNormalFaces };
        _lightingMode = settings?.Lighting ?? LvlLighting.FullBright;
        _bothSides = settings?.BothSides ?? false;
        _edgesMode = settings?.Edges ?? LvlEdges.Off;
        _showStatistics = settings?.ShowStatistics ?? false;
        TriangleLimit = settings?.TriangleLimit ?? LvlSettings.DefaultTriangleLimit;
        SetResourceReference(BackgroundProperty, "Viewport.Background");
        AutomationProperties.SetName(this, "Level preview");

        _surface.Display = _display;
        _markerMeshes = new LvlMarkerMeshes(this) { Shown = settings?.Markers ?? LvlMarkerKinds.Default };
        ArrangeModels();
        _surface.Render += (_, _) =>
        {
            _markerMeshes.Update(_surface.Camera, _surface.RenderSize);
            UpdateEdges();
        };
        _surface.ToolTip = "Left-drag: orbit · Middle or Shift+left-drag: pan · Wheel: zoom · Click: show a face or object · F: frame · V: fly camera";
        _markerLayer = new LvlMarkerLayer(_surface) { Shown = _markerMeshes.Shown };
        _selectionLayer = new LvlSelectionLayer(_surface);
        _surface.OverlayLayers.Add(_selectionLayer);
        _surface.OverlayLayers.Add(_markerLayer);
        _surface.FrameRequested += (_, _) => Frame();
        _surface.ResetRequested += (_, _) =>
        {
            FlyMode = false;
            _surface.Camera.Yaw = 35;
            _surface.Camera.Pitch = 35;
            Frame();
        };
        _surface.Clicked += (_, p) => PickAt(p);
        _fly = new FlyCameraController(_surface);
        _fly.Clicked += (_, p) => PickAt(p);
        _fly.EnabledChanged += (_, _) => { if (_flyToggle!.IsChecked != _fly.IsEnabled) _flyToggle.IsChecked = _fly.IsEnabled; };

        _compiled = Toggle("Compiled", "Show the compiled geometry: the rooms and movers the game draws", (_, _) => ChooseGeometry(true));
        _brushes = Toggle("Brushes", "Show the editor's brushes (what the level editor works on)", (_, _) => ChooseGeometry(false));
        _faces = MenuButton("Faces", "Choose which face classes are shown (portal and invisible faces are not drawn by the game)", BuildFacesMenu);
        _objects = MenuButton("Objects", "Choose which object markers are shown", BuildObjectsMenu);
        _lighting = MenuButton("Lighting", "Choose how faces are lit (lightmaps are not drawn)", BuildLightingMenu);
        _view = MenuButton("View", "Draw the faces' edges, and show the statistics", BuildViewMenu);
        _flyToggle = Toggle("Fly", "Fly camera: drag to look around, W A S D to move, Q and E for down and up, Shift faster, Ctrl slower (V)", (_, _) => { if (!_updatingToggles) FlyMode = _flyToggle!.IsChecked == true; });
        var frame = PreviewUi.Button("Frame", "Show the whole level (F)", (_, _) => Frame());
        _stats = PreviewUi.Secondary("");
        AutomationProperties.SetName(_stats, "Preview statistics");
        var bar = PreviewUi.Toolbar(_compiled, _brushes, PreviewUi.Divider(), _faces, _objects, _lighting, _view, PreviewUi.Divider(), _flyToggle, frame, PreviewUi.Divider(), _stats);
        _toolbarPanel = (WrapPanel)bar.Child;
        SetDock(bar, Dock.Top);
        Children.Add(bar);

        _messageBox.SetResourceReference(Border.BackgroundProperty, "Viewport.Notice");
        _messageBox.Child = _messagePanel;
        AutomationProperties.SetName(_messageBox, "Preview message");
        _infoBox.SetResourceReference(Border.BackgroundProperty, "Viewport.Notice");
        _infoText.SetResourceReference(Control.ForegroundProperty, "Viewport.NoticeText");
        AutomationProperties.SetName(_infoText, "Picked face or object");
        var close = new Button { Content = "×", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top, ToolTip = "Clear the selection" };
        close.SetResourceReference(StyleProperty, "ToolButton");
        AutomationProperties.SetName(close, "Clear the selection");
        close.Click += (_, _) => ClearSelection();
        var infoPanel = new DockPanel();
        SetDock(close, Dock.Right);
        infoPanel.Children.Add(close);
        infoPanel.Children.Add(_infoText);
        _infoBox.Child = infoPanel;
        _stage.Children.Add(_surface);
        _stage.Children.Add(_infoBox);
        _stage.Children.Add(_statsPanel);
        _stage.Children.Add(_messageBox);
        Children.Add(_stage);

        PreviewKeyDown += OnKey;
        UpdateToggles();
        ApplyLighting();
        ShowMessage("Nothing to show yet.");
    }

    /// <summary>The viewport (self-tests drive its camera).</summary>
    public ViewportSurface Surface => _surface;

    /// <summary>The fly camera.</summary>
    public FlyCameraController Fly => _fly;

    /// <summary>Above this many triangles the view asks before building.</summary>
    public int TriangleLimit { get; set; }

    /// <summary>What is shown: compiled geometry or brushes, and which face classes.</summary>
    internal LvlSceneSettings SceneSettings => _sceneSettings;

    /// <summary>The built preview, or null (not built yet, too large, or failed).</summary>
    internal LvlPreviewScene? Scene { get; private set; }

    /// <summary>The triangles the built scene draws (0 before it is built); for self-tests outside this module.</summary>
    public int TrianglesDrawn => Scene?.Triangles ?? 0;

    /// <summary>The textures the built scene uses (0 before it is built); for self-tests outside this module.</summary>
    public int TexturesUsed => Scene?.Textures.Count ?? 0;

    /// <summary>The source shown, or null.</summary>
    public LvlSource? Source => _source;

    /// <summary>Completes when the current build (and its textures) is done.</summary>
    public Task Building { get; private set; } = Task.CompletedTask;

    /// <summary>The message shown over the view (loading, too large, failed, empty), or null.</summary>
    public string? Message => _messageBox.Visibility == Visibility.Visible ? string.Join(" ", _messagePanel.Children.OfType<TextBlock>().Select(t => t.Text)) : null;

    /// <summary>True while the view waits for "Build the preview" (the level is larger than <see cref="TriangleLimit"/>).</summary>
    public bool IsWaitingForConfirmation { get; private set; }

    /// <summary>The picked face's or object's details, or null.</summary>
    public string? SelectionText => _infoBox.Visibility == Visibility.Visible ? _infoText.Text : null;

    /// <summary>The textures found and decoded so far.</summary>
    public int TexturesLoaded => _textureCache.Values.Count(t => t is not null);

    /// <summary>The texture names that could not be found or decoded.</summary>
    public IReadOnlyList<string> MissingTextures => [.. _textureCache.Where(p => p.Value is null).Select(p => p.Key).Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The object markers of the source.</summary>
    internal IReadOnlyList<LvlMarker> Markers => _markers;

    /// <summary>The markers shown.</summary>
    internal LvlMarkerKinds MarkerKinds
    {
        get => _markerLayer.Shown;
        set
        {
            _markerLayer.Shown = _markerMeshes.Shown = value & LvlMarkerKinds.All;
            if (_remember) _settings!.Markers = _markerLayer.Shown;
            if (_markerLayer.Selected is { } selected && (selected.Kind & _markerLayer.Shown) == 0) ClearSelection();
            _surface.Invalidate();
        }
    }

    /// <summary>The markers drawn in the last frame.</summary>
    internal int MarkersDrawn => _markerMeshes.Drawn;

    /// <summary>How faces are lit.</summary>
    public LvlLighting Lighting
    {
        get => _lightingMode;
        set
        {
            if (_lightingMode == value) return;
            _lightingMode = value;
            if (_remember) _settings!.Lighting = value;
            ApplyLighting();
            ApplyMaterials();
        }
    }

    /// <summary>True to draw both sides of each face.</summary>
    public bool BothSides
    {
        get => _bothSides;
        set
        {
            if (_bothSides == value) return;
            _bothSides = value;
            if (_remember) _settings!.BothSides = value;
            ApplyMaterials();
        }
    }

    /// <summary>Whether the faces' edges are drawn (over the faces, or alone).</summary>
    public LvlEdges Edges
    {
        get => _edgesMode;
        set
        {
            if (_edgesMode == value) return;
            _edgesMode = value;
            if (_remember) _settings!.Edges = value;
            EnsureEdges();
            ArrangeModels();
        }
    }

    /// <summary>True to show the counts in a corner.</summary>
    public bool ShowStatistics
    {
        get => _showStatistics;
        set
        {
            if (_showStatistics == value) return;
            _showStatistics = value;
            if (_remember) _settings!.ShowStatistics = value;
            UpdateStats();
        }
    }

    /// <summary>The edges drawn (self-tests).</summary>
    internal WireframeLines EdgeLines => _edges;

    /// <summary>True while the faces are drawn (not edges only).</summary>
    internal bool FacesShown => _surface.Models.Contains(_opaque);

    /// <summary>The statistics shown (empty while the panel is off).</summary>
    internal IReadOnlyList<(string Label, string Value)> StatisticsRows => _statsPanel.Visibility == Visibility.Visible ? _statsPanel.Rows : [];

    /// <summary>True while the fly camera is on.</summary>
    public bool FlyMode
    {
        get => _fly.IsEnabled;
        set
        {
            _fly.IsEnabled = value;
            _updatingToggles = true;
            _flyToggle.IsChecked = value;
            _updatingToggles = false;
            if (value) _surface.Focus();
        }
    }

    /// <summary>Adds an element at the end of the toolbar (the document's Convert button).</summary>
    public void AddToolbarElement(UIElement element)
    {
        _toolbarPanel.Children.Add(PreviewUi.Divider());
        _toolbarPanel.Children.Add(element);
    }

    /// <summary>Shows a message instead of the preview (the source is still being read, or could not be).</summary>
    public void ShowMessage(string text, string? detail = null, UIElement? extra = null)
    {
        _messagePanel.Children.Clear();
        var heading = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Viewport.NoticeText");
        _messagePanel.Children.Add(heading);
        if (detail is not null)
        {
            var more = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            more.SetResourceReference(TextBlock.ForegroundProperty, "Viewport.NoticeText");
            _messagePanel.Children.Add(more);
        }
        if (extra is not null) _messagePanel.Children.Add(extra);
        _messageBox.Visibility = Visibility.Visible;
    }

    private void HideMessage() => _messageBox.Visibility = Visibility.Collapsed;

    /// <summary>
    /// Shows <paramref name="source"/>, with textures through <paramref name="resolver"/> (the document's resolver: its
    /// folder, its packfile's other entries, the game data). The ambient light is the level's.
    /// </summary>
    public void Show(LvlSource source, AssetResolver? resolver)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_disposed) return;
        _source = source;
        _resolver = resolver;
        _framed = false;
        _largeConfirmed = false;
        _textureCache.Clear();
        _markers = LvlMarkers.From(source);
        _markerLayer.Markers = _markerMeshes.Markers = _markers;
        _ambient = source.Level?.Properties is { } p ? Color.FromRgb(p.AmbientColor.R, p.AmbientColor.G, p.AmbientColor.B) : Colors.White;
        // a level without compiled geometry (or a group file) shows its brushes
        if (!source.HasCompiledGeometry) _sceneSettings = _sceneSettings with { Compiled = false };
        else if (source.Level is not null && !source.HasBrushes) _sceneSettings = _sceneSettings with { Compiled = true };
        UpdateToggles();
        Rebuild();
    }

    /// <summary>Changes what is shown and rebuilds the meshes.</summary>
    internal void SetSceneSettings(LvlSceneSettings settings)
    {
        if (settings == _sceneSettings) return;
        _sceneSettings = settings;
        if (_remember)
        {
            _settings!.HiddenFaceClasses = settings.HiddenClasses;
            _settings.ShowNormalFaces = settings.ShowNormalFaces;
        }
        UpdateToggles();
        Rebuild();
    }

    /// <summary>Builds the preview even above <see cref="TriangleLimit"/> (the "Build the preview" button).</summary>
    public void BuildAnyway()
    {
        _largeConfirmed = true;
        Rebuild();
    }

    private void ChooseGeometry(bool compiled)
    {
        if (_updatingToggles) return;
        SetSceneSettings(_sceneSettings with { Compiled = compiled });
        UpdateToggles();
    }

    private void UpdateToggles()
    {
        _updatingToggles = true;
        bool level = _source?.Level is not null;
        _compiled.Visibility = _brushes.Visibility = level || _source is null ? Visibility.Visible : Visibility.Collapsed;
        _compiled.IsEnabled = _source?.HasCompiledGeometry ?? true;
        _brushes.IsEnabled = _source?.HasBrushes ?? true;
        _compiled.IsChecked = _sceneSettings.Compiled;
        _brushes.IsChecked = !_sceneSettings.Compiled;
        _flyToggle.IsChecked = _fly.IsEnabled;
        _updatingToggles = false;
    }

    // ── building ──────────────────────────────────────────────────────────────────────────────────────────────

    private void Rebuild()
    {
        if (_source is not { } source || _disposed) return;
        _buildCts?.Cancel();
        var cts = _buildCts = new CancellationTokenSource();
        int generation = ++_generation;
        IsWaitingForConfirmation = false;
        long triangles = source.TriangleCount(_sceneSettings.Compiled && source.HasCompiledGeometry);
        if (triangles > TriangleLimit && !_largeConfirmed)
        {
            ClearModels();
            Scene = null;
            IsWaitingForConfirmation = true;
            var build = PreviewUi.Button("Build the preview", "Build the 3D preview of the whole level anyway", (_, _) => BuildAnyway());
            build.SetResourceReference(StyleProperty, "PushButton");
            build.HorizontalAlignment = HorizontalAlignment.Left;
            build.Margin = new Thickness(0, 10, 0, 0);
            ShowMessage(string.Format(CultureInfo.CurrentCulture, "This {0} has {1:N0} triangles.", source.IsGroupFile ? "group file" : "level", triangles),
                string.Format(CultureInfo.CurrentCulture, "The preview builds up to {0:N0} without asking; building a larger one takes longer and uses more memory. Hiding face classes or switching between compiled geometry and brushes changes the count.", TriangleLimit),
                build);
            _stats.Text = string.Format(CultureInfo.CurrentCulture, "{0:N0} triangles (not built)", triangles);
            Building = Task.CompletedTask;
            if (!_framed)
            {
                // framed on the level's extent already, so the markers show where things are
                _framed = true;
                _surface.Camera.Yaw = 35;
                _surface.Camera.Pitch = 35;
                Frame();
            }
            return;
        }
        ShowMessage("Building the preview…");
        var settings = _sceneSettings;
        Building = BuildAsync(source, settings, generation, cts.Token);
    }

    private async Task BuildAsync(LvlSource source, LvlSceneSettings settings, int generation, CancellationToken ct)
    {
        LvlPreviewScene scene;
        using (BusyTracker.Begin("level preview"))
        {
            try
            {
                scene = await Task.Run(() => LvlSceneBuilder.Build(source, settings, ct), ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (generation == _generation && !_disposed) ShowMessage("The preview could not be built.", ex.Message);
                return;
            }
            if (generation != _generation || _disposed) return;
            ApplyScene(scene);
        }
        await TexturesDone(generation);
    }

    private TaskCompletionSource? _texturesDone;

    private Task TexturesDone(int generation) => generation == _generation && _texturesDone is { } done ? done.Task : Task.CompletedTask;

    private void ApplyScene(LvlPreviewScene scene)
    {
        ClearModels();
        ClearSelection();
        Scene = scene;
        foreach (var batch in scene.Batches)
        {
            var model = new GeometryModel3D(batch.Mesh, null);
            _models.Add((batch, model));
            (batch.Kind is LvlBatchKind.Alpha or LvlBatchKind.Liquid ? _transparent : _opaque).Children.Add(model);
        }
        EnsureEdges();
        ApplyMaterials();
        if (scene.IsEmpty)
        {
            bool filtered = _source is { } s && s.TriangleCount(_sceneSettings.Compiled && s.HasCompiledGeometry) > 0;
            ShowMessage(filtered ? "No face is shown." : _sceneSettings.Compiled ? "This level has no compiled geometry." : "There are no brushes to show.",
                filtered ? "Every face is in a hidden class: choose more under Faces." : null);
        }
        else HideMessage();
        UpdateStats();
        if (!_framed)
        {
            _framed = true;
            _surface.Camera.Yaw = 35;
            _surface.Camera.Pitch = 35;
            Frame();
        }
        _surface.Invalidate();
        LoadTextures(scene, _generation);
    }

    private void ClearModels()
    {
        _opaque.Children.Clear();
        _transparent.Children.Clear();
        _models.Clear();
        _edges.Clear();
        _edgesFor = null;
        _statsPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>The faces (unless edges only), the object markers, then the edges (when drawn).</summary>
    private void ArrangeModels()
    {
        _surface.Models.Clear();
        bool faces = _edgesMode != LvlEdges.Only;
        if (faces) _surface.Models.Add(_opaque);
        _surface.Models.Add(_markerMeshes.Root);
        if (faces) _surface.Models.Add(_transparent);
        if (_edgesMode != LvlEdges.Off) _surface.Models.Add(_edges.Root);
    }

    /// <summary>Makes the edges of the built scene the first time they are drawn.</summary>
    private void EnsureEdges()
    {
        if (_edgesMode == LvlEdges.Off || Scene is not { } scene || ReferenceEquals(_edgesFor, scene)) return;
        _edges.Clear();
        _edges.Add(scene.EdgePoints);
        _edgesFor = scene;
        _surface.Invalidate();
    }

    /// <summary>Turns the edges to face the camera (each frame the view is redrawn).</summary>
    private void UpdateEdges()
    {
        if (_edgesMode == LvlEdges.Off || _edges.EdgeCount == 0) return;
        _edges.Color = _edgesMode == LvlEdges.OverFaces ? WireframeLines.OverFacesColor
            : WireframeLines.ContrastWith(_surface.Background is SolidColorBrush b ? b.Color : Colors.Black);
        _edges.Update(_surface.Camera, _surface.RenderSize);
    }

    private void UpdateStats()
    {
        if (Scene is not { } scene)
        {
            _statsPanel.Visibility = Visibility.Collapsed;
            return;
        }
        var text = new StringBuilder();
        text.Append(string.Format(CultureInfo.CurrentCulture, "{0:N0} triangles · {1:N0} textures", scene.Triangles, scene.Textures.Count));
        int missing = scene.Textures.Count(t => _textureCache.TryGetValue(t, out var image) && image is null);
        if (missing > 0) text.Append(string.Format(CultureInfo.CurrentCulture, " ({0:N0} missing)", missing));
        if (_pendingTextures > 0) text.Append(string.Format(CultureInfo.CurrentCulture, " · loading {0:N0}", _pendingTextures));
        _stats.Text = text.ToString();
        _stats.ToolTip = missing > 0 ? "Not found: " + string.Join(", ", MissingTextures.Take(20)) + (missing > 20 ? ", ..." : "") : null;
        _statsPanel.Visibility = _showStatistics ? Visibility.Visible : Visibility.Collapsed;
        if (!_showStatistics) return;
        var rows = new List<(string, string)>
        {
            ("Faces", LvlSceneBuilder.N(scene.Faces)),
            ("Triangles", LvlSceneBuilder.N(scene.Triangles)),
            ("Vertices", LvlSceneBuilder.N(scene.Vertices)),
            ("Edges", LvlSceneBuilder.N(scene.EdgePoints.Length / 2)),
        };
        if (_sceneSettings.Compiled && _source?.HasCompiledGeometry == true) rows.Add(("Rooms", LvlSceneBuilder.N(scene.Rooms)));
        else rows.Add(("Brushes", LvlSceneBuilder.N(scene.Brushes)));
        rows.Add(("Movers", LvlSceneBuilder.N(scene.Movers)));
        rows.Add(("Textures", LvlSceneBuilder.N(scene.Textures.Count) + (missing > 0 ? string.Format(CultureInfo.CurrentCulture, " ({0:N0} missing)", missing) : "")));
        rows.Add(("Objects", LvlSceneBuilder.N(_markers.Count)));
        _statsPanel.Show(rows);
    }

    // ── camera ────────────────────────────────────────────────────────────────────────────────────────────────

    private (Vector3 Min, Vector3 Max) Bounds()
    {
        if (Scene is { IsEmpty: false } scene) return (scene.Min, scene.Max);
        if (_source?.Bounds(_sceneSettings.Compiled && _source.HasCompiledGeometry) is { } extent) return extent;
        if (_markers.Count > 0)
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var m in _markers) { min = Vector3.Min(min, m.Position); max = Vector3.Max(max, m.Position); }
            return (min - Vector3.One, max + Vector3.One);
        }
        return (new Vector3(-5), new Vector3(5));
    }

    /// <summary>Frames everything shown; while flying, the eye moves back to see it all and keeps flying.</summary>
    public void Frame()
    {
        var (min, max) = Bounds();
        double aspect = _surface.ActualWidth > 1 && _surface.ActualHeight > 1 ? _surface.ActualWidth / _surface.ActualHeight : 1.6;
        _surface.Camera.FrameBox(min, max, aspect);
        float radius = Math.Max(1, (max - min).Length() / 2);
        // While flying the orbit target stays a short way ahead (the clip planes follow it), and the speed suits the size.
        _fly.LookDistance = Math.Clamp(radius / 25, 1, 200);
        _fly.Speed = Math.Clamp(radius / 10, 5, 150);
        if (_fly.IsEnabled) _fly.Reanchor();
        _surface.Invalidate();
    }

    // ── textures and materials ────────────────────────────────────────────────────────────────────────────────

    private void LoadTextures(LvlPreviewScene scene, int generation)
    {
        _texturesDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wanted = scene.Textures.Where(t => !_textureCache.ContainsKey(t)).ToList();
        if (_resolver is not { } resolver || wanted.Count == 0)
        {
            foreach (var name in wanted) _textureCache[name] = null;
            _pendingTextures = 0;
            UpdateStats();
            ApplyMaterials();
            _texturesDone.TrySetResult();
            return;
        }
        _pendingTextures = wanted.Count;
        UpdateStats();
        var done = _texturesDone;
        foreach (var name in wanted)
        {
            var busy = BusyTracker.Begin("level texture " + name);
            var task = _textures.GetAsync(resolver, name);
            task.ContinueWith(t => Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (generation != _generation || _disposed) return;
                    _textureCache[name] = t.IsCompletedSuccessfully ? t.Result : null;
                    _pendingTextures--;
                    ApplyMaterials(name);
                    UpdateStats();
                }
                finally
                {
                    busy.Dispose();
                    if (generation != _generation || _disposed || _pendingTextures <= 0) done.TrySetResult();
                }
            }), TaskScheduler.Default);
        }
    }

    private void ApplyLighting() => _display.FullBright = _lightingMode != LvlLighting.Shaded;

    private readonly Dictionary<(string, LvlBatchKind), Material> _materials = [];

    /// <summary>Gives every model (or those of one texture) its material for the current lighting and sides.</summary>
    private void ApplyMaterials(string? texture = null)
    {
        if (texture is null) _materials.Clear();
        foreach (var (batch, model) in _models)
        {
            if (texture is not null && !batch.Texture.Equals(texture, StringComparison.OrdinalIgnoreCase)) continue;
            var material = MaterialFor(batch);
            model.Material = material;
            model.BackMaterial = _bothSides ? material : null;
            // A texture with transparent pixels is drawn after the opaque faces.
            if (batch.Kind is LvlBatchKind.Opaque or LvlBatchKind.FullBright && _textureCache.TryGetValue(batch.Texture, out var image) && image?.HasAlpha == true
                && _opaque.Children.Remove(model))
                _transparent.Children.Add(model);
        }
        _surface.Invalidate();
    }

    private Material MaterialFor(LvlPreviewBatch batch)
    {
        string name = batch.Texture;
        var key = (name.ToLowerInvariant(), batch.Kind);
        if (_materials.TryGetValue(key, out var cached) && _textureCache.ContainsKey(name)) return cached;
        Brush brush;
        if (name.Length > 0 && _textureCache.TryGetValue(name, out var image) && image is not null)
        {
            var imageBrush = new ImageBrush(image.Image)
            {
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, 1, 1),
                TileMode = TileMode.Tile,
                Stretch = Stretch.Fill,
                Opacity = batch.Kind == LvlBatchKind.Liquid ? 0.65 : 1,
            };
            RenderOptions.SetBitmapScalingMode(imageBrush, BitmapScalingMode.Linear);
            brush = imageBrush;
        }
        else
        {
            var colour = TryFindResource(name.Length == 0 ? "Viewport.FlatMeshColor" : "Viewport.MissingTextureColor") is Color c ? c : Color.FromRgb(160, 160, 170);
            brush = new SolidColorBrush(colour) { Opacity = batch.Kind == LvlBatchKind.Liquid ? 0.65 : 1 };
        }
        brush.Freeze();
        Material material;
        bool ownColours = batch.Kind == LvlBatchKind.FullBright;
        if (_lightingMode == LvlLighting.Shaded && ownColours)
        {
            // Unlit in a lit view: black diffuse plus the texture as emission.
            material = new MaterialGroup { Children = { new DiffuseMaterial(Brushes.Black), new EmissiveMaterial(brush) } };
        }
        else
        {
            var diffuse = new DiffuseMaterial(brush);
            if (_lightingMode == LvlLighting.LevelAmbient && !ownColours) diffuse.AmbientColor = _ambient;
            material = diffuse;
        }
        material.Freeze();
        if (_textureCache.ContainsKey(name) || name.Length == 0) _materials[key] = material;
        return material;
    }

    // ── menus ─────────────────────────────────────────────────────────────────────────────────────────────────

    private void BuildFacesMenu(ContextMenu menu)
    {
        var classes = new List<RfFaceClass> { RfFaceClass.Normal };
        classes.AddRange(RfFaceClasses.All);
        foreach (var c in classes)
        {
            string text = c == RfFaceClass.Normal ? "Ordinary faces" : RfFaceClasses.DisplayName(c);
            var item = Check(text, FaceClassTip(c), _sceneSettings.Shows(c));
            var faceClass = c;
            item.Click += (_, _) => SetSceneSettings(_sceneSettings.With(faceClass, item.IsChecked));
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var both = Check("Both sides of each face", "Draw the back of each face too (the game draws the front only)", _bothSides);
        both.Click += (_, _) => BothSides = both.IsChecked;
        menu.Items.Add(both);
    }

    private static string FaceClassTip(RfFaceClass c) => c switch
    {
        RfFaceClass.Normal => "Faces in none of the classes below",
        RfFaceClass.Portal => "Faces that join two rooms (the game does not draw them)",
        RfFaceClass.Detail => "Faces of detail brushes",
        RfFaceClass.Sky => "Faces that show the sky",
        RfFaceClass.Invisible => "Faces that collide but are not drawn",
        RfFaceClass.Hole => "Detail faces whose texture has holes shots pass through",
        RfFaceClass.LiquidSurface => "The surfaces of liquid rooms (drawn see-through here)",
        RfFaceClass.Alpha => "Faces drawn with their texture's transparency",
        RfFaceClass.FullBright => "Faces drawn without lighting",
        RfFaceClass.Scrolling => "Faces whose texture scrolls",
        _ => RfFaceClasses.DisplayName(c),
    };

    private void BuildObjectsMenu(ContextMenu menu)
    {
        foreach (var (kind, text) in LvlMarkers.Kinds)
        {
            int count = _markers.Count(m => m.Kind == kind);
            var item = Check(string.Format(CultureInfo.CurrentCulture, "{0} ({1:N0})", text, count), "Show a marker for each of these objects", (MarkerKinds & kind) != 0);
            item.Icon = new System.Windows.Shapes.Ellipse { Width = 9, Height = 9 };
            ((System.Windows.Shapes.Ellipse)item.Icon).SetResourceReference(System.Windows.Shapes.Shape.FillProperty, LvlMarkerLayer.BrushKey(kind));
            var k = kind;
            item.Click += (_, _) => MarkerKinds = item.IsChecked ? MarkerKinds | k : MarkerKinds & ~k;
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var all = new MenuItem { Header = "Show all", ToolTip = "Show every object marker" };
        all.Click += (_, _) => MarkerKinds = LvlMarkerKinds.All;
        var none = new MenuItem { Header = "Hide all", ToolTip = "Hide every object marker" };
        none.Click += (_, _) => MarkerKinds = LvlMarkerKinds.None;
        menu.Items.Add(all);
        menu.Items.Add(none);
    }

    private void BuildLightingMenu(ContextMenu menu)
    {
        void Add(LvlLighting mode, string text, string tip)
        {
            var item = Check(text, tip, _lightingMode == mode);
            item.StaysOpenOnClick = false;
            item.Click += (_, _) => Lighting = mode;
            menu.Items.Add(item);
        }
        Add(LvlLighting.FullBright, "Full bright", "Every face at its texture's own colours");
        Add(LvlLighting.LevelAmbient, "Level ambient light", string.Format(CultureInfo.CurrentCulture, "Every face at the level's ambient light ({0}, {1}, {2}); full-bright faces at their own colours", _ambient.R, _ambient.G, _ambient.B));
        Add(LvlLighting.Shaded, "Shaded", "A light from the camera's side, to read the shapes");
    }

    private void BuildViewMenu(ContextMenu menu)
    {
        void Add(LvlEdges mode, string text, string tip)
        {
            var item = Check(text, tip, _edgesMode == mode);
            item.StaysOpenOnClick = false;
            item.Click += (_, _) => Edges = mode;
            menu.Items.Add(item);
        }
        Add(LvlEdges.Off, "Faces", "Draw the faces");
        Add(LvlEdges.OverFaces, "Faces and edges", "Draw the faces with their edges over them");
        Add(LvlEdges.Only, "Edges only", "Draw only the faces' edges (a wireframe)");
        menu.Items.Add(new Separator());
        var stats = Check("Statistics", "Show the counts in a corner: faces, triangles, vertices, edges, rooms or brushes, movers, textures, objects", _showStatistics);
        stats.StaysOpenOnClick = false;
        stats.Click += (_, _) => ShowStatistics = stats.IsChecked;
        menu.Items.Add(stats);
    }

    private static MenuItem Check(string text, string tip, bool isChecked)
    {
        var item = new MenuItem { Header = text.Replace("_", "__", StringComparison.Ordinal), IsCheckable = true, IsChecked = isChecked, StaysOpenOnClick = true, ToolTip = tip };
        AutomationProperties.SetName(item, text);
        return item;
    }

    private static ToggleButton Toggle(string text, string tip, RoutedEventHandler click)
    {
        var t = new ToggleButton { Content = text, ToolTip = tip, Margin = new Thickness(0, 0, 2, 0) };
        t.SetResourceReference(StyleProperty, "ToolToggle");
        AutomationProperties.SetName(t, text);
        t.Click += click;
        return t;
    }

    private Button MenuButton(string text, string tip, Action<ContextMenu> fill)
    {
        var button = PreviewUi.Button(text + " ▾", tip, (_, _) => { });
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
            fill(menu);
            menu.IsOpen = true;
        };
        return button;
    }

    // ── picking ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Picks the object marker or face under <paramref name="point"/> (surface coordinates) and shows its details.</summary>
    public void PickAt(Point point)
    {
        var face = Scene?.Pick(_surface.RayAt(point), _bothSides);
        // a marker wins unless a face hides it (the markers are drawn in the scene, so a wall in front hides them)
        if (_markerLayer.HitTest(point) is { } marker && (face is null || marker.Distance <= face.Value.Distance + 0.5f))
        {
            ShowMarker(marker.Marker);
            return;
        }
        if (face is not { } hit)
        {
            ClearSelection();
            return;
        }
        ShowFace(hit.Target);
    }

    /// <summary>Clears the picked face or object.</summary>
    public void ClearSelection()
    {
        _selectionLayer.Face = null;
        _selectionLayer.Context = [];
        _markerLayer.Selected = null;
        _infoBox.Visibility = Visibility.Collapsed;
        _surface.Invalidate();
    }

    private void ShowMarker(LvlMarker marker)
    {
        _selectionLayer.Face = null;
        _selectionLayer.Context = [];
        _markerLayer.Selected = marker;
        _infoText.Text = marker.Title + Environment.NewLine + string.Join(Environment.NewLine, marker.Details);
        _infoBox.Visibility = Visibility.Visible;
        _surface.Invalidate();
    }

    private void ShowFace(LvlPickTarget target)
    {
        _markerLayer.Selected = null;
        _selectionLayer.Face = [.. target.Polygon.Corners.Select(c => c.Position)];
        _selectionLayer.Context = target.Part.Kind == LvlPartKind.Room ? []
            : [.. target.Part.Polygons.Where(p => !ReferenceEquals(p, target.Polygon)).Take(400).Select(p => p.Corners.Select(c => c.Position).ToArray())];
        _infoText.Text = Describe(target);
        _infoBox.Visibility = Visibility.Visible;
        _surface.Invalidate();
    }

    /// <summary>The picked face's details: what it belongs to, its texture, classes, flags, ids and scroll.</summary>
    internal string Describe(LvlPickTarget target)
    {
        var inv = CultureInfo.InvariantCulture;
        var part = target.Part;
        var polygon = target.Polygon;
        var face = polygon.Face;
        var lines = new List<string>();
        switch (part.Kind)
        {
            case LvlPartKind.Room:
                string room = part.RoomIndex >= 0 ? "Room " + part.RoomIndex.ToString(inv) : "Compiled geometry";
                if (part.Room is { } r)
                {
                    var traits = new List<string>();
                    if (r.IsSky) traits.Add("sky");
                    if (r.Liquid is { } liquid) traits.Add(liquid.LiquidType switch { 2 => "lava", 3 => "acid", _ => "water" });
                    if (r.IsSubroom) traits.Add("detail room");
                    if (r.IsCold) traits.Add("cold");
                    if (r.IsOutside) traits.Add("outside");
                    if (r.Life >= 0) traits.Add("life " + LvlMarkers.Num(r.Life));
                    if (traits.Count > 0) room += " (" + string.Join(", ", traits) + ")";
                }
                lines.Add(room + ", face " + FaceIdText(face));
                break;
            default:
                string what = part.Kind == LvlPartKind.Mover ? "Mover brush" : "Brush";
                lines.Add($"{what} {part.Uid.ToString(inv)}, face {FaceIdText(face)}");
                if (part.Brush is { } brush)
                    lines.Add($"Solid: {SolidText(brush)}; life {brush.Life.ToString(inv)}" + (brush.Alpine is { } alpine ? $"; Alpine {AlpineText(alpine)}" : ""));
                break;
        }
        lines.Add("Texture: " + (polygon.Texture.Length > 0 ? polygon.Texture + TextureState(polygon.Texture) : "(none)"));
        lines.Add("Classes: " + (polygon.Classes == RfFaceClass.Normal ? "ordinary face"
            : string.Join(", ", RfFaceClasses.All.Where(c => (polygon.Classes & c) != 0).Select(RfFaceClasses.DisplayName))));
        lines.Add($"Flags: 0x{face.RawFlags.ToString("X4", inv)}" + (FlagWords(face.Flags) is { Length: > 0 } words ? " (" + words + ")" : "")
            + $"; smoothing 0x{face.Smoothing.ToString("X", inv)}; {face.Corners.Length.ToString(inv)} corners");
        if (face.Scroll is { } scroll) lines.Add($"Scrolls {LvlMarkers.Num(scroll.X)}, {LvlMarkers.Num(scroll.Y)} repeats per second");
        if (face.HasSurface) lines.Add("Lightmapped (surface " + (face.Surface & 0xFFFF).ToString(inv) + ")");
        return string.Join(Environment.NewLine, lines);
    }

    private static string FaceIdText(RfFace face) => face.FaceId >= 0 ? "id " + face.FaceId.ToString(CultureInfo.InvariantCulture) : "(no id)";

    private string TextureState(string name) => _textureCache.TryGetValue(name, out var image)
        ? image is null ? " (not found)" : image.ResolvedName.Equals(name, StringComparison.OrdinalIgnoreCase) ? $" ({image.Location})" : $" (as {image.ResolvedName}, {image.Location})"
        : " (loading)";

    private static string SolidText(RfBrush brush)
    {
        var words = new List<string> { brush.IsAir ? "air" : "solid" };
        if (brush.IsPortal) words.Add("portal");
        if (brush.IsDetail) words.Add("detail");
        if ((brush.SolidFlags & RfSolidFlags.EmitsSteam) != 0) words.Add("emits steam");
        if ((brush.SolidFlags & RfSolidFlags.Rf2Geoable) != 0) words.Add("geoable");
        return string.Join(", ", words) + $" (0x{((uint)brush.SolidFlags).ToString("X2", CultureInfo.InvariantCulture)})";
    }

    private static string AlpineText(RfAlpineBrushData alpine)
    {
        var words = new List<string>();
        if ((alpine.Flags & RfAlpineBrushFlags.Geoable) != 0) words.Add("geoable");
        if ((alpine.Flags & RfAlpineBrushFlags.Breakable) != 0) words.Add("breakable");
        if ((alpine.Flags & RfAlpineBrushFlags.NoShadowCast) != 0) words.Add("casts no shadow");
        return words.Count == 0 ? "data" : string.Join(", ", words);
    }

    private static string FlagWords(RfFaceFlags f)
    {
        var words = new List<string>();
        void Word(RfFaceFlags flag, string word) { if ((f & flag) != 0) words.Add(word); }
        Word(RfFaceFlags.ShowSky, "sky");
        Word(RfFaceFlags.Mirrored, "mirrored");
        Word(RfFaceFlags.LiquidSurface, "liquid surface");
        Word(RfFaceFlags.Detail, "detail");
        Word(RfFaceFlags.ScrollTexture, "scrolls");
        Word(RfFaceFlags.FullBright, "full bright");
        Word(RfFaceFlags.HasAlpha, "alpha");
        Word(RfFaceFlags.HasHoles, "holes");
        Word(RfFaceFlags.Invisible, "invisible");
        int resolution = ((int)f >> 8) & 3;
        if (resolution != 0) words.Add("lightmap resolution " + resolution.ToString(CultureInfo.InvariantCulture));
        return string.Join(", ", words);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox || e.Handled) return;
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.None)
        {
            FlyMode = !FlyMode;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None && _infoBox.Visibility == Visibility.Visible)
        {
            ClearSelection();
            e.Handled = true;
        }
    }

    /// <summary>Stops building and loading, and releases the meshes.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _generation++;
        _buildCts?.Cancel();
        _texturesDone?.TrySetResult();
        _fly.Dispose();
        ClearModels();
        _textureCache.Clear();
        _materials.Clear();
        Scene = null;
        _source = null;
        GC.SuppressFinalize(this);
    }
}
