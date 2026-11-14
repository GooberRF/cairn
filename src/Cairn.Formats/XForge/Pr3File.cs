using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Cairn.Formats.XForge;

/// <summary>
/// A 12-byte value group of a particle system (the game reads it with one routine): an <c>i32</c> kind, then two
/// floats. The meaning is unknown: the kinds seen are 0, 3 and 5, and the floats are probably a constant, or the ends of
/// a random range, depending on the kind.
/// </summary>
/// <param name="UnknownKind">The kind (0, 3 or 5 in the shipped files).</param>
/// <param name="First">The first float.</param>
/// <param name="Second">The second float.</param>
public readonly record struct Pr3ValueGroup(int UnknownKind, float First, float Second)
{
    /// <summary>The size in the file.</summary>
    public const int Size = 12;

    /// <summary>True when every part is zero.</summary>
    public bool IsZero => UnknownKind == 0 && First == 0 && Second == 0;
}

/// <summary>
/// An X-Forge particle system (<c>.pr3</c>, Red Faction for N-Gage; <c>PS_*</c> emitter nodes of <c>.xff</c> scenes name
/// them): a fixed 268-byte block of fields, then the animation frames as texture names separated by <c>|</c>. Read only:
/// Cairn never writes these, and does not convert them (too few fields are understood to fill an RF1 emitter).
/// </summary>
/// <remarks>
/// <para>
/// All values are little-endian. The block starts with <c>"PS"</c> and the version as a big-endian <c>u16</c> (3). The
/// game reads every float and converts it to 16.16 fixed point. The meanings come from reading the game's loader and the
/// seven shipped files: the properties without "Unknown" in their name are likely meanings (the maximum particle count is
/// verified), and the <c>Unknown*</c> ones are kept as stored, named by their file offset.
/// </para>
/// <para>
/// The frame list is read as the game reads it: characters up to a <c>|</c> make one name, and an empty name (two
/// <c>|</c> in a row) ends the list. The shipped files end with <c>|||</c>, so one <c>|</c> is left after the end. A name
/// the end of the file cuts off (no <c>|</c> after it) is dropped by the game; Cairn keeps it in
/// <see cref="DroppedName"/> and says so in <see cref="Problems"/>.
/// </para>
/// </remarks>
public sealed class Pr3File
{
    /// <summary>The file extension.</summary>
    public const string Extension = ".pr3";

    /// <summary>The size of the field block before the frame list.</summary>
    public const int BlockSize = 268;

    /// <summary>The version this reader knows.</summary>
    public const int KnownVersion = 3;

    /// <summary>The separator between frame names.</summary>
    public const char Separator = '|';

    /// <summary>The longest frame name the game's buffer holds (256 bytes with the terminator).</summary>
    public const int MaxFrameNameLength = 255;

    /// <summary>The most frames the game's list holds.</summary>
    public const int MaxFrames = 256;

    /// <summary>The largest magnitude the game's 16.16 fixed point holds.</summary>
    public const float MaxFixedPoint = 32767;

    private Pr3File(string name) => Name = name;

    /// <summary>The file name used in messages.</summary>
    public string Name { get; }

    /// <summary>The version (3).</summary>
    public int Version { get; private init; }

    /// <summary>Offset 0x04: unknown (bit flags at most; one shipped file holds uninitialised fill).</summary>
    public int Unknown04 { get; private init; }

    /// <summary>Offset 0x08: the emitter's offset or extent (likely).</summary>
    public Vector3 EmitterOffset { get; private init; }

    /// <summary>Offset 0x14: three value groups, one per axis (likely the initial velocity or direction; unknown).</summary>
    public IReadOnlyList<Pr3ValueGroup> Unknown14 { get; private init; } = [];

    /// <summary>Offset 0x38: per-axis damping (likely: a velocity multiplier).</summary>
    public Vector3 Damping { get; private init; }

    /// <summary>Offset 0x44: the acceleration, that is gravity (likely).</summary>
    public Vector3 Gravity { get; private init; }

    /// <summary>Offset 0x50: three value groups (unknown).</summary>
    public IReadOnlyList<Pr3ValueGroup> Unknown50 { get; private init; } = [];

    /// <summary>Offset 0x74: particles emitted per second (likely).</summary>
    public float EmissionRate { get; private init; }

    /// <summary>Offset 0x78: a particle's lifetime in milliseconds (likely).</summary>
    public int LifetimeMs { get; private init; }

