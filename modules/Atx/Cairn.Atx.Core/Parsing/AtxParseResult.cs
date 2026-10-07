using Cairn.Atx.Linting;
using Cairn.Atx.Model;

namespace Cairn.Atx.Parsing;

/// <summary>Why a document is not in the standard <c>[header]</c> + <c>[[frame]]</c> layout.</summary>
public enum NonCanonicalReason
{
    /// <summary><c>frame = [{...}]</c> instead of <c>[[frame]]</c> blocks.</summary>
    FramesAsInlineTableArray,
    /// <summary><c>header = {...}</c> instead of a <c>[header]</c> table.</summary>
    HeaderAsInlineTable,
    /// <summary>Header fields assigned through top-level dotted keys, e.g. <c>header.frame_time = 80</c>.</summary>
    HeaderAsDottedKeys,
    /// <summary>A dotted key inside <c>[header]</c> or a <c>[[frame]]</c>.</summary>
    DottedKeyInTable,
    /// <summary>A sub-table such as <c>[header.extra]</c>.</summary>
    SubTable,
}

/// <summary>The result of parsing one .atx document.</summary>
public sealed class AtxParseResult
{
    /// <summary>The exact text that was parsed.</summary>
    public required string Text { get; init; }

    /// <summary>
    /// The model, or null when the TOML itself is invalid (the game refuses such a file outright,
    /// so there is nothing to show). The GUI keeps displaying the last good model instead.
    /// </summary>
    public AtxModel? Model { get; init; }

    /// <summary>The document's physical layout. Always present, even for an unparseable file.</summary>
    public required SyntaxMap SyntaxMap { get; init; }

    /// <summary>Diagnostics produced by parsing itself: TOML syntax errors and layout errors.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];

    /// <summary>True when the document uses the standard <c>[header]</c> + <c>[[frame]]</c> layout.</summary>
    public bool IsCanonical { get; init; } = true;

    /// <summary>Why <see cref="IsCanonical"/> is false, for the "Convert to standard layout" banner.</summary>
    public IReadOnlyList<NonCanonicalReason> NonCanonicalReasons { get; init; } = [];

    /// <summary>True when the TOML could not be parsed (or has duplicate keys/tables).</summary>
    public bool HasSyntaxErrors => Model is null;
}
