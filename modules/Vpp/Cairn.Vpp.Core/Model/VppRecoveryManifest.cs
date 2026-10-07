using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cairn.Vpp.Model;

/// <summary>One entry of a recovery manifest.</summary>
/// <param name="Name">Current entry name.</param>
/// <param name="Kind">"archive", "file", "memory" or "dropped" (in-memory data over the size cap).</param>
/// <param name="State">The item state.</param>
/// <param name="OriginalName">Name in the packfile on disk, if it came from there.</param>
/// <param name="OriginalIndex">Position in the packfile on disk, or -1.</param>
/// <param name="ArchivePath">For "archive": the packfile holding the data.</param>
/// <param name="Offset">For "archive": data offset.</param>
/// <param name="Length">Data length (all kinds).</param>
/// <param name="FilePath">For "file": the file added.</param>
/// <param name="LastWriteUtc">For "file": its timestamp when added.</param>
/// <param name="Base64">For "memory": the bytes.</param>
/// <param name="Sha256">For "spill": hex SHA-256 of the side file's bytes.</param>
public sealed record VppRecoveryEntry(
    string Name,
    string Kind,
    VppItemState State,
    string? OriginalName = null,
    int OriginalIndex = -1,
    string? ArchivePath = null,
    long Offset = 0,
    long Length = 0,
    string? FilePath = null,
    DateTime? LastWriteUtc = null,
    string? Base64 = null,
    string? Sha256 = null);

/// <summary>An entry a recovery could not bring back, and why.</summary>
public sealed record VppRecoveryProblem(string EntryName, string Reason)
{
    public override string ToString() => $"{EntryName}: {Reason}";
}

/// <summary>
/// What <see cref="VppRecoveryManifest.Restore()"/> rebuilt. When <see cref="IsComplete"/> is false the package
/// holds only the entries that could be restored and has no path (a new, unsaved packfile), so it can never be
/// saved over the original without the user deciding first.
/// </summary>
public sealed record VppRecoveryResult(VppPackage Package, IReadOnlyList<VppRecoveryProblem> Problems, bool OriginalChanged)
{
    public bool IsComplete => Problems.Count == 0 && !OriginalChanged;
}

/// <summary>
/// A small, JSON-serialisable description of an edited packfile for crash recovery: the original path
/// and the ordered entries as references (original entry, added file, small in-memory bytes) rather than
/// the packfile's data.
/// </summary>
public sealed record VppRecoveryManifest(int FormatVersion, string? OriginalPath, VppArchiveStamp? OriginalStamp, ImmutableArray<VppRecoveryEntry> Entries)
{
    /// <summary>Largest in-memory entry stored inline (larger ones are recorded as dropped).</summary>
    public const int DefaultMaxInlineBytes = 1 << 20;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Describes a package. Files on disk are referenced by path, size and time; in-memory data up to
    /// <paramref name="maxInlineBytes"/> is embedded, larger data is written to a side file in
    /// <paramref name="spillFolder"/> (named by its SHA-256, so repeated captures write it once; side files no
    /// longer referenced are removed). Without a spill folder (or when writing it fails) the entry is recorded
    /// as "dropped" and <see cref="Restore()"/> reports it.
    /// </summary>
    public static VppRecoveryManifest Capture(VppPackage package, int maxInlineBytes = DefaultMaxInlineBytes, string? spillFolder = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        var spilled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = package.Items.Select(item => item.Source switch
        {
            ArchiveSource a => new VppRecoveryEntry(item.Name, "archive", item.State, item.OriginalName, item.OriginalIndex, a.ArchivePath, a.Offset, a.Length),
            FileSource f => new VppRecoveryEntry(item.Name, "file", item.State, item.OriginalName, item.OriginalIndex, Length: f.Length, FilePath: f.FilePath, LastWriteUtc: f.LastWriteUtc),
            MemorySource m when m.Bytes.Length <= maxInlineBytes =>
                new VppRecoveryEntry(item.Name, "memory", item.State, item.OriginalName, item.OriginalIndex, Length: m.Bytes.Length, Base64: Convert.ToBase64String(m.Bytes)),
            MemorySource m when spillFolder is not null && Spill(m.Bytes, spillFolder) is { } side =>
                new VppRecoveryEntry(item.Name, "spill", item.State, item.OriginalName, item.OriginalIndex, Length: m.Bytes.Length, FilePath: Track(side.Path), Sha256: side.Hash),
            _ => new VppRecoveryEntry(item.Name, "dropped", item.State, item.OriginalName, item.OriginalIndex, Length: item.Size),
        }).ToImmutableArray();
        if (spillFolder is not null) RemoveUnreferenced(spillFolder, spilled);
        return new VppRecoveryManifest(1, package.Path, package.Stamp, entries);

        string Track(string path) { spilled.Add(path); return path; }
    }

