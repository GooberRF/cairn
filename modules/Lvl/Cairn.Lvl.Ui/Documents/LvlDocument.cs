using System.Globalization;
using System.Windows;
using Cairn.Formats;
using Cairn.Lvl.Ui.Preview;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Ui.Services;
using Cairn.Workspace;

namespace Cairn.Lvl.Ui.Documents;

/// <summary>
/// An open level (.rfl) or group file (.rfg): read-only (Cairn never writes levels; group files are written only by a
/// conversion), so Save and Save As are off and Convert to… is the way out. The file is read on a worker after the tab
/// opens (the largest levels take seconds); the view shows the facts and the 3D preview once it is.
/// </summary>
public sealed class LvlDocument : DocumentBase, IDocumentContent, IDocument
{
    private readonly LvlModule _module;
    private LvlDocumentView? _view;
    private byte[] _bytes;
    private CancellationTokenSource _cts = new();

    internal LvlDocument(LvlModule module, IDocumentKind kind, byte[] bytes, string name, string? path, string? origin)
        : base(module.ShellContext, kind, name, path, origin)
    {
        _module = module;
        _bytes = bytes;
        Loading = LoadAsync(_cts.Token);
    }

    /// <summary>Raised on the UI thread once the file is read (or failed), and after a Reload.</summary>
    public event EventHandler? Loaded;

    /// <summary>The file's bytes as opened.</summary>
    public byte[] Bytes => _bytes;

    /// <summary>The level or group file, once read; null while reading or when it could not be read.</summary>
    public LvlSource? Source { get; private set; }

    /// <summary>Why the file could not be read, or null.</summary>
    public string? LoadError { get; private set; }

    /// <summary>Completes when the file is read (or failed).</summary>
    public Task Loading { get; private set; }

    /// <summary>The packfile the tab was opened from directly (an entry of the game data), or null.</summary>
    public string? ArchivePath { get; internal set; }

    /// <summary>True for a group file.</summary>
    public bool IsGroupFile => LvlSource.IsGroupName(DisplayName);

    /// <summary>The name the file goes by (Convert to… names the outputs after it).</summary>
    public string FileName => DisplayName;

    /// <summary>The file as opened (levels are never edited).</summary>
    public byte[] CurrentBytes() => _bytes;

    private async Task LoadAsync(CancellationToken ct)
    {
        byte[] bytes = _bytes;
        string name = DisplayName;
        LvlSource? source = null;
        string? error = null;
        using (BusyTracker.Begin("level read " + name))
        {
            try
            {
                source = await Task.Run(() => LvlSource.Read(bytes, name), ct);
            }
            catch (OperationCanceledException) { return; }
            catch (AssetFormatException ex) { error = ex.Message; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { error = ex.Message; }
        }
        if (ct.IsCancellationRequested) return;
        Source = source;
        LoadError = error;
        RaiseAll(nameof(Source), nameof(LoadError), nameof(StatusItems));
        Loaded?.Invoke(this, EventArgs.Empty);
        _module.RefreshCommands();
    }

    // ---- the document contract ---------------------------------------------------------------------------------

    /// <summary>Levels and group files are never written in place (nor saved as anything: use Convert to…).</summary>
    public override bool IsReadOnly => true;
    bool IDocument.CanSave => false;
    public override bool IsDirty => false;
    public override bool CanUndo => false;
    public override bool CanRedo => false;
    public override string? UndoLabel => null;
    public override string? RedoLabel => null;
    public override void Undo() { }
    public override void Redo() { }
    public override byte[]? CaptureRecovery() => null;

    public override void SaveTo(string path) =>
        throw new InvalidOperationException("Cairn does not write levels or group files. Use File > Convert to… instead.");

    protected override void LoadBytes(byte[] bytes, bool keepDirty)
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        _bytes = bytes;
        Source = null;
        LoadError = null;
        RaiseAll(nameof(Source), nameof(LoadError), nameof(StatusItems));
        Loading = LoadAsync(_cts.Token);
    }

    protected override bool MatchesSaved(byte[] bytes) => bytes.AsSpan().SequenceEqual(_bytes);

    public override IReadOnlyList<StatusItem> StatusItems
    {
        get
        {
            if (LoadError is not null) return [new StatusItem("Unreadable", LoadError)];
            if (Source is not { } source) return [new StatusItem("Reading…")];
            var items = new List<StatusItem>();
            if (source.Level is { } level)
            {
                items.Add(new StatusItem(VersionText(level.Version), "The level's format version"));
                items.Add(new StatusItem(string.Format(CultureInfo.CurrentCulture, "{0:N0} faces · {1:N0} brushes", level.StaticGeometry.Faces.Length, level.Brushes.Length),
                    "Faces of the compiled geometry, and the editor's brushes"));
            }
            else if (source.GroupFile is { } file)
            {
                items.Add(new StatusItem("Group file, version " + file.Version.ToString(CultureInfo.InvariantCulture)));
                items.Add(new StatusItem(string.Format(CultureInfo.CurrentCulture, "{0:N0} groups · {1:N0} brushes", file.Groups.Length, file.AllBrushes.Count())));
            }
            if (source.Problems.Length > 0)
                items.Add(new StatusItem(source.Problems.Length == 1 ? "1 problem" : string.Format(CultureInfo.CurrentCulture, "{0:N0} problems", source.Problems.Length),
                    string.Join(Environment.NewLine, source.Problems.Take(10))));
            return items;
        }
    }

    internal static string VersionText(int version) => version switch
    {
        0x127 => "Red Faction II level",
        >= 300 => "Alpine level " + version.ToString(CultureInfo.InvariantCulture),
        _ => "Level version " + version.ToString(CultureInfo.InvariantCulture),
    };

    protected override FrameworkElement CreateView() => _view = new LvlDocumentView(this, _module);

    public override void Dispose()
    {
        _cts.Cancel();
        _view?.Dispose();
        _module.Forget(this);
        base.Dispose();
    }
}
