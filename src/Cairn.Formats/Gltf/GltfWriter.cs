using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cairn.Formats.Gltf;

/// <summary>Options for <see cref="GltfWriter"/>.</summary>
public sealed record GltfWriteOptions
{
    /// <summary>
    /// For .gltf output: true writes every buffer that has <see cref="GltfBuffer.Data"/> as a data:
    /// URI; false (default) writes buffer 0 to "&lt;basename&gt;.bin" beside the .gltf and any other
    /// buffer with data to "&lt;basename&gt;_&lt;i&gt;.bin".
    /// </summary>
    public bool EmbedBuffers { get; init; }

    /// <summary>
    /// For .gltf output: images with <see cref="GltfImage.Data"/> and a safe relative Uri are written
    /// as files beside the .gltf; images with Data and no Uri become data: URIs.
    /// For .glb output: images with Data are moved into buffer views of the BIN chunk (Uri cleared,
    /// MimeType kept or inferred from the PNG/JPEG/WebP/KTX2 signature).
    /// When false, image URIs are written verbatim (an image with Data but no other source still
    /// becomes a data: URI, so it is never lost).
    /// </summary>
    public bool WriteImageFiles { get; init; } = true;

    /// <summary>Indent the JSON for readability.</summary>
    public bool Indented { get; init; } = true;
}

/// <summary>
/// Writes a <see cref="GltfDocument"/> as .gltf (+ .bin + image files) or .glb. Only members that
/// are set are emitted (plus the required ones), in the conventional glTF member order, with floats
/// in their shortest round-trip form. Buffer lengths are recomputed from <see cref="GltfBuffer.Data"/>.
/// The document is validated first (every index in range, matrices of 16 values, known accessor
/// types, finite numbers) and an inconsistent one raises <see cref="ArgumentException"/>. The
/// document itself is never modified: URIs and layout changes live in a private write plan.
/// </summary>
public static class GltfWriter
{
    private const uint GlbMagic = 0x46546C67;
    private const uint ChunkJson = 0x4E4F534A;
    private const uint ChunkBin = 0x004E4942;

