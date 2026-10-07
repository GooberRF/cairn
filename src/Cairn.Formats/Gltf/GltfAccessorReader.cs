using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;

namespace Cairn.Formats.Gltf;

/// <summary>
/// Typed reads of accessor data with sparse substitution, byteStride and normalisation applied.
/// The reader only checks JSON shapes, so this is where every index, offset, stride and count the
/// read depends on is validated against the bytes actually present, before anything is allocated.
/// Bad data raises <see cref="AssetFormatException"/> naming the file and the accessor.
/// </summary>
public static class GltfAccessorReader
{
    /// <summary>
    /// The largest component count an accessor without a buffer view (all zeros) may expand to. Such
    /// an accessor has no bytes to bound its count, so a damaged count could otherwise demand gigabytes.
    /// </summary>
    internal const int MaxZeroFilledComponents = 1 << 26;

    /// <summary>
    /// Every component of every element (count * components), as floats. Normalised integers map to
    /// [0, 1] (unsigned) or [-1, 1] (signed, clamped) per the spec; other integers convert as is.
    /// Matrices stay column-major, as stored.
    /// </summary>
    public static float[] ReadFloats(GltfDocument doc, int accessor, string fileName = "glTF")
    {
        var plan = Prepare(doc, accessor, fileName);
        var result = new float[plan.Count * plan.Layout.Components];
        int comps = plan.Layout.Components;
        int ct = plan.Accessor.ComponentType;
        bool norm = plan.Accessor.Normalized;
        if (plan.Data is not null)
        {
            var span = plan.Data.AsSpan();
            for (int e = 0; e < plan.Count; e++)
            {
                int at = plan.Start + e * plan.Stride;
                for (int k = 0; k < comps; k++)
                    result[e * comps + k] = ReadFloat(span[(at + plan.Layout.Offsets[k])..], ct, norm);
            }
        }
        if (plan.Sparse is { } sp)
        {
            var span = sp.ValueData.AsSpan();
            for (int s = 0; s < sp.Indices.Length; s++)
            {
                int at = sp.ValueStart + s * plan.Layout.ElementSize;
                int e = sp.Indices[s];
                for (int k = 0; k < comps; k++)
                    result[e * comps + k] = ReadFloat(span[(at + plan.Layout.Offsets[k])..], ct, norm);
            }
        }
        return result;
    }

    /// <summary>
    /// Every component as an int, for indices and JOINTS_n. Float accessors are rejected because
    /// truncating them would hide a broken file; UNSIGNED_INT values above int.MaxValue are rejected too.
    /// Normalisation is ignored (the raw integers are returned).
    /// </summary>
    public static int[] ReadInts(GltfDocument doc, int accessor, string fileName = "glTF")
    {
        var plan = Prepare(doc, accessor, fileName);
        int ct = plan.Accessor.ComponentType;
        if (ct == GltfComponentType.Float)
            throw Damaged(fileName, $"accessor {accessor} holds floats, but integers (indices or joints) were expected.");
        int comps = plan.Layout.Components;
        var result = new int[plan.Count * comps];
        if (plan.Data is not null)
        {
            var span = plan.Data.AsSpan();
            for (int e = 0; e < plan.Count; e++)
            {
                int at = plan.Start + e * plan.Stride;
                for (int k = 0; k < comps; k++)
                    result[e * comps + k] = ToInt(ReadInteger(span[(at + plan.Layout.Offsets[k])..], ct), fileName, accessor);
            }
        }
        if (plan.Sparse is { } sp)
        {
            var span = sp.ValueData.AsSpan();
            for (int s = 0; s < sp.Indices.Length; s++)
            {
                int at = sp.ValueStart + s * plan.Layout.ElementSize;
                int e = sp.Indices[s];
                for (int k = 0; k < comps; k++)
                    result[e * comps + k] = ToInt(ReadInteger(span[(at + plan.Layout.Offsets[k])..], ct), fileName, accessor);
            }
        }
        return result;
    }

    /// <summary>A VEC2 accessor's elements.</summary>
    public static Vector2[] ReadVector2(GltfDocument doc, int accessor, string fileName = "glTF")
    {
        Expect(doc, accessor, fileName, GltfAccessorType.Vec2);
        float[] f = ReadFloats(doc, accessor, fileName);
        var result = new Vector2[f.Length / 2];
        for (int i = 0; i < result.Length; i++) result[i] = new Vector2(f[i * 2], f[i * 2 + 1]);
        return result;
    }

