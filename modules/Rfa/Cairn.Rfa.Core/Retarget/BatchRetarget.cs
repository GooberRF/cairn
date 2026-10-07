using System.Collections.Immutable;
using Cairn.Formats;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Workspace;

namespace Cairn.Rfa.Retarget;

/// <summary>What to do when an output file already exists (or an earlier item of the batch already claimed the name).</summary>
public enum CollisionDecision
{
    /// <summary>Replace the existing file.</summary>
    Overwrite,
    /// <summary>Leave the existing file alone and skip this item.</summary>
    Skip,
    /// <summary>Write under a free name (<c>name_2.rfa</c>, <c>name_3.rfa</c>, ...).</summary>
    Rename,
    /// <summary>Stop the batch; this and every later item is reported as cancelled.</summary>
    Cancel,
}

/// <summary>How one batch item ended.</summary>
public enum BatchItemStatus
{
    /// <summary>Retargeted (and written, when files are written).</summary>
    Succeeded,
    /// <summary>The source could not be read or the retarget could not run; see the error.</summary>
    Failed,
    /// <summary>The output name was taken and the collision decision was Skip.</summary>
    Skipped,
    /// <summary>The batch was stopped before this item finished.</summary>
    Cancelled,
}

/// <summary>One clip to retarget onto one target rig.</summary>
/// <param name="SourceName">The source clip's file name (for messages and the <c>{source}</c> / <c>{clip}</c> tokens).</param>
/// <param name="Source">The source rig (the mesh the clip was made for).</param>
/// <param name="Target">The target rig, with its reference clip.</param>
public sealed record BatchItem(string SourceName, RetargetRig Source, RetargetRig Target)
{
    /// <summary>The source clip, when already loaded. One of this and <see cref="SourceBytes"/> must be set.</summary>
    public RfaClip? SourceClip { get; init; }

    /// <summary>The source clip's file image, read when <see cref="SourceClip"/> is null.</summary>
    public ReadOnlyMemory<byte>? SourceBytes { get; init; }

    /// <summary>The <c>{clip}</c> token; null derives it from <see cref="SourceName"/> (the base name after its first underscore: park_jeep_driver -> jeep_driver).</summary>
    public string? ClipName { get; init; }

    /// <summary>The <c>{target}</c> token (for example the target mesh name); null uses the target profile name.</summary>
    public string? TargetName { get; init; }

    /// <summary>A bone map for this pair; null maps automatically.</summary>
    public BoneMap? BoneMap { get; init; }

    /// <summary>Retarget settings for this item (an automatic per-clip preset); null uses the batch's <see cref="BatchOptions.Retarget"/>.</summary>
    public RetargetOptions? Options { get; init; }
}

/// <summary>Settings shared by every item of a batch.</summary>
public sealed record BatchOptions
{
    /// <summary>Retarget settings.</summary>
    public RetargetOptions Retarget { get; init; } = RetargetOptions.Default;

    /// <summary>Folder the outputs go to (created when needed). Required when <see cref="WriteFiles"/> is true.</summary>
    public string? OutputFolder { get; init; }

    /// <summary>
    /// Output file name pattern. Tokens: <c>{rig}</c> target profile name, <c>{clip}</c> clip name,
    /// <c>{source}</c> source base name, <c>{target}</c> target name, <c>{sourcerig}</c> source
    /// profile name. ".rfa" is appended when missing.
    /// </summary>
    public string NamingPattern { get; init; } = "af_{rig}_{clip}.rfa";

    /// <summary>
    /// Called with the full path when an output name is taken (an existing file, or an earlier item
    /// of this batch). Null means <see cref="CollisionDecision.Skip"/>: nothing is ever overwritten
    /// without being asked.
    /// </summary>
    public Func<string, CollisionDecision>? OnCollision { get; init; }

    /// <summary>False runs everything in memory: no file is read or written, results only (with the output bytes).</summary>
    public bool WriteFiles { get; init; } = true;

    /// <summary>Build a <see cref="RetargetReport"/> per successful item.</summary>
    public bool BuildReports { get; init; } = true;

    /// <summary>Ticks between report samples.</summary>
    public int ReportSampleStep { get; init; } = 160;
}

