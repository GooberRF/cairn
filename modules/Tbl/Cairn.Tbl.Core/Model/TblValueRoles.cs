using Cairn.Tbl.Schema;
using Cairn.Tbl.Text;

namespace Cairn.Tbl.Model;

/// <summary>What a string value points at.</summary>
public enum TblValueRole
{
    /// <summary>A file name (texture, mesh, sound...).</summary>
    File,
    /// <summary>The name of an entry defined in a table (a weapon, an ammo type, a sound...).</summary>
    Ref,
}

/// <summary>A string value that names a file or another table's entry.</summary>
/// <param name="Role">File or entry reference.</param>
/// <param name="Kind">For a file: texture, mesh, anim, sound... (or "any"); for a reference: weapon, ammo, sound...</param>
/// <param name="Name">The name as written.</param>
/// <param name="Span">The name's characters (inside the quotes).</param>
/// <param name="Field">The field the value belongs to.</param>
public sealed record TblValueRef(TblValueRole Role, string Kind, string Name, TextSpan Span, TblFieldNode Field);

/// <summary>Finds the file names and entry references among field values, by schema or (without one) by shape.</summary>
public static class TblValueRoles
{
    private static readonly Dictionary<string, string[]> KindExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["texture"] = [".tga", ".vbm", ".dds", ".png", ".jpg", ".jpeg", ".bmp"],
        ["image"] = [".tga", ".vbm", ".dds", ".png", ".jpg", ".jpeg", ".bmp"],
        ["mesh"] = [".v3d", ".v3m", ".v3c", ".vcm"],
        ["anim"] = [".mvf", ".rfa"],
        ["effect"] = [".vfx"],
        ["vfx"] = [".vfx"],
        ["sound"] = [".wav", ".ogg"],
        ["music"] = [".wav", ".ogg"],
        ["table"] = [".tbl"],
        ["level"] = [".rfl"],
        ["movie"] = [".bik"],
        ["video"] = [".bik"],
        ["font"] = [".vf"],
    };

    /// <summary>Every extension a table can name a file with.</summary>
    public static IReadOnlyList<string> AllExtensions { get; } =
        [.. KindExtensions.Values.SelectMany(v => v).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The extensions of a file kind (every known one for "any" or an unknown kind).</summary>
    public static IReadOnlyList<string> ExtensionsOf(string? fileKind) =>
        fileKind is not null && KindExtensions.TryGetValue(fileKind, out var e) ? e : AllExtensions;

    /// <summary>The file kind an extension belongs to ("texture" for .tga), or "any".</summary>
    public static string KindOfExtension(string fileName)
    {
        string ext = Path.GetExtension(fileName);
        foreach (var (kind, exts) in KindExtensions)
            if (exts.Contains(ext, StringComparer.OrdinalIgnoreCase)) return kind;
        return "any";
    }

    /// <summary>True for a name with one of the known asset extensions and no path characters.</summary>
    public static bool LooksLikeFileName(string text)
    {
        if (text.Length < 3 || text.Length > 260 || text.IndexOfAny(['"', '<', '>', '|', '*', '?', '\r', '\n']) >= 0) return false;
        string ext = Path.GetExtension(text.Trim());
        return ext.Length > 1 && AllExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The file names and references among <paramref name="field"/>'s own values (not its children).</summary>
    public static IEnumerable<TblValueRef> Of(TblFieldNode field)
    {
        var schema = field.Schema;
        if (schema is null)
        {
            foreach (var v in field.Values.SelectMany(v => v.DescendantsAndSelf()))
            {
                if (v.Kind == TblValueKind.String && LooksLikeFileName(v.Text))
                    yield return new(TblValueRole.File, KindOfExtension(v.Text), v.Text.Trim(), v.ContentSpan, field);
            }
            yield break;
        }
        if (schema.Items.Length > 0)
        {
            for (int i = 0; i < field.Values.Length && i < schema.Items.Length; i++)
                foreach (var r in ByType(field, field.Values[i], schema.Items[i], positional: true)) yield return r;
            yield break;
        }
        foreach (var v in field.Values)
            foreach (var r in ByType(field, v, schema, positional: false)) yield return r;
    }

    private static IEnumerable<TblValueRef> ByType(TblFieldNode field, TblValueNode value, TblFieldSchema s, bool positional)
    {
        bool isFile = s.Type == TblValueType.File || (s.FileKind is not null && s.Type != TblValueType.Ref);
        bool isRef = s.Type == TblValueType.Ref || (s.RefKind is not null && s.Type != TblValueType.File);
        if (!isFile && !isRef) yield break;
        // In an untyped list with both a file and a reference kind, only shape tells them apart.
        bool loose = !positional && s.Type == TblValueType.List && s.ElementType is null;
        foreach (var v in value.DescendantsAndSelf())
        {
            if (v.Kind != TblValueKind.String || v.Text.Trim().Length == 0) continue;
            string text = v.Text.Trim();
            if (isFile && (!loose || LooksLikeFileName(text)))
                yield return new(TblValueRole.File, s.FileKind ?? KindOfExtension(text), text, v.ContentSpan, field);
            else if (isRef && (!loose || !isFile) && s.RefKind is not null)
                yield return new(TblValueRole.Ref, s.RefKind, v.Text, v.ContentSpan, field);
        }
    }

    /// <summary>Every file name and reference in the document.</summary>
    public static IEnumerable<TblValueRef> All(TblDocument doc) => doc.AllFields.SelectMany(Of);
}
