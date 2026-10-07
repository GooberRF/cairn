using System.Globalization;
using Cairn.Tbl.Schema;
using Cairn.Tbl.Text;

namespace Cairn.Tbl.Model;

/// <summary>What is wrong with a value.</summary>
public enum TblValueProblemKind
{
    /// <summary>The value is not of the type the field takes.</summary>
    Type,
    /// <summary>A number outside the field's range.</summary>
    Range,
    /// <summary>A name that is not one of the field's allowed values.</summary>
    NotAllowed,
    /// <summary>The field has no value.</summary>
    Missing,
    /// <summary>More values than the field takes.</summary>
    Extra,
    /// <summary>Characters glued to a value that the game reads past (<c>false`l</c>).</summary>
    Trailing,
}

/// <summary>One problem with a field's value; <c>Expected</c> lists the allowed values for <see cref="TblValueProblemKind.NotAllowed"/>.</summary>
public sealed record TblValueProblem(TblValueProblemKind Kind, TextSpan Span, string Message, ImmutableArray<string> Expected = default);

/// <summary>
/// A field's value read by its schema type: <c>Value</c> is a long, double, bool, string, double[] (vector or
/// colour), string[] (flags), or null.
/// </summary>
public sealed record TblParsedValue(object? Value, ImmutableArray<TblValueProblem> Problems)
{
    public bool IsValid => Problems.IsEmpty;
}

/// <summary>Reads field values by schema type.</summary>
public static class TblValueParser
{
    /// <summary>The most bytes <c>parse_string</c> stores (255 is fatal "Not enough storage provided").</summary>
    public const int MaxStringBytes = 254;

    /// <summary>Parses <paramref name="field"/>'s values as <paramref name="schema"/> says.</summary>
    public static TblParsedValue Parse(TblFieldNode field, TblFieldSchema schema)
    {
        var problems = ImmutableArray.CreateBuilder<TblValueProblem>();
        object? value = null;
        var values = field.Values;
        if (schema.Type == TblValueType.None)
        {
            if (values.Length > 0)
                problems.Add(new(TblValueProblemKind.Type, TextSpan.FromBounds(values[0].Span.Start, values[^1].Span.End), $"{field.Marker} takes no value; the game stops at this text."));
            return new TblParsedValue(null, problems.ToImmutable());
        }
        if (schema.Items.Length > 0)
        {
            for (int i = 0; i < values.Length && i < schema.Items.Length; i++)
                CheckOne(values[i], schema.Items[i], problems, field.Marker);
            int required = schema.Items.Count(x => x.Required);
            if (values.Length == 0 || values.Length < required)
                problems.Add(new(TblValueProblemKind.Missing, values.Length == 0 ? field.MarkerSpan : values[^1].Span, $"{field.Marker} takes {schema.Items.Length} values; {values.Length} given."));
            else if (values.Length > schema.Items.Length && schema.Rows.IsEmpty)
                Extra(values, schema.Items.Length, problems, field.Marker);
            return new TblParsedValue(values.Length > 0 ? values[0].Text : null, problems.ToImmutable());
        }
        if (values.Length == 0)
        {
            if (schema.Type is not (TblValueType.Text or TblValueType.List))
                problems.Add(new(TblValueProblemKind.Missing, field.MarkerSpan, $"{field.Marker} has no value."));
            return new TblParsedValue(null, problems.ToImmutable());
        }
        switch (schema.Type)
        {
            case TblValueType.Text:
                value = values[0].Text;
                break;
            case TblValueType.List:
                value = values[0].Text;
                if (schema.ElementType is { } of)
                {
                    var element = new TblFieldSchema { Name = schema.Name, Type = of, TypeName = of.ToString().ToLowerInvariant(), Values = schema.Values, Min = schema.Min, Max = schema.Max, Strict = schema.Strict };
                    var items = values.Length == 1 && values[0].Kind is TblValueKind.List or TblValueKind.Block or TblValueKind.Vector ? values[0].Items : values;
                    foreach (var item in items) CheckOne(item, element, problems, field.Marker);
                }
                break;
            case TblValueType.Color:
                value = CheckColor(values, schema, problems, field.Marker);
                break;
            default:
                value = CheckOne(values[0], schema, problems, field.Marker);
                if (values.Length > 1 && schema.Rows.IsEmpty) Extra(values, 1, problems, field.Marker);
                break;
        }        return new TblParsedValue(value, problems.ToImmutable());
    }

    /// <summary>The string a value stands for: a string's content, or the text of <c>XSTR(id, "text")</c>.</summary>
    public static string? StringOf(TblValueNode v) => v.Kind switch
    {
        TblValueKind.String => v.Text,
        TblValueKind.Call when v.Text.Equals("XSTR", StringComparison.OrdinalIgnoreCase) => v.Items.FirstOrDefault(i => i.Kind == TblValueKind.String)?.Text,
        _ => null,
    };

