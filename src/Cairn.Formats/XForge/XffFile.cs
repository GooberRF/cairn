using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Cairn.Formats.XForge;

/// <summary>
/// An X-Forge scene file (<c>.xff</c>, Red Faction for N-Gage): a flat run of chunks to the end of the file, each a
/// 12-byte header (<c>u16</c> type, <c>u16</c> version, <c>u32</c> id, <c>u32</c> payload size) and its payload. Read
/// only: Cairn never writes these (<see cref="SyntheticXff"/> makes test files).
/// </summary>
/// <remarks>
/// <para>
/// All values are little-endian. Strings are a <c>u8</c> length and Latin-1 characters without a terminator, except a
/// property's text. Chunks refer to each other by id; <see cref="NoReference"/> is "none". <see cref="XffScene"/>
/// resolves the references.
/// </para>
/// <para>
/// The chunk framing is strict: a header or payload that runs past the end of the file raises
/// <see cref="AssetFormatException"/>. Inside the framing the reader is forgiving: a chunk of a type or version it does
/// not type is kept as an <see cref="XffRawChunk"/> and counted, and a typed chunk whose payload does not match its
/// layout exactly (short, long, or a count that does not fit) is kept raw too, with a <see cref="Diagnostics"/> entry.
/// </para>
/// </remarks>
public sealed class XffFile
{
    /// <summary>The file extension.</summary>
    public const string Extension = ".xff";

    /// <summary>The reference value that means "no chunk".</summary>
    public const uint NoReference = 0xFFFFFFFF;

    private readonly Dictionary<uint, XffChunk> _byId;

    private XffFile(string name, int length, List<XffChunk> chunks, Dictionary<uint, XffChunk> byId, List<XffDiagnostic> diagnostics)
    {
        Name = name;
        Length = length;
        Chunks = chunks;
        _byId = byId;
        Diagnostics = diagnostics;
        var raw = new Dictionary<(ushort Type, ushort Version), int>();
        foreach (var chunk in chunks)
            if (chunk is XffRawChunk)
                raw[(chunk.Type, chunk.Version)] = raw.GetValueOrDefault((chunk.Type, chunk.Version)) + 1;
        RawChunkCounts = raw;
    }

    /// <summary>The file name used in messages.</summary>
    public string Name { get; }

    /// <summary>The file's length in bytes.</summary>
    public int Length { get; }

    /// <summary>Every chunk in file order: typed records, and <see cref="XffRawChunk"/> for the rest.</summary>
    public IReadOnlyList<XffChunk> Chunks { get; }

    /// <summary>What was wrong with single chunks (kept raw, or an id used twice); empty for a clean file.</summary>
    public IReadOnlyList<XffDiagnostic> Diagnostics { get; }

    /// <summary>How many chunks of each type and version were kept raw (types not read, and typed chunks that did not fit their layout).</summary>
    public IReadOnlyDictionary<(ushort Type, ushort Version), int> RawChunkCounts { get; }

    /// <summary>The number of chunks kept raw.</summary>
    public int RawChunkCount => RawChunkCounts.Values.Sum();

    /// <summary>The chunk with id <paramref name="id"/> (the first one, should an id be used twice), or null.</summary>
    public XffChunk? Find(uint id) => _byId.GetValueOrDefault(id);

    /// <summary>The chunks of record type <typeparamref name="T"/> in file order.</summary>
    public IEnumerable<T> All<T>() where T : XffChunk => Chunks.OfType<T>();

    /// <summary>True when the name ends in <c>.xff</c>.</summary>
    public static bool HasExtension(string? name) => name is not null && name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="head"/> (at least 16 bytes) starts like a scene: a version 1 group node or marker chunk
    /// whose parent is "none". Cheap; does not prove the rest of the file is sound.
    /// </summary>
    public static bool LooksLikeXff(ReadOnlySpan<byte> head)
    {
        if (head.Length < XffChunkHeader.Size + 4) return false;
        ushort type = BinaryPrimitives.ReadUInt16LittleEndian(head);
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(head[2..]);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(head[8..]);
        uint parent = BinaryPrimitives.ReadUInt32LittleEndian(head[12..]);
        return (type == XffChunkTypes.GroupNode || XffChunkTypes.IsMarker(type)) && version == 1 && size >= 4 + 1 + XffTransform.Size
            && size <= 4 + 1 + 255 + XffTransform.Size && parent == NoReference;
    }

