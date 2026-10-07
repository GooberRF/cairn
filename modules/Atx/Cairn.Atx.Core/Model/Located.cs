using Cairn.Atx.Text;

namespace Cairn.Atx.Model;

/// <summary>The TOML value kind actually found in the file, used for wrong-type diagnostics.</summary>
public enum TomlValueKind
{
    Unknown,
    String,
    Integer,
    Float,
    Boolean,
    Array,
    InlineTable,
    DateTime,
}

/// <summary>
/// One value read from the document, with the spans needed to edit it in place.
/// A <c>Located</c> only exists when the key is present in the file; <see cref="Accepted"/> says
/// whether the game would actually use it (a wrong-typed value is silently ignored).
/// </summary>
/// <typeparam name="T">The value type the game reads (<see cref="long"/>, <see cref="bool"/> or <see cref="string"/>).</typeparam>
/// <param name="Value">The value as the game would see it, or the default when not accepted.</param>
/// <param name="Accepted">False when the TOML type does not match what the game reads.</param>
/// <param name="KeySpan">Span of the key token.</param>
/// <param name="ValueSpan">Span of the value token only, so trailing comments survive edits.</param>
/// <param name="LineSpan">Span of the whole key/value line including its line break.</param>
/// <param name="ActualKind">The TOML kind found in the file.</param>
/// <param name="RawText">The raw value text exactly as written.</param>
public sealed record Located<T>(
    T? Value,
    bool Accepted,
    TextSpan KeySpan,
    TextSpan ValueSpan,
    TextSpan LineSpan,
    TomlValueKind ActualKind,
    string RawText)
{
    /// <summary>The value if accepted, otherwise <paramref name="fallback"/>.</summary>
    public T ValueOr(T fallback) => Accepted && Value is not null ? Value : fallback;
}

/// <summary>A key present in the document that the schema does not define.</summary>
/// <param name="Name">The key as written.</param>
/// <param name="KeySpan">Span of the key token.</param>
/// <param name="LineSpan">Span of the whole line including its line break.</param>
public sealed record UnknownKey(string Name, TextSpan KeySpan, TextSpan LineSpan);
