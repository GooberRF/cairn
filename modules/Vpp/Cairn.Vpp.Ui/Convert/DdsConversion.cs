using Cairn.Formats.Imaging;
using Cairn.Ui.Modules;
using Cairn.Vpp.Editing;
using Cairn.Vpp.Model;
using Cairn.Workspace;

// Not "Cairn.Vpp.Ui.Convert": a namespace of that name would hide System.Convert in the module's other files.
namespace Cairn.Vpp.Ui.Conversion;

/// <summary>Where converted files go.</summary>
public enum DdsOutputMode
{
    /// <summary>Each original entry is replaced by its <c>.dds</c> (one pending, undoable change).</summary>
    ReplaceOriginals,
    /// <summary>The <c>.dds</c> entries are added next to the originals.</summary>
    KeepOriginals,
    /// <summary>The <c>.dds</c> files are written to a folder; the packfile is not changed.</summary>
    Folder,
}

/// <summary>What to do when a <c>.dds</c> of the target name already exists (in the packfile or the folder).</summary>
public enum DdsExistingPolicy
{
    Replace,
    Skip,
}

/// <summary>The converter's settings, persisted under <c>vpp.dds.</c> keys.</summary>
public sealed record DdsConvertSettings
{
    public DdsEncodeOptions Encode { get; init; } = new();
    public DdsOutputMode Output { get; init; } = DdsOutputMode.ReplaceOriginals;
    public DdsExistingPolicy Existing { get; init; } = DdsExistingPolicy.Replace;
    public string? Folder { get; init; }

    /// <summary>Reads the saved settings (defaults for anything absent).</summary>
    public static DdsConvertSettings Load(ModuleSettings? store)
    {
        if (store is null) return new();
        T E<T>(string key, T fallback) where T : struct, Enum =>
            Enum.TryParse<T>(store.Get<string>("dds." + key), out var v) ? v : fallback;
        var d = new DdsEncodeOptions();
        return new DdsConvertSettings
        {
            Encode = d with
            {
                Format = E("format", d.Format),
                Mips = E("mips", d.Mips),
                MipCount = Math.Clamp(store.Get("dds.mipCount", d.MipCount), 1, 15),
                MipFilter = E("mipFilter", d.MipFilter),
                Quality = E("quality", d.Quality),
                Resize = E("resize", d.Resize),
                Rounding = E("rounding", d.Rounding),
                PremultiplyAlpha = store.Get("dds.premultiply", false),
            },
            Output = E("output", DdsOutputMode.ReplaceOriginals),
            Existing = E("existing", DdsExistingPolicy.Replace),
            Folder = store.Get<string>("dds.folder"),
        };
    }

    /// <summary>Saves the settings.</summary>
    public void Save(ModuleSettings? store)
    {
        if (store is null) return;
        store.Set("dds.format", Encode.Format.ToString());
        store.Set("dds.mips", Encode.Mips.ToString());
        store.Set("dds.mipCount", Encode.MipCount);
        store.Set("dds.mipFilter", Encode.MipFilter.ToString());
        store.Set("dds.quality", Encode.Quality.ToString());
        store.Set("dds.resize", Encode.Resize.ToString());
        store.Set("dds.rounding", Encode.Rounding.ToString());
        store.Set("dds.premultiply", Encode.PremultiplyAlpha);
        store.Set("dds.output", Output.ToString());
        store.Set("dds.existing", Existing.ToString());
        store.Set("dds.folder", Folder);
    }
}

/// <summary>One converted (or failed) image.</summary>
/// <param name="Name">The source entry's name.</param>
/// <param name="DdsName">The target name.</param>
/// <param name="Result">The encoded file, or null on failure.</param>
/// <param name="Error">Why it failed, or null.</param>
public sealed record DdsConvertResult(string Name, string DdsName, DdsEncodeResult? Result, string? Error);

/// <summary>What <see cref="DdsConversion.Apply"/> did.</summary>
public sealed record DdsApplyReport(VppPackage Package, int Converted, IReadOnlyList<string> SkippedExisting);

/// <summary>The packfile-side logic of the DDS converter (no UI).</summary>
public static class DdsConversion
{
    /// <summary>Extensions the converter reads.</summary>
    public static readonly string[] Extensions = [".tga", ".png", ".jpg", ".jpeg"];

