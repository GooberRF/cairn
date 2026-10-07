namespace Cairn.Formats.Text;

/// <summary>Small helper behind every "did you mean …?" suggestion (ATX keys and tokens, table fields and names).</summary>
public static class EditDistance
{
    /// <summary>
    /// The longest value worth measuring. Every ATX key and token is a handful of characters, so a
    /// name longer than this is not a typo of anything and measuring it only costs time — a lot of
    /// it, since the work grows with the product of the two lengths.
    /// </summary>
    public const int MaxLength = 64;

    /// <summary>Case-insensitive Levenshtein distance.</summary>
    public static int Between(string a, string b)
    {
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) previous[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    /// <summary>
    /// The closest candidate to <paramref name="value"/>, or null when nothing is close enough to
    /// be worth suggesting (more than half the shorter name differs).
    /// </summary>
    public static string? Closest(string value, IEnumerable<string> candidates) => Closest(value, candidates, 2);

    /// <summary>
    /// The closest candidate to <paramref name="value"/>, or null when it differs in more than
    /// 1/<paramref name="strictness"/> of the shorter name (at least two edits are always allowed).
    /// A larger <paramref name="strictness"/> suggests less; candidates longer than
    /// <see cref="MaxLength"/> are skipped.
    /// </summary>
    /// <param name="value">The unknown name as written.</param>
    /// <param name="candidates">The known names.</param>
    /// <param name="strictness">The divisor of the shorter length that gives the allowed edits (2 for ATX, 3 for tables).</param>
    public static string? Closest(string value, IEnumerable<string> candidates, int strictness)
    {
        if (value.Length > MaxLength) return null;
        strictness = Math.Max(1, strictness);
        string? best = null;
        int bestDistance = int.MaxValue;
        // One lowercase copy, not one per candidate: Closest runs for every unknown key in the file
        // and the linter runs on every keystroke.
        value = value.ToLowerInvariant();
        foreach (string candidate in candidates)
        {
            if (candidate.Length > MaxLength) continue;
            int d = Between(value, candidate);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = candidate;
            }
        }
        if (best is null) return null;
        int limit = Math.Max(2, Math.Min(value.Length, best.Length) / strictness);
        return bestDistance <= limit ? best : null;
    }
}
