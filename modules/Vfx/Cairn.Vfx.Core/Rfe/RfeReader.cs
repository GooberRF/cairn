using System.Buffers.Binary;
using System.Text;
using Cairn.Formats;
using Cairn.Rfa.Formats.Legacy;

namespace Cairn.Vfx.Rfe;

/// <summary>What the first bytes of a file say about it as a Red Faction II effect.</summary>
public enum RfeLayout
{
    /// <summary>Not an effect ("FEFR" missing).</summary>
    NotAnEffect,
    /// <summary>The PC layout (version 3, then the u32 1): readable.</summary>
    Pc,
    /// <summary>The older memory-image layout (version 3 without the u32 1): the PC game refuses it, and so does Cairn.</summary>
    OldLayout,
    /// <summary>"FEFR" with another version.</summary>
    OtherVersion,
}

/// <summary>
/// Reads Red Faction II effects (<c>.rfe</c>) the way the PC game's loader does (<c>rf2.exe</c> 0x547cd0): little-endian,
/// no padding, every count followed by its elements, and the file must end exactly after the last emitter. Every
/// failure is an <see cref="AssetFormatException"/> with a message for the user.
/// </summary>
public static class RfeReader
{
    /// <summary>The magic as stored ("FEFR", the u32 0x52464546 "RFEF" read big-end first).</summary>
    public const uint Magic = 0x52464546;

    /// <summary>The only version the game loads.</summary>
    public const int Version = 3;

    /// <summary>The bytes <see cref="Identify"/> needs.</summary>
    public const int HeadBytes = 12;

    /// <summary>
    /// The longest effect Cairn reads, in seconds: ten minutes, 36 times the longest stock effect (16.67 s). A longer
    /// header duration is damage. The preview and the conversion sample every 1/30 s of the effect (at this cap the
    /// largest stock effect, stretched, takes about 0.15 s and 25 MB to open), and the converted effect (15 fps) stays
    /// far within the frame count Cairn's own <c>.vfx</c> reader accepts (100,000).
    /// </summary>
    public const float MaxDuration = 600;

    private const int TextureNameBytes = 24;
    private const int EmitterFixedBytes = 0x54;

    /// <summary>The file's extension.</summary>
    public const string Extension = ".rfe";

    /// <summary>True for a name ending in <c>.rfe</c>.</summary>
    public static bool HasExtension(string? name) => name is not null && name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="head"/> starts like a Red Faction II effect of any layout ("FEFR").</summary>
    public static bool IsEffect(ReadOnlySpan<byte> head) => head.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(head) == Magic;

    /// <summary>
    /// The layout of a file from its first bytes (at least <see cref="HeadBytes"/> to tell the old layout apart; with
    /// fewer, an effect of version 3 counts as <see cref="RfeLayout.Pc"/>).
    /// </summary>
    public static RfeLayout Identify(ReadOnlySpan<byte> head)
    {
        if (!IsEffect(head) || head.Length < 8) return RfeLayout.NotAnEffect;
        if (BinaryPrimitives.ReadInt32LittleEndian(head[4..]) != Version) return RfeLayout.OtherVersion;
        if (head.Length < 12) return RfeLayout.Pc;
        // the old layout has the duration here (then the radius and five (count, self-relative pointer) pairs); the
        // loader refuses anything but 1
        return BinaryPrimitives.ReadUInt32LittleEndian(head[8..]) == 1 ? RfeLayout.Pc : RfeLayout.OldLayout;
    }

    /// <summary>The message for a file that is an effect Cairn cannot read (<see cref="RfeLayout.OldLayout"/> or another version), or null.</summary>
    public static string? RefusalFor(ReadOnlySpan<byte> head, string name) => Identify(head) switch
    {
        RfeLayout.OldLayout => $"'{name}' uses an older Red Faction II effect layout that the PC game does not load either, so Cairn cannot show or convert it.",
        RfeLayout.OtherVersion => $"'{name}' is a Red Faction II effect of version {BinaryPrimitives.ReadInt32LittleEndian(head[4..])}; the game loads version 3 only.",
        _ => null,
    };

