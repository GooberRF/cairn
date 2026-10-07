using System.Collections.Immutable;
using System.Globalization;

namespace Cairn.Formats.Tbl;

/// <summary>One key of a table and the tokens that follow it up to the next key or section.</summary>
/// <param name="Key">The key token.</param>
/// <param name="Values">Every token after the key, up to the next key or section header.</param>
/// <param name="Section">The <c>#Section</c> the key sits in, or null before the first one.</param>
public sealed record TblField(TblToken Key, ImmutableArray<TblToken> Values, string? Section)
{
    /// <summary>The key name without prefix or colon.</summary>
    public string Name => Key.Text;

    /// <summary>'$', '+' or '\0'.</summary>
    public char Prefix => Key.Prefix;

    /// <summary>1-based line of the key.</summary>
    public int Line => Key.Line;

    /// <summary>True for this prefix and name (ignoring case).</summary>
    public bool Is(char prefix, string name) => Key.IsKey(prefix, name);

    /// <summary>The <paramref name="index"/>-th string value, or null when there are fewer.</summary>
    public string? String(int index)
    {
        int seen = 0;
        foreach (var t in Values)
        {
            if (t.Kind != TblTokenKind.String) continue;
            if (seen++ == index) return t.Text;
        }
        return null;
    }

    /// <summary>The first value parsed as a number, or null.</summary>
    public float? Number()
    {
        foreach (var t in Values)
        {
            if (t.Kind is TblTokenKind.Word or TblTokenKind.String
                && float.TryParse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
            {
                return v;
            }
        }
        return null;
    }
}

/// <summary>Groups table tokens into <see cref="TblField"/>s.</summary>
public static class TblParser
{
    /// <summary>Tokenises <paramref name="text"/> and groups it into fields, in order.</summary>
    public static IReadOnlyList<TblField> ParseFields(string text)
    {
        var tokens = TblTokenizer.Tokenize(text);
        var fields = new List<TblField>();
        string? section = null;
        TblToken? key = null;
        var values = ImmutableArray.CreateBuilder<TblToken>();
        foreach (var t in tokens)
        {
            if (t.Kind is TblTokenKind.Key or TblTokenKind.Section)
            {
                if (key is { } k) fields.Add(new TblField(k, values.ToImmutable(), section));
                values.Clear();
                key = null;
                if (t.Kind == TblTokenKind.Section) section = t.Text;
                else key = t;
                continue;
            }
            if (key is not null) values.Add(t);
        }
        if (key is { } last) fields.Add(new TblField(last, values.ToImmutable(), section));
        return fields;
    }
}

/// <summary>
/// A file name as a table spells it, with the name it has on disk. The tables still use the
/// exporter's extensions: <c>.mvf</c> is an <c>.rfa</c> clip, <c>.vcm</c> a <c>.v3c</c> character
/// mesh, and <c>.v3d</c> a mesh of either kind — <c>.v3m</c> for static meshes, but weapons.tbl also
/// writes first-person characters as <c>.v3d</c> (<c>fp_glock.v3d</c> is <c>fp_glock.v3c</c>), so a
/// <c>.v3d</c> has both candidates, <c>.v3m</c> first.
/// </summary>
/// <param name="Original">The spelling in the table, unchanged.</param>
public readonly record struct TblFileName(string Original)
{
    /// <summary>True when the table gave an empty name.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Original);

    /// <summary>The most likely disk name (first of <see cref="Candidates"/>).</summary>
    public string DiskName => Candidates[0];

    /// <summary>The name without folder or extension: a clip's identity in the engine (case-insensitive).</summary>
    public string BaseName => Path.GetFileNameWithoutExtension(Original?.Trim() ?? string.Empty);

    /// <summary>Every disk name the engine could mean, most likely first.</summary>
    public IReadOnlyList<string> Candidates => Normalize(Original);

    /// <summary>The disk names a table file name stands for, most likely first.</summary>
    public static IReadOnlyList<string> Normalize(string? name)
    {
        string trimmed = name?.Trim() ?? string.Empty;
        string ext = Path.GetExtension(trimmed);
        string stem = trimmed[..^ext.Length];
        return ext.ToLowerInvariant() switch
        {
            ".mvf" => [stem + ".rfa"],
            ".vcm" => [stem + ".v3c"],
            ".v3d" => [stem + ".v3m", stem + ".v3c"],
            _ => [trimmed],
        };
    }

    public override string ToString() => Original;
}
