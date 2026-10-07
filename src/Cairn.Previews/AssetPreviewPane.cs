using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Cairn.Formats;
using Cairn.Ui.Modules;
using Cairn.Ui.Services;

namespace Cairn.Previews;

/// <summary>
/// The preview of one file: images (animated VBM, DDS mip levels), text, audio with a waveform, other modules'
/// previews (meshes, clips, effects, animated textures) with "Open in Cairn", a hex view for other binaries, and
/// a summary for several files. The file comes as bytes (<see cref="Show"/>, <see cref="ShowBytes"/>) or by name
/// through the game data (<see cref="ShowAsset"/>). Bytes are read and decoded off the UI thread; a newer call
/// cancels an older load, and the previous view is disposed when it is replaced. Callers debounce
/// (<see cref="PreviewDebouncer"/>).
/// </summary>
public class AssetPreviewPane : UserControl, IDisposable
{
    /// <summary>Files larger than this wait for "Preview anyway".</summary>
    public const long DefaultSizeCap = 48L << 20;
    /// <summary>Largest text shown (larger text files get the hex view).</summary>
    private const long MaxTextBytes = 32L << 20;
    private const string EmptyMessage = "Select a file to preview it.";

    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase) { ".tga", ".dds", ".png", ".jpg", ".jpeg", ".vbm" };
    private static readonly HashSet<string> AudioTypes = new(StringComparer.OrdinalIgnoreCase) { ".wav", ".ogg", ".aif", ".aiff", ".aifc", ".mp3" };
    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase) { ".tbl", ".txt", ".log", ".ini", ".gltf", ".cfg", ".xml", ".json", ".htm", ".html" };

    private readonly IShellContext? _shell;
    private readonly ContentControl _host = new() { Focusable = false };
    private CancellationTokenSource? _cts;
    private int _generation;
    private bool _disposed;

    static AssetPreviewPane() => AudioData.SweepStale();

    public AssetPreviewPane(IShellContext? shell)
    {
        _shell = shell;
        Content = _host;
        SetResourceReference(BackgroundProperty, "App.PaneBackground");
        ShowElement(AssetPreviewKind.Empty, PreviewUi.Message(EmptyMessage));
    }

    /// <summary>Files above this size are previewed only after "Preview anyway".</summary>
    public long SizeCap { get; set; } = DefaultSizeCap;

    /// <summary>The name the background work is reported under (<see cref="BusyTracker"/>).</summary>
    public string BusyLabel { get; set; } = "asset preview";

    /// <summary>What is shown now.</summary>
    public AssetPreviewKind Kind { get; private set; }

    /// <summary>The view shown now (for tests).</summary>
    public FrameworkElement? View => _host.Content as FrameworkElement;

    /// <summary>The file the current or pending preview is for.</summary>
    public AssetPreviewSource? Source { get; private set; }

    /// <summary>The last <see cref="ShowAsset"/> lookup (null after other calls).</summary>
    public AssetLookupResult? Lookup { get; private set; }

    /// <summary>The load in progress, or a completed task.</summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    /// <summary>Raised when <see cref="Kind"/> changes (hosts shrink the pane to one line for levels).</summary>
    public event EventHandler? KindChanged;

    /// <summary>True when the pane shows only a line of text and its host may shrink it to fit.</summary>
    public bool IsCompact => Kind == AssetPreviewKind.Level;

    /// <summary>Shows a message and nothing else (cancels a load).</summary>
    public void Clear(string message = EmptyMessage)
    {
        if (_disposed) return;
        Begin(null);
        ShowElement(AssetPreviewKind.Empty, PreviewUi.Message(message));
    }

    /// <summary>Shows how many files, how big, and which types, for a multiple selection.</summary>
    public void ShowSummary(IReadOnlyList<(string Name, long Size)> files)
    {
        if (_disposed) return;
        Begin(null);
        ShowElement(AssetPreviewKind.Summary, new SelectionSummary(files));
    }

    /// <summary>Previews bytes read by <paramref name="read"/> (on a pool thread).</summary>
    /// <param name="name">The file name (its extension picks the preview).</param>
    /// <param name="read">Reads the file.</param>
    /// <param name="siblings">Files that come first when a module preview resolves references.</param>
    /// <param name="size">The size when known (for the size cap), else -1.</param>
    public void ShowBytes(string name, Func<CancellationToken, Task<byte[]>> read, IAssetSiblings? siblings = null, long size = -1) =>
        Show(new AssetPreviewSource(name, read) { Siblings = siblings, Size = size });

    /// <summary>
    /// Previews a file by name: <paramref name="siblings"/> first, then the shell's game data (search folders, the
    /// document folder when given, the game folder and its packfiles) in the engine's order, with texture supersede
    /// (a <c>.tga</c> name finds a <c>.dds</c>/<c>.vbm</c>). A missing file shows where it was looked for.
    /// </summary>
    public void ShowAsset(string name, IAssetSiblings? siblings = null, string? documentFolder = null)
    {
        if (_disposed) return;
        int generation = Begin(null);
        var resolver = AssetLookup.ResolverFor(_shell, siblings, documentFolder);
        ShowElement(AssetPreviewKind.Loading, PreviewUi.Message("Looking for " + name + "..."));
        var cts = _cts = new CancellationTokenSource();
        Pending = ResolveAsync(name, resolver, siblings, generation, cts.Token);
    }

    private async Task ResolveAsync(string name, Cairn.Assets.AssetResolver? resolver, IAssetSiblings? siblings, int generation, CancellationToken ct)
    {
        AssetLookupResult lookup;
        try
        {
            using (BusyTracker.Begin(BusyLabel))
                lookup = await Task.Run(() => AssetLookup.Resolve(resolver, name, siblings, ct), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (_disposed || generation != _generation || ct.IsCancellationRequested) return;
        if (AssetLookup.SourceFor(lookup, siblings) is not { } source)
        {
            Lookup = lookup;
            ShowElement(AssetPreviewKind.NotFound, PreviewUi.Message($"'{lookup.RequestedName}' was not found.",
                lookup.Searched.Count == 0 ? "No game folder or search folders are set." : "Looked in: " + string.Join("; ", lookup.Searched), warning: true));
            return;
        }
        Show(source);
        Lookup = lookup;
        await Pending;
    }

    /// <summary>Shows <paramref name="source"/> now (callers debounce).</summary>
    /// <param name="source">The file.</param>
    /// <param name="force">Preview even above <see cref="SizeCap"/>.</param>
    public void Show(AssetPreviewSource source, bool force = false)
    {
        if (_disposed) return;
        int generation = Begin(source);
        string ext = source.Extension;
        if (ext.Equals(".rfl", StringComparison.OrdinalIgnoreCase))
        {
            var line = PreviewUi.Text("Levels have no preview: the level's information, statistics and referenced files are in the details below.", "HintText");
            line.Margin = new Thickness(10, 6, 10, 6);
            ShowElement(AssetPreviewKind.Level, line);
            return;
        }
        if (source.Size > SizeCap && !force)
        {
            var button = PreviewUi.Button("Preview anyway", "Read and preview this large file", (_, _) => Show(source, force: true));
            button.SetResourceReference(StyleProperty, "PushButton");
            ShowElement(AssetPreviewKind.TooLarge, PreviewUi.Message(
                string.Format(CultureInfo.CurrentCulture, "{0} is large ({1}).", source.FileName, PreviewUi.Size(source.Size)),
                "Previewing it reads the whole file into memory.", button));
            return;
        }
        if (source.Unavailable is { } reason)
        {
            ShowElement(AssetPreviewKind.Empty, PreviewUi.Message($"{source.FileName} cannot be previewed.", reason));
            return;
        }
        // text types too: a module that previews them (tables) shows them better, with "Open in Cairn"; without one they stay plain text
        var provider = ImageTypes.Contains(ext) || AudioTypes.Contains(ext) ? null : FindProvider(source.Name);
        ShowElement(AssetPreviewKind.Loading, PreviewUi.Message("Loading " + source.FileName + "..."));
        var cts = _cts = new CancellationTokenSource();
        Pending = LoadAsync(source, ext, provider, generation, cts.Token);
    }

    /// <summary>Cancels the previous load and starts a new generation.</summary>
    private int Begin(AssetPreviewSource? source)
    {
        _cts?.Cancel();
        _cts = null;
        Source = source;
        Lookup = null;
        Pending = Task.CompletedTask;
        return ++_generation;
    }

    private (IModule Module, IAssetPreviewProvider Provider)? FindProvider(string name)
    {
        if (_shell is null) return null;
        foreach (var module in _shell.Modules)
        {
            if (module is not IAssetPreviewProvider provider) continue;
            try
            {
                if (provider.CanPreview(name)) return (module, provider);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* a broken provider is skipped */ }
        }
        return null;
    }

    private async Task LoadAsync(AssetPreviewSource source, string ext, (IModule Module, IAssetPreviewProvider Provider)? provider, int generation, CancellationToken ct)
    {
        Prepared prepared;
        try
        {
            using (BusyTracker.Begin(BusyLabel))
                prepared = await Task.Run(() => PrepareAsync(source, ext, provider is not null, ct), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (_disposed || generation != _generation || ct.IsCancellationRequested)
        {
            (prepared.Data as IDisposable)?.Dispose();
            return;
        }
        try
        {
            ShowPrepared(source, prepared, provider);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            (prepared.Data as IDisposable)?.Dispose();
            ShowElement(AssetPreviewKind.Hex, new HexPreview(prepared.Head, prepared.Size, "Preview failed: " + ex.Message + "."));
        }
    }

    /// <summary>Data made ready on a pool thread.</summary>
    private sealed record Prepared(AssetPreviewKind Kind, object? Data, byte[] Head, byte[]? All, long Size, string? Error);

    private static async Task<Prepared> PrepareAsync(AssetPreviewSource source, string ext, bool forModule, CancellationToken ct)
    {
        byte[] all;
        byte[] head;
        long size = source.Size;
        try
        {
            bool whole = forModule || ImageTypes.Contains(ext) || AudioTypes.Contains(ext) || (TextTypes.Contains(ext) && size <= MaxTextBytes);
            if (whole || source.ReadHead is null)
            {
                all = await source.ReadAll(ct).ConfigureAwait(false);
                if (size < 0) size = all.Length;
                head = all.Length <= HexPreview.MaxBytes ? all : all[..HexPreview.MaxBytes];
                if (!whole)
                {
                    if (TextData.LooksLikeText(head) && all.Length <= MaxTextBytes) ext = ".txt";
                    else all = [];
                }
            }
            else
            {
                head = await source.ReadHead(HexPreview.MaxBytes, ct).ConfigureAwait(false);
                all = [];
                if (TextData.LooksLikeText(head) && size <= MaxTextBytes)
                {
                    all = head.Length == size ? head : await source.ReadAll(ct).ConfigureAwait(false);
                    if (size < 0) size = all.Length;
                    ext = ".txt";
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AssetFormatException or InvalidOperationException or InvalidDataException)
        {
            return new Prepared(AssetPreviewKind.Hex, null, [], null, Math.Max(0, size), "The data cannot be read: " + ex.Message);
        }
        ct.ThrowIfCancellationRequested();

        try
        {
            if (forModule) return new Prepared(AssetPreviewKind.Module, null, head, all, size, null);
            if (ImageTypes.Contains(ext)) return new Prepared(AssetPreviewKind.Image, ImageData.Decode(all, source.FileName, ct), head, null, size, null);
            if (AudioTypes.Contains(ext)) return new Prepared(AssetPreviewKind.Audio, AudioData.Decode(all, source.FileName, ct), head, null, size, null);
            if (TextTypes.Contains(ext) && all.Length == size) return new Prepared(AssetPreviewKind.Text, TextData.Decode(all, ct), head, null, size, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new Prepared(AssetPreviewKind.Hex, null, head, null, size, ex.Message);
        }
        return new Prepared(AssetPreviewKind.Hex, null, head, null, size, null);
    }

    private void ShowPrepared(AssetPreviewSource source, Prepared prepared, (IModule Module, IAssetPreviewProvider Provider)? provider)
    {
        switch (prepared.Kind)
        {
            case AssetPreviewKind.Image:
                ShowElement(AssetPreviewKind.Image, WithOpenInCairn(source, new ImagePreview((ImageData)prepared.Data!)));
                return;
            case AssetPreviewKind.Audio:
                ShowElement(AssetPreviewKind.Audio, new AudioPreview((AudioData)prepared.Data!, source.FileName));
                return;
            case AssetPreviewKind.Text:
                ShowElement(AssetPreviewKind.Text, WithOpenInCairn(source, new TextPreview((TextData)prepared.Data!)));
                return;
            case AssetPreviewKind.Module when provider is { } p:
                ShowModule(source, prepared, p.Module, p.Provider);
                return;
        }
        string? note = prepared.Error is { } error
            ? "No preview: " + error.TrimEnd('.') + "."
            : FontNote(source.Extension);
        ShowElement(AssetPreviewKind.Hex, WithOpenInCairn(source, new HexPreview(prepared.Head, prepared.Size, note)));
    }

    /// <summary>
    /// Puts an "Open in Cairn" bar above a plain preview (text, image, hex) when a Cairn document kind opens the file
    /// (for example a packfile inside a packfile, or a table when the table module offers no preview).
    /// </summary>
    private FrameworkElement WithOpenInCairn(AssetPreviewSource source, FrameworkElement view)
    {
        if (FindKind(source.Name) is null) return view;
        return new ModulePreviewHost(view, source.FileName, null, () => _ = OpenPlainInCairnAsync(source));
    }

    private async Task OpenPlainInCairnAsync(AssetPreviewSource source)
    {
        byte[] bytes = [];
        bool needsBytes = source.OpenInCairn is null && source.Location is not { FilePath: not null } and not { ArchivePath: not null };
        if (needsBytes)
        {
            try { bytes = await source.ReadAll(CancellationToken.None); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
            {
                _shell?.Dialogs.ShowError("Open in Cairn", source.FileName + " could not be read: " + ex.Message);
                return;
            }
        }
        if (!_disposed) OpenInCairn(source, bytes);
    }

    private static string? FontNote(string ext) =>
        ext.Equals(".vf", StringComparison.OrdinalIgnoreCase) || ext.Equals(".mvf", StringComparison.OrdinalIgnoreCase) ? "No visual preview for this type." : null;

    private void ShowModule(AssetPreviewSource source, Prepared prepared, IModule module, IAssetPreviewProvider provider)
    {
        byte[] bytes = prepared.All!;
        FrameworkElement? element;
        try
        {
            // The siblings (a packfile's other entries, pending changes included) come first when the preview resolves textures, frames or meshes.
            element = provider.CreatePreview(bytes, source.FileName, source.Siblings);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowElement(AssetPreviewKind.Hex, new HexPreview(prepared.Head, prepared.Size, "No preview: " + ex.Message.TrimEnd('.') + "."));
            return;
        }
        if (element is null)
        {
            ShowElement(AssetPreviewKind.Hex, new HexPreview(prepared.Head, prepared.Size, "The " + module.DisplayName + " module cannot show this file."));
            return;
        }
        bool canOpen = source.OpenInCairn is not null || FindKind(source.Name) is not null;
        ShowElement(AssetPreviewKind.Module, new ModulePreviewHost(element, source.FileName, module.DisplayName, canOpen ? () => OpenInCairn(source, bytes) : null));
    }

    private IDocumentKind? FindKind(string name)
    {
        if (_shell is null) return null;
        string ext = Path.GetExtension(name);
        return _shell.Modules.SelectMany(m => m.DocumentKinds)
            .FirstOrDefault(k => k.Extensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// "Open in Cairn": the source's own action (a packfile's work copy); else the resolved location (a loose file or
    /// an archive entry, through the shell); else the bytes through the document kind that takes the extension.
    /// </summary>
    private void OpenInCairn(AssetPreviewSource source, byte[] bytes)
    {
        if (source.OpenInCairn is { } open)
        {
            open(bytes);
            return;
        }
        if (_shell is null) return;
        if (source.Location is { } location && (location.FilePath is not null || location.ArchivePath is not null) && _shell.OpenLocation(location)) return;
        if (FindKind(source.Name) is not { } kind) return;
        try
        {
            var document = kind.OpenBytes(bytes, source.FileName, source.Origin ?? source.FileName);
            _shell.AddDocument(document);
        }
        catch (Exception ex) when (ex is AssetFormatException or IOException or InvalidDataException)
        {
            _shell.Dialogs.ShowError("Open in Cairn", source.FileName + " could not be opened: " + ex.Message);
        }
    }

    private void ShowElement(AssetPreviewKind kind, FrameworkElement element)
    {
        var old = _host.Content;
        _host.Content = element;
        bool changed = Kind != kind;
        Kind = kind;
        (old as IDisposable)?.Dispose();
        if (changed) KindChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops a load, playback and animation, and releases the view.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _cts?.Cancel();
        _cts = null;
        _generation++;
        var old = _host.Content;
        _host.Content = null;
        (old as IDisposable)?.Dispose();
        _disposed = true;
        Source = null;
        Lookup = null;
        OnDisposed();
        GC.SuppressFinalize(this);
    }

    /// <summary>Called once from <see cref="Dispose"/> (derived panes release their own state).</summary>
    protected virtual void OnDisposed() { }
}
