using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Cairn.Formats;

/// <summary>Growable little-endian byte buffer the format writers emit into.</summary>
public sealed class BinaryBuilder
{
    private byte[] _buffer;

    public BinaryBuilder(int capacity = 4096)
    {
        _buffer = new byte[Math.Max(16, capacity)];
    }

    public int Length { get; private set; }

    private Span<byte> Grow(int count)
    {
        int needed = Length + count;
        if (needed > _buffer.Length)
        {
            int size = Math.Max(needed, _buffer.Length * 2);
            Array.Resize(ref _buffer, size);
        }
        var span = _buffer.AsSpan(Length, count);
        Length = needed;
        return span;
    }

    public void WriteByte(byte value) => Grow(1)[0] = value;

    public void WriteSByte(sbyte value) => Grow(1)[0] = unchecked((byte)value);

    public void WriteInt16(short value) => BinaryPrimitives.WriteInt16LittleEndian(Grow(2), value);

    public void WriteUInt16(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Grow(2), value);

    public void WriteInt32(int value) => BinaryPrimitives.WriteInt32LittleEndian(Grow(4), value);

    public void WriteUInt32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Grow(4), value);

    public void WriteSingle(float value) => BinaryPrimitives.WriteSingleLittleEndian(Grow(4), value);

    public void WriteVector2(Vector2 v)
    {
        WriteSingle(v.X);
        WriteSingle(v.Y);
    }

    public void WriteVector3(Vector3 v)
    {
        WriteSingle(v.X);
        WriteSingle(v.Y);
        WriteSingle(v.Z);
    }

    public void WriteQuaternion(Quaternion q)
    {
        WriteSingle(q.X);
        WriteSingle(q.Y);
        WriteSingle(q.Z);
        WriteSingle(q.W);
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes) => bytes.CopyTo(Grow(bytes.Length));

    public void WriteZeros(int count)
    {
        if (count > 0) Grow(count).Clear();
    }

    public void WriteFixedString(FixedString value) => WriteBytes(value.Bytes.AsSpan());

    /// <summary>A zero-terminated Latin-1 string.</summary>
    public void WriteCString(string value)
    {
        WriteBytes(Encoding.Latin1.GetBytes(value));
        WriteByte(0);
    }

    /// <summary>Pads with zeros until the length is a multiple of <paramref name="alignment"/>, measured from <paramref name="origin"/>.</summary>
    public void Align(int alignment, int origin = 0)
    {
        int rel = Length - origin;
        int pad = (alignment - rel % alignment) % alignment;
        WriteZeros(pad);
    }

    public void PatchInt32(int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(offset, 4), value);

    public byte[] ToArray() => _buffer.AsSpan(0, Length).ToArray();
}
