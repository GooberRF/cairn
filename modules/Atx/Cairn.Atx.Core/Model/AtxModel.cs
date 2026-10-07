using Cairn.Atx.Schema;
using Cairn.Atx.Text;

namespace Cairn.Atx.Model;

/// <summary>
/// The <c>[header]</c> table. Every property mirrors what <c>parse_atx</c> reads; the
/// <c>Effective*</c> properties apply the same clamps and fallbacks the game applies.
/// </summary>
public sealed class AtxHeader
{
    /// <summary>False when the file has no header at all (defaults apply).</summary>
    public bool IsPresent { get; init; }

    /// <summary>Span of the <c>[header]</c> line, when the header is a real table.</summary>
    public TextSpan? TableHeaderSpan { get; init; }

    /// <summary>Span of the whole header block (its line plus its key/value lines).</summary>
    public TextSpan? BlockSpan { get; init; }

    public Located<long>? FrameTime { get; init; }
    public Located<bool>? InitiallyOn { get; init; }
    public Located<long>? AnimationMode { get; init; }
    public Located<string>? Format { get; init; }
    public Located<string>? AlphaMask { get; init; }
    public Located<string>? Material { get; init; }

    /// <summary>Keys present in <c>[header]</c> that the schema does not define (the game ignores them).</summary>
    public IReadOnlyList<UnknownKey> UnknownKeys { get; init; } = [];

    /// <summary>Frame time the game will use, after the >= 1 clamp and the 100 ms default.</summary>
    public int EffectiveFrameTimeMs => FrameTime is { Accepted: true } l
        ? ClampFrameTime(l.Value)
        : AtxSchema.DefaultFrameTimeMs;

    /// <summary>
    /// The game reads TOML integers as 64-bit and then narrows every one of them to <c>int</c>
    /// before clamping or range-checking, so a value that does not fit wraps rather than
    /// saturating (2^32 becomes 0, 2^32 + 2 becomes 2).
    /// </summary>
    public static int NarrowToInt(long value) => unchecked((int)value);

    /// <summary>Port of <c>std::max&lt;int&gt;(ATX_MIN_FRAME_TIME_MS, static_cast&lt;int&gt;(*v))</c>.</summary>
    public static int ClampFrameTime(long value) =>
        Math.Max(AtxSchema.MinFrameTimeMs, NarrowToInt(value));

    /// <summary>Whether the animation starts playing, after the default.</summary>
    public bool EffectiveInitiallyOn =>
        InitiallyOn is { Accepted: true } l ? l.Value : AtxSchema.DefaultInitiallyOn;

    /// <summary>Animation mode the game will use; out-of-range values fall back to Static.</summary>
    public AtxAnimationMode EffectiveAnimationMode => AnimationMode is { Accepted: true } l
        ? AtxSchema.ParseAnimationMode(NarrowToInt(l.Value)) ?? AtxSchema.DefaultAnimationMode
        : AtxSchema.DefaultAnimationMode;

    /// <summary>The <c>format</c> token, or null when unset or empty (an empty string is "not set").</summary>
    public string? EffectiveFormat => NonEmpty(Format);

    /// <summary>The alpha-mask filename, or null when unset or empty.</summary>
    public string? EffectiveAlphaMask => NonEmpty(AlphaMask);

    /// <summary>The material token, or null when unset or empty.</summary>
    public string? EffectiveMaterial => NonEmpty(Material);

    internal static string? NonEmpty(Located<string>? l) =>
        l is { Accepted: true, Value: { Length: > 0 } s } ? s : null;
}

/// <summary>One <c>[[frame]]</c> entry.</summary>
public sealed class AtxFrame
{
    /// <summary>0-based index, matching what <c>ATX_Set_Frame</c> uses.</summary>
    public int Index { get; init; }

    /// <summary>Span of the <c>[[frame]]</c> line (or of the inline table, in a non-canonical file).</summary>
    public TextSpan HeaderSpan { get; init; }

    /// <summary>
    /// Span of the whole frame block: attached comment lines, the <c>[[frame]]</c> line, its keys,
    /// and any trailing blank lines. Move/duplicate/remove operate on this, so comments travel.
    /// </summary>
    public TextSpan BlockSpan { get; init; }

    public Located<string>? File { get; init; }
    public Located<long>? FrameTime { get; init; }
    public Located<string>? Material { get; init; }

    /// <summary>Keys present in this frame that the schema does not define.</summary>
    public IReadOnlyList<UnknownKey> UnknownKeys { get; init; } = [];

    /// <summary>The filename the game will load, or null when missing/empty/wrong-typed.</summary>
    public string? EffectiveFile => AtxHeader.NonEmpty(File);

    /// <summary>The per-frame time override after clamping, or null when this frame inherits.</summary>
    public int? FrameTimeOverrideMs => FrameTime is { Accepted: true } l
        ? AtxHeader.ClampFrameTime(l.Value)
        : null;

    /// <summary>The per-frame material token, or null when this frame inherits.</summary>
    public string? MaterialOverride => AtxHeader.NonEmpty(Material);
}

/// <summary>
/// An immutable read of one .atx document. Produced by <c>AtxParser</c>; never mutated — GUI
/// actions go through <c>AtxEditor</c> and change the text instead.
/// </summary>
public sealed class AtxModel
{
    public required AtxHeader Header { get; init; }

    public required IReadOnlyList<AtxFrame> Frames { get; init; }

    /// <summary>Top-level keys and tables that are neither <c>header</c> nor <c>frame</c>.</summary>
    public IReadOnlyList<UnknownKey> UnknownTopLevel { get; init; } = [];

    /// <summary>Effective time for frame <paramref name="index"/>, exactly as <c>frame_time_for</c> computes it.</summary>
    public int FrameTimeMs(int index)
    {
        if (index < 0 || index >= Frames.Count) return Header.EffectiveFrameTimeMs;
        return Frames[index].FrameTimeOverrideMs ?? Header.EffectiveFrameTimeMs;
    }

    /// <summary>Effective material for frame <paramref name="index"/>, or null when nothing overrides.</summary>
    public string? MaterialFor(int index)
    {
        if (index < 0 || index >= Frames.Count) return Header.EffectiveMaterial;
        return Frames[index].MaterialOverride ?? Header.EffectiveMaterial;
    }

    /// <summary>How many frames set their own <c>frame_time</c> or <c>material</c>.</summary>
    public int OverrideCount => Frames.Count(f => f.FrameTime is not null || f.Material is not null);
}
