using System.Collections.Immutable;
using System.Numerics;

namespace Cairn.Rfa.Formats.Rfa;

/// <summary>
/// One rotation key exactly as stored: the int16 quaternion components, the two ease bytes and the
/// pad word. Keeping the raw integers is what lets an untouched key survive any number of saves
/// without drifting; only a key that an edit changes is re-quantised.
/// </summary>
/// <remarks>
/// The quaternion is in the FILE convention: the conjugate of the active rotation, scaled by
/// <see cref="RfaClip.QuaternionScale"/>. Conversion to the active convention happens in
/// <c>Animation</c> (<c>ClipSampler.KeyRotation</c>), never here.
/// </remarks>
/// <param name="Time">Ticks (1/4800 s).</param>
/// <param name="X">File quaternion x, scaled by 16383.</param>
/// <param name="Y">File quaternion y, scaled by 16383.</param>
/// <param name="Z">File quaternion z, scaled by 16383.</param>
/// <param name="W">File quaternion w, scaled by 16383.</param>
/// <param name="EaseIn">Ease into this key from the previous one, -128..127 over 127.</param>
/// <param name="EaseOut">Ease out of this key towards the next one, -128..127 over 127.</param>
/// <param name="Pad">The trailing int16, 0 in every stock file; kept so it round-trips.</param>
public readonly record struct RfaRotKey(
    int Time, short X, short Y, short Z, short W, sbyte EaseIn = 0, sbyte EaseOut = 0, short Pad = 0)
{
    /// <summary>Size of one stored key.</summary>
    public const int Size = 16;

    /// <summary>The stored components over 16383, still in the file convention and not normalised.</summary>
    public Quaternion FileQuaternion => new(
        X / RfaClip.QuaternionScale, Y / RfaClip.QuaternionScale,
        Z / RfaClip.QuaternionScale, W / RfaClip.QuaternionScale);

    /// <summary>
    /// A key whose components are <c>round(c * 16383)</c> of a FILE-convention quaternion, clamped to
    /// the int16 range. The caller is responsible for sign continuity with the previous key.
    /// </summary>
    public static RfaRotKey Quantize(int time, Quaternion fileQuaternion, sbyte easeIn = 0, sbyte easeOut = 0) =>
        new(time, Q(fileQuaternion.X), Q(fileQuaternion.Y), Q(fileQuaternion.Z), Q(fileQuaternion.W), easeIn, easeOut);

    /// <summary>
    /// Like <see cref="Quantize"/>, but never stores a quaternion longer than 1: after rounding, while
    /// the integer length exceeds 16383 the floor/ceiling combination closest in angle is used instead
    /// (<see cref="WithinUnit"/>). This matters in game: the engine's slerp compares the RAW dot product with
    /// <c>1 - 1e-6</c>, so two neighbouring keys that are both slightly longer than 1 stop interpolating
    /// (the segment snaps to its later key) even when they are over a degree apart. The stock exporter
    /// never overshoots (476898 of 489545 stock keys are shorter than 1, 12573 exact, 74 longer).
    /// The angular error stays below 0.01 degrees.
    /// </summary>
    public static RfaRotKey QuantizeWithinUnit(int time, Quaternion fileQuaternion, sbyte easeIn = 0, sbyte easeOut = 0)
    {
        var (x, y, z, w) = WithinUnit(
            fileQuaternion.X * (double)RfaClip.QuaternionScale, fileQuaternion.Y * (double)RfaClip.QuaternionScale,
            fileQuaternion.Z * (double)RfaClip.QuaternionScale, fileQuaternion.W * (double)RfaClip.QuaternionScale);
        return new RfaRotKey(time, x, y, z, w, easeIn, easeOut);
    }

    /// <summary>
    /// Rounds a quaternion already scaled by 16383 to int16 components whose length is at most 16383:
    /// the nearest rounding when that is short enough, otherwise, of the 16 floor/ceiling combinations
    /// that are short enough, the one closest in angle to the exact value.
    /// </summary>
    public static (short X, short Y, short Z, short W) WithinUnit(double x, double y, double z, double w)
    {
        const long limit = 16383L * 16383L;
        Span<double> e = [x, y, z, w];
        Span<long> r = stackalloc long[4];
        for (int i = 0; i < 4; i++) r[i] = (long)Math.Clamp(Math.Round(e[i], MidpointRounding.AwayFromZero), short.MinValue, short.MaxValue);
        if (r[0] * r[0] + r[1] * r[1] + r[2] * r[2] + r[3] * r[3] <= limit)
            return ((short)r[0], (short)r[1], (short)r[2], (short)r[3]);

        double best = double.NegativeInfinity;
        Span<long> pick = stackalloc long[4];
        Span<long> c = stackalloc long[4];
        double en = Math.Sqrt(x * x + y * y + z * z + w * w);
        for (int mask = 0; mask < 16; mask++)
        {
            long len = 0;
            for (int i = 0; i < 4; i++)
            {
                c[i] = (long)Math.Clamp(((mask >> i) & 1) == 0 ? Math.Floor(e[i]) : Math.Ceiling(e[i]), short.MinValue, short.MaxValue);
                len += c[i] * c[i];
            }
            if (len > limit || len == 0) continue;
            // Cosine of the angle to the exact quaternion: larger is closer.
            double cos = (c[0] * x + c[1] * y + c[2] * z + c[3] * w) / (Math.Sqrt(len) * en);
            if (cos > best)
            {
                best = cos;
                c.CopyTo(pick);
            }
        }
        if (double.IsNegativeInfinity(best)) return ((short)r[0], (short)r[1], (short)r[2], (short)r[3]);
        return ((short)pick[0], (short)pick[1], (short)pick[2], (short)pick[3]);
    }

    private static short Q(float c) =>
        (short)Math.Clamp(MathF.Round(c * RfaClip.QuaternionScale, MidpointRounding.AwayFromZero), short.MinValue, short.MaxValue);
}

