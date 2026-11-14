using Cairn.Formats;
using Cairn.Ui.Modules;
using Cairn.Vpp.Model;

namespace Cairn.Vpp.Ui.Preview;

/// <summary>
/// The entries of one packfile snapshot as the files beside a previewed entry (<see cref="IAssetSiblings"/>), so a
/// module preview finds the textures, frames and meshes the packfile carries itself. The snapshot is immutable, so
/// pending added, replaced and renamed entries are included exactly as they are in it, and lookups are safe from any
/// thread. Names compare as the engine does (<see cref="Editing.VppNames.Comparer"/>); an entry that can no longer be
/// read (its file changed or the packfile moved) reads as null. So does every entry stored in the packfile once the file
/// is no longer the one the snapshot was read from (saved since, or replaced on disk: its stamp differs), because the
/// snapshot's offsets then point into other data; from then on <see cref="Contains"/> is false for them too.
/// </summary>
public sealed class VppSiblings : IAssetSiblings
{
    private readonly VppPackage _package;
    private readonly string? _archivePath;
    private volatile bool _stale;

    /// <param name="package">The packfile snapshot, usually the open document's <c>Current</c>.</param>
    public VppSiblings(VppPackage package)
    {
        _package = package ?? throw new ArgumentNullException(nameof(package));
        Label = package.Path is { } path ? Path.GetFileName(path) : "this packfile";
        Names = [.. package.Items.Select(i => i.Name)];
        try { _archivePath = package.Path is { } p && package.Stamp is not null ? Path.GetFullPath(p) : null; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { _archivePath = null; }
    }

    /// <inheritdoc/>
    public string Label { get; }

    /// <inheritdoc/>
    public IReadOnlyList<string> Names { get; }

    /// <inheritdoc/>
    public bool Contains(string name) =>
        !string.IsNullOrWhiteSpace(name) && _package.Find(Path.GetFileName(name.Trim())) is { } item && !(_stale && InArchive(item));

    /// <summary>True when the entry's data is in the packfile file this snapshot was read from (an original entry).</summary>
    private bool InArchive(VppItem item) =>
        _archivePath is not null && item.Source switch
        {
            ArchiveSource a => string.Equals(a.ArchivePath, _archivePath, StringComparison.OrdinalIgnoreCase),
            XForge.CflEntrySource c => string.Equals(c.ArchivePath, _archivePath, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    /// <summary>
    /// False when <paramref name="item"/> is stored in the packfile file and that file is no longer the one the snapshot
    /// was read from (its stamp changed: saved, or replaced on disk); remembered once seen, a stamp does not come back.
    /// </summary>
    private bool Readable(VppItem item)
    {
        if (!InArchive(item)) return true;
        if (_stale) return false;
        VppArchiveStamp now;
        try { now = VppArchiveStamp.Of(_archivePath!); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; } // missing (moved aside mid-save) or locked
        if (now == _package.Stamp) return true;
        _stale = true;
        return false;
    }

    /// <inheritdoc/>
    public byte[]? Read(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || _package.Find(Path.GetFileName(name.Trim())) is not { } item || !Readable(item)) return null;
        try
        {
            return item.Source.ReadAll();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AssetFormatException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    /// <remarks>Reads only the first <paramref name="count"/> bytes of the entry's data (one seek into the packfile).</remarks>
    public byte[]? ReadHead(string name, int count)
    {
        if (string.IsNullOrWhiteSpace(name) || count < 0 || _package.Find(Path.GetFileName(name.Trim())) is not { } item || !Readable(item)) return null;
        try
        {
            using var stream = item.Source.Open();
            var buffer = new byte[(int)Math.Min(item.Source.Size, count)];
            int read = 0;
            while (read < buffer.Length)
            {
                int n = stream.Read(buffer, read, buffer.Length - read);
                if (n <= 0) break;
                read += n;
            }
            return read == buffer.Length ? buffer : buffer[..read];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AssetFormatException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
