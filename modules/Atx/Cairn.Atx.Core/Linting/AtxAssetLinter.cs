using Cairn.Assets;
using Cairn.Atx.Editing;
using Cairn.Formats.Imaging;
using Cairn.Atx.Parsing;
using Cairn.Atx.Schema;

namespace Cairn.Atx.Linting;

/// <summary>Where one referenced image ended up, and what it turned out to be.</summary>
/// <param name="Index">Frame index, or -1 for the alpha mask.</param>
/// <param name="RequestedName">The name written in the .atx.</param>
/// <param name="Location">Where it was found, or null when nothing matched.</param>
/// <param name="Info">What the engine would make of it, or null when it could not be read.</param>
/// <param name="Error">Why it could not be read, when <paramref name="Info"/> is null.</param>
public sealed record ResolvedAsset(
    int Index, string RequestedName, AssetLocation? Location, ImageInfo? Info, string? Error);

/// <summary>Everything the asset pass found, for both the problems list and the frame inspector.</summary>
/// <param name="Diagnostics">Problems to merge into the document's diagnostics.</param>
/// <param name="Frames">One entry per frame, in order.</param>
/// <param name="Mask">The alpha mask, when the header names one.</param>
/// <param name="EffectiveFormat">
/// The format the engine will actually store frames in, after the target format and any alpha-mask
/// promotion, or null when frame 0 could not be read.
/// </param>
public sealed record AssetAnalysis(
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<ResolvedAsset> Frames,
    ResolvedAsset? Mask,
    EngineFormat? EffectiveFormat);

/// <summary>
/// The half of the linter that needs files on disk: whether every frame image exists, whether the
/// frames agree with each other, and whether the alpha mask and format are usable. Runs on a
/// background thread and is cancellable, because VPP indexing and image probing are not instant.
/// </summary>
public static class AtxAssetLinter
{
    /// <summary>Runs the asset rules on a background thread.</summary>
    public static Task<AssetAnalysis> AnalyzeAsync(
        AtxParseResult parse, AssetResolver resolver, LintOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Analyze(parse, resolver, options, cancellationToken), cancellationToken);

    /// <summary>Runs the asset rules synchronously. Prefer <see cref="AnalyzeAsync"/> from the UI.</summary>
    public static AssetAnalysis Analyze(
        AtxParseResult parse, AssetResolver resolver, LintOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parse);
        ArgumentNullException.ThrowIfNull(resolver);
        _ = options;

        var model = parse.Model;
        if (model is null) return new AssetAnalysis([], [], null, null);

        var editor = new AtxEditor(parse.Text, parse);
        var diagnostics = new List<Diagnostic>();
        var frames = new List<ResolvedAsset>();

        for (int i = 0; i < model.Frames.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = model.Frames[i];
            string? name = frame.EffectiveFile;
            if (name is null || name.EndsWith(".atx", StringComparison.OrdinalIgnoreCase))
            {
                frames.Add(new ResolvedAsset(i, name ?? string.Empty, null, null, null));
                continue; // already reported by the structural pass
            }
            frames.Add(Resolve(resolver, i, name, cancellationToken));
        }

        // Frame existence and readability.
        for (int i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            // Frames the structural pass already rejected (no file, or a nested .atx) are skipped.
            if (frame.RequestedName.Length == 0
                || frame.RequestedName.EndsWith(".atx", StringComparison.OrdinalIgnoreCase)) continue;
            var span = model.Frames[i].File?.ValueSpan ?? model.Frames[i].HeaderSpan;
            if (frame.Location is null)
            {
                diagnostics.Add(new Diagnostic(
                    AtxRules.FrameImageNotFound, DiagnosticSeverity.Warning,
                    $"Frame {i}: '{frame.RequestedName}' was not found next to this .atx, in your "
                    + "search folders, or in the game's VPP archives.",
                    "Put the image in the same folder as the .atx file, add its folder under "
                    + "Settings > Search folders, or use Locate file… to pick it. The game will "
                    + "fail to load this texture until it can find the image.",
                    span, i, AtxSchema.KeyFile,
                    [
                        new QuickFix("Locate file…", QuickFixKind.LocateFile, null, frame.RequestedName),
                        new QuickFix("Search folders…", QuickFixKind.OpenSearchSettings),
                    ]));
            }
            else if (frame.Info is null)
            {
                diagnostics.Add(new Diagnostic(
                    AtxRules.ImageUnreadable, DiagnosticSeverity.Warning,
                    $"Frame {i}: '{frame.Location.ResolvedName}' was found in {frame.Location.DisplayLocation} "
                    + $"but could not be read. {frame.Error}",
                    "Re-export the image, or check that it is not a partially written file. Until it "
                    + "reads, its size and format cannot be checked against the other frames.",
                    span, i, AtxSchema.KeyFile));
            }
        }

