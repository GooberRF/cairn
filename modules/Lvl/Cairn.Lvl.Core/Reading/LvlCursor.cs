using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Cairn.Formats.Rfl;
using Cairn.Lvl.Model;

namespace Cairn.Lvl.Reading;

/// <summary>Raised inside a section or record that does not fit; the readers catch it and report the section.</summary>
internal sealed class LvlBodyException(string message) : Exception(message);

/// <summary>
/// Bounds-checked little-endian reader over one window of a file (a section body, or the whole file for group
/// files). It never reads outside its window, so a damaged section cannot move the walk through the file, and
/// every count is checked against the bytes left before anything is allocated for it.
/// </summary>
internal sealed class LvlCursor
{
    private readonly byte[] _data;

    public LvlCursor(byte[] data, int start, int end)
    {
        _data = data;
        Start = start;
        Position = start;
        End = end;
    }

    public byte[] Data => _data;

    /// <summary>First byte of the window (an absolute file offset).</summary>
    public int Start { get; }

    /// <summary>Absolute file offset of the next byte.</summary>
    public int Position { get; set; }

    /// <summary>One past the last byte of the window.</summary>
    public int End { get; }

    public int Left => End - Position;

    public bool AtEnd => Position >= End;

    public void Need(long count)
    {
        if (count < 0 || count > Left)
            throw new LvlBodyException($"{count} bytes needed at offset {Position}, {Math.Max(0, Left)} left");
    }

    /// <summary>Throws unless the window has been read to its last byte.</summary>
    public void ExpectEnd(string what)
    {
        if (Left != 0) throw new LvlBodyException($"{Left} bytes left after the {what}");
    }

    public byte U8()
    {
        Need(1);
        return _data[Position++];
    }

    public sbyte I8() => unchecked((sbyte)U8());

    public bool Bool() => U8() != 0;

    public ushort U16()
    {
        Need(2);
        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Position));
        Position += 2;
        return v;
    }

    public int I32()
    {
        Need(4);
        int v = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(Position));
        Position += 4;
        return v;
    }

    public uint U32()
    {
        Need(4);
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(Position));
        Position += 4;
        return v;
    }

    public float F32()
    {
        Need(4);
        float v = BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan(Position));
        Position += 4;
        return v;
    }

    public Vector2 Vec2()
    {
        Need(8);
        return new Vector2(F32(), F32());
    }

    public Vector3 Vec3()
    {
        Need(12);
        return new Vector3(F32(), F32(), F32());
    }

    public RflColor Color()
    {
        Need(4);
        var c = new RflColor(_data[Position], _data[Position + 1], _data[Position + 2], _data[Position + 3]);
        Position += 4;
        return c;
    }

    /// <summary>A matrix of a stock section: rows stored forward, right, up.</summary>
    public RfMatrix StockMatrix()
    {
        Need(36);
        var f = Vec3();
        var r = Vec3();
        var u = Vec3();
        return new RfMatrix(r, u, f);
    }

    /// <summary>A matrix of an Alpine chunk: rows stored right, up, forward.</summary>
    public RfMatrix AlpineMatrix()
    {
        Need(36);
        var r = Vec3();
        var u = Vec3();
        var f = Vec3();
        return new RfMatrix(r, u, f);
    }

    /// <summary>A u16-length string, decoded as Latin-1 so every byte survives.</summary>
    public string Str()
    {
        int n = U16();
        Need(n);
        string s = n == 0 ? "" : Encoding.Latin1.GetString(_data, Position, n);
        Position += n;
        return s;
    }

    /// <summary>
    /// A record count, proven to fit: each record takes at least <paramref name="minimumBytes"/> bytes, so a count
    /// the window cannot hold is rejected before anything loops on it or allocates for it.
    /// </summary>
    public int Count(int minimumBytes)
    {
        int at = Position;
        int n = I32();
        if (n < 0 || (long)n * minimumBytes > Left)
            throw new LvlBodyException($"count {n} at offset {at} does not fit in the {Left} bytes left");
        return n;
    }

    /// <summary>As <see cref="Count"/> for an unsigned (Alpine) count.</summary>
    public int CountU32(int minimumBytes)
    {
        int at = Position;
        uint n = U32();
        if ((long)n * minimumBytes > Left || n > int.MaxValue)
            throw new LvlBodyException($"count {n} at offset {at} does not fit in the {Left} bytes left");
        return (int)n;
    }

    /// <summary>An i32 count followed by that many i32 values.</summary>
    public ImmutableArray<int> Uids()
    {
        int n = Count(4);
        if (n == 0) return [];
        var a = new int[n];
        for (int i = 0; i < n; i++) a[i] = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(Position + 4 * i));
        Position += 4 * n;
        return ImmutableCollectionsMarshal.AsImmutableArray(a);
    }

    public ImmutableArray<byte> Bytes(int count)
    {
        Need(count);
        var a = _data.AsSpan(Position, count).ToArray();
        Position += count;
        return ImmutableCollectionsMarshal.AsImmutableArray(a);
    }

    /// <summary>A window of the file without copying it.</summary>
    public ReadOnlyMemory<byte> Memory(int count)
    {
        Need(count);
        var m = new ReadOnlyMemory<byte>(_data, Position, count);
        Position += count;
        return m;
    }

    public void Skip(long count)
    {
        Need(count);
        Position += (int)count;
    }
}