    /// <summary>Reads a scene from a file.</summary>
    /// <exception cref="AssetFormatException">The chunk framing is broken.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static XffFile Read(string path) => Read(File.ReadAllBytes(path), Path.GetFileName(path));

    /// <summary>Reads a scene from its bytes; <paramref name="name"/> is used in messages.</summary>
    /// <exception cref="AssetFormatException">The file is empty, or a chunk header or payload runs past its end.</exception>
    public static XffFile Read(byte[] data, string name)
    {
        ArgumentNullException.ThrowIfNull(data);
        var cursor = new BinaryCursor(data, name ?? string.Empty);
        if (data.Length == 0) throw cursor.Fail("the file is empty.");
        var chunks = new List<XffChunk>();
        var byId = new Dictionary<uint, XffChunk>();
        var diagnostics = new List<XffDiagnostic>();
        while (cursor.Remaining > 0)
        {
            int offset = cursor.Position;
            if (cursor.Remaining < XffChunkHeader.Size)
                throw cursor.Fail($"the file ends with {cursor.Remaining} bytes at offset {offset} that are not a whole chunk header.");
            ushort type = cursor.ReadUInt16();
            ushort version = cursor.ReadUInt16();
            uint id = cursor.ReadUInt32();
            uint size = cursor.ReadUInt32();
            if (size > cursor.Remaining)
                throw cursor.Fail($"the payload of the {XffChunkTypes.Describe(type)} chunk #{id} at offset {offset} ({size} bytes) runs past the end of the file ({cursor.Remaining} bytes left).");
            var header = new XffChunkHeader(type, version, id, offset, (int)size);
            var payload = new ReadOnlyMemory<byte>(data, cursor.Position, (int)size);
            cursor.Skip((int)size, "chunk payload");
            var chunk = Parse(header, payload, diagnostics);
            chunks.Add(chunk);
            if (!byId.TryAdd(id, chunk))
                diagnostics.Add(new(id, type, offset, $"the id is used again (first by the {XffChunkTypes.Describe(byId[id].Type)} at offset {byId[id].Header.Offset}); references find the first."));
        }
        return new XffFile(name ?? string.Empty, data.Length, chunks, byId, diagnostics);
    }

    private static XffChunk Parse(XffChunkHeader header, ReadOnlyMemory<byte> payload, List<XffDiagnostic> diagnostics)
    {
        if (XffChunkTypes.KnownVersion(header.Type) is not { } version) return new XffRawChunk(header, payload, null);
        if (header.Version != version) return new XffRawChunk(header, payload, null);
        try
        {
            var r = new PayloadReader(payload.Span);
            XffChunk chunk = header.Type switch
            {
                XffChunkTypes.MeshInstance => ReadMeshInstance(header, payload, ref r),
                XffChunkTypes.Camera => ReadCamera(header, payload, ref r),
                XffChunkTypes.Light => ReadLight(header, payload, ref r),
                XffChunkTypes.Helper => new XffHelper(header, payload, r.U32("parent"), r.Name(), r.Vector3("position")),
                XffChunkTypes.Emitter => new XffEmitter(header, payload, r.U32("parent"), r.Name(), r.F32("value"), r.Name("particle file name")),
                XffChunkTypes.Sprite => new XffSprite(header, payload, r.U32("parent"), r.Name(), r.F32("first value"), r.F32("second value"),
                    r.Name("texture name"), r.Bytes(3, "three bytes").ToArray(), r.U32("reference")),
                XffChunkTypes.Collision => ReadCollision(header, payload, ref r),
                XffChunkTypes.VertexBuffer => ReadVertexBuffer(header, payload, ref r),
                XffChunkTypes.IndexList => ReadIndexList(header, payload, ref r),
                XffChunkTypes.Material => new XffMaterial(header, payload, r.Colour(), r.Colour(), r.Colour(), r.Colour(), r.F32("first value"),
                    r.U32("second value"), r.U32("third value"), r.U8("blend byte"), r.U8("blend byte"), r.F32("last value")),
                XffChunkTypes.Texture => new XffTexture(header, payload, r.Name("file name")),
                XffChunkTypes.AnimationTrack => ReadTrack(header, payload, ref r),
                XffChunkTypes.Prefab => new XffPrefab(header, payload, r.U32("parent"), r.Name(), r.Transform(), r.Name("file name"), r.U8("flag")),
                XffChunkTypes.FaceAttributes => ReadFaceAttributes(header, payload, ref r),
                XffChunkTypes.Property => ReadProperty(header, payload, ref r),
                _ => new XffNode(header, payload, r.U32("parent"), r.Name(), r.Transform()),
            };
            r.ExpectEnd();
            return chunk;
        }
        catch (LayoutException ex)
        {
            diagnostics.Add(new(header.Id, header.Type, header.Offset, ex.Message + " The chunk is kept as bytes."));
            return new XffRawChunk(header, payload, ex.Message);
        }
    }