/// <summary>One position key: a point and the two ABSOLUTE Bezier control points around it.</summary>
/// <param name="Time">Ticks (1/4800 s).</param>
/// <param name="Position">Metres, in the parent bone's frame (model space for the root).</param>
/// <param name="InControl">Control point used on the segment that ends at this key.</param>
/// <param name="OutControl">Control point used on the segment that starts at this key.</param>
public readonly record struct RfaPosKey(int Time, Vector3 Position, Vector3 InControl, Vector3 OutControl)
{
    /// <summary>Size of one stored key.</summary>
    public const int Size = 40;

    /// <summary>A key on a constant track: both control points equal the position, as the stock exporter writes.</summary>
    public static RfaPosKey Constant(int time, Vector3 position) => new(time, position, position, position);
}

/// <summary>One bone's animation in a clip. Bones are matched to the mesh by INDEX.</summary>
/// <param name="Weight">Blend weight of this bone in this clip, 0..10: below 0.00001 the clip ignores the bone; as an action, 10 replaces the states on the bone (see <c>Animation.ClipBlender</c>). Stock uses 0-8 and 10.</param>
/// <param name="RotationKeys">Rotation keys in time order (as stored; not re-sorted).</param>
/// <param name="PositionKeys">Position keys in time order (as stored; not re-sorted).</param>
public sealed record RfaBoneTrack(
    float Weight,
    ImmutableArray<RfaRotKey> RotationKeys,
    ImmutableArray<RfaPosKey> PositionKeys)
{
    /// <summary>Bytes before the keys: weight plus the two int16 counts.</summary>
    public const int HeaderSize = 8;

    /// <summary>Stored size of this bone record.</summary>
    public int ByteSize => HeaderSize + RotationKeys.Length * RfaRotKey.Size + PositionKeys.Length * RfaPosKey.Size;
}

/// <summary>The bounding box version 8 morph positions are quantised against.</summary>
/// <param name="Min">Minimum corner (byte 0).</param>
/// <param name="Max">Maximum corner (byte 255).</param>
public readonly record struct RfaMorphBounds(Vector3 Min, Vector3 Max);

/// <summary>
/// Vertex (morph) animation: per-keyframe positions for a subset of the vertices of the one mesh the
/// clip was made for. Preserved byte for byte, strippable, never edited per vertex, dropped by
/// retargeting. See DESIGN.md section 9 for what is known about how the engine times and applies it.
/// </summary>
/// <param name="VertexIndices">Mesh vertex indices (the LOD's original vertex numbering, mapped through each batch's morph map).</param>
/// <param name="KeyframeCount">The stored keyframe count. It can be non-zero with no vertices (one stock v8 file stores 61 zero times and nothing else).</param>
/// <param name="KeyframeTimes">Version 8: one time in ticks per keyframe. Version 7: empty (the file stores no times).</param>
/// <param name="Bounds">Version 8 with at least one vertex and one keyframe: the quantisation box. Otherwise null.</param>
/// <param name="QuantizedPositions">Version 8: <c>[keyframe][vertex][xyz]</c> bytes, each a 0..255 fraction of <see cref="Bounds"/>. Otherwise empty.</param>
/// <param name="Positions">Version 7: <c>[keyframe][vertex]</c> float positions. Otherwise empty.</param>
public sealed record RfaMorph(
    ImmutableArray<short> VertexIndices,
    int KeyframeCount,
    ImmutableArray<int> KeyframeTimes,
    RfaMorphBounds? Bounds,
    ImmutableArray<byte> QuantizedPositions,
    ImmutableArray<Vector3> Positions)
{
    /// <summary>No morph data at all.</summary>
    public static RfaMorph Empty { get; } = new([], 0, [], null, [], []);

    /// <summary>True when there is no morph data at all (no vertices and a zero keyframe count).</summary>
    public bool IsEmpty => VertexIndices.Length == 0 && KeyframeCount == 0;

    /// <summary>Number of morphed vertices.</summary>
    public int VertexCount => VertexIndices.Length;

    /// <summary>
    /// The decoded position of one morphed vertex at one keyframe. Version 8 bytes are mapped
    /// linearly onto <see cref="Bounds"/> (<c>min + (max - min) * b / 255</c>, per the format notes).
    /// </summary>
    public Vector3 GetPosition(int keyframe, int vertex)
    {
        if ((uint)keyframe >= (uint)KeyframeCount) throw new ArgumentOutOfRangeException(nameof(keyframe));
        if ((uint)vertex >= (uint)VertexCount) throw new ArgumentOutOfRangeException(nameof(vertex));
        int i = keyframe * VertexCount + vertex;
        if (!Positions.IsDefaultOrEmpty) return Positions[i];
        if (QuantizedPositions.IsDefaultOrEmpty || Bounds is not { } box)
            throw new InvalidOperationException("This morph data has no positions.");
        var b = new Vector3(QuantizedPositions[i * 3], QuantizedPositions[i * 3 + 1], QuantizedPositions[i * 3 + 2]);
        return box.Min + (box.Max - box.Min) * (b / 255f);
    }
}

