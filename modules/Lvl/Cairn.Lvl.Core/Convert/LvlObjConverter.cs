using System.Globalization;
using System.Text;
using Cairn.Formats.Obj;
using Cairn.Lvl.Model;
using Cairn.Rfa.Interchange;

namespace Cairn.Lvl.Conversion;

/// <summary>
/// Cairn's metadata lines in level OBJ files (<c># rf_... key=value ...</c>), which only Cairn reads back. Values
/// are invariant-culture numbers, <c>0x</c> hex for flags, comma-separated vectors in RF space, and quoted text with
/// <c>\"</c> and <c>\\</c> escapes. Every line before a face applies to that face and the faces after it in the same
/// object, until the next <c>rf_face</c> line.
/// </summary>
public static class LvlObjMetadata
{
    /// <summary>Header: <c>rf_level name= version= geometry=compiled|brushes</c> (or <c>rf_group_file</c>).</summary>
    public const string Level = "rf_level";
    public const string GroupFile = "rf_group_file";
    /// <summary>Object: <c>rf_brush uid= solid=0x.. life= state= position=x,y,z orientation=9 numbers transform=applied group="name"</c>.</summary>
    public const string Brush = "rf_brush";
    /// <summary>Object: <c>rf_mover</c> with the same keys as <see cref="Brush"/>.</summary>
    public const string Mover = "rf_mover";
    /// <summary>Object: <c>rf_room index= sky= liquid=</c>.</summary>
    public const string Room = "rf_room";
    /// <summary>Before a face: <c>rf_face flags=0x.. smoothing=0x.. [scroll=u,v] [raw=0x..]</c>.</summary>
    public const string Face = "rf_face";

    /// <summary>A metadata line from a keyword and its pairs.</summary>
    public static string Line(string keyword, params (string Key, string Value)[] pairs)
    {
        var sb = new StringBuilder(keyword);
        foreach (var (k, v) in pairs) sb.Append(' ').Append(k).Append('=').Append(v);
        return sb.ToString();
    }

    public static string Number(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    public static string Number(int v) => v.ToString(CultureInfo.InvariantCulture);

    public static string Hex(uint v) => "0x" + v.ToString("X", CultureInfo.InvariantCulture);

    public static string Vector(params float[] values) => string.Join(",", values.Select(Number));

    /// <summary>Quoted text: <c>\</c> and <c>"</c> escaped, and line breaks as <c>\r</c> / <c>\n</c> so the comment stays on one line.</summary>
    public static string Text(string s) => "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

    /// <summary>Parses a line written by <see cref="Line"/>: the keyword and its pairs (quoted text unescaped). Null when it is not one.</summary>
    public static (string Keyword, Dictionary<string, string> Values)? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.StartsWith("rf_", StringComparison.Ordinal)) return null;
        int i = 0;
        while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
        string keyword = text[..i];
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            int keyStart = i;
            while (i < text.Length && text[i] != '=' && !char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length || text[i] != '=') break;
            string key = text[keyStart..i];
            i++;
            var value = new StringBuilder();
            if (i < text.Length && text[i] == '"')
            {
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        i++;
                        value.Append(text[i] switch { 'r' => '\r', 'n' => '\n', char c => c });
                        i++;
                        continue;
                    }
                    value.Append(text[i++]);
                }
                i++;
            }
            else
            {
                while (i < text.Length && !char.IsWhiteSpace(text[i])) value.Append(text[i++]);
            }
            values[key] = value.ToString();
        }
        return (keyword, values);
    }
}

/// <summary>
/// Level/group → OBJ: an <c>o</c> per brush, room or mover, a <c>usemtl</c> per (texture, render state), faces kept as
/// polygons (or triangles), positions mirrored on X into OBJ's right-handed space with the winding turned over, V
/// flipped, and Cairn's <c># rf_</c> lines for what OBJ cannot hold.
/// </summary>
internal sealed class LvlObjConverter(LvlScene scene, LvlConvertOptions options, LvlTextureSet textures, LvlReportBuilder report, CancellationToken ct)
{
    private readonly ObjDocument _obj = new();
    private readonly MtlDocument _mtl = new();
    private readonly Dictionary<(string Texture, string? Alpha, bool Unlit), string> _materials = [];
    private readonly HashSet<string> _materialNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Vector3, int> _positions = [];
    private readonly Dictionary<Vector2, int> _uvs = [];
    private readonly Dictionary<Vector3, int> _normals = [];
    private int _triangulated, _scrolling, _lightmapped, _fullBright, _transparent;

