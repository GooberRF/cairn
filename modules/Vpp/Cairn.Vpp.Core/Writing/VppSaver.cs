using Cairn.Vpp.Model;
using Cairn.Vpp.Validation;

namespace Cairn.Vpp.Writing;

/// <summary>Options for <see cref="VppSaver.SaveAsync"/>.</summary>
/// <param name="KeepBackup">Keep the previous file as "&lt;name&gt;.bak" next to the target.</param>
/// <param name="AllowWarnings">Ignored for now: warnings never block a save; errors always do.</param>
public sealed record VppSaveOptions(bool KeepBackup = false, bool AllowWarnings = true)
{
    public static VppSaveOptions Default { get; } = new();

    /// <summary>Test seam: replaces <see cref="VppPackage.Open"/> for reading the saved file back after the swap.</summary>
    internal Func<string, VppPackage>? ReopenForTest { get; init; }
}

/// <summary>Thrown when a save is refused before anything is written; <see cref="Problems"/> says why.</summary>
public sealed class VppSaveBlockedException(IReadOnlyList<VppProblem> problems)
    : VppSaveException("The packfile cannot be saved: " + string.Join(" ", problems.Where(p => p.Severity == VppSeverity.Error).Select(p => p.Message)))
{
    public IReadOnlyList<VppProblem> Problems { get; } = problems;
}

/// <summary>
/// Thrown when the save itself succeeded (the target holds the new packfile) but the saved file could not be read
/// back, even after retrying for a few seconds (for example a scanner holding it). The caller must treat the save
/// as done and must not read entries through its old package: their offsets point into the replaced file.
/// </summary>
public sealed class VppSavedButUnreadableException(string targetPath, Exception inner)
    : IOException($"'{Path.GetFileName(targetPath)}' was saved, but it could not be read back: {inner.Message}", inner)
{
    public string TargetPath { get; } = targetPath;
}

/// <summary>
/// Saves a package safely: never in place. The data is streamed to a temporary file next to the
/// target, flushed to disk, verified by re-reading its directory, then swapped in atomically. The
/// target may be the very packfile the entries are read from: its stream is opened read-only with
/// delete sharing and closed before the swap. A failed or cancelled save leaves the target untouched
/// and removes the temporary file.
/// </summary>
public static class VppSaver
{
    /// <summary>Validation errors plus disk checks (changed added files, packfile changed on disk): what would block a save.</summary>
    public static IReadOnlyList<VppProblem> CheckBeforeSave(VppPackage package) =>
        [.. VppValidator.Validate(package), .. VppValidator.CheckSources(package)];

    /// <summary>Saves on a worker thread and returns the saved package (every entry an <see cref="ArchiveSource"/> of the new file).</summary>
    /// <exception cref="VppSaveBlockedException">Validation or the disk checks found errors; nothing was written.</exception>
    /// <exception cref="VppSaveException">Writing, verifying or replacing failed; the target is unchanged.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; the target is unchanged.</exception>
    public static Task<VppPackage> SaveAsync(VppPackage package, string targetPath, IProgress<VppSaveProgress>? progress = null,
        CancellationToken cancellationToken = default, VppSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        return Task.Run(() => Save(package, targetPath, progress, cancellationToken, options ?? VppSaveOptions.Default), cancellationToken);
    }

    /// <summary>The synchronous core of <see cref="SaveAsync"/>.</summary>
    public static VppPackage Save(VppPackage package, string targetPath, IProgress<VppSaveProgress>? progress, CancellationToken cancellationToken, VppSaveOptions options)
    {
        string target = Path.GetFullPath(targetPath);
        int count = package.Items.Length;
        long total = package.ArchiveBytes;
        progress?.Report(new VppSaveProgress(VppSavePhase.Checking, 0, total, 0, count, null));
        var problems = CheckBeforeSave(package);
        if (VppValidator.HasErrors(problems)) throw new VppSaveBlockedException(problems);

        string folder = Path.GetDirectoryName(target) ?? throw new VppSaveException($"'{target}' has no folder.");
        bool targetExisted = File.Exists(target);
        if (targetExisted && File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly))
        {
            throw new VppSaveException($"'{Path.GetFileName(target)}' is read-only, so it was not changed. Clear the read-only attribute in the file's Properties, or use Save As to save under another name.");
        }
        Directory.CreateDirectory(folder);
        SweepStaleTemps(folder, Path.GetFileName(target));
        string temp = Path.Combine(folder, $"~{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        bool tempInPlace = false;
        try
        {
            byte[][] hashes;
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                tempInPlace = true;
                output.SetLength(total); // reserve the space up front: a full disk fails here, not at 90 %
                hashes = VppWriter.WriteHashed(package, output, progress, cancellationToken);
                if (output.Position != total) throw new VppSaveException($"Wrote {output.Position:N0} bytes but expected {total:N0}.");
                progress?.Report(new VppSaveProgress(VppSavePhase.Flushing, total, total, count, count, null));
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new VppSaveProgress(VppSavePhase.Verifying, total, total, count, count, null));
            Verify(package, temp, total, hashes, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new VppSaveProgress(VppSavePhase.Replacing, total, total, count, count, null));
            ReplaceTarget(temp, target, options.KeepBackup);
            tempInPlace = false;
        }
        catch (Exception ex) when (ex is not (VppSaveException or OperationCanceledException) && ex is IOException or UnauthorizedAccessException)
        {
            throw new VppSaveException($"Saving '{Path.GetFileName(target)}' failed: {ex.Message}", ex);
        }
        finally
        {
            // A failed swap that left nothing at the target keeps the new file (its name is in the error).
            if (tempInPlace && (!targetExisted || File.Exists(target))) TryDelete(temp);
        }