    /// <summary>A VEC3 accessor's elements.</summary>
    public static Vector3[] ReadVector3(GltfDocument doc, int accessor, string fileName = "glTF")
    {
        Expect(doc, accessor, fileName, GltfAccessorType.Vec3);
        float[] f = ReadFloats(doc, accessor, fileName);
        var result = new Vector3[f.Length / 3];
        for (int i = 0; i < result.Length; i++) result[i] = new Vector3(f[i * 3], f[i * 3 + 1], f[i * 3 + 2]);
        return result;
    }

    /// <summary>A VEC4 accessor's elements.</summary>
    public static Vector4[] ReadVector4(GltfDocument doc, int accessor, string fileName = "glTF")
    {
        Expect(doc, accessor, fileName, GltfAccessorType.Vec4);
        float[] f = ReadFloats(doc, accessor, fileName);
        var result = new Vector4[f.Length / 4];
        for (int i = 0; i < result.Length; i++) result[i] = new Vector4(f[i * 4], f[i * 4 + 1], f[i * 4 + 2], f[i * 4 + 3]);
        return result;
    }

    /// <summary>A VEC4 accessor's elements as (x, y, z, w) quaternions, unnormalised as stored.</summary>
    public static Quaternion[] ReadQuaternions(GltfDocument doc, int accessor, string fileName = "glTF")
    {
        Expect(doc, accessor, fileName, GltfAccessorType.Vec4);
        float[] f = ReadFloats(doc, accessor, fileName);
        var result = new Quaternion[f.Length / 4];
        for (int i = 0; i < result.Length; i++) result[i] = new Quaternion(f[i * 4], f[i * 4 + 1], f[i * 4 + 2], f[i * 4 + 3]);
        return result;
    }

    /// <summary>
    /// A MAT4 accessor's elements as System.Numerics row-vector matrices (translation in M41..M43).
    /// The column-major file order is exactly the row-major order of the transposed matrix.
    /// </summary>
    public static Matrix4x4[] ReadMatrices(GltfDocument doc, int accessor, string fileName = "glTF")
    {
        Expect(doc, accessor, fileName, GltfAccessorType.Mat4);
        float[] f = ReadFloats(doc, accessor, fileName);
        var result = new Matrix4x4[f.Length / 16];
        for (int i = 0; i < result.Length; i++)
        {
            int o = i * 16;
            result[i] = new Matrix4x4(f[o], f[o + 1], f[o + 2], f[o + 3], f[o + 4], f[o + 5], f[o + 6], f[o + 7],
                f[o + 8], f[o + 9], f[o + 10], f[o + 11], f[o + 12], f[o + 13], f[o + 14], f[o + 15]);
        }
        return result;
    }

    /// <summary>
    /// Checks that the accessor exists and its element type is one of <paramref name="allowedTypes"/>,
    /// so callers can reject, say, a VEC2 POSITION with a message instead of misreading it.
    /// </summary>
    /// <exception cref="AssetFormatException">The accessor is missing or has another type.</exception>
    public static void Expect(GltfDocument doc, int accessor, string fileName, params string[] allowedTypes)
    {
        ArgumentNullException.ThrowIfNull(allowedTypes);
        var acc = GetAccessor(doc, accessor, fileName);
        if (Array.IndexOf(allowedTypes, acc.Type) < 0)
            throw Damaged(fileName, $"accessor {accessor} is {acc.Type}, but {string.Join(" or ", allowedTypes)} was expected.");
    }

    // ---- planning and validation ----

    private sealed record SparsePlan(int[] Indices, byte[] ValueData, int ValueStart);

    private sealed record Plan(GltfAccessor Accessor, AccessorLayout Layout, int Count, byte[]? Data, int Start, int Stride, SparsePlan? Sparse);

