namespace Cairn.Tbl.Schema;

/// <summary>Placeholder values for inserting fields (quick fixes, completion snippets).</summary>
public static class TblTemplates
{
    /// <summary>A value to insert for a field: its default (quoted when the type needs it), else a placeholder of its type.</summary>
    public static string DefaultValue(TblFieldSchema fs)
    {
        ArgumentNullException.ThrowIfNull(fs);
        if (fs.Default is { } d)
        {
            bool quoted = fs.Type is TblValueType.String or TblValueType.File or TblValueType.Ref or TblValueType.Enum;
            return quoted && !d.StartsWith('"') ? "\"" + d + "\"" : d;
        }
        return fs.Type switch
        {
            TblValueType.Int => "0",
            TblValueType.Float => "0.0",
            TblValueType.Bool => "false",
            TblValueType.Vec3 => "<0.0, 0.0, 0.0>",
            TblValueType.Color => fs.Syntax is null ? "{255, 255, 255}" : "255 255 255",
            TblValueType.Flags => "()",
            TblValueType.Enum when fs.Values.Length > 0 => "\"" + fs.Values[0] + "\"",
            TblValueType.Text or TblValueType.List => "",
            _ => "\"\"",
        };
    }
}
