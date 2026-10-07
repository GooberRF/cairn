using Cairn.Atx.Text;

namespace Cairn.Atx.Parsing;

/// <summary>What a top-level block in the document is.</summary>
public enum AtxBlockKind
{
    /// <summary>The <c>[header]</c> table.</summary>
    Header,
    /// <summary>A <c>[[frame]]</c> entry.</summary>
    Frame,
    /// <summary>Any other top-level table or key/value the ATX format does not define.</summary>
    Other,
}

/// <summary>
/// One top-level block of the document: its declaration line plus the comment lines directly
/// above it with no blank line between, through the line before the next block.
/// </summary>
/// <param name="Kind">Header, frame or other.</param>
/// <param name="FrameIndex">0-based frame index for <see cref="AtxBlockKind.Frame"/>, else -1.</param>
/// <param name="Span">The whole block, trailing blank lines included.</param>
/// <param name="ContentSpan">The block up to the end of its last non-blank line.</param>
/// <param name="DeclarationSpan">The <c>[header]</c> / <c>[[frame]]</c> line itself, without its line break.</param>
public sealed record AtxBlock(
    AtxBlockKind Kind,
    int FrameIndex,
    TextSpan Span,
    TextSpan ContentSpan,
    TextSpan DeclarationSpan);

/// <summary>A single <c>key = value</c> assignment, with the spans an edit needs.</summary>
/// <param name="Key">The key as written (dotted keys are joined with '.').</param>
/// <param name="KeySpan">Span of the key token.</param>
/// <param name="ValueSpan">Span of the value token only.</param>
/// <param name="LineSpan">Span of the whole line including its line break.</param>
public sealed record AtxKeyEntry(string Key, TextSpan KeySpan, TextSpan ValueSpan, TextSpan LineSpan);

/// <summary>
/// The document's physical layout: block spans, per-key spans and offset lookups. Everything the
/// editor needs to change text in place without reserialising the file.
/// </summary>
public sealed class SyntaxMap
{
    internal SyntaxMap(
        string text,
        LineMap lines,
        LineEndingKind lineEnding,
        IReadOnlyList<AtxBlock> blocks,
        TextSpan preamble,
        IReadOnlyDictionary<string, AtxKeyEntry> headerKeys,
        IReadOnlyList<IReadOnlyDictionary<string, AtxKeyEntry>> frameKeys)
    {
        Text = text;
        Lines = lines;
        LineEnding = lineEnding;
        Blocks = blocks;
        Preamble = preamble;
        HeaderKeys = headerKeys;
        FrameKeys = frameKeys;
        FrameBlocks = [.. blocks.Where(b => b.Kind == AtxBlockKind.Frame)];
        HeaderBlock = blocks.FirstOrDefault(b => b.Kind == AtxBlockKind.Header);
    }

    /// <summary>The exact text this map describes.</summary>
    public string Text { get; }

    /// <summary>Offset/line lookups for <see cref="Text"/>.</summary>
    public LineMap Lines { get; }

    /// <summary>The document's dominant line ending; generated text must match it.</summary>
    public LineEndingKind LineEnding { get; }

    /// <summary>All top-level blocks in document order.</summary>
    public IReadOnlyList<AtxBlock> Blocks { get; }

    /// <summary>Frame blocks in order, one per <c>[[frame]]</c>.</summary>
    public IReadOnlyList<AtxBlock> FrameBlocks { get; }

    /// <summary>The <c>[header]</c> block, or null when the file has none.</summary>
    public AtxBlock? HeaderBlock { get; }

    /// <summary>Leading comments/blank lines that belong to no block (the file's banner comment).</summary>
    public TextSpan Preamble { get; }

    /// <summary>Key entries of <c>[header]</c>, by key name.</summary>
    public IReadOnlyDictionary<string, AtxKeyEntry> HeaderKeys { get; }

    /// <summary>Key entries of each frame, by key name, in frame order.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, AtxKeyEntry>> FrameKeys { get; }

    /// <summary>The frame whose block contains <paramref name="offset"/>, or null.</summary>
    public int? FrameIndexAt(int offset) => FindBlock(FrameBlocks, offset)?.FrameIndex;

    /// <summary>The block containing <paramref name="offset"/>, or null when it is in the preamble.</summary>
    public AtxBlock? BlockAt(int offset) => FindBlock(Blocks, offset);

    /// <summary>
    /// Block spans are half-open and tile the document, so the offset at the start of one block is
    /// also the end of the one before it. Testing inclusively would hand every caret sitting at the
    /// start of a <c>[[frame]]</c> line to the previous frame; only the very end of the text has no
    /// block of its own and belongs to the last one.
    /// </summary>
    private AtxBlock? FindBlock(IReadOnlyList<AtxBlock> blocks, int offset)
    {
        foreach (var b in blocks)
        {
            if (b.Span.Contains(offset)) return b;
        }
        if (blocks.Count > 0 && offset == Text.Length && blocks[^1].Span.End == offset) return blocks[^1];
        return null;
    }
}
