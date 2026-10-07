using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Cairn.Formats;

/// <summary>
/// Bounds-checked little-endian reader over a whole file image, shared by the format readers.
/// Every read checks the bytes are actually there and raises <see cref="AssetFormatException"/>
/// otherwise; <see cref="EnsureCount"/> is how a reader proves a declared count fits in the file
/// before it allocates anything sized by it.
/// </summary>
public sealed class BinaryCursor(byte[] data, string name)
{
    private readonly byte[] _data = data ?? throw new ArgumentNullException(nameof(data));

    /// <summary>The file name used in messages.</summary>
    public string Name { get; } = name;

    public int Position { get; set; }

    public int Length => _data.Length;

    public int Remaining => Math.Max(0, _data.Length - Position);

    public byte[] Data => _data;

    /// <summary>A user-readable failure naming the file.</summary>
    public AssetFormatException Fail(string reason) => new($"'{Name}' is damaged: {reason}");

    /// <summary>Throws unless <paramref name="count"/> bytes remain from the current position.</summary>
    public void Need(long count, string what)
    {
        if (count < 0 || Position < 0 || Position > _data.Length || count > _data.Length - Position)
            throw Fail($"{what} runs past the end of the file (offset {Position}, {count} bytes needed, "
                + $"{Remaining} left).");
    }

    /// <summary>
    /// Throws unless <paramref name="count"/> elements of <paramref name="elementSize"/> bytes fit in
    /// what is left of the file. Call this before allocating an array sized by a value read from the
    /// file, so a header claiming two billion keys costs one comparison.
    /// </summary>
    public void EnsureCount(long count, int elementSize, string what)
    {
        if (count < 0) throw Fail($"{what} has a negative count ({count}).");
        Need(count * elementSize, what);
    }

    public byte ReadByte(string what = "data")
    {
        Need(1, what);
        return _data[Position++];
    }

    public sbyte ReadSByte(string what = "data") => unchecked((sbyte)ReadByte(what));

    public short ReadInt16(string what = "data")
    {
        Need(2, what);
        short v = BinaryPrimitives.ReadInt16LittleEndian(_data.AsSpan(Position));
        Position += 2;
        return v;
    }

    public ushort ReadUInt16(string what = "data")
    {
        Need(2, what);
        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Position));
        Position += 2;
        return v;
    }

    public int ReadInt32(string what = "data")
    {
        Need(4, what);
        int v = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(Position));
        Position += 4;
        return v;
    }

    public uint ReadUInt32(string what = "data")
    {
        Need(4, what);
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(Position));
        Position += 4;
        return v;
    }

    public float ReadSingle(string what = "data")
    {
        Need(4, what);
        float v = BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan(Position));
        Position += 4;
        return v;
    }

    public Vector2 ReadVector2(string what = "data")
    {
        Need(8, what);
        float x = ReadSingle(), y = ReadSingle();
        return new Vector2(x, y);
    }

    public Vector3 ReadVector3(string what = "data")
    {
        Need(12, what);
        float x = ReadSingle(), y = ReadSingle(), z = ReadSingle();
        return new Vector3(x, y, z);
    }

    public Quaternion ReadQuaternion(string what = "data")
    {
        Need(16, what);
        float x = ReadSingle(), y = ReadSingle(), z = ReadSingle(), w = ReadSingle();
        return new Quaternion(x, y, z, w);
    }

    public ReadOnlySpan<byte> ReadBytes(int count, string what = "data")
    {
        Need(count, what);
        var span = _data.AsSpan(Position, count);
        Position += count;
        return span;
    }

    /// <summary>A fixed-size, NUL-padded name field, kept byte for byte.</summary>
    public FixedString ReadFixedString(int length, string what = "name") =>
        FixedString.FromBytes(ReadBytes(length, what));

    /// <summary>A zero-terminated string of unknown length, decoded as Latin-1 so every byte survives.</summary>
    public string ReadCString(string what = "string")
    {
        int start = Position;
        int nul = Array.IndexOf(_data, (byte)0, start);
        if (nul < 0) throw Fail($"{what} at offset {start} is not terminated.");
        Position = nul + 1;
        return Encoding.Latin1.GetString(_data, start, nul - start);
    }

    /// <summary>Moves to an absolute offset, which must lie inside the file.</summary>
    public void Seek(long offset, string what)
    {
        if (offset < 0 || offset > _data.Length)
            throw Fail($"{what} points outside the file (offset {offset}, file is {_data.Length} bytes).");
        Position = (int)offset;
    }

    public void Skip(int count, string what = "data")
    {
        Need(count, what);
        Position += count;
    }
}
