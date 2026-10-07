namespace Cairn.Atx.Text;

/// <summary>A single replacement of <see cref="Span"/> with <see cref="NewText"/>.</summary>
public readonly record struct TextEdit(TextSpan Span, string NewText)
{
    /// <summary>An insertion of <paramref name="text"/> at <paramref name="offset"/>.</summary>
    public static TextEdit Insert(int offset, string text) => new(TextSpan.At(offset), text);

    /// <summary>A deletion of <paramref name="span"/>.</summary>
    public static TextEdit Delete(TextSpan span) => new(span, string.Empty);

    /// <summary>A replacement of <paramref name="span"/> with <paramref name="text"/>.</summary>
    public static TextEdit Replace(TextSpan span, string text) => new(span, text);

    public override string ToString() => $"{Span} => \"{NewText}\"";
}