    private static (string Path, string Hash)? Spill(byte[] bytes, string folder)
    {
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        string path = Path.Combine(Path.GetFullPath(folder), hash + ".bin");
        try
        {
            if (new FileInfo(path) is { Exists: true } info && info.Length == bytes.Length) return (path, hash);
            Directory.CreateDirectory(folder);
            string temp = path + ".part";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
            return (path, hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void RemoveUnreferenced(string folder, HashSet<string> keep)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (var file in Directory.EnumerateFiles(folder, "*.bin"))
                if (!keep.Contains(Path.GetFullPath(file))) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>UTF-8 JSON.</summary>
    public byte[] ToJson() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    /// <summary>Reads a manifest written by <see cref="ToJson"/>.</summary>
    /// <exception cref="Formats.AssetFormatException">Not a packfile recovery manifest.</exception>
    public static VppRecoveryManifest FromJson(ReadOnlySpan<byte> json)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<VppRecoveryManifest>(json, Json);
            if (manifest is null || manifest.FormatVersion != 1 || manifest.Entries.IsDefault)
            {
                throw new Formats.AssetFormatException("The recovery data is not a packfile manifest.");
            }
            return manifest;
        }
        catch (JsonException ex)
        {
            throw new Formats.AssetFormatException($"The recovery data cannot be read: {ex.Message}", ex);
        }
    }

    /// <summary>The names of <see cref="Restore()"/>'s problems; the package has no path when any entry is missing.</summary>
    public VppPackage Restore(out IReadOnlyList<string> lost)
    {
        var result = Restore();
        lost = [.. result.Problems.Select(p => p.EntryName)];
        return result.Package;
    }

    /// <summary>
    /// Rebuilds the package. Nothing is dropped or mixed silently: an entry read from the original packfile
    /// is restored only when that packfile still has the size and time the manifest recorded (otherwise every
    /// such entry is a problem), an added file only when it is still as recorded, spilled data only when the
    /// side file's hash matches. When anything is a problem, the package holds the rest and has no path.
    /// </summary>
    public VppRecoveryResult Restore()
    {
        var problems = new List<VppRecoveryProblem>();
        VppPackage? original = null;
        bool originalChanged = false;
        if (OriginalPath is not null)
        {
            try { original = File.Exists(OriginalPath) ? VppPackage.Open(OriginalPath) : null; }
            catch (Exception ex) when (ex is Formats.AssetFormatException or IOException or UnauthorizedAccessException) { original = null; }
            originalChanged = original is null || (OriginalStamp is not null && original.Stamp != OriginalStamp);
        }

        var items = ImmutableArray.CreateBuilder<VppItem>();
        foreach (var e in Entries)
        {
            string? reason = null;
            VppSource? source = e.Kind switch
            {
                "archive" => ArchiveEntry(e, original, originalChanged, out reason),
                "file" => AddedFile(e, out reason),
                "memory" when e.Base64 is not null => new MemorySource(Convert.FromBase64String(e.Base64)),
                "spill" => Spilled(e, out reason),
                _ => null,
            };
            if (source is null)
            {
                problems.Add(new VppRecoveryProblem(e.Name, reason ?? $"its new data ({e.Length:N0} bytes) was too large to keep for recovery"));
                continue;
            }
            items.Add(new VppItem(e.Name, source, e.State) { OriginalName = e.OriginalName, OriginalIndex = e.OriginalIndex });
        }
        bool complete = problems.Count == 0 && !originalChanged;
        var restored = complete && original is not null
            ? new VppPackage(original.Path, items.ToImmutable(), original.Stamp) { OriginalCount = original.OriginalCount, HeaderArchiveSize = original.HeaderArchiveSize }
            : new VppPackage(null, items.ToImmutable());
        return new VppRecoveryResult(restored, problems, originalChanged);
    }

    private ArchiveSource? ArchiveEntry(VppRecoveryEntry e, VppPackage? original, bool originalChanged, out string? reason)
    {
        reason = null;
        if (e.ArchivePath is null) return null;
        if (OriginalPath is not null && string.Equals(Path.GetFullPath(OriginalPath), Path.GetFullPath(e.ArchivePath), StringComparison.OrdinalIgnoreCase))
        {
            if (original is null) { reason = "the packfile it was read from is gone or unreadable"; return null; }
            if (originalChanged) { reason = "the packfile changed on disk since the recovery data was written"; return null; }
            // The entry must still be exactly where the manifest saw it.
            bool present = original.Items.Any(i => i.Source is ArchiveSource a && a.Offset == e.Offset && a.Length == e.Length
                && (e.OriginalName is null || Editing.VppNames.Comparer.Equals(i.Name, e.OriginalName)));
            if (!present) reason = "its data is no longer where it was in the packfile";
            return present ? new ArchiveSource(original.Path!, e.Offset, (int)e.Length) : null;
        }
        var info = new FileInfo(e.ArchivePath);
        if (info.Exists && e.Offset + e.Length <= info.Length) return new ArchiveSource(info.FullName, e.Offset, (int)e.Length);
        reason = $"the packfile it came from ('{Path.GetFileName(e.ArchivePath)}') is gone or shorter";
        return null;
    }

    private static FileSource? AddedFile(VppRecoveryEntry e, out string? reason)
    {
        reason = "the recovery data does not name its file";
        if (e.FilePath is null || e.LastWriteUtc is not { } time) return null;
        var source = new FileSource(e.FilePath, e.Length, time);
        reason = source.CheckUnchanged() is { } why ? $"'{e.FilePath}': {why}" : null;
        return reason is null ? source : null;
    }

    private static MemorySource? Spilled(VppRecoveryEntry e, out string? reason)
    {
        reason = "its kept data is missing or damaged";
        try
        {
            if (e.FilePath is null || e.Sha256 is null || new FileInfo(e.FilePath) is not { Exists: true } info || info.Length != e.Length) return null;
            var bytes = File.ReadAllBytes(e.FilePath);
            if (!string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), e.Sha256, StringComparison.OrdinalIgnoreCase)) return null;
            reason = null;
            return new MemorySource(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