        // The game always measures every frame against frame 0 and refuses the whole texture if
        // frame 0 itself will not load. When frame 0 is missing or unreadable we still compare the
        // rest to each other — but the messages must name the frame we actually compared against,
        // never call it "frame 0".
        var first = frames.FirstOrDefault(f => f.Info is not null);
        var reference = first?.Info;

        if (reference is not null)
        {
            for (int i = 0; i < frames.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frame = frames[i];
                if (frame.Info is null || ReferenceEquals(frame, first)) continue;
                var span = model.Frames[i].File?.ValueSpan ?? model.Frames[i].HeaderSpan;
                CompareToReference(diagnostics, frame, first!, reference, span, i);
            }
        }

        // Alpha mask.
        ResolvedAsset? mask = null;
        string? maskName = model.Header.EffectiveAlphaMask;
        if (maskName is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            mask = Resolve(resolver, -1, maskName, cancellationToken);
            var span = model.Header.AlphaMask!.ValueSpan;
            if (mask.Location is null)
            {
                diagnostics.Add(new Diagnostic(
                    AtxRules.MaskNotFound, DiagnosticSeverity.Warning,
                    $"The alpha mask '{maskName}' was not found next to this .atx, in your search "
                    + "folders, or in the game's VPP archives.",
                    "Put the mask image beside the .atx file, add its folder under Settings > Search "
                    + "folders, or use Locate file… to pick it.",
                    span, null, AtxSchema.KeyAlphaMask,
                    [
                        new QuickFix("Locate file…", QuickFixKind.LocateFile, null, maskName),
                        new QuickFix("Search folders…", QuickFixKind.OpenSearchSettings),
                    ]));
            }
            else if (mask.Info is null)
            {
                diagnostics.Add(new Diagnostic(
                    AtxRules.ImageUnreadable, DiagnosticSeverity.Warning,
                    $"The alpha mask '{mask.Location.ResolvedName}' was found in "
                    + $"{mask.Location.DisplayLocation} but could not be read. {mask.Error}",
                    "Re-export the mask as an 8-bit greyscale image.",
                    span, null, AtxSchema.KeyAlphaMask));
            }
            else
            {
                if (!EngineFormats.IsEightBitMask(mask.Info.Format))
                {
                    diagnostics.Add(new Diagnostic(
                        AtxRules.MaskNotGreyscale, DiagnosticSeverity.Error,
                        $"The alpha mask '{mask.Location.ResolvedName}' is "
                        + $"{EngineFormats.DisplayName(mask.Info.Format)}, but a mask must be 8-bit greyscale.",
                        "Re-export the mask as an 8-bit greyscale (or 8-bit paletted) TGA or VBM. "
                        + "The game refuses to load the texture otherwise.",
                        span, null, AtxSchema.KeyAlphaMask));
                }
                if (reference is not null)
                {
                    if (mask.Info.Width != reference.Width || mask.Info.Height != reference.Height)
                    {
                        diagnostics.Add(new Diagnostic(
                            AtxRules.MaskMismatch, DiagnosticSeverity.Error,
                            $"The alpha mask is {mask.Info.Width} x {mask.Info.Height} but "
                            + $"{Reference(first!)} is {reference.Width} x {reference.Height}.",
                            "Resize the mask so it matches frame 0 exactly. The game refuses to load "
                            + "the texture otherwise.",
                            span, null, AtxSchema.KeyAlphaMask));
                    }
                    else if (ImageComparison.MipsDiffer(mask.Info, reference, ShortReference(first!),
                        out string mipText, out bool certain))
                    {
                        diagnostics.Add(new Diagnostic(
                            AtxRules.MaskMismatch,
                            certain ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
                            $"The alpha mask's mip levels do not match {Reference(first!)}: {mipText}.",
                            "Re-export the mask with the same mip settings as the frames, or remove "
                            + "the mip levels from both.",
                            span, null, AtxSchema.KeyAlphaMask));
                    }
                }
            }
        }