    /// <summary>Checks one unmarked value (a row or matrix cell) against <paramref name="schema"/>; <paramref name="label"/> names it in messages.</summary>
    internal static (object? Value, ImmutableArray<TblValueProblem> Problems) CheckValue(TblValueNode value, TblFieldSchema schema, string label)
    {
        var problems = ImmutableArray.CreateBuilder<TblValueProblem>();
        object? result = schema.Type == TblValueType.Color ? CheckColor([value], schema, problems, label) : CheckOne(value, schema, problems, label);
        if (schema.MaxLength is int max && StringOf(value) is { } s && s.Length > max)
            problems.Add(new(TblValueProblemKind.Range, value.ContentSpan, $"This text is {s.Length} characters long; {label} holds at most {max}."));
        return (result, problems.ToImmutable());
    }

    private static object? CheckOne(TblValueNode v, TblFieldSchema s, ImmutableArray<TblValueProblem>.Builder problems, string marker)
    {
        switch (s.Type)
        {
            case TblValueType.Int:
            case TblValueType.Float:
            {
                if (v.Kind != TblValueKind.Number || v.Number is not double d)
                {
                    problems.Add(new(TblValueProblemKind.Type, v.Span, $"{marker} takes {(s.Type == TblValueType.Int ? "a whole number" : "a number")}, not {Describe(v)}."));
                    return null;
                }
                bool hex = v.Text.TrimStart('-', '+').StartsWith("0x", StringComparison.OrdinalIgnoreCase);
                if (s.Type == TblValueType.Int && !hex && v.Text.Contains('.'))
                    problems.Add(new(TblValueProblemKind.Type, v.Span, $"{marker} takes a whole number, not {v.Text}."));
                else if (!hex && (v.Text.Contains('e') || v.Text.Contains('E')))
                    problems.Add(new(TblValueProblemKind.Type, v.Span, $"The game cannot read the exponent in {v.Text}; write the number out in full."));
                else if (v.Text.StartsWith('+'))
                    problems.Add(new(TblValueProblemKind.Type, v.Span, $"The game cannot read a '+' sign in {v.Text}; remove it."));
                CheckRange(d, v.Span, s, problems, marker);
                return s.Type == TblValueType.Int ? (object)(long)Math.Truncate(d) : d;
            }
            case TblValueType.Bool:
                if (v.Kind == TblValueKind.Word && new[] { "true", "false", "yes", "no" }.FirstOrDefault(b => v.Text.StartsWith(b, StringComparison.OrdinalIgnoreCase)) is { } prefix)
                {
                    // parse_bool consumes only the prefix: the rest stays in the stream and the next required read
                    // fails, unless the game seeks past it (reported as extra text, which a following seek excuses).
                    problems.Add(new(TblValueProblemKind.Extra, v.Span,
                        $"The game reads {v.Text[..prefix.Length]} here and leaves {v.Text[prefix.Length..]} behind; the next read stops with an error. Write true or false."));
                    return prefix is "true" or "yes";
                }
                if (v.Kind != TblValueKind.Boolean)
                {
                    problems.Add(new(TblValueProblemKind.Type, v.Span, $"{marker} takes true or false, not {Describe(v)}."));
                    return null;
                }
                return v.Text.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Text.Equals("yes", StringComparison.OrdinalIgnoreCase);
            case TblValueType.Text:
            case TblValueType.List:
                return v.Text;
            case TblValueType.Vec3:
                return CheckVector(v, s, problems, marker);
            case TblValueType.Flags:
                return CheckFlags(v, s, problems, marker);
            case TblValueType.Color:
                return CheckColor([v], s, problems, marker);
            default:
            {
                string? text = StringOf(v);
                if (text is null)
                {
                    problems.Add(new(TblValueProblemKind.Type, v.Span, $"{marker} takes a quoted string, not {Describe(v)}."));
                    return null;
                }
                if (s.Type == TblValueType.Enum && s.Strict && s.Values.Length > 0 && !s.Values.Any(x => string.Equals(x, text, StringComparison.OrdinalIgnoreCase)))
                    problems.Add(new(TblValueProblemKind.NotAllowed, v.ContentSpan, $"\"{text}\" is not a value {marker} accepts.", s.Values));
                return text;
            }
        }
    }

    private static double[]? CheckVector(TblValueNode v, TblFieldSchema s, ImmutableArray<TblValueProblem>.Builder problems, string marker)
    {
        if (v.Kind != TblValueKind.Vector || v.Items.Length != 3 || v.Items.Any(i => i.Number is null))
        {
            problems.Add(new(TblValueProblemKind.Type, v.Span, $"{marker} takes a vector such as <0.0, 1.0, 0.0>, not {Describe(v)}."));
            return null;
        }
        var result = v.Items.Select(i => i.Number!.Value).ToArray();
        for (int i = 0; i < 3; i++) CheckRange(result[i], v.Items[i].Span, s, problems, marker);
        return result;
    }