    private static XffMeshInstance ReadMeshInstance(XffChunkHeader header, ReadOnlyMemory<byte> payload, ref PayloadReader r)
    {
        uint node = r.U32("node");
        string name = r.Name();
        int count = r.U16("submesh count");
        if (r.Remaining != count * XffSubmesh.Size)
            throw new LayoutException($"it declares {count} submeshes ({count * XffSubmesh.Size} bytes), but {r.Remaining} bytes follow.");
        var submeshes = new XffSubmesh[count];
        for (int i = 0; i < count; i++)
            submeshes[i] = new XffSubmesh(r.U32("vertex buffer"), r.U32("index list"), r.U32("face attributes"), r.U32("material"), r.U32("texture"),
                r.U32("reference"), r.U32("first index"), r.U32("index count"), r.U8("flag"));
        return new XffMeshInstance(header, payload, node, name, submeshes);
    }

    private static XffCamera ReadCamera(XffChunkHeader header, ReadOnlyMemory<byte> payload, ref PayloadReader r) =>
        new(header, payload, r.U32("parent"), r.U32("reference"), r.Name(), r.Vector3("position"), r.Quaternion("rotation"),
            r.F32("first value"), r.F32("second value"), r.F32("third value"));

    private static XffLight ReadLight(XffChunkHeader header, ReadOnlyMemory<byte> payload, ref PayloadReader r)
    {
        uint parent = r.U32("parent");
        string name = r.Name();
        var position = r.Vector3("position");
        var rotation = r.Quaternion("rotation");
        byte kind = r.U8("kind");
        var colour = r.Colour();
        var singles = new float[7];
        for (int i = 0; i < singles.Length; i++) singles[i] = r.F32("value");
        return new XffLight(header, payload, parent, name, position, rotation, kind, colour, singles);
    }

    private static XffCollision ReadCollision(XffChunkHeader header, ReadOnlyMemory<byte> payload, ref PayloadReader r)
    {
        uint parent = r.U32("parent");
        byte form = r.U8("form");
        byte unknown = r.U8("second byte");
        string name = r.Name();
        byte shape = r.U8("shape");
        if (form == XffCollision.NodeForm)
            return new XffCollision(header, payload, parent, form, unknown, name, shape, Vector3.Zero, Vector3.Zero, XffTransform.Identity, r.U32("source node"));
        if (form != XffCollision.BoxForm) throw new LayoutException($"its form {form} is not known (1 or 101).");
        var min = r.Vector3("box minimum");
        var max = r.Vector3("box maximum");
        return new XffCollision(header, payload, parent, form, unknown, name, shape, min, max, r.Transform(), NoReference);
    }