    /// <summary>Reads a whole effect file.</summary>
    /// <exception cref="AssetFormatException">It is not a readable Red Faction II effect.</exception>
    public static RfeFile Read(ReadOnlySpan<byte> data, string name)
    {
        name = string.IsNullOrEmpty(name) ? "effect" : name;
        if (!IsEffect(data)) throw new AssetFormatException($"'{name}' is not a Red Faction II effect (it does not start with \"FEFR\").");
        if (data.Length < 12) throw new AssetFormatException($"'{name}' is damaged: it ends inside the header ({data.Length} bytes).");
        if (RefusalFor(data, name) is { } refusal) throw new AssetFormatException(refusal);
        var r = new Cursor(data, name) { Position = 12 };
        float duration = r.Single("header");
        float radius = r.Single("header");
        if (!float.IsFinite(duration) || duration <= 0) throw r.Fail($"the duration is {duration} seconds, not a positive number");
        if (duration > MaxDuration) throw r.Fail(DurationTooLong(duration));

        int nt = r.Count("texture", TextureNameBytes);
        var textures = ImmutableArray.CreateBuilder<string>(nt);
        for (int i = 0; i < nt; i++) textures.Add(r.FixedName(TextureNameBytes));

        int nm = r.Count("material", 16);
        var materials = ImmutableArray.CreateBuilder<RfeMaterial>(nm);
        for (int i = 0; i < nm; i++)
        {
            int texture = r.Int32("material");
            if ((uint)texture >= (uint)nt) throw r.Fail($"material {i + 1} uses texture {texture}, but the effect lists {nt}");
            uint blend = r.UInt32("material");
            var self = r.Keys("material track");
            var opacity = r.Keys("material track");
            materials.Add(new RfeMaterial(texture, blend, self, opacity));
        }

        int nb = r.Count("mesh object", 20);
        var meshes = ImmutableArray.CreateBuilder<RfeMeshObject>(nb);
        for (int i = 0; i < nb; i++)
        {
            uint facing = r.UInt32("mesh object");
            int material = r.Int32("mesh object");
            if ((uint)material >= (uint)nm) throw r.Fail($"mesh object {i + 1} uses material {material}, but the effect has {nm}");
            var positions = r.Vectors("mesh position key");
            var rotations = r.Quaternions("mesh rotation key");
            var scales = r.Vectors("mesh scale key");
            int at = r.Position;
            var body = Rf2StaticBody.Read(data, ref at, name);
            r.Position = at;
            meshes.Add(new RfeMeshObject(facing, material, positions, rotations, scales, body));
        }

        int ne = r.Count("emitter", EmitterFixedBytes + 16);
        var emitters = ImmutableArray.CreateBuilder<RfeEmitter>(ne);
        for (int i = 0; i < ne; i++)
        {
            uint shape = r.UInt32("emitter"), flags = r.UInt32("emitter");
            int material = r.Int32("emitter");
            if ((uint)material >= (uint)nm) throw r.Fail($"emitter {i + 1} uses material {material}, but the effect has {nm}");
            int max = r.Int32("emitter");
            if (max < 0 || max > 0xFFFF) throw r.Fail($"emitter {i + 1} has {max} particle slots");
            var e = new RfeEmitter
            {
                Shape = shape, Flags = flags, Material = material, MaxParticles = max,
                Radius = r.Single("emitter"), Streak = r.Single("emitter"), SpeedVariance = r.Single("emitter"), Spread = r.Single("emitter"),
                Lifetime = r.Single("emitter"), LifetimeVariance = r.Single("emitter"), FadeIn = r.Single("emitter"), FadeOut = r.Single("emitter"),
                Grow = r.Single("emitter"), Shrink = r.Single("emitter"), Width = r.Single("emitter"), Height = r.Single("emitter"),
                SpawnCone = r.Single("emitter"), Unknown44 = r.Single("emitter"), Acceleration = r.Vector3("emitter"),
            };
            emitters.Add(e with
            {
                Positions = r.Vectors("emitter position key"),
                Rotations = r.Quaternions("emitter rotation key"),
                BirthRate = r.Keys("emitter birth rate"),
                Speed = r.Keys("emitter speed"),
            });
        }
        if (r.Position != data.Length)
            throw r.Fail($"{data.Length - r.Position:N0} bytes follow the last emitter, so the layout is not the one the game reads");
        return new RfeFile(duration, radius, textures.MoveToImmutable(), materials.MoveToImmutable(), meshes.MoveToImmutable(), emitters.MoveToImmutable())
        {
            ByteLength = data.Length,
        };
    }