        var saved = Reopen(target, options.ReopenForTest ?? VppPackage.Open);
        progress?.Report(new VppSaveProgress(VppSavePhase.Done, total, total, count, count, null));
        return saved;
    }

    /// <summary>Delays between attempts to read the saved file back (about 3.5 s in all).</summary>
    private static readonly int[] ReopenDelaysMs = [100, 200, 400, 800, 1000, 1000];

    /// <summary>
    /// Reads the saved file back. The swap is done, so a transient lock (antivirus, indexer) must not report a failed
    /// save: it is retried with backoff, and a lasting failure becomes <see cref="VppSavedButUnreadableException"/>.
    /// </summary>
    private static VppPackage Reopen(string target, Func<string, VppPackage> open)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return open(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= ReopenDelaysMs.Length) throw new VppSavedButUnreadableException(target, ex);
                Thread.Sleep(ReopenDelaysMs[attempt]);
            }
        }
    }

    /// <summary>
    /// Removes temporary files earlier saves to <paramref name="targetName"/> left in <paramref name="folder"/>
    /// (a crash, a locked file): only Cairn's own names "~name.&lt;32 hex&gt;.tmp|.old", only when older than an
    /// hour (a save in another window may still be running), never anything else.
    /// </summary>
    private static void SweepStaleTemps(string folder, string targetName)
    {
        try
        {
            string prefix = "~" + targetName + ".";
            foreach (var file in Directory.EnumerateFiles(folder, prefix + "*"))
            {
                string rest = Path.GetFileName(file)[prefix.Length..];
                if (rest.Length != 36 || !(rest.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || rest.EndsWith(".old", StringComparison.OrdinalIgnoreCase))) continue;
                if (!rest[..32].All(Uri.IsHexDigit)) continue;
                var info = new FileInfo(file);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LastWriteTimeUtc > DateTime.UtcNow.AddHours(-1)) continue;
                if (info.IsReadOnly) info.IsReadOnly = false;
                info.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void Verify(VppPackage package, string temp, long total, byte[][] hashes, CancellationToken cancellationToken)
    {
        VppPackage written;
        try
        {
            written = VppPackage.Open(temp);
        }
        catch (Cairn.Formats.AssetFormatException ex)
        {
            throw new VppSaveException($"The written packfile does not read back: {ex.Message}", ex);
        }
        if (new FileInfo(temp).Length != total) throw new VppSaveException("The written packfile has the wrong length.");
        if (written.Items.Length != package.Items.Length) throw new VppSaveException("The written packfile has the wrong number of entries.");
        for (int i = 0; i < written.Items.Length; i++)
        {
            var a = package.Items[i];
            var b = written.Items[i];
            if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal) || a.Size != b.Size)
            {
                throw new VppSaveException($"Entry {i} read back as '{b.Name}' ({b.Size:N0} bytes) instead of '{a.Name}' ({a.Size:N0} bytes).");
            }
        }

        // Content: every entry's data as written must hash to what was read from its source.
        using var file = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            for (int i = 0; i < written.Items.Length; i++)
            {
                var source = (ArchiveSource)written.Items[i].Source;
                file.Position = source.Offset;
                long remaining = source.Length;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int n = file.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (n <= 0) throw new VppSaveException($"The written entry '{written.Items[i].Name}' is cut short.");
                    hash.AppendData(buffer, 0, n);
                    remaining -= n;
                }
                if (!hash.GetHashAndReset().AsSpan().SequenceEqual(hashes[i]))
                {
                    throw new VppSaveException($"The written entry '{written.Items[i].Name}' does not match its source ({package.Items[i].Source.Describe()}). The target was not changed.");
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Swaps the finished file in with two renames (they work while readers hold the old file open with
    /// delete sharing): the old file goes aside under Cairn's own temporary name, the new one takes its
    /// place, and only then does the old one become the ".bak" (replacing the previous backup) or get
    /// deleted. Any failure before the new file is in place puts the old one back, so neither the target
    /// nor an existing ".bak" changes; if even that fails, the error names where both files are.
    /// </summary>
    private static void ReplaceTarget(string temp, string target, bool keepBackup)
    {
        string name = Path.GetFileName(target);
        if (!File.Exists(target))
        {
            Retry(() => File.Move(temp, target));
            return;
        }
        string aside = Path.Combine(Path.GetDirectoryName(target)!, $"~{name}.{Guid.NewGuid():N}.old");
        var created = File.GetCreationTimeUtc(target);
        try
        {
            Retry(() => File.Move(target, aside));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new VppSaveException($"'{name}' could not be replaced (is it open in another program, or the game?): {ex.Message} The file was not changed.", ex);
        }
        try
        {
            Retry(() => File.Move(temp, target));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (TryMove(aside, target)) throw new VppSaveException($"'{name}' could not be replaced: {ex.Message} The file was not changed.", ex);
            throw new VppSaveException($"'{name}' could not be replaced and the old file could not be put back: the previous packfile is '{aside}' and the new one '{temp}'. Rename one of them to '{name}'.", ex);
        }
        try { File.SetCreationTimeUtc(target, created); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (keepBackup)
        {
            // The save is done; if the old file cannot become the .bak it stays beside it under its temporary name.
            try { Retry(() => File.Move(aside, target + ".bak", overwrite: true)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        else
        {
            TryDelete(aside);
        }
    }

    private static void Retry(Action action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (attempt < 4 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100 * (attempt + 1));
            }
        }
    }

    private static bool TryMove(string from, string to)
    {
        try { Retry(() => File.Move(from, to)); return true; } catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
