using System.Buffers.Binary;
using System.Numerics;

namespace Cairn.Formats.Gltf;

/// <summary>
/// Builds one binary buffer while a document is assembled. Each Add* call appends a 4-byte-aligned
/// buffer view (and, except <see cref="AddBufferView"/>, an accessor) to the document and returns
/// its index, so exporters never hand-compute offsets. The views' <see cref="GltfBufferView.Buffer"/>
/// stays -1 until <see cref="Finish"/> appends the buffer and patches them; a writer given a document
/// whose builder was never finished therefore fails loudly instead of pointing at the wrong buffer.
/// </summary>
public sealed class GltfBufferBuilder
{
    private const int ArrayBuffer = 34962;
    private const int ElementArrayBuffer = 34963;

    private readonly GltfDocument _doc;
    private readonly List<int> _views = new();
    private byte[] _bytes = new byte[1024];
    private int _length;
    private bool _finished;

    /// <summary>Starts a buffer for <paramref name="doc"/>.</summary>
    public GltfBufferBuilder(GltfDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        _doc = doc;
    }

    /// <summary>The number of bytes written so far.</summary>
    public int Length => _length;

    /// <summary>
    /// A FLOAT accessor of any element type from a flat array of components (matrices column-major).
    /// <paramref name="minMax"/> records per-component bounds (required by the spec for POSITION and
    /// animation input).
    /// </summary>
    public int AddFloats(ReadOnlySpan<float> values, string accessorType, int? target = null, bool minMax = false)
    {
        int comps = Components(accessorType);
        if (values.Length % comps != 0)
            throw new ArgumentException($"{values.Length} floats do not divide into {accessorType} elements.", nameof(values));
        foreach (float v in values)
        {
            if (!float.IsFinite(v)) throw new ArgumentException("Accessor values must be finite.", nameof(values));
        }
        BeginView();
        int start = _length;
        var span = Grow(values.Length * 4);
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(span[(i * 4)..], values[i]);
        int view = EndView(start, null, target);
        var acc = new GltfAccessor
        {
            BufferView = view,
            ComponentType = GltfComponentType.Float,
            Count = values.Length / comps,
            Type = accessorType,
        };
        if (minMax) SetMinMax(acc, values, comps);
        _doc.Accessors.Add(acc);
        return _doc.Accessors.Count - 1;
    }

    /// <summary>A VEC2 float accessor (for example TEXCOORD_0).</summary>
    public int AddVector2(ReadOnlySpan<Vector2> values, int? target = ArrayBuffer) =>
        AddFloats(System.Runtime.InteropServices.MemoryMarshal.Cast<Vector2, float>(values), GltfAccessorType.Vec2, target);

    /// <summary>A VEC3 float accessor; set <paramref name="minMax"/> for POSITION.</summary>
    public int AddVector3(ReadOnlySpan<Vector3> values, int? target = ArrayBuffer, bool minMax = false) =>
        AddFloats(System.Runtime.InteropServices.MemoryMarshal.Cast<Vector3, float>(values), GltfAccessorType.Vec3, target, minMax);

    /// <summary>A VEC4 float accessor (for example WEIGHTS_0 or TANGENT).</summary>
    public int AddVector4(ReadOnlySpan<Vector4> values, int? target = ArrayBuffer) =>
        AddFloats(System.Runtime.InteropServices.MemoryMarshal.Cast<Vector4, float>(values), GltfAccessorType.Vec4, target);

    /// <summary>A VEC4 float accessor of (x, y, z, w) quaternions, for rotation animation output.</summary>
    public int AddQuaternions(ReadOnlySpan<Quaternion> values) =>
        AddFloats(System.Runtime.InteropServices.MemoryMarshal.Cast<Quaternion, float>(values), GltfAccessorType.Vec4);

