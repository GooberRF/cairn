using Cairn.Tbl.Index;
using Cairn.Tbl.Text;

namespace Cairn.Tbl.Linting;

/// <summary>How serious a diagnostic is.</summary>
public enum TblSeverity
{
    Error,
    Warning,
    Information,
}

/// <summary>A one-click repair: text edits on the document.</summary>
public sealed record TblQuickFix(string Title, ImmutableArray<TextEdit> Edits);

/// <summary>One problem found in a table.</summary>
/// <param name="Code">Stable code, <c>TBL001</c>... (see <see cref="TblRules"/>).</param>
/// <param name="Severity">Error, warning or information.</param>
/// <param name="Message">One plain sentence saying what is wrong.</param>
/// <param name="Help">What to do about it, or null.</param>
/// <param name="Span">Where (never past the end of the text).</param>
/// <param name="QuickFixes">Repairs offered (may be empty).</param>
public sealed record TblDiagnostic(string Code, TblSeverity Severity, string Message, string? Help, TextSpan Span, ImmutableArray<TblQuickFix> QuickFixes)
{
    public override string ToString() => $"{Code} {Severity} {Span}: {Message}";
}

/// <summary>A linter rule: code, default severity and a title for help and settings.</summary>
public sealed record TblRule(string Code, TblSeverity Severity, string Title);

/// <summary>Every rule the linter applies.</summary>
public static class TblRules
{
    public static readonly TblRule UnterminatedString = new("TBL001", TblSeverity.Error, "String without a closing quote");
    public static readonly TblRule UnterminatedComment = new("TBL002", TblSeverity.Error, "Block comment without */");
    public static readonly TblRule UnclosedBracket = new("TBL003", TblSeverity.Error, "Bracket not closed");
    public static readonly TblRule StrayCloser = new("TBL004", TblSeverity.Error, "Closing bracket with nothing to close");
    public static readonly TblRule MissingEnd = new("TBL005", TblSeverity.Error, "Section without #End");
    public static readonly TblRule StrayEnd = new("TBL006", TblSeverity.Warning, "#End outside a section");
    public static readonly TblRule MarkerWithoutColon = new("TBL007", TblSeverity.Warning, "Field name without its colon");
    public static readonly TblRule StrayText = new("TBL008", TblSeverity.Error, "Text outside any field");
    public static readonly TblRule CommentWithoutCr = new("TBL009", TblSeverity.Error, "// comment ended by a line break without CR");
    public static readonly TblRule ByteOrderMark = new("TBL010", TblSeverity.Error, "UTF-8 byte-order mark");
    public static readonly TblRule UnknownSection = new("TBL101", TblSeverity.Error, "Unknown section");
    public static readonly TblRule MissingSection = new("TBL102", TblSeverity.Error, "Required section missing");
    public static readonly TblRule UnknownField = new("TBL103", TblSeverity.Error, "Unknown field");
    public static readonly TblRule FieldOrder = new("TBL104", TblSeverity.Error, "Field out of the order the game reads");
    public static readonly TblRule WrongType = new("TBL105", TblSeverity.Error, "Value of the wrong type");
    public static readonly TblRule OutOfRange = new("TBL106", TblSeverity.Warning, "Value out of range");
    public static readonly TblRule NotAllowed = new("TBL107", TblSeverity.Warning, "Value not accepted");
    public static readonly TblRule MissingField = new("TBL108", TblSeverity.Error, "Required field missing");
    public static readonly TblRule DuplicateEntry = new("TBL109", TblSeverity.Information, "Duplicate entry name");
    public static readonly TblRule TooManyEntries = new("TBL112", TblSeverity.Information, "More entries than the game keeps");
    public static readonly TblRule TrailingText = new("TBL113", TblSeverity.Information, "Characters after a value that the game skips");
    public static readonly TblRule TooLong = new("TBL115", TblSeverity.Error, "Text too long");
    public static readonly TblRule TooMany = new("TBL116", TblSeverity.Error, "Too many list items");
    public static readonly TblRule WrongCount = new("TBL114", TblSeverity.Error, "Repeat count does not match");
    public static readonly TblRule RepeatedField = new("TBL110", TblSeverity.Error, "Field repeated");
    public static readonly TblRule MissingValue = new("TBL111", TblSeverity.Error, "Field without a value");
    public static readonly TblRule FileNotFound = new("TBL201", TblSeverity.Warning, "File not found");
    public static readonly TblRule NameNotDefined = new("TBL202", TblSeverity.Warning, "Name not defined");
    public static readonly TblRule ConditionalField = new("TBL117", TblSeverity.Error, "Field the game does not read here (its condition is false)");
    public static readonly TblRule SkippedField = new("TBL118", TblSeverity.Warning, "Field the game skips here (its condition is false)");
    public static readonly TblRule RowShape = new("TBL119", TblSeverity.Error, "Row with the wrong number of values");
    public static readonly TblRule AlpineValue = new("TBL120", TblSeverity.Warning, "Option value Alpine Faction cannot read as written");

    public static IReadOnlyList<TblRule> All { get; } =
    [
        UnterminatedString, UnterminatedComment, UnclosedBracket, StrayCloser, MissingEnd, StrayEnd, MarkerWithoutColon, StrayText, CommentWithoutCr,
        UnknownSection, MissingSection, UnknownField, FieldOrder, WrongType, OutOfRange, NotAllowed, MissingField, DuplicateEntry,
        RepeatedField, MissingValue, TooManyEntries, TrailingText, WrongCount, TooLong, TooMany, ConditionalField, SkippedField, RowShape, AlpineValue,
        ByteOrderMark, FileNotFound, NameNotDefined,
    ];
}

/// <summary>What the linter may consult besides the document.</summary>
public sealed record TblLintContext
{
    /// <summary>Syntax and schema rules only.</summary>
    public static TblLintContext Default { get; } = new();

    /// <summary>
    /// True when a referenced file name exists (game data, search folders, alongside the table). Called with
    /// each disk name the engine could mean (<c>.v3d</c> is tried as <c>.v3m</c> and <c>.v3c</c>). Null skips
    /// the file rule.
    /// </summary>
    public Func<string, bool>? FileExists { get; init; }

    /// <summary>The cross-table index for "name not defined"; null skips the rule.</summary>
    public TblIndex? Index { get; init; }

    /// <summary>How the file is encoded on disk, when known: a UTF-8 byte-order mark is reported.</summary>
    public Text.TblFileEncoding? Encoding { get; init; }

    /// <summary>The line ending inserted by quick fixes (default: the document's own).</summary>
    public string? NewLine { get; init; }

    /// <summary>
    /// References the stock table of the same name also leaves unresolved (from <see cref="TblLinter.UnresolvedNames"/>
    /// on the stock text): the game evidently tolerates them, so they are reported as information, not warnings.
    /// </summary>
    public IReadOnlySet<string>? StockMissing { get; init; }

    /// <summary>A context whose file rule goes through <paramref name="resolver"/>.</summary>
    public static TblLintContext ForResolver(Cairn.Assets.AssetResolver resolver, TblIndex? index = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        return new TblLintContext { FileExists = name => resolver.Resolve(name) is not null, Index = index };
    }
}