    /// <summary>Why a duration above <see cref="MaxDuration"/> is refused.</summary>
    internal static string DurationTooLong(float duration) =>
        $"the duration is {duration:N0} seconds, longer than the {MaxDuration:N0} s Cairn reads (the stock effects last at most 17 s)";

    /// <summary>A bounds-checked little-endian cursor; running out of bytes throws <see cref="AssetFormatException"/>.</summary>
    private ref struct Cursor(ReadOnlySpan<byte> data, string name)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private readonly string _name = name;

        public int Position { get; set; }

        private readonly int Remaining => Math.Max(0, _data.Length - Position);

        public readonly AssetFormatException Fail(string why) => new($"'{_name}' is damaged: {why} (at offset 0x{Position:X}).");

        private readonly void Require(long count, string what)
        {
            if (count < 0 || count > Remaining) throw Fail($"the file ends inside a {what}");
        }

        public uint UInt32(string what)
        {
            Require(4, what);
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(_data[Position..]);
            Position += 4;
            return v;
        }

        public int Int32(string what) => (int)UInt32(what);

        public float Single(string what) => BitConverter.Int32BitsToSingle(Int32(what));

        public Vector3 Vector3(string what) => new(Single(what), Single(what), Single(what));

        /// <summary>A count of records at least <paramref name="size"/> bytes each, checked against the bytes left.</summary>
        public int Count(string what, int size)
        {
            int n = Int32(what + " count");
            if (n < 0 || (long)n * size > Remaining)
            {
                Position -= 4;
                throw Fail($"the {what} count {n:N0} does not fit in the {Remaining - 4:N0} bytes left");
            }
            return n;
        }

        public string FixedName(int size)
        {
            Require(size, "texture name");
            var bytes = _data.Slice(Position, size);
            Position += size;
            int nul = bytes.IndexOf((byte)0);
            return Encoding.Latin1.GetString(nul < 0 ? bytes : bytes[..nul]);
        }

        public ImmutableArray<RfeKey> Keys(string what)
        {
            int n = Count(what + " key", 8);
            var keys = ImmutableArray.CreateBuilder<RfeKey>(n);
            for (int i = 0; i < n; i++) keys.Add(new RfeKey(Single(what), Single(what)));
            return keys.MoveToImmutable();
        }

        public ImmutableArray<Vector3> Vectors(string what)
        {
            int n = Count(what, 12);
            var keys = ImmutableArray.CreateBuilder<Vector3>(n);
            for (int i = 0; i < n; i++) keys.Add(Vector3(what));
            return keys.MoveToImmutable();
        }

        public ImmutableArray<Quaternion> Quaternions(string what)
        {
            int n = Count(what, 16);
            var keys = ImmutableArray.CreateBuilder<Quaternion>(n);
            for (int i = 0; i < n; i++) keys.Add(new Quaternion(Single(what), Single(what), Single(what), Single(what)));
            return keys.MoveToImmutable();
        }
    }
}