    private static XffVertexBuffer ReadVertexBuffer(XffChunkHeader header, ReadOnlyMemory<byte> payload, ref PayloadReader r)
    {
        uint layout = r.U32("vertex format");
        int stride = r.U16("stride");
        uint declared = r.U32("vertex count");
        var format = (XffVertexFormat)layout;
        if ((format & ~(XffVertexFormat.Normal | XffVertexFormat.Colour | XffVertexFormat.Uv)) != 0)
            throw new LayoutException($"its vertex format {layout} has bits that are not known (1 = normal, 2 = colour, 4 = UV).");
        int needed = XffVertexBuffer.VertexBytes(format);
        if (stride < needed)
            throw new LayoutException($"its stride {stride} is smaller than its vertex format needs ({needed} bytes).");
        if ((long)declared * stride != r.Remaining)
            throw new LayoutException($"it declares {declared} vertices of {stride} bytes ({(long)declared * stride} bytes), but {r.Remaining} bytes follow.");
        int count = (int)declared, extra = stride - needed;
        bool hasNormal = format.HasFlag(XffVertexFormat.Normal), hasColour = format.HasFlag(XffVertexFormat.Colour), hasUv = format.HasFlag(XffVertexFormat.Uv);
        var positions = new Vector3[count];
        var normals = new Vector3[hasNormal ? count : 0];
        var colours = new XffColour[hasColour ? count : 0];
        var uvs = new Vector2[hasUv ? count : 0];
        var unknownBytes = new byte[(long)count * extra];
        for (int i = 0; i < count; i++)
        {
            positions[i] = r.Vector3("position");
            if (hasNormal) normals[i] = r.Vector3("normal");
            if (hasColour) colours[i] = r.Colour();
            if (hasUv) uvs[i] = new Vector2(r.F32("u"), r.F32("v"));
            if (extra > 0) r.Bytes(extra, "vertex data").CopyTo(unknownBytes.AsSpan(i * extra));
        }
        return new XffVertexBuffer(header, payload, format, stride, positions, normals, colours, uvs, unknownBytes);
    }

    private static XffIndexList ReadIndexList(XffChunkHeader header, ReadOnlyMemory<byte> payload, ref PayloadReader r)
    {
        int count = r.U16("index count");
        if (r.Remaining != count * 2) throw new LayoutException($"it declares {count} indices ({count * 2} bytes), but {r.Remaining} bytes follow.");
        var indices = new ushort[count];
        for (int i = 0; i < count; i++) indices[i] = r.U16("index");
        return new XffIndexList(header, payload, indices);
    }

    private static XffFaceAttributes ReadFaceAttributes(XffChunkHeader header, ReadOnlyMemory<byte> payload, ref PayloadReader r)
    {
        int triangles = r.U16("triangle count");
        uint layout = r.U32("layout");
        var contents = (XffFaceContents)layout;
        if ((contents & ~(XffFaceContents.Colours | XffFaceContents.Uvs)) != 0)
            throw new LayoutException($"its layout value {layout} has bits that are not known (1 = colours, 4 = UVs).");
        bool colours = contents.HasFlag(XffFaceContents.Colours), uvs = contents.HasFlag(XffFaceContents.Uvs);
        int perTriangle = (colours ? 3 * 4 : 0) + (uvs ? 3 * 8 : 0);
        if (r.Remaining != triangles * perTriangle)
            throw new LayoutException($"it declares {triangles} triangles of {perTriangle} bytes ({triangles * perTriangle} bytes), but {r.Remaining} bytes follow.");
        var colourList = new XffColour[colours ? triangles * 3 : 0];
        var uvList = new Vector2[uvs ? triangles * 3 : 0];
        for (int t = 0; t < triangles; t++)
        {
            if (colours)
                for (int c = 0; c < 3; c++) colourList[t * 3 + c] = r.Colour();
            if (uvs)
                for (int c = 0; c < 3; c++) uvList[t * 3 + c] = new Vector2(r.F32("u"), r.F32("v"));
        }
        return new XffFaceAttributes(header, payload, triangles, contents, colourList, uvList);
    }

