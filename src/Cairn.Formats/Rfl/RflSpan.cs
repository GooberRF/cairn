using System.Buffers.Binary;
using System.Text;

namespace Cairn.Formats.Rfl;

/// <summary>Raised inside a section body when a record does not fit; the section walker catches it.</summary>
internal sealed class RflBodyException(string message) : Exception(message);

/// <summary>
/// Bounds-checked little-endian reader over one section body. It never sees bytes outside the body,
/// so a damaged section cannot move the walk through the section stream.
/// </summary>
internal ref struct RflSpan(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;

    public int Position;

    public readonly int Length => _data.Length;

    public readonly int Left => _data.Length - Position;

    public readonly void Need(long count)
    {
        if (count < 0 || count > Left)
            throw new RflBodyException($"{count} bytes needed at offset {Position}, {Left} left");
    }

    public byte U8()
    {
        Need(1);
        return _data[Position++];
    }

    public bool Bool() => U8() != 0;

    public ushort U16()
    {
        Need(2);
        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_data[Position..]);
        Position += 2;
        return v;
    }

    public int I32()
    {
        Need(4);
        int v = BinaryPrimitives.ReadInt32LittleEndian(_data[Position..]);
        Position += 4;
        return v;
    }

    public uint U32()
    {
        Need(4);
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(_data[Position..]);
        Position += 4;
        return v;
    }

    public float F32()
    {
        Need(4);
        float v = BinaryPrimitives.ReadSingleLittleEndian(_data[Position..]);
        Position += 4;
        return v;
    }

    public RflColor Color()
    {
        Need(4);
        var c = new RflColor(_data[Position], _data[Position + 1], _data[Position + 2], _data[Position + 3]);
        Position += 4;
        return c;
    }

    public void Skip(long count)
    {
        Need(count);
        Position += (int)count;
    }

    /// <summary>A length-prefixed string, decoded as Latin-1 so every byte survives.</summary>
    public string Str()
    {
        int n = U16();
        Need(n);
        string s = n == 0 ? "" : Encoding.Latin1.GetString(_data.Slice(Position, n));
        Position += n;
        return s;
    }

    public void SkipStr() => Skip(U16());

    /// <summary>
    /// A record count, proven to fit: each record takes at least <paramref name="minimumBytes"/>
    /// bytes, so a count the body cannot hold is rejected before anything loops on it.
    /// </summary>
    public int Count(int minimumBytes)
    {
        int n = I32();
        if (n < 0 || (long)n * minimumBytes > Left)
            throw new RflBodyException($"count {n} does not fit in the {Left} bytes left");
        return n;
    }

    /// <summary>A u32 + n x <paramref name="elementBytes"/> list, skipped.</summary>
    public int SkipU32List(int elementBytes)
    {
        uint n = U32();
        Skip((long)n * elementBytes);
        return (int)n;
    }
}
