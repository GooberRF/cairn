using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cairn.Formats.Gltf;

/// <summary>Options for <see cref="GltfReader"/>.</summary>
public sealed record GltfReadOptions
{
    /// <summary>
    /// Read external image files into <see cref="GltfImage.Data"/>. Off by default because most
    /// callers only want geometry and animation, and textures can be large.
    /// </summary>
    public bool LoadImages { get; init; }

    /// <summary>Refuse any file, buffer or image larger than this, so a damaged length cannot exhaust memory.</summary>
    public long MaxBytes { get; init; } = 1L << 30;
}

/// <summary>
/// Reads .gltf (JSON) and .glb (binary) files into a <see cref="GltfDocument"/>. The container is
/// detected from the "glTF" magic, not the extension. Every known member's JSON type is checked and
/// buffers are resolved (GLB chunk, data URI or a file beside the model); accessor contents are
/// validated later, by <see cref="GltfAccessorReader"/>, when they are actually read. Bad input
/// raises <see cref="AssetFormatException"/> and nothing else.
/// </summary>
public static class GltfReader
{
    private const uint GlbMagic = 0x46546C67;
    private const uint ChunkJson = 0x4E4F534A;
    private const uint ChunkBin = 0x004E4942;

    /// <summary>
    /// Reads a .gltf or .glb file. External URIs are resolved relative to the file's folder and may
    /// not leave it.
    /// </summary>
    /// <exception cref="AssetFormatException">The file is not a readable glTF model.</exception>
    /// <exception cref="IOException">The file could not be opened.</exception>
    public static GltfDocument ReadFile(string path, GltfReadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        options ??= new GltfReadOptions();
        string fullPath = Path.GetFullPath(path);
        string name = Path.GetFileName(fullPath);
        string folder = Path.GetDirectoryName(fullPath) ?? fullPath;
        string folderPrefix = Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar;
        byte[] data = ReadLimited(fullPath, name, options.MaxBytes);
        return Read(data, name, uri =>
        {
            string target = Path.GetFullPath(Path.Combine(folder, uri));
            if (!target.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase))
                throw new AssetFormatException($"'{name}' refers to a file outside its folder ('{uri}'), which is not allowed.");
            return File.Exists(target) ? ReadLimited(target, name, options.MaxBytes) : null;
        }, options);
    }

    /// <summary>
    /// Reads a model from its bytes. <paramref name="resolveUri"/> returns the bytes of a relative
    /// external URI (already validated by <see cref="IsSafeRelativeUri"/> and percent-decoded), or
    /// null when the file is missing: a missing buffer is an error, a missing image just leaves
    /// <see cref="GltfImage.Data"/> null. Without a resolver every external buffer counts as missing.
    /// </summary>
    /// <exception cref="AssetFormatException">The data is not a readable glTF model.</exception>
    public static GltfDocument Read(byte[] data, string fileName, Func<string, byte[]?>? resolveUri, GltfReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        fileName ??= "glTF";
        options ??= new GltfReadOptions();
        var ctx = new Ctx(fileName);

        ReadOnlyMemory<byte> json;
        byte[]? bin = null;
        if (data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data) == GlbMagic)
            (json, bin) = SplitGlb(data, fileName);
        else
            json = data;

        GltfDocument doc;
        using (var parsed = ParseJson(json, fileName))
        {
            try
            {
                doc = ParseDocument(ctx, parsed.RootElement);
            }
            catch (InvalidOperationException ex)
            {
                // System.Text.Json raises this when a string escape decodes to invalid text (a lone surrogate).
                throw new AssetFormatException($"'{fileName}' is damaged: its JSON holds a string that is not valid text.", ex);
            }
        }
        ResolveBuffers(doc, ctx, bin, resolveUri, options);
        ResolveImages(doc, ctx, resolveUri, options);
        return doc;
    }

    /// <summary>
    /// True when <paramref name="uri"/> is a safe relative file reference: not a data: URI, not
    /// absolute or rooted, no scheme or drive letter, no ".." segment (also after percent-decoding,
    /// so "%2e%2e/x.bin" is caught), no control characters or alternate-stream colons. Readers refuse
    /// to follow anything else, and writers only create files for URIs that pass.
    /// </summary>
    public static bool IsSafeRelativeUri(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return false;
        if (!IsSafePath(uri)) return false;
        string decoded = DecodeUri(uri);
        return IsSafePath(decoded);
    }

    internal static string DecodeUri(string uri)
    {
        try { return Uri.UnescapeDataString(uri); }
        catch (UriFormatException) { return uri; }
    }

    private static bool IsSafePath(string s)
    {
        if (s.Length == 0) return false;
        foreach (char ch in s)
        {
            if (ch < 0x20 || ch == ':' || ch is '<' or '>' or '|' or '"' or '?' or '*') return false;
        }
        if (s[0] is '/' or '\\') return false;
        if (Path.IsPathRooted(s)) return false;
        foreach (string segment in s.Split('/', '\\'))
        {
            // "..", "...", ". ." and the like: Windows trims trailing dots and spaces, so any
            // all-dots segment longer than "." could climb a level.
            if (segment.Length > 1 && segment.Trim(' ', '.').Length == 0) return false;
        }
        return true;
    }

    // ---- container ----

    private static byte[] ReadLimited(string path, string modelName, long maxBytes)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > maxBytes || stream.Length > Array.MaxLength)
        {
            string file = Path.GetFileName(path);
            string usedBy = file == modelName ? "" : $" (used by '{modelName}')";
            throw new AssetFormatException($"'{file}'{usedBy} is larger than the {maxBytes:N0}-byte limit.");
        }
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static (ReadOnlyMemory<byte> Json, byte[]? Bin) SplitGlb(byte[] data, string fileName)
    {
        if (data.Length < 12)
            throw new AssetFormatException($"'{fileName}' is too short to be a GLB file ({data.Length} bytes).");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        if (version != 2)
            throw new AssetFormatException($"'{fileName}' is GLB version {version}; only version 2 is supported.");
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
        if (length != data.Length)
            throw new AssetFormatException($"'{fileName}' is damaged: its header says {length} bytes, but the file holds {data.Length}.");

        ReadOnlyMemory<byte>? json = null;
        byte[]? bin = null;
        int at = 12;
        int chunk = 0;
        while (at < data.Length)
        {
            if (data.Length - at < 8)
                throw new AssetFormatException($"'{fileName}' is damaged: it ends inside the header of chunk {chunk}.");
            uint chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
            uint chunkType = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 4));
            if (chunkLength > (uint)(data.Length - at - 8))
                throw new AssetFormatException($"'{fileName}' is damaged: chunk {chunk} claims {chunkLength} bytes, past the end of the file.");
            int body = at + 8;
            if (chunk == 0)
            {
                if (chunkType != ChunkJson)
                    throw new AssetFormatException($"'{fileName}' is damaged: its first chunk is not the JSON chunk (type 0x{chunkType:X8}).");
                // Some exporters pad with zeros instead of spaces; neither is part of the JSON.
                int end = body + (int)chunkLength;
                while (end > body && data[end - 1] is 0x20 or 0x00) end--;
                json = data.AsMemory(body, end - body);
            }
            else if (chunk == 1 && chunkType == ChunkBin)
            {
                bin = data.AsSpan(body, (int)chunkLength).ToArray();
            }
            // Unknown chunk types (and any later BIN) must be ignored per the spec.
            at = body + (int)chunkLength;
            chunk++;
        }
        if (json is null)
            throw new AssetFormatException($"'{fileName}' is damaged: it has no JSON chunk.");
        return (json.Value, bin);
    }

    private static JsonDocument ParseJson(ReadOnlyMemory<byte> json, string fileName)
    {
        var span = json.Span;
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF) json = json[3..];
        if (!System.Text.Unicode.Utf8.IsValid(json.Span))
            throw new AssetFormatException($"'{fileName}' is damaged: its JSON is not valid UTF-8 text.");
        try
        {
            return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
        }
        catch (JsonException ex)
        {
            throw new AssetFormatException($"'{fileName}' is damaged: its JSON is not valid ({ex.Message})", ex);
        }
        catch (ArgumentException ex)
        {
            throw new AssetFormatException($"'{fileName}' is damaged: its JSON is not valid ({ex.Message})", ex);
        }
    }

    // ---- buffers and images ----

    private static void ResolveBuffers(GltfDocument doc, Ctx ctx, byte[]? bin, Func<string, byte[]?>? resolve, GltfReadOptions options)
    {
        for (int i = 0; i < doc.Buffers.Count; i++)
        {
            var b = doc.Buffers[i];
            string who = $"buffer {i}";
            if (b.ByteLength < 0) throw ctx.Fail($"{who} has a negative byteLength ({b.ByteLength}).");
            if (b.ByteLength > options.MaxBytes)
                throw new AssetFormatException($"'{ctx.File}' declares {who} of {b.ByteLength:N0} bytes, larger than the {options.MaxBytes:N0}-byte limit.");
            byte[]? data;
            if (b.Uri is null)
            {
                if (i == 0 && bin is not null) data = bin;
                else if (b.Extensions is not null) continue; // e.g. a meshopt fallback buffer, filled by the extension
                else throw ctx.Fail($"{who} has no uri and there is no GLB binary chunk to supply it.");
            }
            else if (IsDataUri(b.Uri))
            {
                data = DecodeDataUri(b.Uri, out bool base64)
                    ?? throw ctx.Fail($"{who} has a data URI that is not valid base64.");
                if (!base64) throw ctx.Fail($"{who} has a data URI that is not base64-encoded.");
            }
            else
            {
                string path = SafePath(ctx, b.Uri);
                data = resolve?.Invoke(path)
                    ?? throw new AssetFormatException($"'{ctx.File}' needs the buffer file '{path}', which is missing.");
            }
            if (data.Length < b.ByteLength)
                throw ctx.Fail($"{who} should hold {b.ByteLength} bytes, but only {data.Length} are present.");
            b.Data = data.Length == b.ByteLength ? data : data.AsSpan(0, b.ByteLength).ToArray();
        }
    }

    private static void ResolveImages(GltfDocument doc, Ctx ctx, Func<string, byte[]?>? resolve, GltfReadOptions options)
    {
        for (int i = 0; i < doc.Images.Count; i++)
        {
            var img = doc.Images[i];
            if (img.BufferView is int view)
            {
                var (bytes, start, length, _) = GltfAccessorReader.ResolveView(doc, view, ctx.File, $"image {i}");
                img.Data = bytes.AsSpan(start, length).ToArray();
            }
            else if (img.Uri is null) { }
            else if (IsDataUri(img.Uri))
            {
                byte[]? decoded = DecodeDataUri(img.Uri, out bool base64);
                img.Data = base64 ? decoded : null;
            }
            else if (options.LoadImages)
            {
                string path = SafePath(ctx, img.Uri);
                img.Data = resolve?.Invoke(path);
            }
        }
    }

    private static string SafePath(Ctx ctx, string uri)
    {
        if (!IsSafeRelativeUri(uri))
            throw new AssetFormatException($"'{ctx.File}' refers to a file outside its folder ('{uri}'), which is not allowed.");
        return DecodeUri(uri);
    }

    internal static bool IsDataUri(string uri) => uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Decodes a data URI's payload; null when it claims base64 but is not valid base64.</summary>
    private static byte[]? DecodeDataUri(string uri, out bool base64)
    {
        int comma = uri.IndexOf(',', StringComparison.Ordinal);
        base64 = false;
        if (comma < 0) return null;
        string header = uri[5..comma];
        base64 = header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase);
        if (!base64) return null;
        string payload = uri[(comma + 1)..];
        var bytes = new byte[(payload.Length * 3 + 3) / 4];
        return Convert.TryFromBase64String(payload, bytes, out int written) ? bytes.AsSpan(0, written).ToArray() : null;
    }

    // ---- JSON object model ----

    /// <summary>Parsing context: the file name for messages.</summary>
    private sealed class Ctx(string file)
    {
        public string File { get; } = file;

        public AssetFormatException Fail(string what) => new($"'{File}' is damaged: {what}");

        public IEnumerable<JsonProperty> Members(JsonElement e, string path)
        {
            if (e.ValueKind != JsonValueKind.Object) throw Fail($"{path} should be a JSON object but is {Kind(e)}.");
            return e.EnumerateObject();
        }

        public IEnumerable<JsonElement> Items(JsonElement e, string path)
        {
            if (e.ValueKind != JsonValueKind.Array) throw Fail($"{path} should be an array but is {Kind(e)}.");
            return e.EnumerateArray();
        }

        public int Int(JsonElement e, string path)
        {
            if (e.ValueKind == JsonValueKind.Number)
            {
                if (e.TryGetInt32(out int v)) return v;
                // Exporters sometimes write whole numbers as 3.0.
                if (e.TryGetDouble(out double d) && d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue) return (int)d;
            }
            throw Fail($"{path} should be a whole number but is {Show(e)}.");
        }

        public int Index(JsonElement e, string path)
        {
            int v = Int(e, path);
            return v >= 0 ? v : throw Fail($"{path} should be an index (0 or more) but is {v}.");
        }

        public float Float(JsonElement e, string path)
        {
            if (e.ValueKind == JsonValueKind.Number && e.TryGetSingle(out float f) && float.IsFinite(f)) return f;
            throw Fail($"{path} should be a finite number but is {Show(e)}.");
        }

        public bool Bool(JsonElement e, string path) => e.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Fail($"{path} should be true or false but is {Show(e)}."),
        };

        public string String(JsonElement e, string path) =>
            e.ValueKind == JsonValueKind.String ? e.GetString()! : throw Fail($"{path} should be a string but is {Show(e)}.");

        public float[] Floats(JsonElement e, string path, int? exact = null)
        {
            var list = new List<float>();
            int i = 0;
            foreach (var item in Items(e, path)) list.Add(Float(item, $"{path}[{i++}]"));
            if (exact is int n && list.Count != n) throw Fail($"{path} should hold {n} numbers but holds {list.Count}.");
            return [.. list];
        }

        public void Indices(JsonElement e, string path, List<int> into)
        {
            int i = 0;
            foreach (var item in Items(e, path)) into.Add(Index(item, $"{path}[{i++}]"));
        }

        public void Strings(JsonElement e, string path, List<string> into)
        {
            int i = 0;
            foreach (var item in Items(e, path)) into.Add(String(item, $"{path}[{i++}]"));
        }

        public Dictionary<string, int> AttributeMap(JsonElement e, string path, Dictionary<string, int>? into = null)
        {
            into ??= new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var p in Members(e, path)) into[p.Name] = Index(p.Value, $"{path}.{p.Name}");
            return into;
        }

        /// <summary>Handles name, extras, extensions and unknown members, common to every object.</summary>
        public void Common(GltfProperty obj, JsonProperty p, string path)
        {
            switch (p.Name)
            {
                case "extras":
                    obj.Extras = ToNode(p.Value);
                    break;
                case "extensions":
                    if (p.Value.ValueKind != JsonValueKind.Object)
                        throw Fail($"{path}.extensions should be a JSON object but is {Kind(p.Value)}.");
                    obj.Extensions = (JsonObject)ToNode(p.Value)!;
                    break;
                case "name" when obj is GltfChildOfRoot child:
                    child.Name = String(p.Value, $"{path}.name");
                    break;
                default:
                    obj.UnknownMembers[p.Name] = ToNode(p.Value);
                    break;
            }
        }

        public AssetFormatException Missing(string path, string member) => Fail($"{path} is missing its required '{member}'.");

        private static string Kind(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.Object => "an object",
            JsonValueKind.Array => "an array",
            JsonValueKind.String => "a string",
            JsonValueKind.Number => "a number",
            JsonValueKind.True or JsonValueKind.False => "a boolean",
            _ => "null",
        };

        private static string Show(JsonElement e)
        {
            if (e.ValueKind is JsonValueKind.Object or JsonValueKind.Array) return Kind(e);
            string raw = e.GetRawText();
            return raw.Length > 40 ? raw[..40] + "..." : raw;
        }
    }

    /// <summary>
    /// Copies a JSON value into a standalone node tree. Built by hand (rather than JsonNode.Parse)
    /// so duplicate keys resolve to the last value instead of throwing later, and numbers keep their
    /// exact text.
    /// </summary>
    internal static JsonNode? ToNode(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var p in e.EnumerateObject()) obj[p.Name] = ToNode(p.Value);
                return obj;
            case JsonValueKind.Array:
                var arr = new JsonArray();
                foreach (var item in e.EnumerateArray()) arr.Add(ToNode(item));
                return arr;
            case JsonValueKind.String:
                return JsonValue.Create(e.GetString());
            case JsonValueKind.Number:
                return JsonValue.Create(e.Clone());
            case JsonValueKind.True:
                return JsonValue.Create(true);
            case JsonValueKind.False:
                return JsonValue.Create(false);
            default:
                return null;
        }
    }

    private static GltfDocument ParseDocument(Ctx c, JsonElement root)
    {
        var doc = new GltfDocument();
        bool hasAsset = false;
        foreach (var p in c.Members(root, "the top-level JSON"))
        {
            string path = p.Name;
            switch (p.Name)
            {
                case "asset": doc.Asset = ParseAsset(c, p.Value); hasAsset = true; break;
                case "scene": doc.Scene = c.Index(p.Value, path); break;
                case "scenes": doc.Scenes.Clear(); doc.Scenes.AddRange(List(c, p.Value, path, ParseScene)); break;
                case "nodes": doc.Nodes.Clear(); doc.Nodes.AddRange(List(c, p.Value, path, ParseNode)); break;
                case "meshes": doc.Meshes.Clear(); doc.Meshes.AddRange(List(c, p.Value, path, ParseMesh)); break;
                case "skins": doc.Skins.Clear(); doc.Skins.AddRange(List(c, p.Value, path, ParseSkin)); break;
                case "materials": doc.Materials.Clear(); doc.Materials.AddRange(List(c, p.Value, path, ParseMaterial)); break;
                case "textures": doc.Textures.Clear(); doc.Textures.AddRange(List(c, p.Value, path, ParseTexture)); break;
                case "images": doc.Images.Clear(); doc.Images.AddRange(List(c, p.Value, path, ParseImage)); break;
                case "samplers": doc.Samplers.Clear(); doc.Samplers.AddRange(List(c, p.Value, path, ParseSampler)); break;
                case "animations": doc.Animations.Clear(); doc.Animations.AddRange(List(c, p.Value, path, ParseAnimation)); break;
                case "accessors": doc.Accessors.Clear(); doc.Accessors.AddRange(List(c, p.Value, path, ParseAccessor)); break;
                case "bufferViews": doc.BufferViews.Clear(); doc.BufferViews.AddRange(List(c, p.Value, path, ParseBufferView)); break;
                case "buffers": doc.Buffers.Clear(); doc.Buffers.AddRange(List(c, p.Value, path, ParseBuffer)); break;
                case "cameras":
                    doc.Cameras.Clear();
                    foreach (var item in c.Items(p.Value, path)) doc.Cameras.Add(ToNode(item));
                    break;
                case "extensionsUsed": doc.ExtensionsUsed.Clear(); c.Strings(p.Value, path, doc.ExtensionsUsed); break;
                case "extensionsRequired": doc.ExtensionsRequired.Clear(); c.Strings(p.Value, path, doc.ExtensionsRequired); break;
                default: c.Common(doc, p, "the document"); break;
            }
        }
        if (!hasAsset) throw c.Fail("it has no 'asset' object, so it is not a glTF file.");
        return doc;
    }

    private static List<T> List<T>(Ctx c, JsonElement e, string path, Func<Ctx, JsonElement, string, T> parse)
    {
        var list = new List<T>();
        foreach (var item in c.Items(e, path)) list.Add(parse(c, item, $"{path}[{list.Count.ToString(CultureInfo.InvariantCulture)}]"));
        return list;
    }

    private static GltfAsset ParseAsset(Ctx c, JsonElement e)
    {
        var a = new GltfAsset();
        bool version = false;
        foreach (var p in c.Members(e, "asset"))
        {
            switch (p.Name)
            {
                case "version": a.Version = c.String(p.Value, "asset.version"); version = true; break;
                case "generator": a.Generator = c.String(p.Value, "asset.generator"); break;
                case "copyright": a.Copyright = c.String(p.Value, "asset.copyright"); break;
                case "minVersion": a.MinVersion = c.String(p.Value, "asset.minVersion"); break;
                default: c.Common(a, p, "asset"); break;
            }
        }
        if (!version) throw c.Missing("asset", "version");
        if (!a.Version.StartsWith("2.", StringComparison.Ordinal))
            throw new AssetFormatException($"'{c.File}' is glTF version {a.Version}; only glTF 2.x is supported.");
        return a;
    }

    private static GltfScene ParseScene(Ctx c, JsonElement e, string path)
    {
        var s = new GltfScene();
        foreach (var p in c.Members(e, path))
        {
            if (p.Name == "nodes") c.Indices(p.Value, $"{path}.nodes", s.Nodes);
            else c.Common(s, p, path);
        }
        return s;
    }

    private static GltfNode ParseNode(Ctx c, JsonElement e, string path)
    {
        var n = new GltfNode();
        foreach (var p in c.Members(e, path))
        {
            string at = $"{path}.{p.Name}";
            switch (p.Name)
            {
                case "children": c.Indices(p.Value, at, n.Children); break;
                case "mesh": n.Mesh = c.Index(p.Value, at); break;
                case "skin": n.Skin = c.Index(p.Value, at); break;
                case "camera": n.Camera = c.Index(p.Value, at); break;
                case "matrix": n.Matrix = c.Floats(p.Value, at, 16); break;
                case "translation": var t = c.Floats(p.Value, at, 3); n.Translation = new Vector3(t[0], t[1], t[2]); break;
                case "rotation": var r = c.Floats(p.Value, at, 4); n.Rotation = new Quaternion(r[0], r[1], r[2], r[3]); break;
                case "scale": var s = c.Floats(p.Value, at, 3); n.Scale = new Vector3(s[0], s[1], s[2]); break;
                case "weights": n.Weights = c.Floats(p.Value, at); break;
                default: c.Common(n, p, path); break;
            }
        }
        return n;
    }

    private static GltfMesh ParseMesh(Ctx c, JsonElement e, string path)
    {
        var m = new GltfMesh();
        bool primitives = false;
        foreach (var p in c.Members(e, path))
        {
            string at = $"{path}.{p.Name}";
            switch (p.Name)
            {
                case "primitives": m.Primitives.Clear(); m.Primitives.AddRange(List(c, p.Value, at, ParsePrimitive)); primitives = true; break;
                case "weights": m.Weights = c.Floats(p.Value, at); break;
                default: c.Common(m, p, path); break;
            }
        }
        if (!primitives) throw c.Missing(path, "primitives");
        return m;
    }

    private static GltfPrimitive ParsePrimitive(Ctx c, JsonElement e, string path)
    {
        var pr = new GltfPrimitive();
        bool attributes = false;
        foreach (var p in c.Members(e, path))
        {
            string at = $"{path}.{p.Name}";
            switch (p.Name)
            {
                case "attributes": pr.Attributes.Clear(); c.AttributeMap(p.Value, at, pr.Attributes); attributes = true; break;
                case "indices": pr.Indices = c.Index(p.Value, at); break;
                case "material": pr.Material = c.Index(p.Value, at); break;
                case "mode": pr.Mode = c.Int(p.Value, at); break;
                case "targets":
                    pr.Targets.Clear();
                    int i = 0;
                    foreach (var item in c.Items(p.Value, at)) pr.Targets.Add(c.AttributeMap(item, $"{at}[{i++}]"));
                    break;
                default: c.Common(pr, p, path); break;
            }
        }
        if (!attributes) throw c.Missing(path, "attributes");
        return pr;
    }

    private static GltfSkin ParseSkin(Ctx c, JsonElement e, string path)
    {
        var s = new GltfSkin();
        bool joints = false;
        foreach (var p in c.Members(e, path))
        {
            string at = $"{path}.{p.Name}";
            switch (p.Name)
            {
                case "inverseBindMatrices": s.InverseBindMatrices = c.Index(p.Value, at); break;
                case "skeleton": s.Skeleton = c.Index(p.Value, at); break;
                case "joints": s.Joints.Clear(); c.Indices(p.Value, at, s.Joints); joints = true; break;
                default: c.Common(s, p, path); break;
            }
        }
        if (!joints) throw c.Missing(path, "joints");
        return s;
    }

    private static GltfTextureInfo ParseTextureInfo(Ctx c, JsonElement e, string path)
    {
        var t = new GltfTextureInfo();
        bool index = false;
        foreach (var p in c.Members(e, path))
        {
            string at = $"{path}.{p.Name}";
            switch (p.Name)
            {
                case "index": t.Index = c.Index(p.Value, at); index = true; break;
                case "texCoord": t.TexCoord = c.Index(p.Value, at); break;
                case "scale": t.Scale = c.Float(p.Value, at); break;
                case "strength": t.Strength = c.Float(p.Value, at); break;
                default: c.Common(t, p, path); break;
            }
        }
        if (!index) throw c.Missing(path, "index");
        return t;
    }

    private static GltfPbrMetallicRoughness ParsePbr(Ctx c, JsonElement e, string path)
    {
        var m = new GltfPbrMetallicRoughness();
        foreach (var p in c.Members(e, path))
        {
            string at = $"{path}.{p.Name}";
            switch (p.Name)
            {
                case "baseColorFactor": m.BaseColorFactor = c.Floats(p.Value, at, 4); break;
                case "baseColorTexture": m.BaseColorTexture = ParseTextureInfo(c, p.Value, at); break;
                case "metallicFactor": m.MetallicFactor = c.Float(p.Value, at); break;
                case "roughnessFactor": m.RoughnessFactor = c.Float(p.Value, at); break;
                case "metallicRoughnessTexture": m.MetallicRoughnessTexture = ParseTextureInfo(c, p.Value, at); break;
                default: c.Common(m, p, path); break;
            }
        }
        return m;
    }

    private static GltfMaterial ParseMaterial(Ctx c, JsonElement e, string path)
    {
        var m = new GltfMaterial();
        foreach (var p in c.Members(e, path))
        {
            string at = $"{path}.{p.Name}";
            switch (p.Name)
            {
                case "pbrMetallicRoughness": m.PbrMetallicRoughness = ParsePbr(c, p.Value, at); break;
                case "normalTexture": m.NormalTexture = ParseTextureInfo(c, p.Value, at); break;
                case "occlusionTexture": m.OcclusionTexture = ParseTextureInfo(c, p.Value, at); break;
                case "emissiveTexture": m.EmissiveTexture = ParseTextureInfo(c, p.Value, at); break;
                case "emissiveFactor": m.EmissiveFactor = c.Floats(p.Value, at, 3); break;
                case "alphaMode": m.AlphaMode = c.String(p.Value, at); break;
                case "alphaCutoff": m.AlphaCutoff = c.Float(p.Value, at); break;
                case "doubleSided": m.DoubleSided = c.Bool(p.Value, at); break;
                default: c.Common(m, p, path); break;
            }
        }
        return m;
    }

    private static GltfTexture ParseTexture(Ctx c, JsonElement e, string path)
    {
        var t = new GltfTexture();
        foreach (var p in c.Members(e, path))
        {
            string at = $"{path}.{p.Name}";
            switch (p.Name)
            {
                case "sampler": t.Sampler = c.Index(p.Value, at); break;
                case "source": t.Source = c.Index(p.Value, at); break;
                default: c.Common(t, p, path); break;
            }
        }
        return t;
    }

    private static GltfImage ParseImage(Ctx c, JsonElement e, string path)
    {
        var img = new GltfImage();
        foreach (var p in c.Members(e, path))
        {
            string at = $"{path}.{p.Name}";
            switch (p.Name)
            {
                case "uri": img.Uri = c.String(p.Value, at); break;
                case "bufferView": img.BufferView = c.Index(p.Value, at); break;
                case "mimeType": img.MimeType = c.String(p.Value, at); break;
                default: c.Common(img, p, path); break;
            }
        }
        return img;
    }

    private static GltfSampler ParseSampler(Ctx c, JsonElement e, string path)
    {
        var s = new GltfSampler();
        foreach (var p in c.Members(e, path))
        {
            string at = $"{path}.{p.Name}";
            switch (p.Name)
            {
                case "magFilter": s.MagFilter = c.Int(p.Value, at); break;
                case "minFilter": s.MinFilter = c.Int(p.Value, at); break;
                case "wrapS": s.WrapS = c.Int(p.Value, at); break;
                case "wrapT": s.WrapT = c.Int(p.Value, at); break;
                default: c.Common(s, p, path); break;
            }
        }
        return s;
    }

    private static GltfAnimation ParseAnimation(Ctx c, JsonElement e, string path)
    {
        var a = new GltfAnimation();
        bool channels = false, samplers = false;
        foreach (var p in c.Members(e, path))
        {
            string at = $"{path}.{p.Name}";
            switch (p.Name)
            {
                case "channels": a.Channels.Clear(); a.Channels.AddRange(List(c, p.Value, at, ParseChannel)); channels = true; break;
                case "samplers": a.Samplers.Clear(); a.Samplers.AddRange(List(c, p.Value, at, ParseAnimationSampler)); samplers = true; break;
                default: c.Common(a, p, path); break;
            }
        }
        if (!channels) throw c.Missing(path, "channels");
        if (!samplers) throw c.Missing(path, "samplers");
        return a;
    }

    private static GltfAnimationChannel ParseChannel(Ctx c, JsonElement e, string path)
    {
        var ch = new GltfAnimationChannel();
        bool sampler = false, target = false;
        foreach (var p in c.Members(e, path))
        {
            string at = $"{path}.{p.Name}";
            switch (p.Name)
            {
                case "sampler": ch.Sampler = c.Index(p.Value, at); sampler = true; break;
                case "target": ch.Target = ParseTarget(c, p.Value, at); target = true; break;
                default: c.Common(ch, p, path); break;
            }
        }
        if (!sampler) throw c.Missing(path, "sampler");
        if (!target) throw c.Missing(path, "target");
        return ch;
    }

    private static GltfAnimationTarget ParseTarget(Ctx c, JsonElement e, string path)
    {
        var t = new GltfAnimationTarget();
        bool hasPath = false;
        foreach (var p in c.Members(e, path))
        {
            string at = $"{path}.{p.Name}";
            switch (p.Name)
            {
                case "node": t.Node = c.Index(p.Value, at); break;
                case "path": t.Path = c.String(p.Value, at); hasPath = true; break;
                default: c.Common(t, p, path); break;
            }
        }
        if (!hasPath) throw c.Missing(path, "path");
        return t;
    }

    private static GltfAnimationSampler ParseAnimationSampler(Ctx c, JsonElement e, string path)
    {
        var s = new GltfAnimationSampler();
        bool input = false, output = false;
        foreach (var p in c.Members(e, path))
        {
            string at = $"{path}.{p.Name}";
            switch (p.Name)
            {
                case "input": s.Input = c.Index(p.Value, at); input = true; break;
                case "output": s.Output = c.Index(p.Value, at); output = true; break;
                case "interpolation": s.Interpolation = c.String(p.Value, at); break;
                default: c.Common(s, p, path); break;
            }
        }
        if (!input) throw c.Missing(path, "input");
        if (!output) throw c.Missing(path, "output");
        return s;
    }

    private static GltfAccessor ParseAccessor(Ctx c, JsonElement e, string path)
    {
        var a = new GltfAccessor();
        bool componentType = false, count = false, type = false;
        foreach (var p in c.Members(e, path))
        {
            string at = $"{path}.{p.Name}";
            switch (p.Name)
            {
                case "bufferView": a.BufferView = c.Index(p.Value, at); break;
                case "byteOffset": a.ByteOffset = c.Index(p.Value, at); break;
                case "componentType": a.ComponentType = c.Int(p.Value, at); componentType = true; break;
                case "normalized": a.Normalized = c.Bool(p.Value, at); break;
                case "count": a.Count = c.Index(p.Value, at); count = true; break;
                case "type": a.Type = c.String(p.Value, at); type = true; break;
                case "min": a.Min = c.Floats(p.Value, at); break;
                case "max": a.Max = c.Floats(p.Value, at); break;
                case "sparse": a.Sparse = ParseSparse(c, p.Value, at); break;
                default: c.Common(a, p, path); break;
            }
        }
        if (!componentType) throw c.Missing(path, "componentType");
        if (!count) throw c.Missing(path, "count");
        if (!type) throw c.Missing(path, "type");
        return a;
    }

    private static GltfSparse ParseSparse(Ctx c, JsonElement e, string path)
    {
        var s = new GltfSparse();
        bool count = false, indices = false, values = false;
        foreach (var p in c.Members(e, path))
        {
            string at = $"{path}.{p.Name}";
            switch (p.Name)
            {
                case "count": s.Count = c.Index(p.Value, at); count = true; break;
                case "indices": s.Indices = ParseSparseIndices(c, p.Value, at); indices = true; break;
                case "values": s.Values = ParseSparseValues(c, p.Value, at); values = true; break;
                default: c.Common(s, p, path); break;
            }
        }
        if (!count) throw c.Missing(path, "count");
        if (!indices) throw c.Missing(path, "indices");
        if (!values) throw c.Missing(path, "values");
        return s;
    }

    private static GltfSparseIndices ParseSparseIndices(Ctx c, JsonElement e, string path)
    {
        var s = new GltfSparseIndices();
        bool view = false, componentType = false;
        foreach (var p in c.Members(e, path))
        {
            string at = $"{path}.{p.Name}";
            switch (p.Name)
            {
                case "bufferView": s.BufferView = c.Index(p.Value, at); view = true; break;
                case "byteOffset": s.ByteOffset = c.Index(p.Value, at); break;
                case "componentType": s.ComponentType = c.Int(p.Value, at); componentType = true; break;
                default: c.Common(s, p, path); break;
            }
        }
        if (!view) throw c.Missing(path, "bufferView");
        if (!componentType) throw c.Missing(path, "componentType");
        return s;
    }

    private static GltfSparseValues ParseSparseValues(Ctx c, JsonElement e, string path)
    {
        var s = new GltfSparseValues();
        bool view = false;
        foreach (var p in c.Members(e, path))
        {
            string at = $"{path}.{p.Name}";
            switch (p.Name)
            {
                case "bufferView": s.BufferView = c.Index(p.Value, at); view = true; break;
                case "byteOffset": s.ByteOffset = c.Index(p.Value, at); break;
                default: c.Common(s, p, path); break;
            }
        }
        if (!view) throw c.Missing(path, "bufferView");
        return s;
    }

    private static GltfBufferView ParseBufferView(Ctx c, JsonElement e, string path)
    {
        var v = new GltfBufferView();
        bool buffer = false, length = false;
        foreach (var p in c.Members(e, path))
        {
            string at = $"{path}.{p.Name}";
            switch (p.Name)
            {
                case "buffer": v.Buffer = c.Index(p.Value, at); buffer = true; break;
                case "byteOffset": v.ByteOffset = c.Index(p.Value, at); break;
                case "byteLength": v.ByteLength = c.Index(p.Value, at); length = true; break;
                case "byteStride": v.ByteStride = c.Int(p.Value, at); break;
                case "target": v.Target = c.Int(p.Value, at); break;
                default: c.Common(v, p, path); break;
            }
        }
        if (!buffer) throw c.Missing(path, "buffer");
        if (!length) throw c.Missing(path, "byteLength");
        return v;
    }

    private static GltfBuffer ParseBuffer(Ctx c, JsonElement e, string path)
    {
        var b = new GltfBuffer();
        bool length = false;
        foreach (var p in c.Members(e, path))
        {
            string at = $"{path}.{p.Name}";
            switch (p.Name)
            {
                case "uri": b.Uri = c.String(p.Value, at); break;
                case "byteLength": b.ByteLength = c.Index(p.Value, at); length = true; break;
                default: c.Common(b, p, path); break;
            }
        }
        if (!length) throw c.Missing(path, "byteLength");
        return b;
    }
}
