using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cairn.Assets;
using Cairn.Ui.Modules;
using Cairn.Ui.Services;
using Cairn.Viewport;

namespace Cairn.Lvl.Ui.Preview;

/// <summary>
/// Read-only preview of a level or group file for other modules (the packfile's preview pane): the file is read on a
/// worker, then shown in a <see cref="LvlSceneView"/> whose textures come from the files beside it first. Creating it
/// is quick; disposing it before it finished loading is safe. It never writes settings.
/// </summary>
public sealed class LvlPreview : Grid, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly LvlSceneView _view;
    private readonly IAssetSiblings? _siblings;
    private bool _disposed;

    /// <param name="bytes">The whole file.</param>
    /// <param name="fileName">Its name (.rfl or .rfg).</param>
    /// <param name="siblings">The files beside it (its packfile's other entries), looked in first for textures, or null.</param>
    /// <param name="resolver">Finds the textures after the siblings (the game data), or null.</param>
    /// <param name="textures">The texture decoder and cache.</param>
    /// <param name="settings">The module's settings (read only: the toggles' starting values and the size limit), or null.</param>
    internal LvlPreview(byte[] bytes, string fileName, IAssetSiblings? siblings, AssetResolver? resolver, TextureService textures, LvlSettings? settings)
    {
        _siblings = siblings;
        FileName = fileName;
        AutomationProperties.SetName(this, "Level preview: " + fileName);
        // in the small preview pane the level's shape comes first: object markers start hidden (Objects shows them)
        _view = new LvlSceneView(textures, settings, remember: false) { MarkerKinds = LvlMarkerKinds.None };
        _view.ShowMessage("Reading " + fileName + "…");
        Children.Add(_view);
        Loading = LoadAsync(bytes, fileName, resolver, _cts.Token);
    }

    /// <summary>The previewed file's name.</summary>
    public string FileName { get; }

    /// <summary>The scene view (self-tests).</summary>
    public LvlSceneView View => _view;

    /// <summary>Completes when the file is read and its preview built (or the failure shown), or the preview was disposed first.</summary>
    public Task Loading { get; }

    private async Task LoadAsync(byte[] bytes, string fileName, AssetResolver? resolver, CancellationToken ct)
    {
        LvlSource source;
        using (BusyTracker.Begin("level preview read"))
        {
            try
            {
                source = await Task.Run(() => LvlSource.Read(bytes, fileName), ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (!_disposed) _view.ShowMessage("Cannot preview " + fileName + ".", ex.Message);
                return;
            }
        }
        if (_disposed) return;
        // a Red Faction II level's textures are in the .peg packs beside it
        var textures = source.Level?.IsRf2 == true ? LvlTexturePacks.ResolverFor(source, null, _siblings, resolver) : _siblings.Layer(resolver);
        _view.Show(source, textures);
        await _view.Building;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _view.Dispose();
        Children.Clear();
        GC.SuppressFinalize(this);
    }
}