    private static XffAnimationTrack ReadTrack(XffChunkHeader header, ReadOnlyMemory<byte> payload, ref PayloadReader r)
    {
        uint parent = r.U32("parent");
        string name = r.Name();
        int animationCount = r.U16("animation count");
        // each animation is at least 18 bytes (start, end, rate, flags, key count)
        if (animationCount * 18L > r.Remaining)
            throw new LayoutException($"it declares {animationCount} animations, more than its {r.Remaining} bytes can hold.");
        const XffAnimationFlags known = XffAnimationFlags.Loop | XffAnimationFlags.Position | XffAnimationFlags.Rotation | XffAnimationFlags.Scale;
        var animations = new XffAnimation[animationCount];
        for (int a = 0; a < animationCount; a++)
        {
            int start = r.I32("start");
            int end = r.I32("end");
            float rate = r.F32("rate");
            ushort rawFlags = r.U16("flags");
            uint count = r.U32("key count");
            var flags = (XffAnimationFlags)rawFlags;
            if ((flags & ~known) != 0)
                throw new LayoutException($"its animation flags {rawFlags} have bits that are not known (1 = loop, 2 = position, 4 = rotation, 8 = scale).");
            int size = XffKey.SizeFor(flags);
            // keys without a channel take no bytes, so no payload can back their count (every known track has flags 7)
            if (size == 0 && count > 0)
                throw new LayoutException($"it declares {count} keys, but its flags {rawFlags} name no key channel (position, rotation or scale), so the keys hold nothing.");
            if ((long)count * size > r.Remaining)
                throw new LayoutException($"it declares {count} keys of {size} bytes ({(long)count * size} bytes), but {r.Remaining} bytes follow.");
            var keys = new XffKey[count];
            for (int i = 0; i < count; i++)
            {
                var position = flags.HasFlag(XffAnimationFlags.Position) ? r.Vector3("key position") : Vector3.Zero;
                var rotation = flags.HasFlag(XffAnimationFlags.Rotation) ? r.Quaternion("key rotation") : System.Numerics.Quaternion.Identity;
                var scale = flags.HasFlag(XffAnimationFlags.Scale) ? r.Vector3("key scale") : Vector3.One;
                keys[i] = new XffKey(position, rotation, scale);
            }
            animations[a] = new XffAnimation(start, end, rate, flags, keys);
        }
        return new XffAnimationTrack(header, payload, parent, name, animations);
    }

    private static XffProperty ReadProperty(XffChunkHeader header, ReadOnlyMemory<byte> payload, ref PayloadReader r)
    {
        uint node = r.U32("node");
        uint length = r.U32("text length");
        if (length != r.Remaining) throw new LayoutException($"it declares {length} bytes of text, but {r.Remaining} bytes follow.");
        var text = r.Bytes((int)length, "text");
        if (text.Length == 0 || text[^1] != 0) throw new LayoutException("its text does not end with a zero.");
        return new XffProperty(header, payload, node, Encoding.Latin1.GetString(text[..^1]));
    }

    /// <summary>A record that does not fit its chunk: the chunk is kept raw.</summary>
    private sealed class LayoutException(string message) : Exception(message);

    /// <summary>Bounds-checked little-endian reads over one payload; running short raises <see cref="LayoutException"/>.</summary>
    private ref struct PayloadReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;

        public readonly int Remaining => _data.Length - _position;

        private ReadOnlySpan<byte> Take(int count, string what)
        {
            if (count < 0 || count > Remaining)
                throw new LayoutException($"its {what} at payload offset {_position} runs past the end of the payload ({Remaining} bytes left).");
            var span = _data.Slice(_position, count);
            _position += count;
            return span;
        }

        public byte U8(string what) => Take(1, what)[0];
        public ushort U16(string what) => BinaryPrimitives.ReadUInt16LittleEndian(Take(2, what));
        public uint U32(string what) => BinaryPrimitives.ReadUInt32LittleEndian(Take(4, what));
        public int I32(string what) => BinaryPrimitives.ReadInt32LittleEndian(Take(4, what));
        public float F32(string what) => BinaryPrimitives.ReadSingleLittleEndian(Take(4, what));
        public ReadOnlySpan<byte> Bytes(int count, string what) => Take(count, what);
        public Vector3 Vector3(string what) => new(F32(what), F32(what), F32(what));
        public Quaternion Quaternion(string what) => new(F32(what), F32(what), F32(what), F32(what));
        public XffColour Colour() { var b = Take(4, "colour"); return new XffColour(b[0], b[1], b[2], b[3]); }
        public XffTransform Transform() => new(Vector3("position"), Quaternion("rotation"), Vector3("scale"));

        public string Name(string what = "name")
        {
            int length = U8(what + " length");
            return Encoding.Latin1.GetString(Take(length, what));
        }

        public readonly void ExpectEnd()
        {
            if (Remaining != 0) throw new LayoutException($"{Remaining} bytes are left over after the record (payload offset {_position}).");
        }
    }
}
