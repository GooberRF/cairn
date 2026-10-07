using System.Collections.Immutable;
using Cairn.Tbl.Linting;
using Cairn.Tbl.Model;
using Cairn.Tbl.Schema;
using Cairn.Tbl.Text;

namespace Cairn.Tbl.Ui.Documents;

/// <summary>
/// One parse of a table document's text: the parsed table, its token classes and its diagnostics. Immutable;
/// a new one replaces it after every (debounced) edit. <see cref="Version"/> counts the parses of one document.
/// </summary>
/// <param name="Version">Increases with every parse of the document.</param>
/// <param name="Text">The text this model was built from.</param>
/// <param name="Parsed">The parsed table (sections, entries, fields with spans).</param>
/// <param name="Schema">The table's schema, or null for an unknown table.</param>
/// <param name="Classes">Token classes for highlighting (ordered, non-overlapping).</param>
/// <param name="Diagnostics">Lint results sorted by offset (empty until the first lint ran).</param>
public sealed record TblModel(
    int Version,
    string Text,
    Cairn.Tbl.Model.TblDocument Parsed,
    TblTableSchema? Schema,
    ImmutableArray<TblClassifiedSpan> Classes,
    ImmutableArray<TblDiagnostic> Diagnostics)
{
    /// <summary>Number of error diagnostics.</summary>
    public int ErrorCount => Diagnostics.Count(d => d.Severity == TblSeverity.Error);
    /// <summary>Number of warning diagnostics.</summary>
    public int WarningCount => Diagnostics.Count(d => d.Severity == TblSeverity.Warning);
    /// <summary>Number of information diagnostics.</summary>
    public int InfoCount => Diagnostics.Count(d => d.Severity == TblSeverity.Information);
    /// <summary>Entries in every section.</summary>
    public int EntryCount => _entryCount ??= Parsed.Entries.Count();
    private int? _entryCount;

    /// <summary>The classified token at <paramref name="offset"/>, or null on white space.</summary>
    public TblClassifiedSpan? ClassAt(int offset)
    {
        int lo = 0, hi = Classes.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var span = Classes[mid].Span;
            if (offset < span.Start) hi = mid - 1;
            else if (offset >= span.End) lo = mid + 1;
            else return Classes[mid];
        }
        return null;
    }
}

/// <summary>A token the user clicked (file name or entry reference), as raised by <see cref="TblDocument.TokenActivated"/>.</summary>
/// <param name="Class">The token's class (<see cref="TblTextClass.FileName"/> or <see cref="TblTextClass.RefName"/>).</param>
/// <param name="Span">Where it is in the text (string content only, no quotes).</param>
/// <param name="Text">Its text.</param>
/// <param name="Symbol">What the assist layer knows about it (file kind, referenced kind), or null.</param>
public sealed record TblToken(TblTextClass Class, TextSpan Span, string Text, Cairn.Tbl.Assist.TblSymbol? Symbol);
