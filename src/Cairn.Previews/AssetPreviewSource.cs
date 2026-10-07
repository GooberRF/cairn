using Cairn.Assets;
using Cairn.Ui.Modules;

namespace Cairn.Previews;

/// <summary>What the preview pane shows now.</summary>
public enum AssetPreviewKind
{
    Empty,
    Loading,
    Summary,
    Image,
    Text,
    Audio,
    Module,
    Hex,
    Level,
    TooLarge,
    NotFound,
}

/// <summary>
/// A file to preview: its name (the extension picks the preview) and how to read it. The reads run on a pool
/// thread. Built by the caller for a packfile entry, a file on disk or bytes in memory; <see cref="AssetPreviewPane.ShowAsset"/>
/// builds one from a resolved game-data location.
/// </summary>
/// <param name="Name">The file name (a path is allowed; only its extension and file name are used).</param>
/// <param name="ReadAll">Reads every byte.</param>
public sealed record AssetPreviewSource(string Name, Func<CancellationToken, Task<byte[]>> ReadAll)
{
    /// <summary>The size in bytes, or -1 when unknown (then no size cap applies).</summary>
    public long Size { get; init; } = -1;

    /// <summary>Reads at most the given number of bytes from the start (types without a preview show them in hex). Null: <see cref="ReadAll"/>.</summary>
    public Func<int, CancellationToken, Task<byte[]>>? ReadHead { get; init; }

    /// <summary>Files that come first when another module's preview resolves textures, frames or meshes.</summary>
    public IAssetSiblings? Siblings { get; init; }

    /// <summary>Where the file was found in the game data, when it was resolved by name ("Open in Cairn" opens it there).</summary>
    public AssetLocation? Location { get; init; }

    /// <summary>Where the bytes came from, for a document opened from them ("Open in Cairn").</summary>
    public string? Origin { get; init; }

    /// <summary>Why the file cannot be read now; the pane shows the reason instead of reading.</summary>
    public string? Unavailable { get; init; }

    /// <summary>"Open in Cairn" for a module preview, replacing the default (the location, else the bytes through the document kind).</summary>
    public Action<byte[]>? OpenInCairn { get; init; }

    /// <summary>The extension, including the dot.</summary>
    public string Extension => Path.GetExtension(Name);

    /// <summary>The file name without a folder.</summary>
    public string FileName => Path.GetFileName(Name);

    /// <summary>A source over bytes already in memory.</summary>
    public static AssetPreviewSource FromBytes(string name, byte[] bytes, IAssetSiblings? siblings = null) =>
        new(name, _ => Task.FromResult(bytes)) { Size = bytes.Length, Siblings = siblings };
}

/// <summary>The result of looking a file up by name in the game data (and the siblings first).</summary>
/// <param name="RequestedName">The name asked for.</param>
/// <param name="Location">Where it was found, or null.</param>
/// <param name="Searched">Where it was looked for, in order (siblings first).</param>
public sealed record AssetLookupResult(string RequestedName, AssetLocation? Location, IReadOnlyList<string> Searched)
{
    /// <summary>True when the file was found.</summary>
    public bool Found => Location is not null;

    /// <summary>The found file's size, or -1 when unknown (an in-memory sibling).</summary>
    public long Size
    {
        get
        {
            if (Location is not { } l) return -1;
            try
            {
                if (l.Entry is { } e) return e.Size;
                if (l.FilePath is { } p) return new FileInfo(p).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return -1;
        }
    }

    /// <summary>"name in location", or the not-found sentence with the places searched.</summary>
    public string Describe() => Location is { } l
        ? (l.IsSupersede || !string.Equals(l.ResolvedName, RequestedName, StringComparison.OrdinalIgnoreCase)
            ? $"{RequestedName} (as {l.ResolvedName})" : l.ResolvedName) + " in " + l.DisplayLocation
        : $"'{RequestedName}' was not found. Looked in: " + (Searched.Count == 0 ? "nowhere (no game folder or search folders are set)" : string.Join("; ", Searched)) + ".";
}

/// <summary>Looks files up by name the way the game does (search folders, game folder, packfiles; texture supersede).</summary>
public static class AssetLookup
{
    /// <summary>The shell's resolver (for a document folder when given) with <paramref name="siblings"/> in front; null without both.</summary>
    public static AssetResolver? ResolverFor(IShellContext? shell, IAssetSiblings? siblings, string? documentFolder = null)
    {
        AssetResolver? resolver = shell is null ? null
            : documentFolder is null ? shell.Assets.Resolver : shell.Assets.ResolverFor(documentFolder);
        return siblings.Layer(resolver);
    }

