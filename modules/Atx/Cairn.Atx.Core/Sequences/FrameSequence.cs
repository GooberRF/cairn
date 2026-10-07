using System.Globalization;
using System.Text.RegularExpressions;

namespace Cairn.Atx.Sequences;

/// <summary>
/// The shape of a numbered filename: everything before the trailing digit group, the number, its
/// zero padding, and the extension. <c>hazard_strip_07.tga</c> becomes
/// (<c>hazard_strip_</c>, 7, padding 2, <c>.tga</c>).
/// </summary>
/// <param name="Prefix">Text before the number.</param>
/// <param name="Number">The number itself.</param>
/// <param name="Padding">How many digits were written, including leading zeroes.</param>
/// <param name="Extension">The extension including its dot, or empty.</param>
public sealed record SequencePattern(string Prefix, int Number, int Padding, string Extension)
{
    /// <summary>Builds the filename for <paramref name="number"/> using this pattern's padding.</summary>
    public string NameFor(int number) =>
        Prefix + number.ToString(CultureInfo.InvariantCulture).PadLeft(Padding, '0') + Extension;

    /// <summary>Builds the filename for <paramref name="number"/> with no padding at all.</summary>
    public string NameForUnpadded(int number) =>
        Prefix + number.ToString(CultureInfo.InvariantCulture) + Extension;
}

/// <summary>Finds and generates runs of numbered image files, for Add Sequence.</summary>
public static partial class FrameSequence
{
    [GeneratedRegex(@"^(?<prefix>.*?)(?<digits>\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingDigits();

    /// <summary>
    /// Splits a filename into its numbered pattern, or returns null when it has no trailing digit
    /// group before the extension.
    /// </summary>
    public static SequencePattern? DetectPattern(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        string name = Path.GetFileName(fileName);
        string extension = Path.GetExtension(name);
        string stem = extension.Length > 0 ? name[..^extension.Length] : name;

        var match = TrailingDigits().Match(stem);
        if (!match.Success) return null;
        string digits = match.Groups["digits"].Value;
        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int number)) return null;
        return new SequencePattern(match.Groups["prefix"].Value, number, digits.Length, extension);
    }

    /// <summary>
    /// Given one file of a numbered run, returns every file of that run that exists in the same
    /// folder, in natural order. Files whose number is written with different padding still count,
    /// because artists are not always consistent. Returns just the one file when it is not part of
    /// a run.
    /// </summary>
    public static IReadOnlyList<string> DetectOnDisk(string filePath)
    {
        string folder = Path.GetDirectoryName(filePath) ?? string.Empty;
        string name = Path.GetFileName(filePath);
        var pattern = DetectPattern(name);
        if (pattern is null || !Directory.Exists(folder)) return [name];

        var candidates = new List<string>();
        foreach (string path in Directory.EnumerateFiles(folder, "*" + pattern.Extension))
            candidates.Add(Path.GetFileName(path));
        return DetectInNames(name, candidates);
    }

    /// <summary>
    /// The same detection over a list of names that are not on disk — the entries of a .vpp, say.
    /// Given one name of a numbered run, returns every name in <paramref name="names"/> that
    /// belongs to the same run, in natural order. Files whose number is written with different
    /// padding still count, because artists are not always consistent. Returns just the seed when
    /// it is not part of a run, and never returns a name twice.
    /// </summary>
    /// <param name="seedName">One file of the run; only its bare name is used.</param>
    /// <param name="names">The names to look through, e.g. one archive's image entries.</param>
    public static IReadOnlyList<string> DetectInNames(string seedName, IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        string name = Path.GetFileName(seedName ?? string.Empty);
        if (name.Length == 0) return [];

        var pattern = DetectPattern(name);
        if (pattern is null) return [name];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (string candidate in names)
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            var candidatePattern = DetectPattern(candidate);
            if (candidatePattern is null) continue;
            if (!string.Equals(candidatePattern.Prefix, pattern.Prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(candidatePattern.Extension, pattern.Extension, StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Add(candidate)) result.Add(candidate);
        }
        if (result.Count == 0) result.Add(name);
        result.Sort(NaturalStringComparer.Instance);
        return result;
    }

    /// <summary>
    /// The most names <see cref="Generate"/> will ever return. A run longer than this is a typo,
    /// not an animation, and the dialog says so rather than trying to build the list.
    /// </summary>
    public const int MaxGenerated = 10_000;

    /// <summary>
    /// How many names the given range asks for, before <see cref="MaxGenerated"/> is applied. The
    /// Add Sequence dialog uses this to say "showing the first 10,000 of 4,294,967,295".
    /// </summary>
    /// <param name="start">First number, inclusive.</param>
    /// <param name="end">Last number, inclusive.</param>
    /// <param name="step">Increment; treated as a positive magnitude.</param>
    public static long CountFor(int start, int end, int step = 1)
    {
        long stride = Math.Max(1, Math.Abs((long)step));
        long span = Math.Abs((long)end - start);
        return span / stride + 1;
    }

    /// <summary>
    /// Generates filenames from a pattern, for files that may not exist yet. At most
    /// <see cref="MaxGenerated"/> names come back; <see cref="CountFor"/> says how many the range
    /// really covers.
    /// </summary>
    /// <param name="prefix">Text before the number.</param>
    /// <param name="start">First number, inclusive.</param>
    /// <param name="end">Last number, inclusive. May be below <paramref name="start"/> to count down.</param>
    /// <param name="padding">Minimum digits; 0 means no padding.</param>
    /// <param name="extension">Extension with or without its leading dot.</param>
    /// <param name="step">Increment; always treated as a positive magnitude.</param>
    public static IReadOnlyList<string> Generate(
        string prefix, int start, int end, int padding, string extension, int step = 1)
    {
        // The counter is 64-bit on purpose: `for (int i = start; i <= end; i += step)` never ends
        // when start is near int.MaxValue, because the increment wraps back round to negative.
        long stride = Math.Max(1, Math.Abs((long)step));
        padding = Math.Clamp(padding, 0, 12);
        if (extension.Length > 0 && !extension.StartsWith('.')) extension = "." + extension;

        long wanted = Math.Min(CountFor(start, end, step), MaxGenerated);
        var names = new List<string>((int)wanted);
        long direction = start <= end ? stride : -stride;
        long value = start;
        for (long n = 0; n < wanted; n++, value += direction)
        {
            names.Add(Compose(prefix, (int)value, padding, extension));
        }
        return names;
    }

    /// <summary>Appends the run in reverse (minus the endpoints) to make a manual ping-pong.</summary>
    public static IReadOnlyList<string> WithReversedTail(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count < 3) return [.. names];
        var result = new List<string>(names);
        for (int i = names.Count - 2; i >= 1; i--) result.Add(names[i]);
        return result;
    }

    private static string Compose(string prefix, int number, int padding, string extension) =>
        prefix + number.ToString(CultureInfo.InvariantCulture).PadLeft(padding, '0') + extension;
}
