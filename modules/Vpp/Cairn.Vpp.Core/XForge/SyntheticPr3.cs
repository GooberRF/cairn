using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Cairn.Formats.XForge;

namespace Cairn.Vpp.XForge;

/// <summary>
/// Writes X-Forge particle systems (<c>.pr3</c>) for tests and self-tests: the 268-byte field block and the frame list.
/// The defaults are an explosion like the N-Gage release's (six frames growing from 0.15 to 5). Cairn itself never writes
/// these files.
/// </summary>
internal sealed record SyntheticPr3
{
    /// <summary>The two magic bytes.</summary>
    public string Magic { get; init; } = "PS";
    /// <summary>The version (big-endian u16).</summary>
    public ushort Version { get; init; } = Pr3File.KnownVersion;
    public Vector3 EmitterOffset { get; init; }
    public Vector3 Damping { get; init; } = new(0.95f, 0.95f, 0.95f);
    public Vector3 Gravity { get; init; }
    public float EmissionRate { get; init; } = 104;
    public int LifetimeMs { get; init; } = 500;
    public int Count88 { get; init; } = 3;
    public int MaxParticles { get; init; } = 3;
    public float StartAlpha { get; init; } = 255;
    public float StartSize { get; init; } = 0.15f;
    public float EndSize { get; init; } = 5;
    /// <summary>The frame names; written separated by '|' and ended by <see cref="End"/>.</summary>
    public IReadOnlyList<string> Frames { get; init; } = [.. Enumerable.Range(1, 6).Select(i => $"spr_rlexplosion_{i:00}.pcx")];
    /// <summary>What follows the last name: "|||" as the shipped files end (one '|' after the name, an empty name, one left over).</summary>
    public string End { get; init; } = "|||";
    /// <summary>Raw 32-bit values written over the block afterwards, by file offset (for the unknown fields).</summary>
    public IReadOnlyDictionary<int, uint> Raw { get; init; } = new Dictionary<int, uint>();

    /// <summary>The file's bytes.</summary>
    public byte[] Write()
    {
        var block = new byte[Pr3File.BlockSize];
        Encoding.ASCII.GetBytes(Magic.PadRight(2).AsSpan(0, 2), block);
        BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(2), Version);
        void I(int at, int v) => BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(at), v);
        void F(int at, float v) => BinaryPrimitives.WriteSingleLittleEndian(block.AsSpan(at), v);
        void V(int at, Vector3 v) { F(at, v.X); F(at + 4, v.Y); F(at + 8, v.Z); }
        I(0x04, 0x003E0000);
        V(0x08, EmitterOffset);
        V(0x38, Damping);
        V(0x44, Gravity);
        F(0x74, EmissionRate);
        I(0x78, LifetimeMs);
        I(0x88, Count88);
        I(0x8C, MaxParticles);
        F(0x90, 1);
        F(0x94, 1);
        F(0x98, 50);
        I(0x9C, 1);
        F(0xA0, StartAlpha);
        F(0xB4, StartSize);
        F(0xB8, EndSize);
        foreach (var (at, v) in Raw) BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(at), v);
        string text = Frames.Count == 0 ? End : string.Join(Pr3File.Separator, Frames) + End;
        return [.. block, .. Encoding.Latin1.GetBytes(text)];
    }
}
