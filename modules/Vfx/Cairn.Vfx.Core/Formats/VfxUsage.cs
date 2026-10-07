using Cairn.Formats.Tbl;

namespace Cairn.Vfx.Formats;

/// <summary>One table line that names a <c>.vfx</c> file.</summary>
/// <param name="Table">The table file, e.g. <c>vclip.tbl</c>.</param>
/// <param name="Entry">The entry's <c>$Name</c> (or <c>$Class Name</c>), or null before the first entry.</param>
/// <param name="Field">The field as written in the table, with its prefix, e.g. <c>$VFX Filename</c>.</param>
/// <param name="VfxName">The effect file name as the table spells it.</param>
/// <param name="Line">1-based line in the table.</param>
public sealed record VfxTableReference(string Table, string? Entry, string Field, string VfxName, int Line)
{
    /// <summary>
    /// True when the referencing context plays the effect once (vclips, tracers, sparks, warm-ups);
    /// false when it loops while the owner exists (thrusters, cockpit, items, clutter, projectiles).
    /// </summary>
    public bool IsOneShot =>
        Table.Equals("vclip.tbl", StringComparison.OrdinalIgnoreCase)
        || Field.Contains("Tracer", StringComparison.OrdinalIgnoreCase)
        || Field.Contains("Spark", StringComparison.OrdinalIgnoreCase)
        || Field.Contains("Warmup", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Finds which table entries reference which effect files.</summary>
public static class VfxTableUsage
{
    /// <summary>The tables that reference effects in the stock game.</summary>
    public static IReadOnlyList<string> Tables { get; } = ["vclip.tbl", "entity.tbl", "weapons.tbl", "items.tbl", "clutter.tbl"];

    /// <summary>
    /// Reads every table in <see cref="Tables"/> through <paramref name="readTable"/> (which returns the
    /// table text, or null when it is not available) and returns every <c>.vfx</c> reference in table order.
    /// </summary>
    public static IReadOnlyList<VfxTableReference> Find(Func<string, string?> readTable)
    {
        var all = new List<VfxTableReference>();
        foreach (var table in Tables)
            if (readTable(table) is { } text) all.AddRange(FindInTable(table, text));
        return all;
    }

    /// <summary>Every <c>.vfx</c> reference in one table's text.</summary>
    public static IReadOnlyList<VfxTableReference> FindInTable(string table, string text)
    {
        var list = new List<VfxTableReference>();
        string? entry = null;
        foreach (var field in TblParser.ParseFields(text))
        {
            if (field.Prefix == '$' && (field.Name.Equals("Name", StringComparison.OrdinalIgnoreCase)
                                        || field.Name.Equals("Class Name", StringComparison.OrdinalIgnoreCase)))
            {
                entry = field.String(0);
                continue;
            }
            for (int i = 0; i < field.Values.Length; i++)
                if (field.String(i) is { } value && value.EndsWith(".vfx", StringComparison.OrdinalIgnoreCase))
                    list.Add(new VfxTableReference(table, entry, field.Prefix + field.Name, value, field.Line));
        }
        return list;
    }

    /// <summary>The references to one effect file (name compared without case).</summary>
    public static IEnumerable<VfxTableReference> ReferencesTo(IEnumerable<VfxTableReference> references, string vfxName) =>
        references.Where(r => r.VfxName.Equals(System.IO.Path.GetFileName(vfxName), StringComparison.OrdinalIgnoreCase));
}