    private static double[]? CheckColor(ImmutableArray<TblValueNode> values, TblFieldSchema s, ImmutableArray<TblValueProblem>.Builder problems, string marker)
    {
        if (values.IsEmpty) return null;
        // parse_color: "{r, g, b[, a]}"; the brace and the first two commas are required (fatal otherwise).
        // Fields with their own syntax (Alpine colours) are read differently.
        if (s.Syntax is null && !(values.Length == 1 && values[0].Kind == TblValueKind.Block && (values[0].CommaGaps & 3) == 3))
        {
            problems.Add(new(TblValueProblemKind.Type, TextSpan.FromBounds(values[0].Span.Start, values[^1].Span.End),
                $"{marker} takes a colour in braces with commas, such as {{255, 255, 255}} or {{255, 255, 255, 255}}."));
            return null;
        }
        var items = values.Length == 1 && values[0].Kind is TblValueKind.Vector or TblValueKind.List or TblValueKind.Block ? values[0].Items : values;
        if (items.Length is < 3 or > 4 || items.Any(i => i.Number is null))
        {
            problems.Add(new(TblValueProblemKind.Type, values[0].Span, $"{marker} takes a colour of three or four numbers (red green blue [alpha])."));
            return null;
        }
        var result = items.Select(i => i.Number!.Value).ToArray();
        double min = s.Min ?? 0, max = s.Max ?? 255;
        for (int i = 0; i < result.Length; i++)
        {
            if (result[i] < min || result[i] > max)
                problems.Add(new(TblValueProblemKind.Range, items[i].Span, $"{items[i].Text} is outside {Fmt(min)} to {Fmt(max)} for {marker}."));
        }
        return result;
    }

    private static string[]? CheckFlags(TblValueNode v, TblFieldSchema s, ImmutableArray<TblValueProblem>.Builder problems, string marker)
    {
        var items = v.Kind == TblValueKind.List ? v.Items : [v];
        var result = new List<string>();
        if (v.Kind != TblValueKind.List)
            problems.Add(new(TblValueProblemKind.Type, v.Span, $"{marker} takes a list in parentheses such as (\"flag1\" \"flag2\")."));
        else if (v.HasCommas)
            problems.Add(new(TblValueProblemKind.Type, v.Span, $"The names in {marker} must be separated by spaces, not commas."));
        foreach (var item in items)
        {
            if (item.Kind != TblValueKind.String)
            {
                problems.Add(new(TblValueProblemKind.Type, item.Span, $"{marker} takes a list of quoted flag names such as (\"flag1\" \"flag2\"), not {Describe(item)}."));
                continue;
            }
            if (item.Text.Length > 0 && s.Strict && s.Values.Length > 0 && !s.Values.Any(x => string.Equals(x, item.Text, StringComparison.OrdinalIgnoreCase)))
                problems.Add(new(TblValueProblemKind.NotAllowed, item.ContentSpan, $"\"{item.Text}\" is not a flag {marker} accepts.", s.Values));
            result.Add(item.Text);
        }
        return [.. result];
    }

    private static void Extra(ImmutableArray<TblValueNode> values, int expected, ImmutableArray<TblValueProblem>.Builder problems, string marker) =>
        problems.Add(new(TblValueProblemKind.Extra, TextSpan.FromBounds(values[expected].Span.Start, values[^1].Span.End),
            $"{marker} takes {(expected == 1 ? "one value" : expected + " values")}; the game stops at this extra text."));

    private static void CheckRange(double d, TextSpan span, TblFieldSchema s, ImmutableArray<TblValueProblem>.Builder problems, string marker)
    {
        if (s.Min is double min && d < min || s.Max is double max && d > max)
        {
            string range = (s.Min, s.Max) switch
            {
                (double a, double b) => $"{Fmt(a)} to {Fmt(b)}",
                (double a, null) => $"at least {Fmt(a)}",
                (null, double b) => $"at most {Fmt(b)}",
                _ => "",
            };
            problems.Add(new(TblValueProblemKind.Range, span, $"{Fmt(d)} is out of range for {marker}: {range}{(s.Unit is { } u ? " " + u : "")}."));
        }
    }

    private static string Fmt(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>"a quoted string", "the word foo", ... for messages.</summary>
    public static string Describe(TblValueNode v) => v.Kind switch
    {
        TblValueKind.String => $"the string \"{Clip(v.Text)}\"",
        TblValueKind.Number => $"the number {v.Text}",
        TblValueKind.Boolean => v.Text,
        TblValueKind.Word => $"the unquoted word {Clip(v.Text)}",
        TblValueKind.List => "a list in parentheses",
        TblValueKind.Vector => "a vector",
        TblValueKind.Block => "a block in braces",
        TblValueKind.Call => v.Text + "(...)",
        _ => "a stray bracket",
    };

    private static string Clip(string s) => s.Length > 40 ? s[..37] + "..." : s;
}