    public (ObjDocument Obj, MtlDocument Mtl) Run()
    {
        bool meta = options.ObjMetadata;
        _obj.Comments.Add(new ObjComment($"Cairn OBJ export of {scene.Name} ({(scene.Geometry == LvlGeometryChoice.Compiled ? "compiled geometry" : "brushes")})"));
        _obj.Comments.Add(new ObjComment("Metres, Y up; X is mirrored from Red Faction's left-handed space and V is flipped (OBJ convention)."));
        if (meta)
        {
            _obj.Comments.Add(new ObjComment("The rf_ lines are Cairn's own metadata for importing back; other tools ignore them."));
            int version = scene.Level?.Version ?? scene.GroupFile?.Version ?? 0;
            _obj.Comments.Add(new ObjComment(LvlObjMetadata.Line(scene.Level is not null ? LvlObjMetadata.Level : LvlObjMetadata.GroupFile,
                ("name", LvlObjMetadata.Text(scene.Name)), ("version", LvlObjMetadata.Number(version)),
                ("geometry", scene.Geometry == LvlGeometryChoice.Compiled ? "compiled" : "brushes"))));
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in scene.Parts)
        {
            ct.ThrowIfCancellationRequested();
            AddPart(part, names, meta);
        }
        Summarise(meta);
        return (_obj, _mtl);
    }

    private void AddPart(LvlPart part, HashSet<string> names, bool meta)
    {
        string name = part.Name;
        for (int i = 2; !names.Add(name); i++) name = $"{part.Name}_{i}";
        int objectIndex = _obj.Objects.Count;
        var obj = new ObjObject(name);
        _obj.Objects.Add(obj);
        if (meta) _obj.Comments.Add(new ObjComment(PartLine(part), objectIndex, 0));

        (uint Flags, uint Smoothing, Vector2? Scroll, uint Raw)? last = null;
        foreach (var polygon in part.Polygons)
        {
            var f = polygon.Face;
            var state = ((uint)f.Flags, f.Smoothing, f.Scroll, f.RawFlags);
            if (meta && last != state)
            {
                var pairs = new List<(string, string)> { ("flags", LvlObjMetadata.Hex((uint)f.Flags)), ("smoothing", LvlObjMetadata.Hex(f.Smoothing)) };
                if (f.Scroll is { } s) pairs.Add(("scroll", LvlObjMetadata.Vector(s.X, s.Y)));
                if (f.RawFlags != (uint)f.Flags) pairs.Add(("raw", LvlObjMetadata.Hex(f.RawFlags)));
                _obj.Comments.Add(new ObjComment(LvlObjMetadata.Line(LvlObjMetadata.Face, [.. pairs]), objectIndex, obj.Faces.Count));
                last = state;
            }
            if (f.Scroll is not null) _scrolling++;
            if (polygon.Corners[0].LightmapUv is not null) _lightmapped++;
            string material = Material(polygon);
            var corners = polygon.Corners;
            if (options.ObjTriangulate && corners.Length > 3)
            {
                var tris = LvlGeometryHelpers.Triangulate(polygon);
                _triangulated++;
                foreach (var (a, b, c) in tris) AddFace(obj, material, corners[a], corners[c], corners[b]);
                report.Triangles += tris.Count;
            }
            else
            {
                // The X mirror turns the winding over: the corners go in reverse order.
                var reversed = new LvlCorner[corners.Length];
                for (int k = 0; k < corners.Length; k++) reversed[k] = corners[corners.Length - 1 - k];
                AddFace(obj, material, reversed);
                report.Triangles += corners.Length - 2;
            }
            report.Faces++;
        }
        report.Parts++;
    }