    /// <summary>
    /// A MAT4 float accessor (for example inverse bind matrices). System.Numerics row-vector matrices
    /// stored row by row are exactly glTF's column-major column-vector matrices.
    /// </summary>
    public int AddMatrices(ReadOnlySpan<Matrix4x4> values) =>
        AddFloats(System.Runtime.InteropServices.MemoryMarshal.Cast<Matrix4x4, float>(values), GltfAccessorType.Mat4);

    /// <summary>A SCALAR float accessor, with min/max by default because animation input requires them.</summary>
    public int AddScalars(ReadOnlySpan<float> values, bool minMax = true) =>
        AddFloats(values, GltfAccessorType.Scalar, null, minMax);

    /// <summary>
    /// A SCALAR index accessor: UNSIGNED_SHORT when every index is below 65535 (65535 is the
    /// primitive-restart value), otherwise UNSIGNED_INT; target ELEMENT_ARRAY_BUFFER, with min/max.
    /// </summary>
    public int AddIndices(ReadOnlySpan<int> indices)
    {
        int max = -1, min = int.MaxValue;
        foreach (int i in indices)
        {
            if (i < 0) throw new ArgumentException($"Index {i} is negative.", nameof(indices));
            max = Math.Max(max, i);
            min = Math.Min(min, i);
        }
        int ct = max < 65535 ? GltfComponentType.UnsignedShort : GltfComponentType.UnsignedInt;
        int acc = AddIntegers(indices, ct, GltfAccessorType.Scalar, false, ElementArrayBuffer);
        if (indices.Length > 0)
        {
            _doc.Accessors[acc].Min = [min];
            _doc.Accessors[acc].Max = [max];
        }
        return acc;
    }

    /// <summary>An UNSIGNED_SHORT VEC4 vertex accessor (for example JOINTS_0); the span holds 4 values per element.</summary>
    public int AddUnsignedShortVec4(ReadOnlySpan<ushort> values)
    {
        if (values.Length % 4 != 0)
            throw new ArgumentException($"{values.Length} values do not divide into VEC4 elements.", nameof(values));
        var ints = new int[values.Length];
        for (int i = 0; i < values.Length; i++) ints[i] = values[i];
        return AddIntegers(ints, GltfComponentType.UnsignedShort, GltfAccessorType.Vec4, false, ArrayBuffer);
    }

    /// <summary>
    /// An accessor of any integer component type (BYTE .. UNSIGNED_INT), every value range-checked.
    /// Vertex data (target ARRAY_BUFFER) whose elements are not a multiple of 4 bytes gets a padded
    /// byteStride, and 1-/2-byte matrices get the spec's column padding, so the result is always valid.
    /// </summary>
    public int AddIntegers(ReadOnlySpan<int> values, int componentType, string accessorType, bool normalized = false, int? target = null)
    {
        if (componentType == GltfComponentType.Float || !GltfComponentType.IsValid(componentType))
            throw new ArgumentException($"{componentType} is not an integer glTF component type.", nameof(componentType));
        var layout = AccessorLayout.For(accessorType, componentType);
        int comps = layout.Components;
        if (values.Length % comps != 0)
            throw new ArgumentException($"{values.Length} values do not divide into {accessorType} elements.", nameof(values));
        (long lo, long hi) = componentType switch
        {
            GltfComponentType.Byte => (sbyte.MinValue, sbyte.MaxValue),
            GltfComponentType.UnsignedByte => (byte.MinValue, byte.MaxValue),
            GltfComponentType.Short => (short.MinValue, short.MaxValue),
            GltfComponentType.UnsignedShort => (ushort.MinValue, ushort.MaxValue),
            _ => (0L, (long)uint.MaxValue),
        };
        foreach (int v in values)
        {
            if (v < lo || v > hi)
                throw new ArgumentException($"{v} does not fit component type {GltfComponentType.Describe(componentType)}.", nameof(values));
        }
        int count = values.Length / comps;
        int stride = layout.ElementSize;
        int? byteStride = null;
        if (target == ArrayBuffer && stride % 4 != 0)
        {
            stride = (stride + 3) & ~3;
            byteStride = stride;
        }
        int size = GltfComponentType.Size(componentType);
        BeginView();
        int start = _length;
        var span = Grow(count * stride);
        span.Clear();
        for (int e = 0; e < count; e++)
        {
            for (int k = 0; k < comps; k++)
            {
                var dst = span[(e * stride + layout.Offsets[k])..];
                int v = values[e * comps + k];
                switch (size)
                {
                    case 1: dst[0] = unchecked((byte)v); break;
                    case 2: BinaryPrimitives.WriteUInt16LittleEndian(dst, unchecked((ushort)v)); break;
                    default: BinaryPrimitives.WriteUInt32LittleEndian(dst, unchecked((uint)v)); break;
                }
            }
        }
        int view = EndView(start, byteStride, target);
        _doc.Accessors.Add(new GltfAccessor
        {
            BufferView = view,
            ComponentType = componentType,
            Normalized = normalized,
            Count = count,
            Type = accessorType,
        });
        return _doc.Accessors.Count - 1;
    }