    /// <summary>Writes a .gltf file plus its .bin and image files (see <see cref="GltfWriteOptions"/>).</summary>
    public static void WriteGltf(GltfDocument doc, string path, GltfWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentException.ThrowIfNullOrEmpty(path);
        options ??= new GltfWriteOptions();
        Validate(doc);

        string full = Path.GetFullPath(path);
        string folder = Path.GetDirectoryName(full) ?? full;
        string baseName = Path.GetFileNameWithoutExtension(full);
        var plan = new Plan(doc);
        var files = new List<(string RelativePath, byte[] Bytes)>();

        for (int i = 0; i < doc.Buffers.Count; i++)
        {
            var data = doc.Buffers[i].Data;
            if (data is null) continue;
            if (options.EmbedBuffers)
            {
                plan.BufferUri[i] = DataUri("application/octet-stream", data);
            }
            else
            {
                string file = i == 0 ? baseName + ".bin" : string.Create(CultureInfo.InvariantCulture, $"{baseName}_{i}.bin");
                plan.BufferUri[i] = Uri.EscapeDataString(file);
                files.Add((file, data));
            }
        }
        for (int i = 0; i < doc.Images.Count; i++)
        {
            var img = doc.Images[i];
            if (img.Data is null || img.BufferView is not null) continue;
            if (img.Uri is null || GltfReader.IsDataUri(img.Uri))
                plan.ImageUri[i] = DataUri(img.MimeType ?? SniffMime(img.Data) ?? "application/octet-stream", img.Data);
            else if (options.WriteImageFiles && GltfReader.IsSafeRelativeUri(img.Uri))
                files.Add((GltfReader.DecodeUri(img.Uri), img.Data));
        }

        string json = Emit(doc, plan, options.Indented);
        Directory.CreateDirectory(folder);
        string prefix = Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar;
        // Every target is checked before anything is written, so a refused name leaves no partial export.
        var targets = new List<(string Target, byte[] Bytes)>();
        foreach (var (relative, bytes) in files)
        {
            string target = Path.GetFullPath(Path.Combine(folder, relative));
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"The glTF file '{relative}' would be written outside '{folder}'.", nameof(doc));
            targets.Add((target, bytes));
        }
        // Each file is written atomically (temporary file, then replaced), the .gltf last: a failure part-way
        // never leaves a truncated file, and never a .gltf pointing at side files that were not written.
        foreach (var (target, bytes) in targets)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Workspace.AtomicFile.WriteAllBytes(target, bytes);
        }
        Workspace.AtomicFile.WriteAllBytes(full, new System.Text.UTF8Encoding(false).GetBytes(json));
    }

    /// <summary>
    /// The files besides <paramref name="path"/> that <see cref="WriteGltf"/> would write (the .bin buffers
    /// and image files), as full paths, so a caller can ask once before replacing any that already exist.
    /// </summary>
    public static IReadOnlyList<string> SideFiles(GltfDocument doc, string path, GltfWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentException.ThrowIfNullOrEmpty(path);
        options ??= new GltfWriteOptions();
        string full = Path.GetFullPath(path);
        string folder = Path.GetDirectoryName(full) ?? full;
        string baseName = Path.GetFileNameWithoutExtension(full);
        var result = new List<string>();
        if (!options.EmbedBuffers)
        {
            for (int i = 0; i < doc.Buffers.Count; i++)
            {
                if (doc.Buffers[i].Data is null) continue;
                result.Add(Path.Combine(folder, i == 0 ? baseName + ".bin" : string.Create(CultureInfo.InvariantCulture, $"{baseName}_{i}.bin")));
            }
        }
        if (options.WriteImageFiles)
        {
            foreach (var img in doc.Images)
            {
                if (img.Data is null || img.BufferView is not null || img.Uri is null || GltfReader.IsDataUri(img.Uri)) continue;
                if (GltfReader.IsSafeRelativeUri(img.Uri)) result.Add(Path.GetFullPath(Path.Combine(folder, GltfReader.DecodeUri(img.Uri))));
            }
        }
        return result;
    }

    /// <summary>Writes a .glb file (see <see cref="ToGlb"/>), atomically.</summary>
    public static void WriteGlb(GltfDocument doc, string path, GltfWriteOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        byte[] glb = ToGlb(doc, options);
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full) ?? full);
        Workspace.AtomicFile.WriteAllBytes(full, glb);
    }

    /// <summary>
    /// The document as a GLB image. Buffer 0's Data becomes the BIN chunk (padded to 4 with zeros;
    /// the JSON chunk is padded with spaces); other buffers with Data become data URIs. With
    /// <see cref="GltfWriteOptions.WriteImageFiles"/>, images with Data are appended to the BIN chunk
    /// as new buffer views (a buffer 0 is created when the document has none; when buffer 0 has no
    /// Data the images fall back to data URIs).
    /// </summary>
    public static byte[] ToGlb(GltfDocument doc, GltfWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        options ??= new GltfWriteOptions();
        Validate(doc);
        var plan = new Plan(doc);

        var bin = new ArrayBufferWriter<byte>();
        bool hasBin = false;
        if (doc.Buffers.Count == 0)
        {
            if (options.WriteImageFiles && doc.Images.Any(i => i.Data is not null && i.BufferView is null))
            {
                plan.SyntheticBuffer = true;
                hasBin = true;
            }
        }
        else if (doc.Buffers[0].Data is { } data0)
        {
            bin.Write(data0);
            hasBin = true;
            plan.BufferUri[0] = null;
        }
        for (int i = 1; i < doc.Buffers.Count; i++)
        {
            if (doc.Buffers[i].Data is { } data) plan.BufferUri[i] = DataUri("application/octet-stream", data);
        }

        for (int i = 0; i < doc.Images.Count; i++)
        {
            var img = doc.Images[i];
            if (img.Data is null || img.BufferView is not null) continue;
            string? mime = img.MimeType ?? SniffMime(img.Data);
            if (options.WriteImageFiles && hasBin && mime is not null)
            {
                int pad = (4 - (bin.WrittenCount & 3)) & 3;
                bin.Write(new byte[pad]);
                plan.ExtraViews.Add(new GltfBufferView { Buffer = 0, ByteOffset = bin.WrittenCount, ByteLength = img.Data.Length });
                bin.Write(img.Data);
                plan.ImageUri[i] = null;
                plan.ImageBufferView[i] = doc.BufferViews.Count + plan.ExtraViews.Count - 1;
                plan.ImageMime[i] = mime;
            }
            else if (img.Uri is null || GltfReader.IsDataUri(img.Uri))
            {
                plan.ImageUri[i] = DataUri(mime ?? "application/octet-stream", img.Data);
            }
        }
        if (hasBin) plan.Buffer0Length = bin.WrittenCount;

        byte[] json = System.Text.Encoding.UTF8.GetBytes(Emit(doc, plan, options.Indented));
        int jsonPadded = (json.Length + 3) & ~3;
        int binPadded = (bin.WrittenCount + 3) & ~3;
        long total = 12 + 8 + (long)jsonPadded + (hasBin ? 8 + (long)binPadded : 0);
        if (total > int.MaxValue)
            throw new ArgumentException("The document is too large for a GLB file.", nameof(doc));

        var glb = new byte[total];
        var span = glb.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, GlbMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)jsonPadded);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], ChunkJson);
        json.CopyTo(span[20..]);
        span.Slice(20 + json.Length, jsonPadded - json.Length).Fill(0x20);
        if (hasBin)
        {
            int at = 20 + jsonPadded;
            BinaryPrimitives.WriteUInt32LittleEndian(span[at..], (uint)binPadded);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 4)..], ChunkBin);
            bin.WrittenSpan.CopyTo(span[(at + 8)..]); // padding stays zero
        }
        return glb;
    }

    /// <summary>
    /// The JSON as it would be written for .gltf, except that only buffers without an external Uri
    /// (and images with Data but no other source) are embedded as data: URIs; everything else keeps
    /// its Uri verbatim and no files are written.
    /// </summary>
    public static string ToJson(GltfDocument doc, GltfWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        options ??= new GltfWriteOptions();
        Validate(doc);
        var plan = new Plan(doc);
        for (int i = 0; i < doc.Buffers.Count; i++)
        {
            var b = doc.Buffers[i];
            if (b.Data is not null && (b.Uri is null || GltfReader.IsDataUri(b.Uri)))
                plan.BufferUri[i] = DataUri("application/octet-stream", b.Data);
        }
        for (int i = 0; i < doc.Images.Count; i++)
        {
            var img = doc.Images[i];
            if (img.Data is not null && img.BufferView is null && (img.Uri is null || GltfReader.IsDataUri(img.Uri)))
                plan.ImageUri[i] = DataUri(img.MimeType ?? SniffMime(img.Data) ?? "application/octet-stream", img.Data);
        }
        return Emit(doc, plan, options.Indented);
    }

    /// <summary>The MIME type implied by an image's signature, or null when it is not a recognised format.</summary>
    internal static string? SniffMime(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return "image/png";
        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])) return "image/jpeg";
        if (data.Length >= 12 && data.StartsWith("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        if (data.StartsWith((ReadOnlySpan<byte>)[0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A])) return "image/ktx2";
        return null;
    }

    private static string DataUri(string mime, byte[] data) => $"data:{mime};base64,{Convert.ToBase64String(data)}";

    // ---- write plan ----

    /// <summary>The per-write values that differ from the document (URIs, lengths, appended views).</summary>
    private sealed class Plan
    {
        public Plan(GltfDocument doc)
        {
            BufferUri = doc.Buffers.Select(b => b.Uri).ToArray();
            ImageUri = doc.Images.Select(i => i.Uri).ToArray();
            ImageBufferView = doc.Images.Select(i => i.BufferView).ToArray();
            ImageMime = doc.Images.Select(i => i.MimeType).ToArray();
        }

        public string?[] BufferUri { get; }

        public string?[] ImageUri { get; }

        public int?[] ImageBufferView { get; }

        public string?[] ImageMime { get; }

        public List<GltfBufferView> ExtraViews { get; } = new();

        /// <summary>A buffer 0 the document lacks, created to hold GLB-embedded images.</summary>
        public bool SyntheticBuffer { get; set; }

        /// <summary>Buffer 0's byteLength when the GLB BIN chunk grew beyond its Data.</summary>
        public int? Buffer0Length { get; set; }
    }

    // ---- validation ----

    private static void Validate(GltfDocument doc)
    {
        if (doc.Asset is null || doc.Asset.Version is null) Bad("the asset (and its version) must be set");
        Check(doc.Scene, doc.Scenes.Count, "the default scene", "scene");
        for (int i = 0; i < doc.Scenes.Count; i++)
            foreach (int n in doc.Scenes[i].Nodes) Check(n, doc.Nodes.Count, $"scene {i}", "node");
        for (int i = 0; i < doc.Nodes.Count; i++)
        {
            var n = doc.Nodes[i];
            foreach (int c in n.Children) Check(c, doc.Nodes.Count, $"node {i}", "child node");
            Check(n.Mesh, doc.Meshes.Count, $"node {i}", "mesh");
            Check(n.Skin, doc.Skins.Count, $"node {i}", "skin");
            Check(n.Camera, doc.Cameras.Count, $"node {i}", "camera");
            if (n.Matrix is not null && n.Matrix.Length != 16) Bad($"node {i} has a matrix of {n.Matrix.Length} values instead of 16");
        }
        for (int i = 0; i < doc.Meshes.Count; i++)
        {
            foreach (var p in doc.Meshes[i].Primitives)
            {
                if (p is null) Bad($"mesh {i} has a null primitive");
                foreach (int a in p.Attributes.Values) Check(a, doc.Accessors.Count, $"mesh {i}", "attribute accessor");
                Check(p.Indices, doc.Accessors.Count, $"mesh {i}", "index accessor");
                Check(p.Material, doc.Materials.Count, $"mesh {i}", "material");
                foreach (var t in p.Targets)
                    foreach (int a in t.Values) Check(a, doc.Accessors.Count, $"mesh {i}", "morph target accessor");
            }
        }
        for (int i = 0; i < doc.Skins.Count; i++)
        {
            var s = doc.Skins[i];
            Check(s.InverseBindMatrices, doc.Accessors.Count, $"skin {i}", "inverse bind matrix accessor");
            Check(s.Skeleton, doc.Nodes.Count, $"skin {i}", "skeleton node");
            foreach (int j in s.Joints) Check(j, doc.Nodes.Count, $"skin {i}", "joint node");
        }
        for (int i = 0; i < doc.Materials.Count; i++)
        {
            var m = doc.Materials[i];
            foreach (var t in new[] { m.PbrMetallicRoughness?.BaseColorTexture, m.PbrMetallicRoughness?.MetallicRoughnessTexture,
                m.NormalTexture, m.OcclusionTexture, m.EmissiveTexture })
            {
                if (t is not null) Check(t.Index, doc.Textures.Count, $"material {i}", "texture");
            }
        }
        for (int i = 0; i < doc.Textures.Count; i++)
        {
            Check(doc.Textures[i].Sampler, doc.Samplers.Count, $"texture {i}", "sampler");
            Check(doc.Textures[i].Source, doc.Images.Count, $"texture {i}", "image");
        }
        for (int i = 0; i < doc.Images.Count; i++)
            Check(doc.Images[i].BufferView, doc.BufferViews.Count, $"image {i}", "buffer view");
        for (int i = 0; i < doc.Animations.Count; i++)
        {
            var a = doc.Animations[i];
            foreach (var c in a.Channels)
            {
                Check(c.Sampler, a.Samplers.Count, $"animation {i}", "sampler");
                if (c.Target is null || c.Target.Path is null) Bad($"animation {i} has a channel without a target path");
                Check(c.Target.Node, doc.Nodes.Count, $"animation {i}", "target node");
            }
            foreach (var s in a.Samplers)
            {
                Check(s.Input, doc.Accessors.Count, $"animation {i}", "input accessor");
                Check(s.Output, doc.Accessors.Count, $"animation {i}", "output accessor");
            }
        }
        for (int i = 0; i < doc.Accessors.Count; i++)
        {
            var a = doc.Accessors[i];
            Check(a.BufferView, doc.BufferViews.Count, $"accessor {i}", "buffer view");
            if (!GltfAccessorType.TryComponentCount(a.Type, out _)) Bad($"accessor {i} has the unknown type '{a.Type}'");
            if (!GltfComponentType.IsValid(a.ComponentType)) Bad($"accessor {i} has the unknown component type {a.ComponentType}");
            if (a.Count < 0 || a.ByteOffset < 0) Bad($"accessor {i} has a negative count or byteOffset");
            if (a.Sparse is { } sp)
            {
                if (sp.Indices is null || sp.Values is null) Bad($"accessor {i} has sparse data without indices or values");
                Check(sp.Indices.BufferView, doc.BufferViews.Count, $"the sparse part of accessor {i}", "index buffer view");
                Check(sp.Values.BufferView, doc.BufferViews.Count, $"the sparse part of accessor {i}", "value buffer view");
            }
        }
        for (int i = 0; i < doc.BufferViews.Count; i++)
        {
            var v = doc.BufferViews[i];
            if (v.Buffer < 0 || v.Buffer >= doc.Buffers.Count)
                Bad($"buffer view {i} refers to buffer {v.Buffer}, which does not exist (was GltfBufferBuilder.Finish called?)");
            int length = doc.Buffers[v.Buffer].Data?.Length ?? doc.Buffers[v.Buffer].ByteLength;
            if (v.ByteOffset < 0 || v.ByteLength < 0 || (long)v.ByteOffset + v.ByteLength > length)
                Bad($"buffer view {i} (offset {v.ByteOffset}, length {v.ByteLength}) reaches past the end of buffer {v.Buffer} ({length} bytes)");
        }
    }

    private static void Check(int? index, int count, string who, string what)
    {
        if (index is int i && (i < 0 || i >= count))
            Bad($"{who} refers to {what} {i}, which does not exist (the document has {count})");
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Bad(string what) => throw new ArgumentException($"The glTF document is inconsistent: {what}.", "doc");

    // ---- JSON emission ----

    private static string Emit(GltfDocument doc, Plan plan, bool indented)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = indented,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            SkipValidation = false,
        }))
        {
            new Emitter(writer).Document(doc, plan);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Writes objects in glTF's conventional member order, tracking names to refuse duplicates.</summary>
    private sealed class Emitter(Utf8JsonWriter w)
    {
        private readonly Stack<HashSet<string>> _names = new();

        public void Document(GltfDocument d, Plan plan)
        {
            Begin();
            Name("asset"); Asset(d.Asset);
            Strings("extensionsUsed", d.ExtensionsUsed);
            Strings("extensionsRequired", d.ExtensionsRequired);
            Int("scene", d.Scene);
            Array("scenes", d.Scenes, Scene);
            Array("nodes", d.Nodes, Node);
            if (d.Cameras.Count > 0)
            {
                Name("cameras");
                w.WriteStartArray();
                foreach (var c in d.Cameras) Raw(c);
                w.WriteEndArray();
            }
            Array("meshes", d.Meshes, Mesh);
            Array("skins", d.Skins, Skin);
            Array("materials", d.Materials, Material);
            Array("textures", d.Textures, Texture);
            if (d.Images.Count > 0)
            {
                Name("images");
                w.WriteStartArray();
                for (int i = 0; i < d.Images.Count; i++) Image(d.Images[i], plan.ImageUri[i], plan.ImageBufferView[i], plan.ImageMime[i]);
                w.WriteEndArray();
            }
            Array("samplers", d.Samplers, Sampler);
            Array("animations", d.Animations, Animation);
            Array("accessors", d.Accessors, Accessor);
            if (d.BufferViews.Count + plan.ExtraViews.Count > 0)
            {
                Name("bufferViews");
                w.WriteStartArray();
                foreach (var v in d.BufferViews) BufferView(v);
                foreach (var v in plan.ExtraViews) BufferView(v);
                w.WriteEndArray();
            }
            if (d.Buffers.Count > 0 || plan.SyntheticBuffer)
            {
                Name("buffers");
                w.WriteStartArray();
                if (plan.SyntheticBuffer)
                {
                    Begin();
                    Int("byteLength", plan.Buffer0Length ?? 0);
                    _names.Pop();
                    w.WriteEndObject();
                }
                for (int i = 0; i < d.Buffers.Count; i++)
                {
                    var b = d.Buffers[i];
                    int length = i == 0 && plan.Buffer0Length is int l ? l : b.Data?.Length ?? b.ByteLength;
                    Begin();
                    Str("name", b.Name);
                    Str("uri", plan.BufferUri[i]);
                    Int("byteLength", length);
                    Tail(b);
                }
                w.WriteEndArray();
            }
            Tail(d);
        }

        private void Asset(GltfAsset a)
        {
            Begin();
            Str("copyright", a.Copyright);
            Str("generator", a.Generator);
            Str("version", a.Version);
            Str("minVersion", a.MinVersion);
            Tail(a);
        }

        private void Scene(GltfScene s)
        {
            Begin();
            Str("name", s.Name);
            Ints("nodes", s.Nodes);
            Tail(s);
        }

        private void Node(GltfNode n)
        {
            Begin();
            Str("name", n.Name);
            Ints("children", n.Children);
            Int("mesh", n.Mesh);
            Int("skin", n.Skin);
            Int("camera", n.Camera);
            Floats("matrix", n.Matrix);
            if (n.Translation is { } t) Floats("translation", [t.X, t.Y, t.Z]);
            if (n.Rotation is { } r) Floats("rotation", [r.X, r.Y, r.Z, r.W]);
            if (n.Scale is { } s) Floats("scale", [s.X, s.Y, s.Z]);
            Floats("weights", n.Weights);
            Tail(n);
        }

        private void Mesh(GltfMesh m)
        {
            Begin();
            Str("name", m.Name);
            Name("primitives");
            w.WriteStartArray();
            foreach (var p in m.Primitives) Primitive(p);
            w.WriteEndArray();
            Floats("weights", m.Weights);
            Tail(m);
        }

        private void Primitive(GltfPrimitive p)
        {
            Begin();
            Name("attributes");
            Map(p.Attributes);
            Int("indices", p.Indices);
            Int("material", p.Material);
            Int("mode", p.Mode);
            if (p.Targets.Count > 0)
            {
                Name("targets");
                w.WriteStartArray();
                foreach (var t in p.Targets) Map(t);
                w.WriteEndArray();
            }
            Tail(p);
        }

        private void Map(Dictionary<string, int> map)
        {
            w.WriteStartObject();
            foreach (var (k, v) in map) w.WriteNumber(k, v);
            w.WriteEndObject();
        }

        private void Skin(GltfSkin s)
        {
            Begin();
            Str("name", s.Name);
            Int("inverseBindMatrices", s.InverseBindMatrices);
            Int("skeleton", s.Skeleton);
            Name("joints");
            w.WriteStartArray();
            foreach (int j in s.Joints) w.WriteNumberValue(j);
            w.WriteEndArray();
            Tail(s);
        }

        private void TextureInfo(string name, GltfTextureInfo? t)
        {
            if (t is null) return;
            Name(name);
            Begin();
            Int("index", t.Index);
            Int("texCoord", t.TexCoord);
            Float("scale", t.Scale);
            Float("strength", t.Strength);
            Tail(t);
        }

        private void Material(GltfMaterial m)
        {
            Begin();
            Str("name", m.Name);
            if (m.PbrMetallicRoughness is { } pbr)
            {
                Name("pbrMetallicRoughness");
                Begin();
                Floats("baseColorFactor", pbr.BaseColorFactor);
                TextureInfo("baseColorTexture", pbr.BaseColorTexture);
                Float("metallicFactor", pbr.MetallicFactor);
                Float("roughnessFactor", pbr.RoughnessFactor);
                TextureInfo("metallicRoughnessTexture", pbr.MetallicRoughnessTexture);
                Tail(pbr);
            }
            TextureInfo("normalTexture", m.NormalTexture);
            TextureInfo("occlusionTexture", m.OcclusionTexture);
            TextureInfo("emissiveTexture", m.EmissiveTexture);
            Floats("emissiveFactor", m.EmissiveFactor);
            Str("alphaMode", m.AlphaMode);
            Float("alphaCutoff", m.AlphaCutoff);
            if (m.DoubleSided is bool ds) { Name("doubleSided"); w.WriteBooleanValue(ds); }
            Tail(m);
        }

        private void Texture(GltfTexture t)
        {
            Begin();
            Str("name", t.Name);
            Int("sampler", t.Sampler);
            Int("source", t.Source);
            Tail(t);
        }

        private void Image(GltfImage img, string? uri, int? bufferView, string? mime)
        {
            Begin();
            Str("name", img.Name);
            Str("uri", uri);
            Str("mimeType", mime);
            Int("bufferView", bufferView);
            Tail(img);
        }

        private void Sampler(GltfSampler s)
        {
            Begin();
            Str("name", s.Name);
            Int("magFilter", s.MagFilter);
            Int("minFilter", s.MinFilter);
            Int("wrapS", s.WrapS);
            Int("wrapT", s.WrapT);
            Tail(s);
        }

        private void Animation(GltfAnimation a)
        {
            Begin();
            Str("name", a.Name);
            Name("channels");
            w.WriteStartArray();
            foreach (var c in a.Channels)
            {
                Begin();
                Int("sampler", c.Sampler);
                Name("target");
                Begin();
                Int("node", c.Target.Node);
                Str("path", c.Target.Path);
                Tail(c.Target);
                Tail(c);
            }
            w.WriteEndArray();
            Name("samplers");
            w.WriteStartArray();
            foreach (var s in a.Samplers)
            {
                Begin();
                Int("input", s.Input);
                Str("interpolation", s.Interpolation);
                Int("output", s.Output);
                Tail(s);
            }
            w.WriteEndArray();
            Tail(a);
        }

        private void Accessor(GltfAccessor a)
        {
            Begin();
            Str("name", a.Name);
            Int("bufferView", a.BufferView);
            if (a.ByteOffset != 0) Int("byteOffset", a.ByteOffset);
            Int("componentType", a.ComponentType);
            if (a.Normalized) { Name("normalized"); w.WriteBooleanValue(true); }
            Int("count", a.Count);
            Str("type", a.Type);
            Floats("max", a.Max);
            Floats("min", a.Min);
            if (a.Sparse is { } sp)
            {
                Name("sparse");
                Begin();
                Int("count", sp.Count);
                Name("indices");
                Begin();
                Int("bufferView", sp.Indices.BufferView);
                if (sp.Indices.ByteOffset != 0) Int("byteOffset", sp.Indices.ByteOffset);
                Int("componentType", sp.Indices.ComponentType);
                Tail(sp.Indices);
                Name("values");
                Begin();
                Int("bufferView", sp.Values.BufferView);
                if (sp.Values.ByteOffset != 0) Int("byteOffset", sp.Values.ByteOffset);
                Tail(sp.Values);
                Tail(sp);
            }
            Tail(a);
        }

        private void BufferView(GltfBufferView v)
        {
            Begin();
            Str("name", v.Name);
            Int("buffer", v.Buffer);
            if (v.ByteOffset != 0) Int("byteOffset", v.ByteOffset);
            Int("byteLength", v.ByteLength);
            Int("byteStride", v.ByteStride);
            Int("target", v.Target);
            Tail(v);
        }

        // ---- primitives ----

        private void Begin()
        {
            w.WriteStartObject();
            _names.Push(new HashSet<string>(StringComparer.Ordinal));
        }

        /// <summary>Writes unknown members, extensions and extras, then closes the object.</summary>
        private void Tail(GltfProperty p)
        {
            foreach (var (name, value) in p.UnknownMembers)
            {
                Name(name);
                Raw(value);
            }
            if (p.Extensions is not null) { Name("extensions"); Raw(p.Extensions); }
            if (p.Extras is not null) { Name("extras"); Raw(p.Extras); }
            _names.Pop();
            w.WriteEndObject();
        }

        private void Name(string name)
        {
            if (!_names.Peek().Add(name))
                Bad($"the member '{name}' would be written twice (an UnknownMembers entry collides with a modelled member)");
            w.WritePropertyName(name);
        }

        private void Raw(JsonNode? node)
        {
            if (node is null) w.WriteNullValue();
            else node.WriteTo(w);
        }

        private void Str(string name, string? value)
        {
            if (value is null) return;
            Name(name);
            w.WriteStringValue(value);
        }

        private void Int(string name, int? value)
        {
            if (value is not int v) return;
            Name(name);
            w.WriteNumberValue(v);
        }

        private void Float(string name, float? value)
        {
            if (value is not float v) return;
            Name(name);
            FloatValue(v, name);
        }

        private void FloatValue(float v, string name)
        {
            if (!float.IsFinite(v)) Bad($"'{name}' holds the non-finite value {v.ToString(CultureInfo.InvariantCulture)}");
            w.WriteNumberValue(v);
        }

        private void Floats(string name, float[]? values)
        {
            if (values is null) return;
            Name(name);
            w.WriteStartArray();
            foreach (float v in values) FloatValue(v, name);
            w.WriteEndArray();
        }

        private void Ints(string name, List<int> values)
        {
            if (values.Count == 0) return;
            Name(name);
            w.WriteStartArray();
            foreach (int v in values) w.WriteNumberValue(v);
            w.WriteEndArray();
        }

        private void Strings(string name, List<string> values)
        {
            if (values.Count == 0) return;
            Name(name);
            w.WriteStartArray();
            foreach (string v in values) w.WriteStringValue(v);
            w.WriteEndArray();
        }

        private void Array<T>(string name, List<T> items, Action<T> write)
        {
            if (items.Count == 0) return;
            Name(name);
            w.WriteStartArray();
            foreach (var item in items) write(item);
            w.WriteEndArray();
        }
    }
}
