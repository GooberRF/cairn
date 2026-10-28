using System.Globalization;
using Cairn.Formats.Obj;
using Cairn.Lvl.Model;
using Cairn.Rfa.Interchange;

namespace Cairn.Lvl.Conversion;

public static partial class LvlConverter
{
    /// <summary>
    /// OBJ to a group file: one brush per <c>o</c> / <c>g</c> part. Positions come back to RF's left-handed space
    /// (X mirrored, V flipped, corners in RF order); Cairn's <c># rf_</c> lines restore uids, solid flags, life,
    /// state, transforms, face flags, smoothing, scrolling, Alpine flags and group names; other OBJ files go through
    /// the pre-flight check (closed meshes become solid brushes, others detail brushes).
    /// </summary>
    /// <param name="obj">The OBJ document.</param>
    /// <param name="materials">Its material library (texture names), or null.</param>
    /// <param name="sourceName">The source file name.</param>
    /// <param name="options">What meshes become; null uses the defaults.</param>
    /// <param name="cancellationToken">Stops the conversion.</param>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static LvlConversion ObjToRfg(ObjDocument obj, MtlDocument? materials, string sourceName, RfgImportOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        return new RfgFromObj(obj, materials, sourceName, options ?? new RfgImportOptions(), cancellationToken).Run();
    }
}

/// <summary>OBJ to group file (see <see cref="LvlConverter.ObjToRfg"/>).</summary>
internal sealed class RfgFromObj(ObjDocument obj, MtlDocument? mtl, string sourceName, RfgImportOptions options, CancellationToken ct)
{
    private readonly LvlReportBuilder _report = new();
    private readonly HashSet<string> _guessedMaterials = new(StringComparer.Ordinal);
    private int _metadataParts, _plainParts, _movers, _missingPositions, _unlit, _sGroups, _reduxNames, _doubleSided, _otherLodObjects, _lodsClamped;

    private sealed record FaceState(RfFaceFlags Flags, uint Smoothing, Vector2? Scroll, uint? Raw);

