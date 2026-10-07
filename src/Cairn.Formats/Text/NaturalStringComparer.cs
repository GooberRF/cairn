namespace Cairn.Formats.Text;

/// <summary>
/// Orders names the way a person expects: <c>frame_2.tga</c> before <c>frame_10.tga</c>.
/// Digit runs compare numerically, everything else compares case-insensitively.
/// </summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    /// <summary>A shared case-insensitive instance.</summary>
    public static NaturalStringComparer Instance { get; } = new();

    /// <summary>
    /// Only 0-9 counts as a digit here. <see cref="char.IsDigit(char)"/> is true for every Unicode
    /// decimal digit, including ones this comparer then measures with plain character arithmetic —
    /// which made the ordering disagree with itself ("Z" &lt; "٠" &lt; "29" &lt; "Z") and could make
    /// <see cref="List{T}.Sort(IComparer{T})"/> throw on an ordinary folder of filenames.
    /// </summary>
    private static bool IsAsciiDigit(char c) => (uint)(c - '0') <= 9;

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (IsAsciiDigit(x[i]) && IsAsciiDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && IsAsciiDigit(x[i])) i++;
                while (j < y.Length && IsAsciiDigit(y[j])) j++;

                var dx = x.AsSpan(si, i - si).TrimStart('0');
                var dy = y.AsSpan(sj, j - sj).TrimStart('0');
                if (dx.Length != dy.Length) return dx.Length - dy.Length;
                int cmp = dx.SequenceCompareTo(dy);
                if (cmp != 0) return cmp;
                // Equal numerically: shorter padding sorts first so ordering stays stable.
                if ((i - si) != (j - sj)) return (i - si) - (j - sj);
            }
            else
            {
                char cx = char.ToUpperInvariant(x[i]);
                char cy = char.ToUpperInvariant(y[j]);
                if (cx != cy) return cx - cy;
                i++;
                j++;
            }
        }
        return (x.Length - i) - (y.Length - j);
    }
}