    private void AddFace(ObjObject obj, string material, params LvlCorner[] corners)
    {
        var face = new ObjFace { Material = material };
        foreach (var c in corners)
        {
            int p = Pool(_positions, _obj.Positions, GltfSpace.ToGltf(c.Position));
            int t = Pool(_uvs, _obj.TexCoords, new Vector2(c.Uv.X, 1f - c.Uv.Y));
            int n = Pool(_normals, _obj.Normals, GltfSpace.ToGltf(c.Normal));
            face.Corners.Add(new ObjCorner(p, t, n));
        }
        obj.Faces.Add(face);
    }

    private static int Pool<T>(Dictionary<T, int> map, List<T> list, T value) where T : notnull
    {
        if (map.TryGetValue(value, out int index)) return index;
        index = list.Count;
        list.Add(value);
        map[value] = index;
        return index;
    }

    private string PartLine(LvlPart part)
    {
        if (part.Kind == LvlPartKind.Room)
        {
            var room = part.Room;
            return LvlObjMetadata.Line(LvlObjMetadata.Room, ("index", LvlObjMetadata.Number(part.RoomIndex)),
                ("sky", room?.IsSky == true ? "1" : "0"), ("liquid", room?.IsLiquid == true ? "1" : "0"));
        }
        var b = part.Brush!;
        var m = b.Orientation;
        var pairs = new List<(string, string)>
        {
            ("uid", LvlObjMetadata.Number(b.Uid)),
            ("solid", LvlObjMetadata.Hex((uint)b.SolidFlags)),
            ("life", LvlObjMetadata.Number(b.Life)),
            ("state", LvlObjMetadata.Number(b.State)),
            ("position", LvlObjMetadata.Vector(b.Position.X, b.Position.Y, b.Position.Z)),
            ("orientation", LvlObjMetadata.Vector(m.Right.X, m.Right.Y, m.Right.Z, m.Up.X, m.Up.Y, m.Up.Z, m.Forward.X, m.Forward.Y, m.Forward.Z)),
            ("transform", "applied"),
        };
        if (b.Alpine is { } alpine)
        {
            pairs.Add(("alpine_flags", LvlObjMetadata.Hex((uint)alpine.Flags)));
            pairs.Add(("alpine_material", LvlObjMetadata.Number(alpine.Material)));
        }
        if (part.Group >= 0 && part.Group < scene.Groups.Count) pairs.Add(("group", LvlObjMetadata.Text(scene.Groups[part.Group].Name)));
        return LvlObjMetadata.Line(part.Kind == LvlPartKind.Mover ? LvlObjMetadata.Mover : LvlObjMetadata.Brush, [.. pairs]);
    }

    private string Material(LvlPolygon polygon)
    {
        string texture = polygon.Texture;
        _usedTextures.Add(texture);
        var image = textures.Get(texture);
        bool unlit = (polygon.Classes & RfFaceClass.FullBright) != 0;
        string? alpha = (polygon.Classes & RfFaceClass.Alpha) != 0 ? "blend" : (polygon.Classes & RfFaceClass.Hole) != 0 || image?.HasAlpha == true ? "mask" : null;
        if (unlit) _fullBright++;
        if ((polygon.Classes & (RfFaceClass.Alpha | RfFaceClass.Hole)) != 0) _transparent++;
        var key = (texture.ToLowerInvariant(), alpha, unlit);
        if (_materials.TryGetValue(key, out var existing)) return existing;

        string baseName = texture.Length == 0 ? "untextured" : MaterialName(texture);
        if (alpha is not null) baseName += "_" + alpha;
        if (unlit) baseName += "_fullbright";
        string name = baseName;
        for (int i = 2; !_materialNames.Add(name); i++) name = $"{baseName}_{i}";
        var mat = new MtlMaterial(name)
        {
            Ambient = Vector3.One,
            Diffuse = Vector3.One,
            Specular = Vector3.Zero,
            Dissolve = 1f,
            // illum 0 is "colour on, ambient off": the nearest MTL has to full-bright.
            Illumination = unlit ? 0 : 1,
        };
        // Level strings can hold any byte; a control character in the RF name would break the map_Kd line (the
        // rf_texture line keeps the real name).
        if (texture.Length > 0) mat.DiffuseMap = new MtlTextureMap(image?.FileName ?? new string([.. texture.Select(c => char.IsControl(c) ? '_' : c)]));
        if (image is { HasAlpha: true } && alpha is not null) mat.OtherStatements.Add(new("map_d", image.FileName));
        _mtl.Materials.Add(mat);
        // An empty name marks the untextured material, so reading back gives no texture rather than a guess.
        if (options.ObjMetadata) _mtl.Comments.Add(new MtlComment($"rf_texture name={LvlObjMetadata.Text(texture)}", _mtl.Materials.Count - 1));
        _materials[key] = name;
        report.Materials++;
        return name;
    }

