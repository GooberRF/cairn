namespace Cairn.Formats.Imaging;

/// <summary>
/// The one place that decides whether two images agree well enough for the engine to put them in
/// the same animated texture, and how to say what differs. The asset linter's ATX011 and the Add
/// Frames from VPP browser's "doesn't match this texture's frames" note both read from here, so a
/// rule can never be tightened in one and not the other.
/// </summary>
public static class ImageComparison
{
    /// <summary>
    /// The ways <paramref name="candidate"/> differs from <paramref name="reference"/>, each as a
    /// phrase that fits after "…: ". An empty list means they match.
    /// </summary>
    /// <param name="candidate">The image being checked.</param>
    /// <param name="reference">The image every frame has to agree with, normally frame 0.</param>
    /// <param name="referenceLabel">What to call the reference mid-sentence, e.g. "frame 0".</param>
    /// <param name="certain">
    /// False when at least one difference could not actually be verified — two different container
    /// types whose mip counts this app derives differently. The caller downgrades the severity,
    /// because claiming the game will refuse a file we could not measure would be a lie.
    /// </param>
    public static IReadOnlyList<string> Differences(
        ImageInfo candidate, ImageInfo reference, string referenceLabel, out bool certain)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(reference);

        certain = true;
        var problems = new List<string>();

        if (candidate.Width != reference.Width || candidate.Height != reference.Height)
        {
            problems.Add($"it is {candidate.Width} x {candidate.Height} but {referenceLabel} is "
                + $"{reference.Width} x {reference.Height}");
        }
        if (candidate.Format != reference.Format)
        {
            problems.Add($"it is {EngineFormats.DisplayName(candidate.Format)} but {referenceLabel} "
                + $"is {EngineFormats.DisplayName(reference.Format)}");
        }
        if (MipsDiffer(candidate, reference, referenceLabel, out string mipText, out bool mipCertain))
        {
            problems.Add(mipText);
            certain = mipCertain;
        }
        return problems;
    }

    /// <summary>
    /// True when the two images would not load as frames of the same texture. Same rule as
    /// <see cref="Differences"/>, for callers that only need the yes or no.
    /// </summary>
    /// <param name="candidate">The image being checked.</param>
    /// <param name="reference">The image it has to agree with.</param>
    public static bool Matches(ImageInfo candidate, ImageInfo reference) =>
        Differences(candidate, reference, "frame 0", out _).Count == 0;

    /// <summary>
    /// Mip counts are only comparable when both files state one. TGA does not: the engine derives
    /// the count from the dimensions, so two TGAs of equal size always agree and there is nothing
    /// to compare. Across container types the counts come from different rules, so a difference is
    /// reported but not claimed as certain.
    /// </summary>
    /// <param name="a">The image being checked.</param>
    /// <param name="b">The image it has to agree with.</param>
    /// <param name="otherLabel">What to call <paramref name="b"/> mid-sentence, e.g. "frame 0".</param>
    /// <param name="text">The phrase describing the difference, when there is one.</param>
    /// <param name="certain">False when the difference could not actually be verified.</param>
    public static bool MipsDiffer(
        ImageInfo a, ImageInfo b, string otherLabel, out string text, out bool certain)
    {
        text = string.Empty;
        certain = true;
        if (a.MipLevels is null && b.MipLevels is null) return false;

        if (a.MipLevels is { } am && b.MipLevels is { } bm)
        {
            if (am == bm) return false;
            // Two different container types can legitimately report counts we derive differently.
            certain = a.Container == b.Container;
            text = certain
                ? $"it has {am} mip level(s) but {otherLabel} has {bm}"
                : $"it has {am} mip level(s) and {otherLabel} has {bm}, which could not be verified "
                  + "because the two files are different image types";
            return true;
        }
        certain = false;
        text = "its mip count could not be verified, because the two files are different image types";
        return true;
    }
}