/// <summary>The outcome of one batch item.</summary>
/// <param name="Item">The item.</param>
/// <param name="Status">How it ended.</param>
public sealed record BatchItemResult(BatchItem Item, BatchItemStatus Status)
{
    /// <summary>True when the item succeeded.</summary>
    public bool Success => Status == BatchItemStatus.Succeeded;

    /// <summary>When it failed: what is wrong and how to fix it.</summary>
    public string? Error { get; init; }

    /// <summary>The output path (written, or the one it would have been when files are not written); null when no name was settled.</summary>
    public string? OutputPath { get; init; }

    /// <summary>The output file name.</summary>
    public string? OutputName { get; init; }

    /// <summary>The serialised output clip (also when nothing was written).</summary>
    public ImmutableArray<byte> OutputBytes { get; init; } = [];

    /// <summary>The retarget result (null when the source could not be read or the item was cancelled first).</summary>
    public RetargetResult? Result { get; init; }

    /// <summary>The validation report, when built.</summary>
    public RetargetReport? Report { get; init; }
}

/// <summary>Progress of a batch.</summary>
/// <param name="Completed">Items finished so far.</param>
/// <param name="Total">Items in the batch.</param>
/// <param name="Current">The item just finished (or starting, when <paramref name="Last"/> is null).</param>
/// <param name="Last">The result of the item just finished, or null when an item is starting.</param>
public sealed record BatchProgress(int Completed, int Total, string Current, BatchItemResult? Last);

/// <summary>
/// Retargets many clips on a worker thread: names each output from a pattern, asks the collision
/// callback when a name is taken, writes atomically (<see cref="AtomicFile"/>), reports progress
/// after each item and stops between items when cancelled. Cancellation (the token, or a
/// <see cref="CollisionDecision.Cancel"/>) does not throw: the list comes back complete, with the
/// unfinished items marked <see cref="BatchItemStatus.Cancelled"/>, so a results table can show
/// what was done.
/// </summary>
public static class BatchRetarget
{
    /// <summary>Runs the batch.</summary>
    /// <exception cref="ArgumentException">Files are to be written but no output folder is set.</exception>
    public static Task<IReadOnlyList<BatchItemResult>> RunAsync(
        IReadOnlyList<BatchItem> items, BatchOptions options, IProgress<BatchProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(options);
        if (options.WriteFiles && string.IsNullOrWhiteSpace(options.OutputFolder))
            throw new ArgumentException("Choose an output folder, or run without writing files.", nameof(options));
        return Task.Run<IReadOnlyList<BatchItemResult>>(() => Run(items, options, progress, ct), CancellationToken.None);
    }

