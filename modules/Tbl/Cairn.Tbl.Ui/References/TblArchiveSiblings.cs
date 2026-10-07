using Cairn.Formats.Vpp;
using Cairn.Ui.Modules;

namespace Cairn.Tbl.Ui.References;

/// <summary>
/// The files of the packfile a table was opened from, so a table's references resolve to the files that came with it
/// before the game data (as the packfile module's previews do). The directory is read once, lazily; thread-safe.
/// </summary>
public sealed class TblArchiveSiblings : IAssetSiblings
{
    private readonly string _archivePath;
    private readonly Lazy<VppArchive?> _archive;

    /// <param name="archivePath">The packfile on disk.</param>
    public TblArchiveSiblings(string archivePath)
    {
        _archivePath = archivePath ?? throw new ArgumentNullException(nameof(archivePath));
        _archive = new Lazy<VppArchive?>(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The packfile's path.</summary>
    public string ArchivePath => _archivePath;

    /// <inheritdoc/>
    public string Label => Path.GetFileName(_archivePath);

    /// <inheritdoc/>
    public IReadOnlyList<string> Names => _archive.Value is { } a ? a.Entries.Select(e => e.Name).ToList() : [];

    /// <inheritdoc/>
    public bool Contains(string name) =>
        !string.IsNullOrEmpty(name) && _archive.Value is { } a && a.TryGetEntry(Path.GetFileName(name), out _);

    /// <inheritdoc/>
    public byte[]? Read(string name)
    {
        if (string.IsNullOrEmpty(name) || _archive.Value is not { } a || !a.TryGetEntry(Path.GetFileName(name), out var entry)) return null;
        try { return a.ReadEntry(entry); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Cairn.Formats.AssetFormatException) { return null; }
    }

    private VppArchive? Load()
    {
        try { return VppArchive.Open(_archivePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Cairn.Formats.AssetFormatException) { return null; }
    }
}
