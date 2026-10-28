using System.Buffers.Binary;
using Cairn.Formats.Rfl;
using Cairn.Lvl.Model;

namespace Cairn.Lvl.Writing;

/// <summary>
/// Little-endian writer for level and group records: the exact counterpart of the readers' cursor. Strings are
/// u16-length Latin-1 (every byte a Latin-1 string holds comes back unchanged); text a group file cannot hold is
/// counted so the writer can say what it changed.
/// </summary>
internal sealed class LvlWriter
{
    private byte[] _buffer;
    private int _length;

    public LvlWriter(int capacity = 1 << 16) => _buffer = new byte[Math.Max(256, capacity)];

    public int Length => _length;

    /// <summary>Strings with characters outside Latin-1 (written as '?').</summary>
    public int LossyStrings { get; private set; }

    /// <summary>Strings longer than a u16 length holds (cut to 65,535 characters).</summary>
    public int LongStrings { get; private set; }

    private Span<byte> Take(int count)
    {
        if (_length + (long)count > Array.MaxLength)
            throw new InvalidOperationException("The group file would be larger than 2 GB, which the format cannot hold.");
        if (_length + count > _buffer.Length)
        {
            long grown = Math.Max((long)_buffer.Length * 2, (long)_length + count);
            Array.Resize(ref _buffer, (int)Math.Min(grown, Array.MaxLength));
        }
        var span = _buffer.AsSpan(_length, count);
        _length += count;
        return span;
    }

    public LvlWriter U8(byte value)
    {
        Take(1)[0] = value;
        return this;
    }

    public LvlWriter I8(sbyte value) => U8(unchecked((byte)value));

    public LvlWriter Bool(bool value) => U8(value ? (byte)1 : (byte)0);

    public LvlWriter U16(ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(Take(2), value);
        return this;
    }

    public LvlWriter I32(int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(Take(4), value);
        return this;
    }

    public LvlWriter U32(uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(Take(4), value);
        return this;
    }

    /// <summary>A float, bit for bit (NaN payloads included).</summary>
    public LvlWriter F32(float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(Take(4), value);
        return this;
    }

    public LvlWriter Vec2(Vector2 v) => F32(v.X).F32(v.Y);

    public LvlWriter Vec3(Vector3 v) => F32(v.X).F32(v.Y).F32(v.Z);

    public LvlWriter Color(RflColor c)
    {
        var s = Take(4);
        s[0] = c.R;
        s[1] = c.G;
        s[2] = c.B;
        s[3] = c.A;
        return this;
    }

    /// <summary>A matrix of a stock record: rows forward, right, up.</summary>
    public LvlWriter StockMatrix(RfMatrix m) => Vec3(m.Forward).Vec3(m.Right).Vec3(m.Up);

    /// <summary>A matrix of an Alpine chunk: rows right, up, forward.</summary>
    public LvlWriter AlpineMatrix(RfMatrix m) => Vec3(m.Right).Vec3(m.Up).Vec3(m.Forward);

    /// <summary>A u16-length Latin-1 string.</summary>
    public LvlWriter Str(string? text)
    {
        text ??= "";
        if (text.Length > ushort.MaxValue)
        {
            LongStrings++;
            text = text[..ushort.MaxValue];
        }
        U16((ushort)text.Length);
        var s = Take(text.Length);
        bool lossy = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c > 0xFF)
            {
                lossy = true;
                c = '?';
            }
            s[i] = (byte)c;
        }
        if (lossy) LossyStrings++;
        return this;
    }

    /// <summary>An i32 count followed by that many i32 values.</summary>
    public LvlWriter Uids(ImmutableArray<int> values)
    {
        var list = values.IsDefault ? [] : values;
        I32(list.Length);
        foreach (int v in list) I32(v);
        return this;
    }

    public LvlWriter Bytes(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(Take(bytes.Length));
        return this;
    }

    /// <summary>Overwrites an i32 written earlier (chunk lengths).</summary>
    public void PatchI32(int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(at, 4), value);

    public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();
}