    /// <summary>Offset 0x7C: a value group (lifetime or rate jitter; unknown).</summary>
    public Pr3ValueGroup Unknown7C { get; private init; }

    /// <summary>Offset 0x88: a particle count (the minimum or the burst; unknown).</summary>
    public int Unknown88 { get; private init; }

    /// <summary>Offset 0x8C: the most particles alive at once (verified: the loader allocates that many particles).</summary>
    public int MaxParticles { get; private init; }

    /// <summary>Offset 0x90: unknown (a scale, likely; the loader replaces 0 with 1).</summary>
    public float Unknown90 { get; private init; }

    /// <summary>Offset 0x94: unknown (1 in every shipped file).</summary>
    public float Unknown94 { get; private init; }

    /// <summary>Offset 0x98: unknown (50 in every shipped file).</summary>
    public float Unknown98 { get; private init; }

    /// <summary>Offset 0x9C: unknown (a blend or frame mode, perhaps).</summary>
    public int Unknown9C { get; private init; }

    /// <summary>Offset 0xA0: the start alpha or brightness, 0–255 (likely).</summary>
    public float StartAlpha { get; private init; }

    /// <summary>Offset 0xA4: unknown.</summary>
    public float UnknownA4 { get; private init; }

    /// <summary>Offset 0xA8: a value group (unknown).</summary>
    public Pr3ValueGroup UnknownA8 { get; private init; }

    /// <summary>Offset 0xB4: a particle's size at birth (likely).</summary>
    public float StartSize { get; private init; }

    /// <summary>Offset 0xB8: a particle's size at death (likely).</summary>
    public float EndSize { get; private init; }

    /// <summary>Offset 0xBC: a value group (unknown).</summary>
    public Pr3ValueGroup UnknownBC { get; private init; }

    /// <summary>Offset 0xC8: unknown.</summary>
    public float UnknownC8 { get; private init; }

    /// <summary>Offset 0xCC: three value groups (unknown).</summary>
    public IReadOnlyList<Pr3ValueGroup> UnknownCC { get; private init; } = [];

    /// <summary>Offset 0xF0: three floats (unknown).</summary>
    public Vector3 UnknownF0 { get; private init; }

    /// <summary>Offset 0xFC: three floats (unknown).</summary>
    public Vector3 UnknownFC { get; private init; }

    /// <summary>Offset 0x108: unknown.</summary>
    public int Unknown108 { get; private init; }

    /// <summary>The animation frames: texture names in the archive, in playing order (as the game reads them).</summary>
    public IReadOnlyList<string> Frames { get; private init; } = [];

    /// <summary>True when the list ends with an empty name (<c>||</c>), as every shipped file does.</summary>
    public bool FrameListEnded { get; private init; }

    /// <summary>A name the end of the file cut off without a <c>|</c> (the game drops it), or null.</summary>
    public string? DroppedName { get; private init; }

    /// <summary>The bytes after the list's end (1, the last <c>|</c> of <c>|||</c>, in the shipped files).</summary>
    public int TrailingBytes { get; private init; }

    /// <summary>What the game would read differently or not at all (the file still reads); empty for a clean file.</summary>
    public IReadOnlyList<string> Problems { get; private init; } = [];

    /// <summary>True when the name ends in <c>.pr3</c>.</summary>
    public static bool HasExtension(string? name) => name is not null && name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="head"/> starts with <c>"PS"</c> and version 3.</summary>
    public static bool LooksLikePr3(ReadOnlySpan<byte> head) =>
        head.Length >= 4 && head[0] == (byte)'P' && head[1] == (byte)'S' && BinaryPrimitives.ReadUInt16BigEndian(head[2..]) == KnownVersion;

