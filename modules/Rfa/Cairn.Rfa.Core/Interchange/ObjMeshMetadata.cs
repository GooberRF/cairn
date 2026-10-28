using System.Globalization;
using System.Numerics;

namespace Cairn.Rfa.Interchange;

/// <summary>
/// Cairn's own metadata in a mesh .obj: comment lines that start with <c>rf_</c>, which every other tool
/// ignores (and drops when it saves), so a mesh that goes Cairn → OBJ → Cairn keeps what OBJ cannot hold.
/// Each line is a keyword and <c>key=value</c> pairs; text values are percent-escaped (no spaces), numbers
/// are invariant and round-trip exactly.
/// <list type="bullet">
/// <item><c>rf_mesh kind=static|character lod=0|all</c> (header): marks a Cairn export.</item>
/// <item><c>rf_slot submesh=0 slot=1 texture=… emissive=… specular=… glossiness=… reflection=… refl_map=… flags=…</c>
/// (header): one submesh material, in material order.</item>
/// <item><c>rf_material name=… submesh=0 slot=1 render_flags=0x518C41 double_sided=0</c> (header): what an
/// OBJ material (<c>usemtl</c>) stands for.</item>
/// <item><c>rf_prop name=… parent=-1 position=x,y,z rotation=x,y,z,w</c> (header): a prop point, in RF space.</item>
/// <item><c>rf_submesh index=0 name=… parent=None lod=0 distance=0 lod_flags=0x20 offset=x,y,z</c> (first line of
/// an object): which submesh and LOD the object is.</item>
/// </list>
/// </summary>
public static class ObjMeshMetadata
{
    /// <summary>The prefix every metadata line starts with.</summary>
    public const string Prefix = "rf_";

    /// <summary>Header: the export marker.</summary>
    public const string Mesh = "rf_mesh";

    /// <summary>Header: one submesh material.</summary>
    public const string Slot = "rf_slot";

    /// <summary>Header: an OBJ material's meaning.</summary>
    public const string Material = "rf_material";

    /// <summary>Header: a prop point.</summary>
    public const string Prop = "rf_prop";

    /// <summary>Object: its submesh and LOD.</summary>
    public const string Submesh = "rf_submesh";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>A metadata line: the keyword then <c>key=value</c> for each pair (values written as given; use the helpers).</summary>
    public static string Line(string keyword, params (string Key, string Value)[] pairs) =>
        keyword + string.Concat(pairs.Select(p => " " + p.Key + "=" + p.Value));

    /// <summary>A text value, percent-escaped so it holds no spaces (an empty string stays empty).</summary>
    public static string Text(string? value) => string.IsNullOrEmpty(value) ? string.Empty : Uri.EscapeDataString(value);

    /// <summary>A float that reads back bit for bit.</summary>
    public static string Number(float value) => value.ToString("R", Inv);

    /// <summary>An integer.</summary>
    public static string Number(int value) => value.ToString(Inv);

    /// <summary>Flags as hexadecimal ("0x518C41").</summary>
    public static string Hex(uint value) => "0x" + value.ToString("X", Inv);

    /// <summary>A vector as "x,y,z".</summary>
    public static string Vector(Vector3 v) => $"{Number(v.X)},{Number(v.Y)},{Number(v.Z)}";

    /// <summary>A quaternion as "x,y,z,w".</summary>
    public static string Quaternion(Quaternion q) => $"{Number(q.X)},{Number(q.Y)},{Number(q.Z)},{Number(q.W)}";

    /// <summary>
    /// Splits a comment into its keyword and pairs, or null when it is not a metadata line. Malformed pairs (no
    /// <c>=</c>) are skipped; a repeated key keeps its last value.
    /// </summary>
    public static (string Keyword, IReadOnlyDictionary<string, string> Values)? Parse(string comment)
    {
        if (string.IsNullOrEmpty(comment) || !comment.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var tokens = comment.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < tokens.Length; i++)
        {
            int eq = tokens[i].IndexOf('=');
            if (eq <= 0) continue;
            values[tokens[i][..eq]] = tokens[i][(eq + 1)..];
        }
        return (tokens[0], values);
    }

    /// <summary>A text value, unescaped; null when absent or badly escaped.</summary>
    public static string? GetText(IReadOnlyDictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var raw)) return null;
        try { return Uri.UnescapeDataString(raw); }
        catch (UriFormatException) { return null; }
    }

    /// <summary>An integer value.</summary>
    public static bool TryGetInt(IReadOnlyDictionary<string, string> values, string key, out int value)
    {
        value = 0;
        return values.TryGetValue(key, out var raw) && int.TryParse(raw, NumberStyles.Integer, Inv, out value);
    }

    /// <summary>A finite float value.</summary>
    public static bool TryGetFloat(IReadOnlyDictionary<string, string> values, string key, out float value)
    {
        value = 0;
        return values.TryGetValue(key, out var raw) && float.TryParse(raw, NumberStyles.Float, Inv, out value) && float.IsFinite(value);
    }

    /// <summary>An unsigned value, decimal or <c>0x</c> hexadecimal.</summary>
    public static bool TryGetUInt(IReadOnlyDictionary<string, string> values, string key, out uint value)
    {
        value = 0;
        if (!values.TryGetValue(key, out var raw)) return false;
        return raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(raw.AsSpan(2), NumberStyles.HexNumber, Inv, out value)
            : uint.TryParse(raw, NumberStyles.Integer, Inv, out value);
    }

    /// <summary>A boolean value ("1"/"0", "true"/"false").</summary>
    public static bool TryGetBool(IReadOnlyDictionary<string, string> values, string key, out bool value)
    {
        value = false;
        if (!values.TryGetValue(key, out var raw)) return false;
        if (raw is "1" or "true") { value = true; return true; }
        return raw is "0" or "false";
    }

    /// <summary>A vector of <paramref name="count"/> finite floats, comma-separated.</summary>
    public static bool TryGetFloats(IReadOnlyDictionary<string, string> values, string key, int count, out float[] result)
    {
        result = [];
        if (!values.TryGetValue(key, out var raw)) return false;
        var parts = raw.Split(',');
        if (parts.Length != count) return false;
        var floats = new float[count];
        for (int i = 0; i < count; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, Inv, out floats[i]) || !float.IsFinite(floats[i])) return false;
        }
        result = floats;
        return true;
    }
}