    private static GltfAccessor GetAccessor(GltfDocument doc, int accessor, string fileName)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (accessor < 0 || accessor >= doc.Accessors.Count)
            throw Damaged(fileName, $"accessor {accessor} does not exist (the file has {doc.Accessors.Count}).");
        return doc.Accessors[accessor];
    }

    private static Plan Prepare(GltfDocument doc, int index, string fileName)
    {
        var acc = GetAccessor(doc, index, fileName);
        if (!GltfAccessorType.TryComponentCount(acc.Type, out _))
            throw Damaged(fileName, $"accessor {index} has the unknown type '{acc.Type}'.");
        if (!GltfComponentType.IsValid(acc.ComponentType))
            throw Damaged(fileName, $"accessor {index} has the unknown component type {acc.ComponentType}.");
        if (acc.Count < 0)
            throw Damaged(fileName, $"accessor {index} has a negative count ({acc.Count}).");
        if (acc.ByteOffset < 0)
            throw Damaged(fileName, $"accessor {index} has a negative byteOffset ({acc.ByteOffset}).");
        var layout = AccessorLayout.For(acc.Type, acc.ComponentType);

        byte[]? data = null;
        int start = 0, stride = layout.ElementSize;
        if (acc.BufferView is int viewIndex)
        {
            var (bytes, viewStart, viewLength, view) = ResolveView(doc, viewIndex, fileName, $"accessor {index}");
            if (view.ByteStride is int s)
            {
                if (s < 4 || s > 252 || s % 4 != 0)
                    throw Damaged(fileName, $"buffer view {viewIndex} has byteStride {s}; glTF allows multiples of 4 from 4 to 252.");
                if (s < layout.ElementSize)
                    throw Damaged(fileName, $"buffer view {viewIndex} has byteStride {s}, smaller than the {layout.ElementSize}-byte elements of accessor {index}.");
                stride = s;
            }
            long needed = acc.Count == 0 ? acc.ByteOffset : acc.ByteOffset + (long)stride * (acc.Count - 1) + layout.ElementSize;
            if (needed > viewLength)
                throw Damaged(fileName, $"accessor {index} reads past the end of buffer view {viewIndex} (it needs {needed} bytes, the view holds {viewLength}).");
            data = bytes;
            start = viewStart + acc.ByteOffset;
        }
        else if ((long)acc.Count * layout.Components > MaxZeroFilledComponents)
        {
            throw Damaged(fileName, $"accessor {index} has no buffer view but claims {acc.Count} elements, which is implausibly many.");
        }

        SparsePlan? sparse = acc.Sparse is null ? null : PrepareSparse(doc, index, acc, layout, fileName);
        return new Plan(acc, layout, acc.Count, data, start, stride, sparse);
    }

    private static SparsePlan PrepareSparse(GltfDocument doc, int index, GltfAccessor acc, AccessorLayout layout, string fileName)
    {
        var sp = acc.Sparse!;
        string who = $"the sparse part of accessor {index}";
        if (sp.Count < 0 || sp.Count > acc.Count)
            throw Damaged(fileName, $"{who} substitutes {sp.Count} elements, but the accessor has {acc.Count}.");
        int ict = sp.Indices.ComponentType;
        if (ict is not (GltfComponentType.UnsignedByte or GltfComponentType.UnsignedShort or GltfComponentType.UnsignedInt))
            throw Damaged(fileName, $"{who} has index component type {GltfComponentType.Describe(ict)}; only unsigned integer types are allowed.");
        int isize = GltfComponentType.Size(ict);

        var (ibytes, istart, ilen, _) = ResolveView(doc, sp.Indices.BufferView, fileName, who);
        if (sp.Indices.ByteOffset < 0 || sp.Indices.ByteOffset + (long)sp.Count * isize > ilen)
            throw Damaged(fileName, $"the indices of {who} read past the end of buffer view {sp.Indices.BufferView}.");
        var (vbytes, vstart, vlen, _) = ResolveView(doc, sp.Values.BufferView, fileName, who);
        if (sp.Values.ByteOffset < 0 || sp.Values.ByteOffset + (long)sp.Count * layout.ElementSize > vlen)
            throw Damaged(fileName, $"the values of {who} read past the end of buffer view {sp.Values.BufferView}.");

        var indices = new int[sp.Count];
        var span = ibytes.AsSpan(istart + sp.Indices.ByteOffset, sp.Count * isize);
        long previous = -1;
        for (int i = 0; i < sp.Count; i++)
        {
            long value = ReadInteger(span[(i * isize)..], ict);
            if (value <= previous)
                throw Damaged(fileName, $"the indices of {who} are not strictly increasing (index {i} is {value} after {previous}).");
            if (value >= acc.Count)
                throw Damaged(fileName, $"{who} substitutes element {value}, but the accessor has only {acc.Count}.");
            indices[i] = (int)value;
            previous = value;
        }
        return new SparsePlan(indices, vbytes, vstart + sp.Values.ByteOffset);
    }

    /// <summary>
    /// Validates a buffer view and its buffer, returning the backing array and the view's absolute
    /// range in it. Shared with the reader, which fills image bytes from buffer views.
    /// </summary>
    internal static (byte[] Data, int Start, int Length, GltfBufferView View) ResolveView(GltfDocument doc, int viewIndex, string fileName, string who)
    {
        if (viewIndex < 0 || viewIndex >= doc.BufferViews.Count)
            throw Damaged(fileName, $"{who} refers to buffer view {viewIndex}, which does not exist (the file has {doc.BufferViews.Count}).");
        var view = doc.BufferViews[viewIndex];
        if (view.Buffer < 0 || view.Buffer >= doc.Buffers.Count)
            throw Damaged(fileName, $"buffer view {viewIndex} refers to buffer {view.Buffer}, which does not exist (the file has {doc.Buffers.Count}).");
        var buffer = doc.Buffers[view.Buffer];
        if (buffer.Data is null)
            throw Damaged(fileName, $"buffer {view.Buffer} has no data (its file or binary chunk was not loaded).");
        if (buffer.ByteLength < 0 || buffer.Data.Length < buffer.ByteLength)
            throw Damaged(fileName, $"buffer {view.Buffer} holds {buffer.Data.Length} bytes but declares {buffer.ByteLength}.");
        if (view.ByteOffset < 0 || view.ByteLength < 0 || (long)view.ByteOffset + view.ByteLength > buffer.Data.Length)
            throw Damaged(fileName, $"buffer view {viewIndex} (offset {view.ByteOffset}, length {view.ByteLength}) reaches past the end of buffer {view.Buffer} ({buffer.Data.Length} bytes).");
        return (buffer.Data, view.ByteOffset, view.ByteLength, view);
    }

    // ---- component decoding ----

    private static float ReadFloat(ReadOnlySpan<byte> s, int ct, bool normalized) => ct switch
    {
        GltfComponentType.Float => BinaryPrimitives.ReadSingleLittleEndian(s),
        GltfComponentType.Byte => normalized ? MathF.Max((sbyte)s[0] / 127f, -1f) : (sbyte)s[0],
        GltfComponentType.UnsignedByte => normalized ? s[0] / 255f : s[0],
        GltfComponentType.Short => normalized
            ? MathF.Max(BinaryPrimitives.ReadInt16LittleEndian(s) / 32767f, -1f)
            : BinaryPrimitives.ReadInt16LittleEndian(s),
        GltfComponentType.UnsignedShort => normalized
            ? BinaryPrimitives.ReadUInt16LittleEndian(s) / 65535f
            : BinaryPrimitives.ReadUInt16LittleEndian(s),
        // Normalised UNSIGNED_INT is outside the spec; map it the obvious way rather than reject.
        _ => normalized
            ? (float)(BinaryPrimitives.ReadUInt32LittleEndian(s) / 4294967295.0)
            : BinaryPrimitives.ReadUInt32LittleEndian(s),
    };

    private static long ReadInteger(ReadOnlySpan<byte> s, int ct) => ct switch
    {
        GltfComponentType.Byte => (sbyte)s[0],
        GltfComponentType.UnsignedByte => s[0],
        GltfComponentType.Short => BinaryPrimitives.ReadInt16LittleEndian(s),
        GltfComponentType.UnsignedShort => BinaryPrimitives.ReadUInt16LittleEndian(s),
        _ => BinaryPrimitives.ReadUInt32LittleEndian(s),
    };

    private static int ToInt(long value, string fileName, int accessor) =>
        value <= int.MaxValue ? (int)value
        : throw Damaged(fileName, $"accessor {accessor} holds the value {value.ToString(CultureInfo.InvariantCulture)}, too large for an index.");

    internal static AssetFormatException Damaged(string fileName, string what) => new($"'{fileName}' is damaged: {what}");
}

