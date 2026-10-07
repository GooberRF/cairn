using Cairn.Tbl.Schema;

namespace Cairn.Tbl.Model;

/// <summary>
/// The engine's conditional reads: children read only in one branch of their parent (<c>if: "true"</c>,
/// <c>childrenWhen</c>) and fields read only when another field has a value (<c>when: "required if $Flags contains
/// player_wep"</c>). Every answer is null when the text does not decide it (a value still being typed).
/// </summary>
public static class TblConditions
{
    /// <summary>True when the game reads <paramref name="field"/>'s children, false when it does not, null when unknown.</summary>
    public static bool? ChildrenRead(TblFieldNode field)
    {
        ArgumentNullException.ThrowIfNull(field);
        var fs = field.Schema;
        if (fs is null || fs.Children.IsEmpty) return null;
        if (fs.ChildrenOnlyIfTrue) return field.Parsed?.Value is bool b ? b : null;
        string? when = fs.ChildrenWhen?.Trim().ToLowerInvariant();
        if (when is null) return true;
        string? text = field.Values.FirstOrDefault() is { } v ? TblValueParser.StringOf(v) : null;
        if (text is null) return null;
        if (when.StartsWith("length>", StringComparison.Ordinal) && int.TryParse(when["length>".Length..], out int length))
            return text.Length > length;
        if (when is "known" or "unless")
        {
            bool known = fs.Values.IsEmpty || fs.Values.Any(x => string.Equals(x, text, StringComparison.OrdinalIgnoreCase));
            return known && !fs.ChildrenUnless.Any(x => string.Equals(x, text, StringComparison.OrdinalIgnoreCase));
        }
        return null;
    }

    /// <summary>When the game reads the children of a field like <paramref name="parent"/>, in words ("when $Glow: is true"), or null when always.</summary>
    public static string? DescribeChildrenGate(TblFieldSchema parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (parent.ChildrenOnlyIfTrue) return $"when {parent.Name} is true";
        string? when = parent.ChildrenWhen?.Trim().ToLowerInvariant();
        if (when is null) return null;
        if (when.StartsWith("length>", StringComparison.Ordinal)) return $"when {parent.Name} is longer than {when["length>".Length..]} characters";
        if (when is "known" or "unless")
            return parent.ChildrenUnless.IsEmpty ? $"when {parent.Name} is one of its known values"
                : $"when {parent.Name} is a known value other than " + string.Join(", ", parent.ChildrenUnless.Select(x => "\"" + x + "\""));
        return parent.ChildrenWhen;
    }

    /// <summary>
    /// Whether <paramref name="condition"/> holds among <paramref name="siblings"/> (the fields read at the same level):
    /// true or false when decided, null when the field it looks at has no readable value yet.
    /// </summary>
    public static bool? Holds(TblFieldCondition condition, IEnumerable<TblFieldNode> siblings)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(siblings);
        var field = siblings.FirstOrDefault(f => f.Is(condition.Field));
        if (field is null) return false;
        if (field.Values.IsEmpty || field.Values.Any(v => v.IsUnclosed || v.IsUnterminated)) return null;
        return field.Values.SelectMany(v => v.DescendantsAndSelf())
            .Any(v => TblValueParser.StringOf(v) is { } s && string.Equals(s, condition.Value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The fields read at the same level as <paramref name="field"/> (its parent's children, its entry's or its section's fields).</summary>
    public static ImmutableArray<TblFieldNode> SiblingsOf(TblFieldNode field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (field.Parent is { } p) return p.Children;
        if (field.Entry is { } e) return e.Fields;
        return field.Section.Fields;
    }

    /// <summary>
    /// Whether the game would read a field of schema <paramref name="fs"/> next to <paramref name="siblings"/>: false
    /// only when its <see cref="TblFieldSchema.RequiredIf"/> condition is false and it is <see cref="TblFieldSchema.AbsentOtherwise"/>.
    /// </summary>
    public static bool IsReadAmong(TblFieldSchema fs, IEnumerable<TblFieldNode> siblings)
    {
        ArgumentNullException.ThrowIfNull(fs);
        return fs.RequiredIf is not { } c || !fs.AbsentOtherwise || Holds(c, siblings) != false;
    }
}