    /// <summary>True when <paramref name="name"/> is an image the converter takes.</summary>
    public static bool IsConvertible(string name) =>
        Extensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);

    /// <summary><c>name.tga</c> -> <c>name.dds</c>, keeping the name otherwise.</summary>
    public static string DdsNameFor(string name) => Path.ChangeExtension(name, ".dds");

    /// <summary>Splits a selection into convertible images and skipped entries.</summary>
    public static (IReadOnlyList<VppItem> Images, IReadOnlyList<VppItem> Skipped) Split(IEnumerable<VppItem> selection)
    {
        var all = selection.ToList();
        return (all.Where(i => IsConvertible(i.Name)).ToList(), all.Where(i => !IsConvertible(i.Name)).ToList());
    }

    /// <summary>Decodes and encodes one entry; never throws (the error is in the result).</summary>
    public static DdsConvertResult ConvertOne(VppItem item, DdsEncodeOptions options, CancellationToken cancel)
    {
        string ddsName = DdsNameFor(item.Name);
        try
        {
            var image = ImageDecoder.Decode(item.Source.ReadAll(), item.Name);
            return new DdsConvertResult(item.Name, ddsName, DdsEncoder.EncodeDetailed(image, options, cancel), null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is ImageDecodeException or IOException or ArgumentException or InvalidDataException or UnauthorizedAccessException)
        {
            return new DdsConvertResult(item.Name, ddsName, null, ex.Message);
        }
    }

    /// <summary>
    /// Converts every image off the calling thread (a few at a time). Cancelling throws
    /// <see cref="OperationCanceledException"/> and nothing is returned, so nothing is applied.
    /// </summary>
    public static async Task<IReadOnlyList<DdsConvertResult>> ConvertAllAsync(
        IReadOnlyList<VppItem> images, DdsEncodeOptions options, Action<int, string>? progress, CancellationToken cancel)
    {
        var results = new DdsConvertResult[images.Count];
        int done = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, images.Count),
            new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) },
            (i, ct) =>
            {
                results[i] = ConvertOne(images[i], options, ct);
                progress?.Invoke(Interlocked.Increment(ref done), images[i].Name);
                return ValueTask.CompletedTask;
            });
        cancel.ThrowIfCancellationRequested();
        return results;
    }

    /// <summary>Applies the converted files to <paramref name="package"/> (a pure function, so one undo step).</summary>
    public static DdsApplyReport Apply(VppPackage package, IEnumerable<DdsConvertResult> results, DdsOutputMode mode, DdsExistingPolicy existing)
    {
        var p = package;
        int converted = 0;
        var skipped = new List<string>();
        foreach (var r in results)
        {
            if (r.Result is null) continue;
            var source = new MemorySource(r.Result.Bytes);
            bool exists = p.IndexOf(r.DdsName) >= 0;
            if (exists && existing == DdsExistingPolicy.Skip) { skipped.Add(r.DdsName); continue; }
            if (mode == DdsOutputMode.ReplaceOriginals)
            {
                if (exists)
                {
                    p = VppEdit.Remove(VppEdit.Replace(p, r.DdsName, source), [r.Name]);
                }
                else
                {
                    // Rename then replace keeps the entry's position in the packfile.
                    p = VppEdit.Replace(VppEdit.Rename(p, r.Name, r.DdsName), r.DdsName, source);
                }
            }
            else
            {
                p = exists ? VppEdit.Replace(p, r.DdsName, source)
                    : VppEdit.AddSources(p, [(r.DdsName, (VppSource)source)], VppClashPolicy.Replace).Package;
            }
            converted++;
        }
        return new DdsApplyReport(p, converted, skipped);
    }

    /// <summary>
    /// Writes the converted files into <paramref name="folder"/>, each atomically. Files that exist are
    /// replaced only when <paramref name="overwrite"/> says so for that name (the caller asks first).
    /// Returns the names written and the per-file errors.
    /// </summary>
    public static (IReadOnlyList<string> Written, IReadOnlyList<string> Errors) WriteToFolder(
        string folder, IEnumerable<DdsConvertResult> results, Func<string, bool> overwrite)
    {
        var written = new List<string>();
        var errors = new List<string>();
        Directory.CreateDirectory(folder);
        foreach (var r in results)
        {
            if (r.Result is null) continue;
            string path = Path.Combine(folder, r.DdsName);
            if (File.Exists(path) && !overwrite(r.DdsName)) continue;
            try
            {
                AtomicFile.WriteAllBytes(path, r.Result.Bytes);
                written.Add(r.DdsName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{r.DdsName}: {ex.Message}");
            }
        }
        return (written, errors);
    }
}