    /// <summary>Resolves <paramref name="name"/> (any thread; may read archive directories).</summary>
    /// <param name="resolver">The resolver, normally from <see cref="ResolverFor"/> (siblings already layered in).</param>
    /// <param name="name">The name as written (a path is cut to its file name).</param>
    /// <param name="siblings">The siblings layered into <paramref name="resolver"/>, named first in <see cref="AssetLookupResult.Searched"/>.</param>
    /// <param name="ct">Cancels the lookup.</param>
    public static AssetLookupResult Resolve(AssetResolver? resolver, string name, IAssetSiblings? siblings, CancellationToken ct = default)
    {
        string bare = Path.GetFileName((name ?? "").Trim().Replace('/', '\\'));
        var searched = new List<string>();
        if (siblings is not null) searched.Add(siblings.Label);
        if (resolver is not null)
        {
            try
            {
                searched.AddRange(resolver.DescribeSearchOrder());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (bare.Length == 0 || resolver is null) return new AssetLookupResult(bare, null, searched);
        AssetLocation? location = null;
        try
        {
            // The engine's request-name mapping first (an .mvf is loaded as <stem>.rfa, and so on); the resolver
            // applies the texture supersede chain to each candidate itself.
            foreach (string candidate in EngineNames(bare))
            {
                location = resolver.Resolve(candidate, ct);
                if (location is not null) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            location = null;
        }
        return new AssetLookupResult(bare, location, searched);
    }

    /// <summary>
    /// The names under which the engine looks for a requested file, in its order: the bare file name (any folder part
    /// dropped); an animation (<c>.rfa</c>, or a legacy <c>.mvf</c>) is cut at the first dot and loaded as
    /// <c>&lt;stem&gt;.rfa</c>; the exporter's mesh extensions stand for the saved ones (<c>.vcm</c> = <c>.v3c</c>,
    /// <c>.v3d</c> = <c>.v3m</c>, then <c>.v3c</c>). Texture names are returned as written: the resolver tries the
    /// supersede chain itself. Empty when nothing is left of the name.
    /// </summary>
    /// <param name="name">The name as written in a table, level or other file.</param>
    public static IReadOnlyList<string> EngineNames(string? name)
    {
        string bare = (name ?? "").Trim();
        int slash = bare.LastIndexOfAny(['\\', '/']);
        if (slash >= 0) bare = bare[(slash + 1)..];
        if (bare.Length == 0) return [];
        string ext = Path.GetExtension(bare);
        if (ext.Equals(".rfa", StringComparison.OrdinalIgnoreCase) || ext.Equals(".mvf", StringComparison.OrdinalIgnoreCase))
        {
            int dot = bare.IndexOf('.');
            return dot > 0 ? [bare[..dot] + ".rfa"] : [];
        }
        string stem = bare[..^ext.Length];
        if (ext.Equals(".vcm", StringComparison.OrdinalIgnoreCase)) return [stem + ".v3c"];
        if (ext.Equals(".v3d", StringComparison.OrdinalIgnoreCase)) return [stem + ".v3m", stem + ".v3c"];
        return [bare];
    }

    /// <summary>A preview source reading a found file (null when <paramref name="lookup"/> found nothing).</summary>
    public static AssetPreviewSource? SourceFor(AssetLookupResult lookup, IAssetSiblings? siblings)
    {
        if (lookup.Location is not { } location) return null;
        return new AssetPreviewSource(location.ResolvedName, _ => Task.FromResult(location.ReadAllBytes()))
        {
            Size = lookup.Size,
            ReadHead = (count, _) => Task.FromResult(ReadHead(location, count)),
            Siblings = siblings,
            Location = location,
            Origin = location.DisplayLocation,
        };
    }

    private static byte[] ReadHead(AssetLocation location, int count)
    {
        using var stream = location.Open();
        var buffer = new byte[Math.Max(0, count)];
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer, read, buffer.Length - read);
            if (n <= 0) break;
            read += n;
        }
        return read == buffer.Length ? buffer : buffer[..read];
    }
}