        // Format conversion feasibility (the transform block of parse_and_load).
        EngineFormat? effective = null;
        if (reference is not null)
        {
            var target = AtxSchema.ParseFormatToken(model.Header.EffectiveFormat)?.Format;
            bool hasMask = maskName is not null;
            effective = AlphaMask.EffectiveFormat(reference.Format, target, hasMask);

            bool formatChange = target is { } t && t != reference.Format;
            if ((formatChange || hasMask) && !EngineFormats.IsUncompressedRgb(reference.Format))
            {
                bool blameFormat = formatChange;
                var span = blameFormat
                    ? model.Header.Format!.ValueSpan
                    : model.Header.AlphaMask!.ValueSpan;
                string key = blameFormat ? AtxSchema.KeyFormat : AtxSchema.KeyAlphaMask;
                diagnostics.Add(new Diagnostic(
                    AtxRules.CompressedTransform, DiagnosticSeverity.Error,
                    $"The frames are {EngineFormats.DisplayName(reference.Format)}, which the game "
                    + $"cannot transform, so '{key}' makes it refuse to load this texture.",
                    EngineFormats.IsCompressed(reference.Format)
                        ? "DXT-compressed images are uploaded as-is. Remove the line, or export the "
                          + "frames as uncompressed images (.tga or .png) instead."
                        : "Only 565, 4444, 1555, 888 and 8888 images can be transformed. Remove the "
                          + "line, or export the frames in one of those formats.",
                    span, null, key,
                    [new QuickFix("Remove key", QuickFixKind.Edit, () => editor.RemoveHeaderKey(key))]));
            }
            else if (hasMask && model.Header.EffectiveFormat is null
                && EngineFormats.IsUncompressedRgb(reference.Format))
            {
                // With no `format` key the promotion applies to whatever the frames already are.
                // The structural pass cannot see that, so the note belongs here.
                var maskSpan = model.Header.AlphaMask!.ValueSpan;
                if (!EngineFormats.HasAlpha(reference.Format))
                {
                    diagnostics.Add(new Diagnostic(
                        AtxRules.MaskPromotesFormat, DiagnosticSeverity.Info,
                        $"The frames are {EngineFormats.DisplayName(reference.Format)}, which has no "
                        + $"transparency, so the alpha mask makes the game store them as "
                        + $"{EngineFormats.DisplayName(effective!.Value)} instead.",
                        "This is fine — it just uses more memory than the frames do on disk. Set "
                        + "format explicitly if you would rather say so in the file.",
                        maskSpan, null, AtxSchema.KeyAlphaMask));
                }
                else if (reference.Format == EngineFormat.Argb1555)
                {
                    diagnostics.Add(new Diagnostic(
                        AtxRules.MaskWith1555, DiagnosticSeverity.Info,
                        "The frames are 1555 ARGB, which only stores transparency as fully on or "
                        + "fully off, so the alpha mask will be reduced to hard edges.",
                        "Anything 50% grey or lighter in the mask becomes opaque, the rest becomes "
                        + "invisible. Add format = \"4444\" or format = \"8888\" for soft edges.",
                        maskSpan, null, AtxSchema.KeyAlphaMask));
                }
            }
        }

        return new AssetAnalysis(diagnostics, frames, mask, effective);
    }

    private static void CompareToReference(
        List<Diagnostic> diagnostics, ResolvedAsset frame, ResolvedAsset first,
        ImageInfo reference, Text.TextSpan span, int index)
    {
        // The rules themselves live in ImageComparison, so the VPP browser's "doesn't match this
        // texture's frames" note and this diagnostic can never drift apart.
        var problems = ImageComparison.Differences(
            frame.Info!, reference, ShortReference(first), out bool certain);
        if (problems.Count == 0) return;

        diagnostics.Add(new Diagnostic(
            AtxRules.FrameMismatch,
            certain ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
            $"Frame {index} ('{frame.RequestedName}') does not match {Reference(first)}: "
            + string.Join("; ", problems) + ".",
            "Every frame must have the same width, height, pixel format and mip count as frame 0. "
            + "Re-export this image with the same settings as the others.",
            span, index, AtxSchema.KeyFile));
    }

    /// <summary>
    /// Names the frame the comparison was made against. Normally that is frame 0; when frame 0
    /// could not be read it is the first frame that could, and the message says so rather than
    /// calling another frame "frame 0".
    /// </summary>
    private static string Reference(ResolvedAsset first) => first.Index == 0
        ? $"frame 0 ('{first.RequestedName}')"
        : $"frame {first.Index} ('{first.RequestedName}'), the first frame that could be read";

    /// <summary>The same reference frame, named briefly for use mid-sentence.</summary>
    private static string ShortReference(ResolvedAsset first) => $"frame {first.Index}";

    private static ResolvedAsset Resolve(
        AssetResolver resolver, int index, string name, CancellationToken cancellationToken)
    {
        AssetLocation? location;
        try
        {
            location = resolver.Resolve(name, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ResolvedAsset(index, name, null, null, ex.Message);
        }
        if (location is null) return new ResolvedAsset(index, name, null, null, null);

        try
        {
            return new ResolvedAsset(index, name, location, resolver.ProbeImage(location), null);
        }
        catch (ImageDecodeException ex)
        {
            return new ResolvedAsset(index, name, location, null, ex.Message);
        }
    }
}