    /// <summary>A raw buffer view (for example an embedded PNG or interleaved vertex data); returns the view index.</summary>
    public int AddBufferView(ReadOnlySpan<byte> bytes, int? byteStride = null, int? target = null)
    {
        BeginView();
        int start = _length;
        bytes.CopyTo(Grow(bytes.Length));
        return EndView(start, byteStride, target);
    }

    /// <summary>
    /// Appends the finished buffer (Data = the bytes, ByteLength) to the document, points every view
    /// this builder made at it, and returns its index. The builder cannot be used afterwards.
    /// </summary>
    public int Finish(string? uri = null)
    {
        ThrowIfFinished();
        _finished = true;
        int index = _doc.Buffers.Count;
        _doc.Buffers.Add(new GltfBuffer { Uri = uri, ByteLength = _length, Data = _bytes.AsSpan(0, _length).ToArray() });
        foreach (int v in _views) _doc.BufferViews[v].Buffer = index;
        return index;
    }

    private static int Components(string accessorType) =>
        GltfAccessorType.TryComponentCount(accessorType, out int n) ? n
        : throw new ArgumentException($"'{accessorType}' is not a glTF accessor type.", nameof(accessorType));

    private static void SetMinMax(GltfAccessor acc, ReadOnlySpan<float> values, int comps)
    {
        if (values.Length == 0) return;
        var min = new float[comps];
        var max = new float[comps];
        Array.Fill(min, float.PositiveInfinity);
        Array.Fill(max, float.NegativeInfinity);
        for (int i = 0; i < values.Length; i++)
        {
            int k = i % comps;
            min[k] = MathF.Min(min[k], values[i]);
            max[k] = MathF.Max(max[k], values[i]);
        }
        acc.Min = min;
        acc.Max = max;
    }

    private void BeginView()
    {
        ThrowIfFinished();
        int pad = (4 - (_length & 3)) & 3;
        Grow(pad).Clear();
    }

    private int EndView(int start, int? byteStride, int? target)
    {
        _doc.BufferViews.Add(new GltfBufferView
        {
            Buffer = -1,
            ByteOffset = start,
            ByteLength = _length - start,
            ByteStride = byteStride,
            Target = target,
        });
        _views.Add(_doc.BufferViews.Count - 1);
        return _doc.BufferViews.Count - 1;
    }

    private Span<byte> Grow(int count)
    {
        long needed = (long)_length + count;
        if (needed > Array.MaxLength) throw new InvalidOperationException("The glTF buffer would exceed 2 GB.");
        if (needed > _bytes.Length)
            Array.Resize(ref _bytes, (int)Math.Min(Array.MaxLength, Math.Max(needed, _bytes.Length * 2L)));
        var span = _bytes.AsSpan(_length, count);
        _length = (int)needed;
        return span;
    }

    private void ThrowIfFinished()
    {
        if (_finished) throw new InvalidOperationException("This glTF buffer has already been finished.");
    }
}
