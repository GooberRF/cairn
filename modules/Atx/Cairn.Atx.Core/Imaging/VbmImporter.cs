using Cairn.Atx.Text;
using Cairn.Workspace;

namespace Cairn.Atx.Imaging;

/// <summary>How far an import has got.</summary>
/// <param name="Completed">Frames written so far.</param>
/// <param name="Total">Frames in the whole import.</param>
/// <param name="CurrentFile">The frame being written, as a bare name.</param>
public sealed record VbmImportProgress(int Completed, int Total, string CurrentFile)
{
    /// <summary>0 to 1, for a progress bar.</summary>
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Completed / Total, 0, 1);
}

/// <summary>What to do about files the import would land on top of.</summary>
public enum VbmCollisionChoice
{
    /// <summary>Overwrite them.</summary>
    Replace,

    /// <summary>Leave them alone and skip those files.</summary>
    KeepExisting,

    /// <summary>Abandon the whole import.</summary>
    Cancel,
}

/// <summary>How an import ended.</summary>
/// <param name="Outcome">Succeeded, cancelled, or failed.</param>
/// <param name="AtxPath">The generated .atx, when there is one to open.</param>
/// <param name="Written">Everything this run put on disk, in order.</param>
/// <param name="Kept">Files left alone because they were already there.</param>
/// <param name="Failures">One line per file that could not be written.</param>
/// <param name="Message">A sentence for the user when something went wrong.</param>
public sealed record VbmImportResult(
    VbmImportOutcome Outcome,
    string? AtxPath,
    IReadOnlyList<string> Written,
    IReadOnlyList<string> Kept,
    IReadOnlyList<string> Failures,
    string? Message)
{
    /// <summary>True when there is a generated .atx worth opening.</summary>
    public bool Succeeded => Outcome == VbmImportOutcome.Succeeded;
}

/// <summary>The three ways an import can end.</summary>
public enum VbmImportOutcome
{
    Succeeded,
    Cancelled,
    Failed,
}

/// <summary>
/// Turns a <see cref="VbmImportPlan"/> into files.
///
/// <para>
/// Frames are decoded one at a time and released, so a hundred-frame 512-pixel texture costs one
/// frame of memory rather than a hundred. Everything is written to temporary names first and only
/// moved into place once every frame has encoded, so a cancel — or a disk filling up on frame
/// ninety — leaves the output folder exactly as it was found. The <c>.atx</c> goes last, through
/// <see cref="AtomicFile"/>, because it is the file that makes the rest mean something.
/// </para>
/// <para>
/// This class is UI-free on purpose: collisions come back through a callback so the app can ask
/// with the prompt it already uses for copying images in, once for the whole batch.
/// </para>
/// </summary>
public static class VbmImporter
{
    /// <summary>
    /// Runs an import. Call it from a worker thread: it decodes, encodes and writes.
    /// </summary>
    /// <param name="vbmBytes">The .vbm file's bytes.</param>
    /// <param name="plan">What to write. Must be runnable (<see cref="VbmImportPlan.CanRun"/>).</param>
    /// <param name="onCollision">
    /// Asked once, with the bare names already present, when the import would land on existing
    /// files. Null means Cancel, i.e. never overwrite anything without being told to.
    /// </param>
    /// <param name="progress">Called as each frame is encoded.</param>
    /// <param name="cancellationToken">Cancels between frames and between writes.</param>
    public static VbmImportResult Run(
        byte[] vbmBytes,
        VbmImportPlan plan,
        Func<IReadOnlyList<string>, VbmCollisionChoice>? onCollision = null,
        IProgress<VbmImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vbmBytes);
        ArgumentNullException.ThrowIfNull(plan);

        if (!plan.CanRun)
        {
            string? first = plan.Messages
                .FirstOrDefault(m => m.Severity == VbmImportSeverity.Error)?.Text;
            return Failed(first ?? "There is nothing to import.");
        }