/// <summary>
/// Where each component of one accessor element sits. Matrices with 1- or 2-byte components pad
/// every column to a 4-byte boundary (glTF 2.0 section 3.6.2.4), so MAT2 bytes, MAT3 bytes and
/// MAT3 shorts are not tightly packed; everything else is.
/// </summary>
internal sealed class AccessorLayout
{
    private AccessorLayout(int components, int elementSize, int[] offsets)
    {
        Components = components;
        ElementSize = elementSize;
        Offsets = offsets;
    }

    public int Components { get; }

    public int ElementSize { get; }

    public int[] Offsets { get; }

    public static AccessorLayout For(string type, int componentType)
    {
        int comps = GltfAccessorType.ComponentCount(type);
        int size = GltfComponentType.Size(componentType);
        int rows = type switch { GltfAccessorType.Mat2 => 2, GltfAccessorType.Mat3 => 3, GltfAccessorType.Mat4 => 4, _ => 0 };
        var offsets = new int[comps];
        if (rows > 0 && size < 4)
        {
            int columnStride = (rows * size + 3) & ~3;
            for (int c = 0; c < rows; c++)
                for (int r = 0; r < rows; r++)
                    offsets[c * rows + r] = c * columnStride + r * size;
            return new AccessorLayout(comps, columnStride * rows, offsets);
        }
        for (int k = 0; k < comps; k++) offsets[k] = k * size;
        return new AccessorLayout(comps, comps * size, offsets);
    }
}