/// <summary>
/// An immutable .rfa animation clip, field for field as the file stores it
/// (research/anim_retarget/rfa_format.md). <see cref="RfaReader"/> and <see cref="RfaWriter"/>
/// round-trip every stock clip byte for byte.
/// </summary>
public sealed record RfaClip
{
    /// <summary>"VMVF" read as a little-endian int32.</summary>
    public const uint Signature = 0x46564D56;

    /// <summary>Animation time units per second.</summary>
    public const int TicksPerSecond = 4800;

    /// <summary>One frame at 30 fps, and the start time of every stock clip.</summary>
    public const int TicksPerFrame = 160;

    /// <summary>Scale of the stored int16 quaternion components.</summary>
    public const float QuaternionScale = 16383f;

    /// <summary>Scale of the stored int8 ease values.</summary>
    public const float EaseScale = 127f;

    /// <summary>Size of the fixed header, up to and including <c>total_translation</c>.</summary>
    public const int HeaderSize = 0x48;

    /// <summary>Offset of the bone offset table (after the two morph offsets).</summary>
    public const int BoneTableOffset = 0x50;

    /// <summary>The bone limit the engine poses (and Alpine Faction checks).</summary>
    public const int MaxEngineBones = 50;

    /// <summary>7 or 8. Version 8 stores morph keyframe times and quantised morph positions.</summary>
    public int Version { get; init; } = 8;

    /// <summary>Exporter position tolerance; never read by the game.</summary>
    public float PosReduction { get; init; }

    /// <summary>Exporter rotation tolerance; never read by the game.</summary>
    public float RotReduction { get; init; }

    /// <summary>Ticks; stock clips start at 160.</summary>
    public int StartTime { get; init; } = TicksPerFrame;

    /// <summary>Ticks.</summary>
    public int EndTime { get; init; } = TicksPerFrame;

    /// <summary>Ticks the weight ramps up over when the clip plays as an action (ramp-in wins where the ramps overlap).</summary>
    public int RampIn { get; init; }

    /// <summary>Ticks the weight ramps down over before the end when the clip plays as an action.</summary>
    public int RampOut { get; init; }

    /// <summary>
    /// Not read by the game (only <c>Skeleton::get_total_rotation_unused</c> touches it). (0, 0, 0, 1) in
    /// 871 of the 1009 stock files; the others hold exporter leftovers. Raw floats, kept as stored.
    /// </summary>
    public Quaternion TotalRotation { get; init; } = Quaternion.Identity;

    /// <summary>Not read by the game (<c>Skeleton::get_total_translation_unused</c>). Zero in most stock files.</summary>
    public Vector3 TotalTranslation { get; init; }

    /// <summary>One track per mesh bone, in the mesh's bone order.</summary>
    public ImmutableArray<RfaBoneTrack> Bones { get; init; } = [];

    /// <summary>Morph (vertex) animation; <see cref="RfaMorph.Empty"/> when there is none.</summary>
    public RfaMorph Morph { get; init; } = RfaMorph.Empty;

    /// <summary>Number of bone tracks.</summary>
    public int BoneCount => Bones.Length;

    /// <summary><see cref="EndTime"/> minus <see cref="StartTime"/>, in ticks.</summary>
    public int Duration => EndTime - StartTime;

    /// <summary>Duration in seconds.</summary>
    public double DurationSeconds => Duration / (double)TicksPerSecond;
}
