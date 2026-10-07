using System.Collections;

namespace Cairn.Atx.Text;

/// <summary>
/// A set of non-overlapping <see cref="TextEdit"/>s that are applied as one undo step.
/// Edits are stored sorted by offset and applied back-to-front so earlier offsets stay valid.
/// </summary>
public sealed class TextEditBatch : IReadOnlyList<TextEdit>
{
    private readonly TextEdit[] _edits;

    /// <summary>An empty batch (a no-op).</summary>
    public static TextEditBatch Empty { get; } = new([], string.Empty);

    /// <param name="edits">Edits in any order; must not overlap.</param>
    /// <param name="label">Short human-readable name for the undo step, e.g. "Set frame time".</param>
    public TextEditBatch(IEnumerable<TextEdit> edits, string label = "")
    {
        _edits = edits.OrderBy(e => e.Span.Start).ThenBy(e => e.Span.Length).ToArray();
        Label = label;
        for (int i = 1; i < _edits.Length; i++)
        {
            // Two pure insertions at the same offset are allowed (they concatenate in order);
            // anything else that overlaps is a bug in the caller.
            var prev = _edits[i - 1].Span;
            var cur = _edits[i].Span;
            if (cur.Start < prev.End)
            {
                throw new ArgumentException(
                    $"Overlapping edits: {prev} and {cur}.", nameof(edits));
            }
        }
    }

    /// <summary>Short human-readable name for the undo step.</summary>
    public string Label { get; }

    public int Count => _edits.Length;

    public TextEdit this[int index] => _edits[index];

    /// <summary>True when this batch changes nothing.</summary>
    public bool IsEmpty => _edits.Length == 0;

    /// <summary>Applies the batch to <paramref name="text"/> and returns the result.</summary>
    public string Apply(string text)
    {
        if (_edits.Length == 0) return text;
        var sb = new System.Text.StringBuilder(text);
        for (int i = _edits.Length - 1; i >= 0; i--)
        {
            var e = _edits[i];
            sb.Remove(e.Span.Start, e.Span.Length);
            sb.Insert(e.Span.Start, e.NewText);
        }
        return sb.ToString();
    }

    public IEnumerator<TextEdit> GetEnumerator() => ((IEnumerable<TextEdit>)_edits).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => $"{Label} ({_edits.Length} edit(s))";
}
