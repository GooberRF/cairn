using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Cairn.Formats.Vpp;
using Cairn.Vpp.Editing;
using Cairn.Vpp.Model;

namespace Cairn.Vpp.Writing;

/// <summary>Stage of a save, for progress display.</summary>
public enum VppSavePhase
{
    Checking,
    Writing,
    Flushing,
    Verifying,
    Replacing,
    Done,
}

/// <summary>Progress of a save.</summary>
/// <param name="Phase">What the save is doing.</param>
/// <param name="BytesDone">Bytes written so far.</param>
/// <param name="BytesTotal">Bytes the packfile will have.</param>
/// <param name="EntriesDone">Entries written so far.</param>
/// <param name="EntriesTotal">Entries in the packfile.</param>
/// <param name="CurrentName">The entry being written, if any.</param>
public sealed record VppSaveProgress(VppSavePhase Phase, long BytesDone, long BytesTotal, int EntriesDone, int EntriesTotal, string? CurrentName)
{
    /// <summary>0 to 1.</summary>
    public double Fraction => BytesTotal <= 0 ? 0 : Math.Clamp((double)BytesDone / BytesTotal, 0, 1);
}

/// <summary>Thrown when a packfile cannot be written (a source vanished or ended early, a limit was exceeded, ...).</summary>
public class VppSaveException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>
/// Streams a package to a stream in the RF1 layout: a 2048-byte header (signature, version 1, entry
/// count, archive size), the directory of 64-byte entries padded to 2048, then each entry's data padded
/// to 2048 with zeros. Data is copied through one pooled buffer; nothing is held in memory.
/// </summary>
public static class VppWriter
{
    private const int CopyBufferBytes = 1 << 20;
    private const long ProgressStepBytes = 4 << 20;

    /// <summary>Writes <paramref name="items"/> to <paramref name="output"/> from its current position, zero-filling all padding.</summary>
    /// <exception cref="VppSaveException">A name or size cannot be stored, or a source cannot deliver its data.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public static void Write(IReadOnlyList<VppItem> items, Stream output, IProgress<VppSaveProgress>? progress = null, CancellationToken cancellationToken = default) =>
        WriteCore(items, output, progress, cancellationToken, null, copyPadding: false);

    /// <summary>
    /// Writes a package, keeping what the original packfile had in places the game never reads: the
    /// padding after each entry still taken from a packfile is copied from there, and when the entry list
    /// is unchanged (<see cref="VppPackage.HasOriginalDirectory"/>) so is the directory's slack. An
    /// unmodified packfile therefore re-saves byte for byte, whatever tool wrote it; new regions get zeros.
    /// </summary>
    public static void Write(VppPackage package, Stream output, IProgress<VppSaveProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        WriteCore(package.Items, output, progress, cancellationToken, package.HasOriginalDirectory ? package.Path : null, copyPadding: true);
    }

    /// <summary>As <see cref="Write(VppPackage, Stream, IProgress{VppSaveProgress}?, CancellationToken)"/>, returning the SHA-256 of each entry's data as it was read (for verification).</summary>
    internal static byte[][] WriteHashed(VppPackage package, Stream output, IProgress<VppSaveProgress>? progress, CancellationToken cancellationToken)
    {
        var hashes = new byte[package.Items.Length][];
        WriteCore(package.Items, output, progress, cancellationToken, package.HasOriginalDirectory ? package.Path : null, copyPadding: true, hashes);
        return hashes;
    }