        try
        {
            Directory.CreateDirectory(plan.OutputFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            return Failed($"'{plan.OutputFolder}' could not be created: {ex.Message}");
        }

        var targets = plan.AllPaths;
        var existing = new List<string>();
        foreach (string path in targets)
        {
            if (Exists(path)) existing.Add(path);
        }

        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (existing.Count > 0)
        {
            var choice = onCollision?.Invoke([.. existing.Select(BareName)])
                ?? VbmCollisionChoice.Cancel;
            if (choice == VbmCollisionChoice.Cancel) return Cancelled();
            if (choice == VbmCollisionChoice.KeepExisting) skip.UnionWith(existing);
        }

        // Phase 1: encode every frame into a temporary file next to where it will live. Nothing the
        // user can see changes until this has finished for all of them.
        var staged = new List<(string Temp, string Target)>(plan.FrameFileNames.Count);
        var failures = new List<string>();
        try
        {
            for (int i = 0; i < plan.FrameFileNames.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = plan.FrameFileNames[i];
                string target = plan.FramePaths[i];
                progress?.Report(new VbmImportProgress(i, plan.FrameFileNames.Count, name));
                if (skip.Contains(target)) continue;

                var image = VbmCodec.DecodeFrame(vbmBytes, plan.SourceFrames[i], plan.SourceName);
                byte[] tga = TgaWriter.Write(image, plan.FramesHaveAlpha);
                string temp = TempNameFor(target);
                File.WriteAllBytes(temp, tga);
                staged.Add((temp, target));
            }
            progress?.Report(new VbmImportProgress(
                plan.FrameFileNames.Count, plan.FrameFileNames.Count, plan.AtxFileName));
        }
        catch (OperationCanceledException)
        {
            DeleteAll(staged.Select(s => s.Temp));
            return Cancelled();
        }
        catch (ImageDecodeException ex)
        {
            DeleteAll(staged.Select(s => s.Temp));
            return Failed(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException or PathTooLongException)
        {
            DeleteAll(staged.Select(s => s.Temp));
            return Failed(WriteFailureText(plan.OutputFolder, ex));
        }

        // Phase 2: move each staged frame into place, remembering which targets this run created so
        // a failure can take back exactly those and nothing that was already there.
        var created = new List<string>();
        var written = new List<string>();
        foreach (var (temp, target) in staged)
        {
            bool isNew = !Exists(target);
            try
            {
                File.Move(temp, target, overwrite: true);
                if (isNew) created.Add(target);
                written.Add(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or NotSupportedException or ArgumentException)
            {
                failures.Add($"{Path.GetFileName(target)} — {ex.Message}");
            }
        }
        DeleteAll(staged.Select(s => s.Temp));

        if (failures.Count > 0)
        {
            DeleteAll(created);
            return new VbmImportResult(
                VbmImportOutcome.Failed, null, [], [], failures,
                WriteFailureText(plan.OutputFolder, null));
        }

        // Phase 3: the .atx itself, last, because it is what makes the frames a texture.
        if (!skip.Contains(plan.AtxPath))
        {
            bool atxIsNew = !Exists(plan.AtxPath);
            try
            {
                AtomicFile.WriteAllText(plan.AtxPath, plan.BuildText(LineEndingKind.CrLf));
                if (atxIsNew) created.Add(plan.AtxPath);
                written.Add(plan.AtxPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or NotSupportedException or ArgumentException)
            {
                DeleteAll(created);
                return new VbmImportResult(
                    VbmImportOutcome.Failed, null, [], [],
                    [$"{plan.AtxFileName} — {ex.Message}"],
                    WriteFailureText(plan.OutputFolder, ex));
            }
        }

        return new VbmImportResult(
            VbmImportOutcome.Succeeded, plan.AtxPath, written,
            [.. skip.Select(BareName)], [], null);
    }

    private static string BareName(string path)
    {
        try { return Path.GetFileName(path) is { Length: > 0 } name ? name : path; }
        catch (ArgumentException) { return path; }
    }

    private static string WriteFailureText(string folder, Exception? ex) =>
        $"The frames could not be written into '{folder}'. "
        + "Check the folder is not read-only and that the files are not open in another program."
        + (ex is null ? string.Empty : $" ({ex.Message})");

    private static VbmImportResult Failed(string message) =>
        new(VbmImportOutcome.Failed, null, [], [], [], message);

    private static VbmImportResult Cancelled() =>
        new(VbmImportOutcome.Cancelled, null, [], [], [], null);

    private static string TempNameFor(string target)
    {
        string folder = Path.GetDirectoryName(target) ?? string.Empty;
        return Path.Combine(folder, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
    }

    private static bool Exists(string path)
    {
        try { return File.Exists(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static void DeleteAll(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or NotSupportedException or ArgumentException)
            {
                // Nothing useful can be done about a leftover we cannot remove, and saying so would
                // bury the failure that is actually being reported.
            }
        }
    }
}
