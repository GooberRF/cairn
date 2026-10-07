using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Ui.Services;
using Cairn.Rfa.Ui.ViewModels;
using Cairn.Rfa.Ui.Viewport;

namespace Cairn.Rfa.Ui.Preview;

/// <summary>
/// The scene and transport of one read-only preview: its own display toggles (skeleton off, never stored),
/// bone selection, texture cache and looping transport. Nothing here touches a document or the settings.
/// </summary>
internal sealed class RfaPreviewHost : ObservableObject, IViewportHost
{
    public RfaPreviewHost()
    {
        Display = new ViewportDisplaySettings { ShowSkeleton = false };
        Scene = new SceneViewModel(Display, new BoneSelection(), new TextureService());
        Playback = new PlaybackViewModel(() => TimeUnit.Frames, RfaTime.Base) { Loop = true };
        Playback.TimeChanged += (_, _) => Scene.SetAnimation(Clip, Playback.Time);
    }

    public SceneViewModel Scene { get; }
    public PlaybackViewModel Playback { get; }
    public ViewportDisplaySettings Display { get; }
    /// <summary>The clip playing, or null for a mesh preview.</summary>
    public RfaClip? Clip { get; set; }
}

/// <summary>
/// Read-only preview of a .v3m/.v3c mesh or an .rfa clip for other modules (the packfile preview pane): the
/// module's viewport over a <see cref="RfaPreviewHost"/>. A clip plays on a fitting mesh beside it (same packfile)
/// or else the mesh the clip documents would pick from the asset library, with a play button and a scrubber.
/// Textures resolve among the files beside the asset first. Parsing and the mesh load run off the UI
/// thread; failures show a one-line message. <see cref="Dispose"/> stops playback and detaches the viewport.
/// </summary>
public sealed class RfaPreview : Grid, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly AssetHost? _assets;
    private readonly AssetServices? _library;
    private readonly IAssetSiblings? _siblings;
    private readonly TextBlock _message;
    private RfaPreviewHost? _host;
    private ViewportControl? _viewport;
    private bool _waitingForLibrary;
    private bool _disposed;

    /// <param name="bytes">The whole file.</param>
    /// <param name="fileName">Its name (type, messages and the clip's table lookups).</param>
    /// <param name="assets">The shell's asset host (textures), or null.</param>
    /// <param name="library">The module's asset library (a clip's preview mesh), or null.</param>
    /// <param name="siblings">The files beside the asset (same packfile), searched first for textures and a clip's mesh, or null.</param>
    internal RfaPreview(byte[] bytes, string fileName, AssetHost? assets, AssetServices? library, IAssetSiblings? siblings = null)
    {
        _assets = assets;
        _library = library;
        _siblings = siblings;
        FileName = fileName;
        IsClip = fileName.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase);
        SetResourceReference(BackgroundProperty, "Viewport.Background");
        AutomationProperties.SetName(this, (IsClip ? "Animation preview: " : "Mesh preview: ") + fileName);
        _message = new TextBlock { Text = "Loading " + fileName + "…", TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12) };
        _message.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        Children.Add(_message);
        Loaded += (_, _) => { if (_host?.Clip is not null) _host.Playback.Play(); };
        Unloaded += (_, _) => _host?.Playback.Pause();
        Loading = LoadAsync(bytes, fileName, _cts.Token);
    }

    /// <summary>The previewed file's name.</summary>
    public string FileName { get; }
    /// <summary>True for an .rfa clip.</summary>
    public bool IsClip { get; }
    /// <summary>Completes when the file is shown (a clip's mesh may still be loading), the message is shown, or the preview was disposed first.</summary>
    public Task Loading { get; }
    /// <summary>Completes when a clip's preview mesh is shown or known to be unavailable (diagnostics).</summary>
    public Task MeshLoading => _meshLoaded.Task;
    private readonly TaskCompletionSource _meshLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>The message shown instead of the file; null once it shows.</summary>
    public string? Message => _message.Parent is null ? null : _message.Text;
    /// <summary>The viewport's notice (no preview mesh, bone count mismatch), or null.</summary>
    public string? Notice => _host?.Scene.Notice;
    /// <summary>The mesh shown (a clip's preview mesh), or null.</summary>
    public string? MeshName => _host?.Scene.Mesh is null ? null : _host.Scene.MeshName;
    /// <summary>The transport (a clip preview), or null.</summary>
    public PlaybackViewModel? Playback => _host?.Clip is null ? null : _host.Playback;

    private async Task LoadAsync(byte[] bytes, string fileName, CancellationToken ct)
    {
        using var busy = BusyTracker.Begin("animation preview");
        object parsed;
        try
        {
            parsed = await Task.Run<object>(() => IsClip ? RfaReader.Read(bytes, fileName) : V3dReader.Read(bytes, fileName), ct);
        }
        catch (OperationCanceledException) { _meshLoaded.TrySetResult(); return; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_disposed) _message.Text = $"Cannot preview {fileName}: {ex.Message}";
            _meshLoaded.TrySetResult();
            return;
        }
        if (_disposed) { _meshLoaded.TrySetResult(); return; }
        try
        {
            var host = _host = new RfaPreviewHost();
            _viewport = new ViewportControl { DataContext = host };
            Children.Remove(_message);
            Children.Add(_viewport);
            if (parsed is RfaClip clip) ShowClip(host, clip);
            else
            {
                host.Scene.SetMesh((V3dFile)parsed, fileName, _siblings.Layer(_assets?.Resolver));
                _meshLoaded.TrySetResult();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Clear();
            _message.Text = $"Cannot preview {fileName}: {ex.Message}";
            Children.Add(_message);
            _meshLoaded.TrySetResult();
        }
    }

    private void ShowClip(RfaPreviewHost host, RfaClip clip)
    {
        host.Clip = clip;
        host.Playback.SetClip(clip);
        host.Scene.SetAnimation(clip, host.Playback.Time);
        var bar = Transport(host.Playback);
        DockPanel.SetDock(bar, Dock.Bottom);
        // the viewport above the transport
        Children.Remove(_viewport);
        var dock = new DockPanel();
        dock.Children.Add(bar);
        dock.Children.Add(_viewport);
        Children.Add(dock);
        ChooseMesh(host, clip);
        if (IsLoaded) host.Playback.Play();
    }

    /// <summary>Play/pause, the time bar and the time readout.</summary>
    private static Border Transport(PlaybackViewModel playback)
    {
        var play = new Button { Command = playback.PlayPauseCommand, MinWidth = 32, Height = 26, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        play.SetResourceReference(StyleProperty, "ToolButton");
        var glyph = new TextBlock { FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14 };
        glyph.SetBinding(TextBlock.TextProperty, new Binding(nameof(PlaybackViewModel.PlayGlyph)) { Source = playback });
        play.Content = glyph;
        play.SetBinding(ToolTipProperty, new Binding(nameof(PlaybackViewModel.IsPlaying)) { Source = playback, Converter = new PlayTip() });
        AutomationProperties.SetName(play, "Play or pause the preview");
        var time = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), FontSize = 12, MinWidth = 70 };
        time.SetBinding(TextBlock.TextProperty, new Binding(nameof(PlaybackViewModel.TimeText)) { Source = playback });
        time.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        var scrubber = new TimeScrubber { DataContext = playback, VerticalAlignment = VerticalAlignment.Center };
        var row = new DockPanel();
        DockPanel.SetDock(play, Dock.Left);
        DockPanel.SetDock(time, Dock.Right);
        row.Children.Add(play);
        row.Children.Add(time);
        row.Children.Add(scrubber);
        var border = new Border { Child = row, Padding = new Thickness(6, 2, 6, 3), BorderThickness = new Thickness(0, 1, 0, 0) };
        border.SetResourceReference(Border.BackgroundProperty, "App.ChromeBackground");
        border.SetResourceReference(Border.BorderBrushProperty, "App.Border");
        AutomationProperties.SetName(border, "Transport");
        return border;
    }

    private sealed class PlayTip : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is true ? "Pause" : "Play";
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => Binding.DoNothing;
    }

    // ── Preview mesh (the clip documents' choice, nothing remembered) ──────────

    private void ChoosePreviewMesh(RfaPreviewHost host, RfaClip clip)
    {
        if (_disposed) return;
        var library = _library;
        var snapshot = library?.Snapshot;
        if (library is null || snapshot is null || snapshot.Meshes.Length == 0)
        {
            if (library is { IsLoading: true })
            {
                host.Scene.SetNotice("Looking for a mesh to play this clip on…");
                if (!_waitingForLibrary)
                {
                    _waitingForLibrary = true;
                    // weak: the library is the module's and outlives every preview
                    WeakEventManager<AssetServices, EventArgs>.AddHandler(library, nameof(AssetServices.LibraryChanged), OnLibraryChanged);
                }
                return;
            }
            NoMesh(host, clip);
            return;
        }
        StopWaiting();
        var pick = ClipDocumentViewModel.PickPreviewMesh(snapshot, library.Usage, FileName, clip.BoneCount, null, RfaModule.Workspace?.LastPreviewMeshFor(clip.BoneCount));
        if (pick is null) { NoMesh(host, clip); return; }
        host.Scene.SetNotice($"Loading {pick.Name}…");
        var ct = _cts.Token;
        var busy = BusyTracker.Begin("preview mesh " + pick.Name);
        _ = LoadMeshAsync();

        async Task LoadMeshAsync()
        {
            try
            {
                var mesh = await Task.Run(() => V3dReader.Read(pick.Location.ReadAllBytes(), pick.Name), ct);
                if (_disposed || !ReferenceEquals(host, _host)) return;
                string? folder = pick.Location.FilePath is { } path ? Path.GetDirectoryName(path) : null;
                ShowMesh(host, clip, mesh, pick.Name, _siblings.Layer(_assets?.ResolverFor(folder)));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (!_disposed) host.Scene.SetNotice($"{pick.Name} could not be loaded: {ex.Message}", warning: true);
            }
            finally
            {
                busy.Dispose();
                _meshLoaded.TrySetResult();
            }
        }
    }

    private static void ShowMesh(RfaPreviewHost host, RfaClip clip, V3dFile mesh, string name, AssetResolver? textures)
    {
        host.Scene.SetMesh(mesh, name, textures);
        host.Scene.SetAnimation(clip, host.Playback.Time);
        int meshBones = host.Scene.Skeleton.Count;
        host.Scene.SetNotice(meshBones == clip.BoneCount ? null
            : $"This clip has {clip.BoneCount} bones but {name} has {meshBones}; it plays as the game would on this mesh.", warning: meshBones != clip.BoneCount);
    }

    // ── Preview mesh from the files beside the clip (same packfile) ────────────

    /// <summary>Most sibling meshes read while looking for one that fits a clip.</summary>
    private const int MaxSiblingMeshes = 200;

    /// <summary>
    /// A mesh beside the clip wins over the library's pick: one with the clip's bone count, preferring the meshes the
    /// tables name for the clip, then the longest name prefix shared with the clip. Without one, the library decides.
    /// </summary>
    private void ChooseMesh(RfaPreviewHost host, RfaClip clip)
    {
        var siblings = _siblings;
        var names = siblings?.Names.Where(n => n.EndsWith(".v3c", StringComparison.OrdinalIgnoreCase)).Take(MaxSiblingMeshes).ToList();
        if (siblings is null || names is not { Count: > 0 }) { ChoosePreviewMesh(host, clip); return; }
        var tables = new HashSet<string>(_library?.Usage.MeshesForClip(FileName) ?? Enumerable.Empty<string>(),StringComparer.OrdinalIgnoreCase);
        host.Scene.SetNotice("Looking for a mesh to play this clip on…");
        var ct = _cts.Token;
        _ = FindAsync();

        async Task FindAsync()
        {
            (string Name, V3dFile Mesh)? found;
            using (BusyTracker.Begin("preview mesh in " + siblings.Label))
            {
                try { found = await Task.Run(() => FindSiblingMesh(siblings, names, tables, FileName, clip.BoneCount, ct), ct); }
                catch (OperationCanceledException) { _meshLoaded.TrySetResult(); return; }
            }
            if (_disposed || !ReferenceEquals(host, _host)) { _meshLoaded.TrySetResult(); return; }
            if (found is not { } f) { ChoosePreviewMesh(host, clip); return; }
            try { ShowMesh(host, clip, f.Mesh, f.Name, siblings.Layer(_assets?.Resolver)); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { host.Scene.SetNotice($"{f.Name} could not be shown: {ex.Message}", warning: true); }
            finally { _meshLoaded.TrySetResult(); }
        }
    }

    private static (string Name, V3dFile Mesh)? FindSiblingMesh(IAssetSiblings siblings, List<string> names, HashSet<string> tables,
        string clipName, int boneCount, CancellationToken ct)
    {
        string stem = Path.GetFileNameWithoutExtension(clipName);
        int Shared(string name)
        {
            string s = Path.GetFileNameWithoutExtension(name);
            int i = 0;
            while (i < s.Length && i < stem.Length && char.ToLowerInvariant(s[i]) == char.ToLowerInvariant(stem[i])) i++;
            return i;
        }
        foreach (string name in names.OrderByDescending(tables.Contains).ThenByDescending(Shared).ThenBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (siblings.Read(name) is not { } bytes) continue;
            try
            {
                var mesh = V3dReader.Read(bytes, name);
                if (mesh.Bones.Length == boneCount) return (name, mesh);
            }
            catch (Exception ex) when (ex is AssetFormatException or IOException or ArgumentException or InvalidOperationException)
            {
                // a broken sibling is skipped; the library may still have a mesh
            }
        }
        return null;
    }

    private void NoMesh(RfaPreviewHost host, RfaClip clip)
    {
        StopWaiting();
        // a clip stores no bone hierarchy of its own, so there is no skeleton to draw without a mesh
        host.Scene.SetNotice($"No mesh with {clip.BoneCount} bones was found to play this clip on ({clip.BoneCount} bones, {clip.Duration / (double)RfaClip.TicksPerFrame:0} frames).", warning: true);
        _meshLoaded.TrySetResult();
    }

    private void OnLibraryChanged(object? sender, EventArgs e)
    {
        if (_disposed || _host is not { Clip: { } clip } host || host.Scene.Mesh is not null) { StopWaiting(); return; }
        if (_library is { IsLoading: true } && _library.Snapshot.Meshes.Length == 0) return;
        ChoosePreviewMesh(host, clip);
    }

    private void StopWaiting()
    {
        if (!_waitingForLibrary || _library is null) return;
        _waitingForLibrary = false;
        WeakEventManager<AssetServices, EventArgs>.RemoveHandler(_library, nameof(AssetServices.LibraryChanged), OnLibraryChanged);
    }

    private void Clear()
    {
        StopWaiting();
        if (_host is { } host) host.Playback.Pause();
        if (_viewport is { } viewport) viewport.DataContext = null;
        _host = null;
        _viewport = null;
        Children.Clear(); // the viewport leaves the render loop when it unloads
    }

    /// <summary>Stops playback, cancels loads still running and detaches the viewport.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        Clear();
        _meshLoaded.TrySetResult();
    }
}