    private static void WriteCore(IReadOnlyList<VppItem> items, Stream output, IProgress<VppSaveProgress>? progress,
        CancellationToken cancellationToken, string? slackFrom, bool copyPadding, byte[][]? hashes = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(output);
        long total = VppPackage.ComputeArchiveBytes(items.Select(i => i.Size), items.Count);
        if (total > uint.MaxValue) throw new VppSaveException($"The packfile would be {total:N0} bytes; the format cannot describe more than {uint.MaxValue:N0}.");

        var header = new byte[VppArchive.BlockSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), VppArchive.Signature);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)items.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)total);
        output.Write(header);

        var directory = new byte[VppPackage.AlignUp((long)items.Count * VppArchive.EntryBytes)];
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (VppNames.Validate(item.Name) is { } problem) throw new VppSaveException($"Entry '{item.Name}' cannot be stored: {problem}");
            if (item.Size > int.MaxValue) throw new VppSaveException($"Entry '{item.Name}' is too large ({item.Size:N0} bytes).");
            int at = i * VppArchive.EntryBytes;
            VppNames.Encoding.GetBytes(item.Name, directory.AsSpan(at, VppArchive.NameBytes));
            BinaryPrimitives.WriteInt32LittleEndian(directory.AsSpan(at + VppArchive.NameBytes), (int)item.Size);
        }

        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        var archives = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (slackFrom is not null)
            {
                // Unchanged entry list: the slack after the last directory slot is whatever the original held.
                int used = items.Count * VppArchive.EntryBytes;
                var archive = ArchiveStream(slackFrom, archives);
                archive.Position = VppArchive.BlockSize + used;
                ReadUpTo(archive, directory.AsSpan(used));
            }
            output.Write(directory);

            long done = header.Length + directory.Length;
            long nextReport = 0;
            progress?.Report(new VppSaveProgress(VppSavePhase.Writing, done, total, 0, items.Count, null));
            for (int i = 0; i < items.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = items[i];
                using var hash = hashes is null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using (var source = OpenSource(item, archives))
                {
                    long remaining = item.Size;
                    while (remaining > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int n = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                        if (n <= 0) throw new VppSaveException($"Entry '{item.Name}' ended after {item.Size - remaining:N0} of {item.Size:N0} bytes ({item.Source.Describe()}).");
                        hash?.AppendData(buffer, 0, n);
                        output.Write(buffer, 0, n);
                        remaining -= n;
                        done += n;
                        if (progress is not null && done >= nextReport)
                        {
                            progress.Report(new VppSaveProgress(VppSavePhase.Writing, done, total, i, items.Count, item.Name));
                            nextReport = done + ProgressStepBytes;
                        }
                    }
                    if (source is FileStream file && item.Source is FileSource added) CheckStillAsRecorded(file, added, item.Name);
                }
                if (hashes is not null) hashes[i] = hash!.GetHashAndReset();
                int pad = (int)(VppPackage.AlignUp(item.Size) - item.Size);
                if (pad > 0)
                {
                    Array.Clear(buffer, 0, pad);
                    if (copyPadding && item.Source is ArchiveSource from)
                    {
                        // The original padding (zeros, or stale bytes some tools leave): the game never reads it.
                        var archive = ArchiveStream(from.ArchivePath, archives);
                        archive.Position = from.Offset + from.Length;
                        ReadUpTo(archive, buffer.AsSpan(0, pad));
                    }
                    output.Write(buffer, 0, pad);
                    done += pad;
                }
            }
            progress?.Report(new VppSaveProgress(VppSavePhase.Writing, done, total, items.Count, items.Count, null));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            foreach (var stream in archives.Values) stream.Dispose();
        }
    }

    /// <summary>Throws when the open file's size or last write time differs from what was recorded when it was added.</summary>
    private static void CheckStillAsRecorded(FileStream file, FileSource added, string entryName)
    {
        long length = file.Length;
        var written = File.GetLastWriteTimeUtc(file.SafeFileHandle);
        if (length == added.Length && written == added.LastWriteUtc) return;
        string what = length != added.Length ? $"its size is now {length:N0} bytes instead of {added.Length:N0}" : "it was modified";
        throw new VppSaveException($"'{added.FilePath}' (entry '{entryName}') changed while the packfile was being saved: {what}. The target was not changed; save again to take the current file.");
    }

    private static FileStream ArchiveStream(string path, Dictionary<string, FileStream> archives)
    {
        if (!archives.TryGetValue(path, out var stream))
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            archives.Add(path, stream);
        }
        return stream;
    }

    /// <summary>Fills as much of <paramref name="target"/> as the stream still has; the rest keeps its (zero) content.</summary>
    private static void ReadUpTo(Stream stream, Span<byte> target)
    {
        while (target.Length > 0)
        {
            int n = stream.Read(target);
            if (n <= 0) return;
            target = target[n..];
        }
    }

    /// <summary>
    /// Archive entries share one stream per packfile (opened read-only and allowing delete, so the
    /// packfile being rewritten can still be replaced afterwards); other sources open their own.
    /// </summary>
    private static Stream OpenSource(VppItem item, Dictionary<string, FileStream> archives)
    {
        try
        {
            if (item.Source is ArchiveSource archive)
            {
                var stream = ArchiveStream(archive.ArchivePath, archives);
                if (archive.Offset + archive.Length > stream.Length)
                {
                    throw new VppSaveException($"Entry '{item.Name}' lies beyond the end of '{Path.GetFileName(archive.ArchivePath)}' (the packfile is truncated).");
                }
                return new WindowStream(stream, archive.Offset, archive.Length, ownsInner: false);
            }
            if (item.Source is FileSource added)
            {
                // No writers while the file is copied, and it must still be the file that was checked: a file an
                // editor saves during the save would otherwise be stored torn (part old, part new).
                var file = new FileStream(added.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
                try
                {
                    CheckStillAsRecorded(file, added, item.Name);
                    return file;
                }
                catch
                {
                    file.Dispose();
                    throw;
                }
            }
            return item.Source.Open();
        }
        catch (Exception ex) when (ex is (IOException or UnauthorizedAccessException) and not VppSaveException)
        {
            throw new VppSaveException($"Entry '{item.Name}' could not be read ({item.Source.Describe()}): {ex.Message}", ex);
        }
    }
}
