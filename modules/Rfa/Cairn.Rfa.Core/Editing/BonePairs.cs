using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cairn.Rfa.Editing;

/// <summary>
/// Left/right bone pairs for mirroring: <see cref="Partner"/>[i] is bone i's mirror partner, or i
/// itself for a centre (unpaired) bone. Always symmetric (<c>Partner[Partner[i]] == i</c>). Immutable;
/// edit with <see cref="With"/> / <see cref="Without"/>. Serialises with <see cref="JsonSerializer"/>
/// as <c>{"Partner":[...]}</c>, or with <see cref="ToJson"/> / <see cref="FromJson"/> (validated).
/// </summary>
/// <param name="Partner">Each bone's partner index (itself when unpaired).</param>
public sealed record BonePairMap(ImmutableArray<int> Partner)
{
    /// <summary>Number of bones.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int Count => Partner.IsDefault ? 0 : Partner.Length;

    /// <summary>A map where every bone is its own partner.</summary>
    public static BonePairMap Identity(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return new BonePairMap([.. Enumerable.Range(0, count)]);
    }

    /// <summary>Bone <paramref name="bone"/>'s partner (itself when unpaired).</summary>
    public int PartnerOf(int bone)
    {
        if ((uint)bone >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(bone), $"The pair map has {Count} bones; there is no bone {bone}.");
        return Partner[bone];
    }

    /// <summary>True when <paramref name="bone"/> has a partner other than itself.</summary>
    public bool IsPaired(int bone) => PartnerOf(bone) != bone;

    /// <summary>
    /// This map with <paramref name="a"/> and <paramref name="b"/> paired. Their previous partners
    /// become unpaired. <c>With(a, a)</c> unpairs <paramref name="a"/>.
    /// </summary>
    public BonePairMap With(int a, int b)
    {
        PartnerOf(a);
        PartnerOf(b);
        var p = Partner.ToBuilder();
        int oldA = p[a], oldB = p[b];
        p[oldA] = oldA;
        p[oldB] = oldB;
        p[a] = b;
        p[b] = a;
        return new BonePairMap(p.MoveToImmutable());
    }

    /// <summary>This map with <paramref name="bone"/> (and its partner) unpaired.</summary>
    public BonePairMap Without(int bone) => With(bone, bone);

    /// <summary>Throws a plain-language <see cref="ArgumentException"/> unless the map is well formed for <paramref name="boneCount"/> bones.</summary>
    public void Validate(int boneCount)
    {
        if (Partner.IsDefault) throw new ArgumentException("The bone pair map has no partner list.");
        if (Partner.Length != boneCount)
            throw new ArgumentException($"The bone pair map is for {Partner.Length} bones but the clip has {boneCount}; detect the pairs again for this clip's bones.");
        for (int i = 0; i < Partner.Length; i++)
        {
            int p = Partner[i];
            if ((uint)p >= (uint)Partner.Length)
                throw new ArgumentException($"Bone {i}'s mirror partner {p} does not exist; partners must be bone indices 0..{Partner.Length - 1}.");
            if (Partner[p] != i)
                throw new ArgumentException($"Bone {i} is paired with bone {p}, but bone {p} is paired with bone {Partner[p]}; pairs must point at each other.");
        }
    }

    /// <summary>The map as JSON: <c>{"Partner":[...]}</c>.</summary>
    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>Reads a map written by <see cref="ToJson"/>, checking it is symmetric and in range.</summary>
    /// <exception cref="FormatException">The text is not a valid bone pair map.</exception>
    public static BonePairMap FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        BonePairMap? map;
        try
        {
            map = JsonSerializer.Deserialize<BonePairMap>(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException("The bone pair text is not valid JSON: " + ex.Message, ex);
        }
        if (map is null || map.Partner.IsDefault) throw new FormatException("The bone pair text has no \"Partner\" list.");
        try
        {
            map.Validate(map.Partner.Length);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException(ex.Message, ex);
        }
        return map;
    }

    /// <summary>Equality by partner list contents.</summary>
    public bool Equals(BonePairMap? other) =>
        other is not null && (Partner.IsDefault ? other.Partner.IsDefault : !other.Partner.IsDefault && Partner.SequenceEqual(other.Partner));

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var h = new HashCode();
        if (!Partner.IsDefault) foreach (int p in Partner) h.Add(p);
        return h.ToHashCode();
    }
}