    /// <summary>The output file name for an item under a pattern (no folder, no collision handling).</summary>
    public static string OutputName(BatchItem item, string pattern)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(pattern);
        string source = Path.GetFileNameWithoutExtension(item.SourceName);
        string clip = item.ClipName ?? ClipToken(source);
        string name = pattern
            .Replace("{rig}", item.Target.Profile.Name, StringComparison.OrdinalIgnoreCase)
            .Replace("{sourcerig}", item.Source.Profile.Name, StringComparison.OrdinalIgnoreCase)
            .Replace("{clip}", clip, StringComparison.OrdinalIgnoreCase)
            .Replace("{source}", source, StringComparison.OrdinalIgnoreCase)
            .Replace("{target}", item.TargetName ?? item.Target.Profile.Name, StringComparison.OrdinalIgnoreCase);
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        if (!name.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)) name += ".rfa";
        return name;
    }

    /// <summary>The base name after its first underscore (the AnimType prefix): park_jeep_driver -> jeep_driver.</summary>
    public static string ClipToken(string sourceBaseName)
    {
        ArgumentNullException.ThrowIfNull(sourceBaseName);
        int at = sourceBaseName.IndexOf('_');
        return at > 0 && at < sourceBaseName.Length - 1 ? sourceBaseName[(at + 1)..] : sourceBaseName;
    }

    private static List<BatchItemResult> Run(IReadOnlyList<BatchItem> items, BatchOptions options, IProgress<BatchProgress>? progress, CancellationToken ct)
    {
        var results = new List<BatchItemResult>(items.Count);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool cancelled = false;
        for (int n = 0; n < items.Count; n++)
        {
            var item = items[n] ?? throw new ArgumentException($"Batch item {n} is null.", nameof(items));
            if (cancelled || ct.IsCancellationRequested)
            {
                cancelled = true;
                results.Add(new BatchItemResult(item, BatchItemStatus.Cancelled) { Error = "The batch was stopped before this clip." });
                continue;
            }
            progress?.Report(new BatchProgress(n, items.Count, item.SourceName, null));
            var result = RunOne(item, options, claimed, out bool stop);
            results.Add(result);
            if (stop) cancelled = true;
            progress?.Report(new BatchProgress(n + 1, items.Count, item.SourceName, result));
        }
        return results;
    }

    private static BatchItemResult RunOne(BatchItem item, BatchOptions options, HashSet<string> claimed, out bool stop)
    {
        stop = false;
        RfaClip clip;
        if (item.SourceClip is { } loaded) clip = loaded;
        else if (item.SourceBytes is { } bytes)
        {
            try
            {
                clip = RfaReader.Read(bytes.ToArray(), item.SourceName);
            }
            catch (AssetFormatException ex)
            {
                return new BatchItemResult(item, BatchItemStatus.Failed) { Error = $"'{item.SourceName}' could not be read as a clip: {ex.Message}" };
            }
        }
        else
        {
            return new BatchItemResult(item, BatchItemStatus.Failed) { Error = $"'{item.SourceName}' has no clip data. Add the clip again." };
        }

        var request = new RetargetRequest(clip, item.Source, item.Target) { BoneMap = item.BoneMap, Options = item.Options ?? options.Retarget };
        RetargetResult result;
        byte[] output;
        RetargetReport? report;
        try
        {
            result = Retargeter.Retarget(request);
            if (!result.Success || result.Clip is null)
                return new BatchItemResult(item, BatchItemStatus.Failed) { Error = result.Error, Result = result };

            output = RfaWriter.Write(result.Clip);
            report = options.BuildReports && result.BoneMap is not null
                ? RetargetReport.Build(clip, item.Source.Skeleton, item.Source.Profile, result.Clip, item.Target.Skeleton, item.Target.Profile,
                    result.BoneMap, Math.Max(1, options.ReportSampleStep), contacts: result.Contacts)
                : null;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            // One clip the retargeter or the writer cannot handle (a format limit, an unexpected input) fails
            // on its own; the rest of the batch goes on.
            return new BatchItemResult(item, BatchItemStatus.Failed)
            {
                Error = $"'{item.SourceName}' could not be retargeted: {ex.Message}",
            };
        }

        string name = OutputName(item, options.NamingPattern);
        string folder = options.OutputFolder ?? string.Empty;
        string path = Path.Combine(folder, name);
        if (Taken(path, options, claimed))
        {
            var decision = options.OnCollision?.Invoke(path) ?? CollisionDecision.Skip;
            switch (decision)
            {
                case CollisionDecision.Skip:
                    return new BatchItemResult(item, BatchItemStatus.Skipped)
                    {
                        Error = $"'{name}' already exists; it was left alone.", OutputName = name, Result = result, Report = report, OutputBytes = [.. output],
                    };
                case CollisionDecision.Cancel:
                    stop = true;
                    return new BatchItemResult(item, BatchItemStatus.Cancelled) { Error = "The batch was stopped at this clip.", OutputName = name, Result = result, Report = report };
                case CollisionDecision.Rename:
                    string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
                    for (int k = 2; ; k++)
                    {
                        name = $"{stem}_{k}{ext}";
                        path = Path.Combine(folder, name);
                        if (!Taken(path, options, claimed)) break;
                    }
                    break;
            }
        }
        claimed.Add(Path.GetFullPath(path));

        if (options.WriteFiles)
        {
            try
            {
                AtomicFile.WriteAllBytes(path, output);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new BatchItemResult(item, BatchItemStatus.Failed)
                {
                    Error = $"'{name}' could not be written: {ex.Message} Check that the output folder exists and is writable.",
                    OutputName = name, Result = result, Report = report, OutputBytes = [.. output],
                };
            }
        }
        return new BatchItemResult(item, BatchItemStatus.Succeeded) { OutputPath = path, OutputName = name, Result = result, Report = report, OutputBytes = [.. output] };
    }

    private static bool Taken(string path, BatchOptions options, HashSet<string> claimed) =>
        claimed.Contains(Path.GetFullPath(path)) || (options.WriteFiles && File.Exists(path));
}