    /// <summary>Reads a particle system from its bytes; <paramref name="name"/> is used in messages.</summary>
    /// <exception cref="AssetFormatException">Not a particle system, another version, or shorter than its field block.</exception>
    public static Pr3File Read(byte[] data, string name)
    {
        ArgumentNullException.ThrowIfNull(data);
        name ??= string.Empty;
        if (data.Length < 4 || data[0] != (byte)'P' || data[1] != (byte)'S')
            throw new AssetFormatException($"'{name}' is not an X-Forge particle system (it does not start with \"PS\").");
        int version = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2));
        if (version != KnownVersion)
            throw new AssetFormatException($"'{name}' is an X-Forge particle system of version {version}; Cairn reads version {KnownVersion}.");
        var c = new BinaryCursor(data, name);
        if (data.Length < BlockSize)
            throw c.Fail($"the file is {data.Length} bytes, shorter than the particle system's {BlockSize}-byte field block.");
        var problems = new List<string>();

        float F(string what)
        {
            int pos = c.Position;
            float v = c.ReadSingle(what);
            if (!float.IsFinite(v) || Math.Abs(v) > MaxFixedPoint)
                problems.Add($"The {what} at offset 0x{pos:X} ({v.ToString(System.Globalization.CultureInfo.InvariantCulture)}) is outside what the game's 16.16 fixed point holds (±{MaxFixedPoint:0}); the game reads another value.");
            return v;
        }
        Vector3 V(string what) => new(F(what), F(what), F(what));
        Pr3ValueGroup G(string what) => new(c.ReadInt32(what + " kind"), F(what), F(what));
        Pr3ValueGroup[] G3(string what) => [G(what), G(what), G(what)];

        // the frame list, as the game's loader reads it
        var frames = new List<string>();
        var current = new List<byte>();
        bool ended = false;
        int at = BlockSize;
        for (; at < data.Length; at++)
        {
            byte b = data[at];
            if (b != (byte)Separator)
            {
                current.Add(b);
                continue;
            }
            if (current.Count == 0)
            {
                ended = true;
                at++;
                break;
            }
            frames.Add(Encoding.Latin1.GetString([.. current]));
            current.Clear();
        }
        string? dropped = !ended && current.Count > 0 ? Encoding.Latin1.GetString([.. current]) : null;
        if (dropped is not null)
            problems.Add($"The frame list ends without a '|' after '{Shorten(dropped)}'; the game does not use that name.");
        else if (!ended && frames.Count > 0)
            problems.Add("The frame list has no end marker (an empty name, '||'); the game reads to the end of the file.");
        if (frames.Count == 0) problems.Add("The frame list is empty; the particles have no texture.");
        if (frames.Count > MaxFrames) problems.Add($"The frame list has {frames.Count} frames; the game's list holds {MaxFrames}.");
        foreach (var frame in frames.Where(f => f.Length > MaxFrameNameLength).Take(3))
            problems.Add($"The frame name '{Shorten(frame)}' is {frame.Length} characters; the game's buffer holds {MaxFrameNameLength}.");
        foreach (var frame in frames.Where(f => f.Any(ch => ch < ' ')).Take(3))
            problems.Add($"The frame name '{Shorten(frame)}' holds control characters.");

        c.Position = 4;
        var file = new Pr3File(name)
        {
            Version = version,
            Unknown04 = c.ReadInt32("value"),
            EmitterOffset = V("emitter offset"),
            Unknown14 = G3("value group"),
            Damping = V("damping"),
            Gravity = V("gravity"),
            Unknown50 = G3("value group"),
            EmissionRate = F("emission rate"),
            LifetimeMs = c.ReadInt32("lifetime"),
            Unknown7C = G("value group"),
            Unknown88 = c.ReadInt32("count"),
            MaxParticles = c.ReadInt32("particle count"),
            Unknown90 = F("value"),
            Unknown94 = F("value"),
            Unknown98 = F("value"),
            Unknown9C = c.ReadInt32("value"),
            StartAlpha = F("start alpha"),
            UnknownA4 = F("value"),
            UnknownA8 = G("value group"),
            StartSize = F("start size"),
            EndSize = F("end size"),
            UnknownBC = G("value group"),
            UnknownC8 = F("value"),
            UnknownCC = G3("value group"),
            UnknownF0 = V("value"),
            UnknownFC = V("value"),
            Unknown108 = c.ReadInt32("value"),
            Frames = frames,
            FrameListEnded = ended,
            DroppedName = dropped,
            TrailingBytes = ended ? data.Length - at : 0,
            Problems = problems,
        };
        if (c.Position != BlockSize) throw new InvalidOperationException("The particle system's field block was not read to its end.");
        if (file.MaxParticles < 0) problems.Add($"The particle count {file.MaxParticles} is negative; the game cannot allocate the particles.");

        return file;
    }

    /// <summary>Reads a particle system from a file.</summary>
    /// <exception cref="AssetFormatException">Not a particle system this reader knows.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static Pr3File Read(string path) => Read(File.ReadAllBytes(path), Path.GetFileName(path));

    private static string Shorten(string text)
    {
        var clean = new string([.. text.Take(48).Select(ch => ch < ' ' ? '?' : ch)]);
        return text.Length > 48 ? clean + "..." : clean;
    }
}