/// <summary>
/// Finds left/right bone pairs from bone names. A name is split into word tokens at <c>-</c>,
/// <c>_</c>, spaces, dots and lower-to-upper case changes; a token is a SIDE token when it is
/// <c>l</c>/<c>r</c>/<c>left</c>/<c>right</c> optionally followed by digits and one letter
/// (<c>-l</c>, <c>-r2</c>, <c>-l01a</c>, <c>thumb-la</c>, <c>cape-lt</c>, <c>_R</c>, <c> L </c>), or a
/// token starting or ending with <c>left</c>/<c>right</c> (<c>LeftArm</c>, <c>armright</c>);
/// case-insensitive. The LAST side token of a name decides its side. Two bones pair when their
/// names are identical apart from the side (so <c>arm-l-upper</c> pairs with <c>arm-r-upper</c>,
/// <c>foot-l-toes</c> with <c>foot-r-toes</c>, <c>fingers-l2</c> with <c>fingers-r2</c>); a second
/// pass ignores where the side token sits (<c>bdbn-r-hand</c> with <c>bdbn-hand-l</c>). A candidate
/// that is ambiguous (two bones with the same name and side) is left unpaired. Everything else,
/// including every centre bone, is its own partner.
/// </summary>
public static class BonePairs
{
    private static readonly Regex SideToken = new(
        @"^(?<side>l|r|left|right)(?<rest>\d*[a-z]?)$", RegexOptions.CultureInvariant);

    private static readonly Regex Splitter = new(
        @"[-_ .]+|(?<=[a-z0-9])(?=[A-Z])", RegexOptions.CultureInvariant);

    /// <summary>Detects the pairs of a bone list (see the class remarks for the rules).</summary>
    public static BonePairMap Detect(IReadOnlyList<string> boneNames)
    {
        ArgumentNullException.ThrowIfNull(boneNames);
        int n = boneNames.Count;
        var partner = Enumerable.Range(0, n).ToArray();
        var parsed = new (char Side, string Exact, string Loose)?[n];
        for (int i = 0; i < n; i++) parsed[i] = Parse(boneNames[i] ?? string.Empty);

        Pair(p => p.Exact);
        Pair(p => p.Loose);
        return new BonePairMap([.. partner]);

        void Pair(Func<(char Side, string Exact, string Loose), string> keyOf)
        {
            var groups = new Dictionary<(string Key, char Side), List<int>>();
            for (int i = 0; i < n; i++)
            {
                if (partner[i] != i || parsed[i] is not { } p) continue;
                var k = (keyOf(p), p.Side);
                if (!groups.TryGetValue(k, out var list)) groups[k] = list = [];
                list.Add(i);
            }
            foreach (var ((key, side), lefts) in groups)
            {
                if (side != 'l' || lefts.Count != 1) continue;
                if (!groups.TryGetValue((key, 'r'), out var rights) || rights.Count != 1) continue;
                int a = lefts[0], b = rights[0];
                if (partner[a] != a || partner[b] != b) continue;
                partner[a] = b;
                partner[b] = a;
            }
        }
    }

    /// <summary>
    /// The side of a bone name (<c>'l'</c>, <c>'r'</c>, or null for a centre bone) and two side-free
    /// keys: the exact layout with the side marked in place, and a loose one without the side token.
    /// </summary>
    private static (char Side, string Exact, string Loose)? Parse(string name)
    {
        var tokens = Splitter.Split(name.Trim()).Where(t => t.Length > 0).Select(t => t.ToLowerInvariant()).ToList();
        for (int i = tokens.Count - 1; i >= 0; i--)
        {
            if (Side(tokens[i]) is not { } s) continue;
            var exact = new StringBuilder();
            var loose = new StringBuilder();
            for (int j = 0; j < tokens.Count; j++)
            {
                if (j > 0) exact.Append('\u0001');
                if (j == i)
                {
                    exact.Append('\u0002').Append(s.Tail);
                }
                else
                {
                    exact.Append(tokens[j]);
                    loose.Append(tokens[j]).Append('\u0001');
                }
            }
            loose.Append('\u0002').Append(s.Tail);
            return (s.Side, exact.ToString(), loose.ToString());
        }
        return null;
    }

    private static (char Side, string Tail)? Side(string token)
    {
        var m = SideToken.Match(token);
        if (m.Success) return (m.Groups["side"].Value[0], "\u0003" + m.Groups["rest"].Value);
        foreach (var (word, side) in new[] { ("left", 'l'), ("right", 'r') })
        {
            if (token.Length > word.Length && token.StartsWith(word, StringComparison.Ordinal))
                return (side, "\u0004" + token[word.Length..]);
            if (token.Length > word.Length && token.EndsWith(word, StringComparison.Ordinal))
                return (side, "\u0005" + token[..^word.Length]);
        }
        return null;
    }
}
