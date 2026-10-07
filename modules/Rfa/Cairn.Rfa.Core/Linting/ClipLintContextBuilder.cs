using Cairn.Rfa.Animation;
using Cairn.Assets;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Tbl;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Linting;

/// <summary>
/// Fills a <see cref="ClipLintContext"/> from the App's services: the preview mesh, the table usage
/// index and the asset library. This is the "async" half of linting (it may read a reference clip
/// from disk or a VPP); <see cref="ClipLinter.Analyze"/> itself stays cheap.
/// </summary>
public static class ClipLintContextBuilder
{
    /// <summary>
    /// Builds the context for a clip document.
    /// <list type="bullet">
    /// <item>Table uses (RFA013): every usage of the clip's base name, with the mesh each list plays it
    /// on and that mesh's bone count from the library.</item>
    /// <item>Name collisions (RFA024): other copies of the base name the library sees, except the
    /// document's own location, with their bytes (bounded) so the linter can drop copies identical to
    /// the clip.</item>
    /// <item>Reference clip (RFA023): the preview mesh's "stand" clip from the tables (else the clip
    /// the library would preview on it), read through the library, when it is not the document itself.</item>
    /// </list>
    /// </summary>
    /// <param name="fileName">The document's file name (with extension), or null for an unsaved clip.</param>
    /// <param name="documentPath">The document's full path or archive location description, to exclude it from collisions.</param>
    /// <param name="previewMesh">The preview mesh, if any.</param>
    /// <param name="previewMeshName">Its file name.</param>
    /// <param name="library">The library snapshot, if built.</param>
    /// <param name="usage">The table usage index, if loaded.</param>
    /// <param name="cancellationToken">Cancels the reference clip read.</param>
    public static ClipLintContext Build(
        string? fileName, string? documentPath, V3dFile? previewMesh, string? previewMeshName,
        LibrarySnapshot? library, ClipUsageIndex? usage, CancellationToken cancellationToken = default)
    {
        var skeleton = previewMesh is null ? null : Skeleton.FromFile(previewMesh);
        var context = new ClipLintContext
        {
            FileName = fileName,
            PreviewMesh = previewMesh,
            PreviewMeshName = previewMeshName,
            Skeleton = skeleton is { Count: > 0 } ? skeleton : null,
        };
        if (string.IsNullOrWhiteSpace(fileName)) return context;

        if (usage is not null)
        {
            var uses = new List<ClipTableUse>();
            foreach (var u in usage.UsagesOf(fileName))
            {
                var lists = usage.MeshClipLists.Where(l => l.Clips.Contains(u)).ToList();
                if (lists.Count == 0) uses.Add(new ClipTableUse(u.ClassName, u.Table, u.SlotName, u.Kind == ClipUsageKind.State, null, null));
                foreach (var list in lists)
                {
                    int? bones = library?.FindMesh(list.MeshName)?.BoneCount;
                    uses.Add(new ClipTableUse(u.ClassName, u.Table, u.SlotName, u.Kind == ClipUsageKind.State, list.MeshName, bones > 0 ? bones : null));
                }
            }
            context = context with { TableUses = uses };
        }

        if (library is not null)
        {
            var copies = library.CopiesOfClip(fileName)
                .Where(c => !IsDocument(c.Location, documentPath))
                .ToList();
            context = context with
            {
                CollidingClips = [.. copies.Select(c => $"{c.Name} in {c.Location.DisplayLocation}")],
                CollidingClipBytes = [.. copies.Select((c, i) => i < MaxComparedCopies ? ReadForComparison(c.Location, cancellationToken) : null)],
            };

            if (previewMeshName is { Length: > 0 } && context.Skeleton is not null)
            {
                var reference = FindReferenceClip(previewMeshName, library, usage);
                if (reference is not null && !IsDocument(reference.Location, documentPath)
                    && !string.Equals(reference.BaseName, Path.GetFileNameWithoutExtension(fileName), StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var clip = RfaReader.Read(reference.Location.ReadAllBytes(), reference.Name);
                        if (clip.BoneCount == context.Skeleton.Count)
                            context = context with { ReferenceClip = clip, ReferenceClipName = reference.Name };
                    }
                    catch (Exception ex) when (ex is IOException or Cairn.Formats.AssetFormatException or UnauthorizedAccessException)
                    {
                        // No reference clip: RFA023 simply does not run.
                    }
                }
            }
        }
        return context;
    }

    /// <summary><see cref="Build"/> on a worker thread.</summary>
    public static Task<ClipLintContext> BuildAsync(
        string? fileName, string? documentPath, V3dFile? previewMesh, string? previewMeshName,
        LibrarySnapshot? library, ClipUsageIndex? usage, CancellationToken cancellationToken = default) =>
        Task.Run(() => Build(fileName, documentPath, previewMesh, previewMeshName, library, usage, cancellationToken), cancellationToken);

    private static LibraryClip? FindReferenceClip(string meshName, LibrarySnapshot library, ClipUsageIndex? usage)
    {
        if (usage is not null)
        {
            foreach (var list in usage.ClipListsForMesh(meshName))
            {
                var stand = list.Clips.FirstOrDefault(c => c.Kind == ClipUsageKind.State && c.WeaponBlock is null
                    && string.Equals(c.SlotName, "stand", StringComparison.OrdinalIgnoreCase));
                if (stand is not null && library.FindClip(stand.ClipBaseName) is { IsReadable: true } hit) return hit;
            }
        }
        return library.DefaultPreviewClip(meshName, usage);
    }

    /// <summary>RFA024 compares the bytes of at most this many colliding copies with the document.</summary>
    public const int MaxComparedCopies = 8;

    /// <summary>A colliding copy larger than this is not read for the comparison (no stock clip comes close).</summary>
    public const int MaxComparedBytes = 8 * 1024 * 1024;

    private static byte[]? ReadForComparison(AssetLocation location, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            long size = location.Entry?.Size ?? (location.FilePath is { } path ? new FileInfo(path).Length : -1);
            return size is >= 0 and <= MaxComparedBytes ? location.ReadAllBytes() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or Cairn.Formats.AssetFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="location"/> is the document's own file: the same loose file, or the same
    /// archive entry (<see cref="ArchivedDocumentPath"/>). Paths are compared after normalisation, so a
    /// game directory spelt with a trailing separator or forward slashes still matches.
    /// </summary>
    internal static bool IsDocument(AssetLocation location, string? documentPath)
    {
        if (string.IsNullOrWhiteSpace(documentPath)) return false;
        int bar = documentPath.LastIndexOf('|');
        if (bar > 0)
        {
            return location.ArchivePath is not null
                && SamePath(location.ArchivePath, documentPath[..bar])
                && string.Equals(location.ResolvedName, documentPath[(bar + 1)..], StringComparison.OrdinalIgnoreCase);
        }
        return location.FilePath is not null && SamePath(location.FilePath, documentPath);
    }

    private static bool SamePath(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>The document-path form <see cref="Build"/> uses for a file opened from inside a VPP.</summary>
    public static string ArchivedDocumentPath(AssetLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return location.ArchivePath is null ? location.FilePath ?? location.ResolvedName : $"{location.ArchivePath}|{location.ResolvedName}";
    }
}