    public LvlConversion Run()
    {
        string? headerName = null;
        foreach (var c in obj.Comments.Where(c => c.ObjectIndex < 0))
            if (LvlObjMetadata.Parse(c.Text) is { } m && m.Keyword is LvlObjMetadata.Level or LvlObjMetadata.GroupFile && m.Values.TryGetValue("name", out var n))
                headerName = Path.GetFileNameWithoutExtension(n);

        var asm = new RfgFileAssembler(options.GroupName ?? headerName ?? Path.GetFileNameWithoutExtension(sourceName), _report);
        var objectData = new Dictionary<int, (string Keyword, Dictionary<string, string> Values)>();
        var faceStates = new Dictionary<int, List<(int FaceIndex, FaceState State)>>();
        foreach (var c in obj.Comments)
        {
            if (c.ObjectIndex < 0 || LvlObjMetadata.Parse(c.Text) is not { } m) continue;
            if (m.Keyword is LvlObjMetadata.Brush or LvlObjMetadata.Mover or LvlObjMetadata.Room)
                objectData.TryAdd(c.ObjectIndex, m);
            else if (m.Keyword == LvlObjMetadata.Face && ParseFace(m.Values) is { } state)
            {
                if (!faceStates.TryGetValue(c.ObjectIndex, out var list)) faceStates[c.ObjectIndex] = list = [];
                list.Add((c.FaceIndex, state));
            }
        }
        asm.Reserve(objectData.Values.Select(d => d.Values.TryGetValue("uid", out var u) && int.TryParse(u, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1));
        var (doubleSided, otherLods) = MeshMetadata();

        var builder = new RfgBrushBuilder(options.BrushKind, _report);
        var faceIndex = new Dictionary<ObjFace, int>(ReferenceEqualityComparer.Instance);
        foreach (var o in obj.Objects)
            for (int i = 0; i < o.Faces.Count; i++) faceIndex[o.Faces[i]] = i;

        foreach (var part in obj.SplitIntoParts())
        {
            ct.ThrowIfCancellationRequested();
            if (otherLods.Contains(part.ObjectIndex)) continue;
            objectData.TryGetValue(part.ObjectIndex, out var data);
            var states = faceStates.GetValueOrDefault(part.ObjectIndex);
            var source = new RfgSourceBrush { Name = part.Name };
            bool hasData = data.Keyword is not null;
            if (hasData) _metadataParts++;
            else _plainParts++;

            // Corners in RF space: X mirrored back, V flipped back, and the winding turned with the mirror.
            var faces = new List<(RfgSourceCorner[] Corners, ObjFace Face)>();
            foreach (var f in part.Faces)
            {
                var corners = new RfgSourceCorner[f.Corners.Count];
                bool ok = true;
                for (int k = 0; k < corners.Length; k++)
                {
                    var c = f.Corners[corners.Length - 1 - k];
                    if ((uint)c.Position >= (uint)obj.Positions.Count)
                    {
                        ok = false;
                        break;
                    }
                    var uv = c.HasTexCoord && c.TexCoord < obj.TexCoords.Count ? obj.TexCoords[c.TexCoord] : Vector2.Zero;
                    Vector3? normal = c.HasNormal && c.Normal < obj.Normals.Count ? GltfSpace.FromGltf(obj.Normals[c.Normal]) : null;
                    corners[k] = new RfgSourceCorner(GltfSpace.FromGltf(obj.Positions[c.Position]), new Vector2(uv.X, 1f - uv.Y), normal);
                }
                if (!ok)
                {
                    _missingPositions++;
                    continue;
                }
                faces.Add((corners, f));
            }

            // Placement: Cairn's line gives the brush's stored position and orientation (its vertices are in world
            // space); otherwise the brush sits at the centre of its bounds.
            var position = Center(faces.SelectMany(f => f.Corners.Select(c => c.Position)));
            var orientation = RfMatrix.Identity;
            if (hasData && data.Keyword != LvlObjMetadata.Room)
            {
                var v = data.Values;
                if (v.TryGetValue("uid", out var uid) && int.TryParse(uid, NumberStyles.Integer, CultureInfo.InvariantCulture, out int u)) source.Uid = u;
                if (v.TryGetValue("solid", out var solid) && Hex(solid) is { } s) source.SolidFlags = (RfSolidFlags)s;
                if (v.TryGetValue("life", out var life) && int.TryParse(life, NumberStyles.Integer, CultureInfo.InvariantCulture, out int l)) source.Life = l;
                if (v.TryGetValue("state", out var st) && int.TryParse(st, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sv)) source.State = sv;
                if (v.TryGetValue("position", out var p) && Floats(p, 3) is { } pos) position = new Vector3(pos[0], pos[1], pos[2]);
                if (v.TryGetValue("orientation", out var or) && Floats(or, 9) is { } m)
                    orientation = new RfMatrix(new Vector3(m[0], m[1], m[2]), new Vector3(m[3], m[4], m[5]), new Vector3(m[6], m[7], m[8]));
                if (v.TryGetValue("alpine_flags", out var af) && Hex(af) is { } flags)
                {
                    byte material = v.TryGetValue("alpine_material", out var am) && byte.TryParse(am, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte b) ? b : (byte)0;
                    source.Alpine = new RfAlpineBrushData((RfAlpineBrushFlags)flags, material, []);
                }
                if (v.TryGetValue("group", out var group) && group.Length > 0) source.Group = group;
                if (data.Keyword == LvlObjMetadata.Mover) _movers++;
            }
            else if (!hasData && ReduxBrushName(obj.Objects[part.ObjectIndex].Name) is { } redux)
            {
                // REDUX names its objects Brush_<uid>_<A|S>_<D|nD>_<P|nP>_<ES|nES>_<G|nG>; its world-space vertices
                // need no other placement.
                source.Uid = redux.Uid;
                source.SolidFlags = redux.Flags;
                source.CheckStoredFlags = true;
                _reduxNames++;
                _plainParts--;
            }
            source.Position = position;
            source.Orientation = orientation;

            foreach (var (corners, f) in faces)
            {
                var local = corners.Select(c => c with
                {
                    Position = Unrotate(orientation, c.Position - position),
                    Normal = c.Normal is { } n ? Unrotate(orientation, n) : null,
                }).ToArray();
                var (texture, unlit) = Texture(f.Material);
                var state = StateAt(states, faceIndex.GetValueOrDefault(f, 0));
                var face = new RfgSourceFace(local, texture);
                if (state is not null)
                {
                    face = face with { Flags = state.Flags, Smoothing = state.Smoothing, Scroll = state.Scroll, RawFlags = state.Raw };
                }
                else
                {
                    if (unlit)
                    {
                        face = face with { Flags = RfFaceFlags.FullBright };
                        _unlit++;
                    }
                    if (f.SmoothingGroup > 0)
                    {
                        face = face with { Smoothing = 1u << ((f.SmoothingGroup - 1) % 32) };
                        _sGroups++;
                    }
                }
                source.Faces.Add(face);
                if (f.Material is { } mat && doubleSided.Contains(mat))
                {
                    // Brush faces are one-sided: a double-sided triangle of a mesh is written once each way, as from the .v3m.
                    source.Faces.Add(face with { Corners = [.. Enumerable.Reverse(local)] });
                    _doubleSided++;
                }
            }
            if (source.Faces.Count == 0) continue;
            if (builder.Build(source, asm.Uid(source.Uid)) is { } brush) asm.AddBrush(asm.Group(source.Group), brush);
        }
        builder.Summarise();
        Summarise();

        var file = asm.Build(Path.GetFileNameWithoutExtension(sourceName) + ".rfg", options.Version);
        if (file.Groups.All(g => g.Brushes.Length == 0))
        {
            _report.Error(LvlConvertCodes.Empty, "The OBJ file has no face a brush can use.", LvlIssueKind.Dropped);
            return new LvlConversion(LvlConvertTarget.Rfg, _report.Build(), null, null, null);
        }
        return RfgOutput.Finish(file, options.Version, _report);
    }

    /// <summary>
    /// What Cairn's mesh OBJ export (the <c>rf_mesh</c> marker) says about its materials and objects: the materials
    /// whose triangles are double-sided, and the objects that are not the chosen level of detail of their submesh
    /// (an export of every LOD holds each submesh once per LOD; <see cref="RfgImportOptions.Lod"/> picks one, or the
    /// submesh's last one below it). Both are empty for any other OBJ.
    /// </summary>
    private (HashSet<string> DoubleSided, HashSet<int> OtherLods) MeshMetadata()
    {
        var doubleSided = new HashSet<string>(StringComparer.Ordinal);
        var others = new HashSet<int>();
        if (!ObjMeshImport.HasCairnMetadata(obj)) return (doubleSided, others);
        var bySubmesh = new Dictionary<int, List<(int Object, int Lod)>>();
        foreach (var c in obj.Comments)
        {
            if (ObjMeshMetadata.Parse(c.Text) is not { } line) continue;
            var v = line.Values;
            if (c.ObjectIndex < 0 && line.Keyword == ObjMeshMetadata.Material)
            {
                if (ObjMeshMetadata.GetText(v, "name") is { } name && ObjMeshMetadata.TryGetBool(v, "double_sided", out bool two) && two) doubleSided.Add(name);
            }
            else if (c.ObjectIndex >= 0 && c.FaceIndex == 0 && line.Keyword == ObjMeshMetadata.Submesh
                && ObjMeshMetadata.TryGetInt(v, "index", out int sub) && ObjMeshMetadata.TryGetInt(v, "lod", out int lod) && sub >= 0 && lod >= 0)
            {
                if (!bySubmesh.TryGetValue(sub, out var list)) bySubmesh[sub] = list = [];
                list.Add((c.ObjectIndex, lod));
            }
        }
        foreach (var list in bySubmesh.Values)
        {
            int below = list.Where(e => e.Lod <= options.Lod).Select(e => e.Lod).DefaultIfEmpty(-1).Max();
            int chosen = below >= 0 ? below : list.Min(e => e.Lod);
            if (chosen != options.Lod && list.Count > 1) _lodsClamped++;
            foreach (var (o, lod) in list)
            {
                if (lod == chosen) continue;
                others.Add(o);
                _otherLodObjects++;
            }
        }
        return (doubleSided, others);
    }

    /// <summary>
    /// The uid and solid flags of a REDUX object name (<c>Brush_12_S_nD_nP_nES_nG</c>: solid or air, detail, portal,
    /// emits steam, and RF2's geoable, which RED does not keep). Null for any other name.
    /// </summary>
    internal static (int Uid, RfSolidFlags Flags)? ReduxBrushName(string name)
    {
        var parts = name.Split('_');
        if (parts.Length != 7 || parts[0] != "Brush" || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int uid)) return null;
        RfSolidFlags flags = 0;
        (string Yes, string No, RfSolidFlags Flag)[] tokens = [("A", "S", RfSolidFlags.Air), ("D", "nD", RfSolidFlags.Detail), ("P", "nP", RfSolidFlags.Portal), ("ES", "nES", RfSolidFlags.EmitsSteam), ("G", "nG", 0)];
        for (int i = 0; i < tokens.Length; i++)
        {
            if (parts[i + 2] == tokens[i].Yes) flags |= tokens[i].Flag;
            else if (parts[i + 2] != tokens[i].No) return null;
        }
        return (uid, flags);
    }

