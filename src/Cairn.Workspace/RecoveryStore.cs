using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cairn.Workspace;

/// <summary>One unsaved document remembered on disk.</summary>
/// <param name="Id">Stable id for the document's tab, chosen by the app.</param>
/// <param name="OriginalPath">Where the document came from, or null for an unsaved new file.</param>
/// <param name="DisplayName">What the tab was called.</param>
/// <param name="SavedUtc">When the snapshot was taken.</param>
/// <param name="DocumentKind">What the bytes are, e.g. "rfa" or "v3c", so recovery opens the right document type.</param>
/// <param name="Data">The document's serialised bytes at that moment (stored base64 in the snapshot file).</param>
public sealed record RecoverySnapshot(
    string Id, string? OriginalPath, string DisplayName, DateTime SavedUtc, string DocumentKind, byte[] Data);

/// <summary>
/// Keeps a copy of every dirty document in <c>%LOCALAPPDATA%\Cairn\recovery</c> so work
/// survives a crash. Documents are binary (.rfa, .v3c), stored base64 inside a small JSON envelope.
/// Snapshots are written atomically and every operation swallows IO failures: crash recovery must
/// never be the thing that crashes.
/// </summary>
public sealed class RecoveryStore
{
    /// <summary>
    /// Snapshots larger than this on disk are skipped when listing: a damaged or foreign file in the
    /// folder must not be read into memory whole.
    /// </summary>
    public const long MaxSnapshotFileBytes = 128L * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <param name="directory">Override the default location; used by tests.</param>
    public RecoveryStore(string? directory = null)
    {
        Directory = directory ?? DefaultDirectory;
    }

    /// <summary><c>%LOCALAPPDATA%\Cairn\recovery</c>.</summary>
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cairn", "recovery");

    /// <summary>Where snapshots are kept.</summary>
    public string Directory { get; }

    /// <summary>Writes (or replaces) the snapshot for one document. Returns false on IO failure.</summary>
    public bool Save(RecoverySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            AtomicFile.WriteAllText(PathFor(snapshot.Id), JsonSerializer.Serialize(snapshot, Options));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            // Failing to write a snapshot must never be worse than not having one: this runs on a
            // timer and, at the worst moment, from the crash handler.
            return false;
        }
    }

    /// <summary>Convenience overload that stamps the current time.</summary>
    public bool Save(string id, string? originalPath, string displayName, string documentKind, byte[] data) =>
        Save(new RecoverySnapshot(id, originalPath, displayName, DateTime.UtcNow, documentKind, data));

    /// <summary>Every snapshot currently on disk, newest first. Unreadable files are skipped.</summary>
    public IReadOnlyList<RecoverySnapshot> List()
    {
        var result = new List<RecoverySnapshot>();
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return result;
            foreach (string file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
            {
                try
                {
                    if (new FileInfo(file).Length > MaxSnapshotFileBytes) continue;
                    var snapshot = JsonSerializer.Deserialize<RecoverySnapshot>(File.ReadAllText(file), Options);
                    if (snapshot is not null && !string.IsNullOrEmpty(snapshot.Id) && snapshot.Data is not null)
                        result.Add(snapshot);
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
                    or NotSupportedException or FormatException)
                {
                    // Skip the damaged snapshot; the rest are still worth offering.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result;
        }
        result.Sort((a, b) => b.SavedUtc.CompareTo(a.SavedUtc));
        return result;
    }

    /// <summary>Forgets one document's snapshot, e.g. after it is saved or closed cleanly.</summary>
    public void Discard(string id)
    {
        try
        {
            string path = PathFor(id);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Forgets every snapshot, e.g. once the user dismisses the recovery prompt.</summary>
    public void Clear()
    {
        foreach (var snapshot in List()) Discard(snapshot.Id);
    }

    private string PathFor(string id)
    {
        var safe = new string([.. id.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)]);
        if (safe.Length == 0) safe = "document";
        return Path.Combine(Directory, safe + ".json");
    }
}