    private static string MaterialName(string texture)
    {
        string stem = Path.GetFileNameWithoutExtension(texture.Replace('\\', '/'));
        var chars = stem.Select(c => char.IsWhiteSpace(c) || char.IsControl(c) ? '_' : c).ToArray();
        string name = new string(chars).Trim();
        return name.Length == 0 ? "material" : name;
    }

    private void Summarise(bool meta)
    {
        textures.Summarise(report, _usedTextures);
        report.Vertices = _obj.Positions.Count;
        if (_triangulated > 0)
            report.Info(LvlConvertCodes.Triangulated, $"{LvlReportBuilder.Count(_triangulated, "polygon")} were written as triangles.", LvlIssueKind.Split);
        else
            report.Info(LvlConvertCodes.Triangulated, "Faces are written as polygons, as Red Faction stores them.");
        if (meta)
            report.Info(LvlConvertCodes.ObjMetadata, "Face flags, smoothing groups, scrolling and brush data (solid flags, life, state, transform) are kept in '# rf_' comment lines that only Cairn reads back; other tools drop them.", LvlIssueKind.Approximated);
        else
            report.Info(LvlConvertCodes.FaceFlags, "Face flags, smoothing groups, scrolling and brush data are not written (metadata lines are off).", LvlIssueKind.Dropped);
        report.Info(LvlConvertCodes.SmoothingNormals, "Smoothing is written as vertex normals (OBJ smoothing groups hold only one group per face).", LvlIssueKind.Approximated);
        if (_scrolling > 0 && !meta)
            report.Info(LvlConvertCodes.Scroll, $"{LvlReportBuilder.Count(_scrolling, "face")} scroll their texture; OBJ cannot, so the speed is dropped.", LvlIssueKind.Dropped);
        if (_lightmapped > 0)
            report.Info(LvlConvertCodes.Lightmaps, "Lightmap coordinates and images are not exported (OBJ has one set of texture coordinates).", LvlIssueKind.Dropped);
        if (_fullBright > 0)
            report.Info(LvlConvertCodes.FullBright, $"{LvlReportBuilder.Count(_fullBright, "full-bright face")} use materials with 'illum 0'; they stay one-sided.", LvlIssueKind.Approximated);
        if (_transparent > 0)
            report.Info(LvlConvertCodes.Transparency, $"{LvlReportBuilder.Count(_transparent, "face")} with alpha or holes use materials named _blend or _mask; the .mtl names the alpha map where the texture has one.", LvlIssueKind.Approximated);
        int objects = scene.Objects.Count + scene.ObjectsFiltered;
        if (objects > 0)
            report.Info(LvlConvertCodes.ObjectsDropped, $"{LvlReportBuilder.Count(objects, "object")} not written: OBJ holds geometry only (convert to glTF or .rfg to keep them).", LvlIssueKind.Dropped);
        if (scene.Groups.Count > 0)
            report.Info(LvlConvertCodes.Groups, $"{LvlReportBuilder.Count(scene.Groups.Count, "group")} not written: OBJ objects are brushes or rooms only.", LvlIssueKind.Dropped);
    }
}