    private static Vector3 Unrotate(RfMatrix m, Vector3 d) => new(Vector3.Dot(d, m.Right), Vector3.Dot(d, m.Up), Vector3.Dot(d, m.Forward));

    private static Vector3 Center(IEnumerable<Vector3> points)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in points)
        {
            if (!RfgBrushBuilder.Finite(p)) continue;
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return min.X <= max.X ? (min + max) / 2 : Vector3.Zero;
    }

    /// <summary>The face state in force at a face: the last <c>rf_face</c> line before it in its object.</summary>
    private static FaceState? StateAt(List<(int FaceIndex, FaceState State)>? states, int face)
    {
        if (states is null) return null;
        FaceState? result = null;
        foreach (var (at, state) in states)
        {
            if (at > face) break;
            result = state;
        }
        return result;
    }

    private static FaceState? ParseFace(Dictionary<string, string> v)
    {
        if (!v.TryGetValue("flags", out var f) || Hex(f) is not { } flags) return null;
        uint smoothing = v.TryGetValue("smoothing", out var s) && Hex(s) is { } sm ? sm : 0;
        Vector2? scroll = v.TryGetValue("scroll", out var sc) && Floats(sc, 2) is { } x ? new Vector2(x[0], x[1]) : null;
        uint? raw = v.TryGetValue("raw", out var r) ? Hex(r) : null;
        return new FaceState((RfFaceFlags)(flags & 0xFFFF), smoothing, scroll, raw ?? flags);
    }

    private static uint? Hex(string text)
    {
        string t = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        return uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v) ? v : null;
    }

    private static float[]? Floats(string text, int count)
    {
        var parts = text.Split(',');
        if (parts.Length != count) return null;
        var result = new float[count];
        for (int i = 0; i < count; i++)
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out result[i])) return null;
        return result;
    }

    /// <summary>
    /// The RF texture of a material: Cairn's <c>rf_texture</c> line (an empty name means untextured), else the
    /// diffuse map's file name, else the material name.
    /// </summary>
    private (string Texture, bool Unlit) Texture(string? material)
    {
        if (material is null || mtl is null) return (material is null ? "" : Guess(material), false);
        int index = mtl.Materials.FindLastIndex(m => string.Equals(m.Name, material, StringComparison.Ordinal));
        if (index < 0) return (Guess(material), false);
        var m = mtl.Materials[index];
        bool unlit = m.Illumination == 0;
        foreach (var c in mtl.Comments)
        {
            if (c.MaterialIndex != index || LvlObjMetadata.Parse(c.Text) is not { } meta || meta.Keyword != "rf_texture") continue;
            if (meta.Values.TryGetValue("name", out var name)) return (name, unlit);
        }
        if (m.DiffuseMap is { } map && map.FileName.Length > 0)
        {
            _guessedMaterials.Add(material);
            return (RfgFromGltf.RfTextureName(map.FileName), unlit);
        }
        return (Guess(material), unlit);
    }

    private string Guess(string material)
    {
        // Cairn's level OBJ names its material for faces without a texture "untextured" (with "_2", "_blend" and so on
        // after it), without a map.
        if (UntexturedMaterial(material)) return "";
        _guessedMaterials.Add(material);
        return RfgFromGltf.RfTextureName(material);
    }

    /// <summary>True for the material name Cairn's level glTF and OBJ give faces without a texture ("untextured", "untextured_fullbright", ...).</summary>
    internal static bool UntexturedMaterial(string name) =>
        name.Equals("untextured", StringComparison.Ordinal) || name.StartsWith("untextured_", StringComparison.Ordinal);

    private void Summarise()
    {
        static string N(int n, string one, string? many = null) => LvlReportBuilder.Count(n, one, many);
        if (_metadataParts > 0)
            _report.Info(LvlConvertCodes.ObjMetadata, $"{N(_metadataParts, "object")} kept Cairn's brush data in '# rf_' lines (uid, solid flags, life, state, transform, face flags, smoothing, scrolling); it was restored.");
        if (_reduxNames > 0)
            _report.Info(LvlConvertCodes.ObjMetadata, $"Uids and solid flags came from REDUX's object names: {N(_reduxNames, "object")}.");
        if (_plainParts > 0)
            _report.Info(LvlConvertCodes.ObjMetadata, $"{N(_plainParts, "object")} had no Cairn brush data: they sit at the centre of their bounds, unrotated, with no face flags.", LvlIssueKind.Approximated);
        if (_movers > 0)
            _report.Info(LvlConvertCodes.Groups, $"{N(_movers, "mover brush", "mover brushes")} became brushes of their group: OBJ has no keyframes, so the groups do not move.", LvlIssueKind.Dropped);
        if (_guessedMaterials.Count > 0)
            _report.Info(LvlRfgCodes.TextureNames, $"Texture names of {N(_guessedMaterials.Count, "material")} come from the material's map or name; PNG and JPG names became .tga. Check them in RED.", LvlIssueKind.Approximated);
        if (_unlit > 0)
            _report.Info(LvlRfgCodes.FaceData, $"{N(_unlit, "face")} use a material with 'illum 0' and became full bright.", LvlIssueKind.Approximated);
        if (_sGroups > 0)
            _report.Info(LvlRfgCodes.FaceData, $"{N(_sGroups, "face")} took their smoothing group from the OBJ 's' statement.", LvlIssueKind.Approximated);
        if (_otherLodObjects > 0)
            _report.Info(LvlRfgCodes.FaceData, $"The mesh's other levels of detail were left out ({N(_otherLodObjects, "object")}); each submesh became one brush from LOD {options.Lod}"
                + (_lodsClamped > 0 ? $", or its nearest one for {N(_lodsClamped, "submesh", "submeshes")} without it." : "."), LvlIssueKind.Dropped);
        if (_doubleSided > 0)
            _report.Info(LvlRfgCodes.DoubleSided, $"{N(_doubleSided, "double-sided triangle")} were written twice, once facing each way (brush faces are one-sided).", LvlIssueKind.Split);
        if (_missingPositions > 0)
            _report.Warning(LvlConvertCodes.BadVertexFaces, $"{N(_missingPositions, "face")} name a position the file does not have and were left out.", LvlIssueKind.Dropped);
    }
}
