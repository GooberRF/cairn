namespace Cairn.Rfa.Retarget;

/// <summary>Which side of the body a bone name says it is on.</summary>
internal enum BoneSide
{
    None,
    Left,
    Right,
}

/// <summary>A bone name broken into normalised words plus its side.</summary>
internal sealed record BoneTokenSet(BoneSide Side, IReadOnlyList<string> Tokens);

/// <summary>
/// Splits bone names into body words for the fuzzy step of <see cref="BoneMapper"/> and the humanoid
/// detection of <see cref="RigProfile.Generic(Animation.Skeleton)"/>: split at separators, case
/// changes and digits; glued words are segmented against a small anatomy vocabulary
/// (<c>upperarm</c> -> upper arm); synonyms are folded (thigh -> upper leg, calf/shin -> lower leg,
/// forearm -> lower arm, fingers -> finger); <c>l/left/r/right</c> become the side; numbers lose
/// leading zeros; filler words (bone, bip01, joint, ...) are dropped.
/// </summary>
internal static class BoneTokens
{
    private static readonly string[] Vocabulary =
    [
        "shoulderpad", "forearm", "clavicle", "shoulder", "fingers", "finger", "collar", "pelvis", "middle", "spine",
        "thigh", "thumb", "upper", "lower", "right", "ankle", "elbow", "wrist", "pinky", "index", "chest", "torso",
        "head", "neck", "hand", "foot", "feet", "toes", "calf", "shin", "knee", "hips", "left", "ring", "tail",
        "root", "arm", "leg", "toe", "hip", "jaw", "eye", "pad", "ball", "up", "low",
    ];

    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "bone", "bip", "bip01", "jnt", "joint", "def", "mixamorig", "b", "bn",
    };

    private static readonly Dictionary<string, string[]> Synonyms = new(StringComparer.Ordinal)
    {
        ["thigh"] = ["upper", "leg"],
        ["calf"] = ["lower", "leg"],
        ["shin"] = ["lower", "leg"],
        ["forearm"] = ["lower", "arm"],
        ["fingers"] = ["finger"],
        ["toes"] = ["toe"],
        ["feet"] = ["foot"],
        ["hips"] = ["hip"],
        ["up"] = ["upper"],
        ["low"] = ["lower"],
        ["shoulderpad"] = ["shoulder", "pad"],
    };

    public static BoneTokenSet Parse(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var raw = new List<string>();
        var current = new System.Text.StringBuilder();
        char prev = '\0';
        foreach (char ch in name)
        {
            bool boundary = !char.IsLetterOrDigit(ch)
                || (current.Length > 0 && (char.IsDigit(ch) != char.IsDigit(prev) || (char.IsUpper(ch) && char.IsLower(prev))));
            if (boundary && current.Length > 0)
            {
                raw.Add(current.ToString());
                current.Clear();
            }
            if (char.IsLetterOrDigit(ch)) current.Append(char.ToLowerInvariant(ch));
            prev = ch;
        }
        if (current.Length > 0) raw.Add(current.ToString());

        var side = BoneSide.None;
        var tokens = new List<string>();
        bool afterFiller = false;
        foreach (string part in raw)
        {
            if (char.IsDigit(part[0]))
            {
                // "Bip01": the number belongs to the filler word, not to the bone.
                if (!afterFiller)
                {
                    string trimmed = part.TrimStart('0');
                    tokens.Add(trimmed.Length == 0 ? "0" : trimmed);
                }
                afterFiller = false;
                continue;
            }
            afterFiller = false;
            foreach (string word in Segment(part))
            {
                if (word is "l" or "left")
                {
                    if (side == BoneSide.None) side = BoneSide.Left;
                    continue;
                }
                if (word is "r" or "right")
                {
                    if (side == BoneSide.None) side = BoneSide.Right;
                    continue;
                }
                if (Filler.Contains(word))
                {
                    afterFiller = true;
                    continue;
                }
                afterFiller = false;
                if (Synonyms.TryGetValue(word, out var replacement)) tokens.AddRange(replacement);
                else tokens.Add(word);
            }
        }
        return new BoneTokenSet(side, tokens);
    }

    /// <summary>Dice similarity of the two token multisets (0..1); sides must agree or the score is 0.</summary>
    public static double Similarity(BoneTokenSet a, BoneTokenSet b)
    {
        if (a.Side != b.Side) return 0.0;
        if (a.Tokens.Count == 0 || b.Tokens.Count == 0) return 0.0;
        var pool = new List<string>(b.Tokens);
        int common = 0;
        foreach (string t in a.Tokens)
        {
            int at = pool.IndexOf(t);
            if (at >= 0)
            {
                common++;
                pool.RemoveAt(at);
            }
        }
        return 2.0 * common / (a.Tokens.Count + b.Tokens.Count);
    }

    // Greedy longest-match segmentation of a glued word against the vocabulary; an unknown remainder
    // stays one word. A leading or trailing single l/r next to a known word is a side ("lhand").
    private static IEnumerable<string> Segment(string word)
    {
        var result = new List<string>();
        int i = 0;
        var unknown = new System.Text.StringBuilder();
        while (i < word.Length)
        {
            string? match = null;
            foreach (string v in Vocabulary)
            {
                if (string.CompareOrdinal(word, i, v, 0, v.Length) == 0 && (match is null || v.Length > match.Length)) match = v;
            }
            if (match is null)
            {
                unknown.Append(word[i]);
                i++;
                continue;
            }
            if (unknown.Length > 0)
            {
                result.Add(unknown.ToString());
                unknown.Clear();
            }
            result.Add(match);
            i += match.Length;
        }
        if (unknown.Length > 0) result.Add(unknown.ToString());

        // A known word must cover most of the original for the split to count; otherwise keep it whole.
        int known = result.Where(w => Vocabulary.Contains(w)).Sum(w => w.Length);
        bool sideLetters = result.All(w => Vocabulary.Contains(w) || w is "l" or "r");
        if (result.Count > 1 && !sideLetters && known * 2 < word.Length) return [word];
        return result;
    }
}
