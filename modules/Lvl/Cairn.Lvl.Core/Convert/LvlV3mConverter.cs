using System.Text;
using Cairn.Formats;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Linting;
using Cairn.Lvl.Model;

namespace Cairn.Lvl.Conversion;

/// <summary>
/// Level/group → static mesh: a <see cref="V3dMeshDescription"/> with one submesh per brush (or per room and mover)
/// or one for the whole level, built by <see cref="V3dBuilder"/> (which splits batches at the 16-bit size limits)
/// and checked by the mesh linter. A submesh that would use more than the engine's seven textures per LOD is split
/// into several. Full-bright faces get a material with emissive 1, the V3M way of saying full bright.
/// </summary>
internal sealed class LvlV3mConverter(LvlScene scene, LvlConvertOptions options, LvlReportBuilder report, CancellationToken ct)
{
    private int _triangulated, _splitSubmeshes, _splitBatches, _shortenedNames, _scrolling, _lightmapped, _special, _fullBright;
    private readonly HashSet<string> _submeshNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _badTextures = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Unit(string Name, Vector3 Offset, List<LvlPolygon> Polygons);

    public (V3dFile? Mesh, V3dMeshDescription? Description) Run(string outputFileName)
    {
        var units = new List<Unit>();
        if (options.V3mGrouping == LvlV3mGrouping.WholeLevel)
        {
            var all = new List<LvlPolygon>();
            foreach (var p in scene.Parts) all.AddRange(p.Polygons);
            units.Add(new Unit(Path.GetFileNameWithoutExtension(scene.Name), Vector3.Zero, all));
        }
        else
        {
            foreach (var p in scene.Parts)
                units.Add(new Unit(p.Name, p.Brush?.Position ?? Vector3.Zero, p.Polygons));
        }

        var submeshes = ImmutableArray.CreateBuilder<V3dSubmeshDescription>();
        foreach (var unit in units)
        {
            ct.ThrowIfCancellationRequested();
            if (unit.Polygons.Count == 0) continue;
            AddUnit(unit, submeshes);
            report.Parts++;
        }
        Summarise();
        if (submeshes.Count == 0)
        {
            report.Error(LvlConvertCodes.Empty, "Nothing is left to put in the mesh (no faces pass the filter).", LvlIssueKind.Dropped);
            return (null, null);
        }

        var description = new V3dMeshDescription { Kind = V3dKind.StaticMesh, Submeshes = submeshes.ToImmutable() };
        V3dFile mesh;
        try
        {
            mesh = V3dBuilder.Build(description);
        }
        catch (ArgumentException e)
        {
            report.Error(LvlConvertCodes.MeshBuild, $"The mesh could not be built: {e.Message}");
            return (null, description);
        }

        foreach (var d in MeshLinter.Analyze(mesh, new MeshLintContext { FileName = outputFileName }))
        {
            var severity = d.Severity switch
            {
                DiagnosticSeverity.Error => LvlIssueSeverity.Error,
                DiagnosticSeverity.Warning => LvlIssueSeverity.Warning,
                _ => LvlIssueSeverity.Info,
            };
            report.Add(severity, LvlIssueKind.LimitBroken, LvlConvertCodes.MeshLint, $"{d.Code}: {d.Message}");
        }
        return (mesh, description);
    }

    private void AddUnit(Unit unit, ImmutableArray<V3dSubmeshDescription>.Builder submeshes)
    {
        // Materials in first-use order; seven per submesh at most (the engine's per-LOD texture limit).
        var materials = new List<(string Texture, bool FullBright)>();
        var materialOf = new Dictionary<(string, bool), int>();
        var polygonMaterial = new int[unit.Polygons.Count];
        for (int i = 0; i < unit.Polygons.Count; i++)
        {
            var p = unit.Polygons[i];
            bool fullBright = (p.Classes & RfFaceClass.FullBright) != 0;
            var key = (p.Texture.ToLowerInvariant(), fullBright);
            if (!materialOf.TryGetValue(key, out int m))
            {
                m = materials.Count;
                materials.Add((p.Texture, fullBright));
                materialOf[key] = m;
            }
            polygonMaterial[i] = m;
            if (fullBright) _fullBright++;
            if (p.Face.Scroll is not null) _scrolling++;
            if (p.Corners[0].LightmapUv is not null) _lightmapped++;
            if ((p.Classes & (RfFaceClass.Sky | RfFaceClass.Invisible | RfFaceClass.LiquidSurface | RfFaceClass.Portal | RfFaceClass.Alpha | RfFaceClass.Hole)) != 0) _special++;
        }
        int chunks = (materials.Count + MeshRules.MaxTexturesPerLod - 1) / MeshRules.MaxTexturesPerLod;
        if (chunks > 1) _splitSubmeshes += chunks - 1;

        for (int chunk = 0; chunk < chunks; chunk++)
        {
            int first = chunk * MeshRules.MaxTexturesPerLod;
            int count = Math.Min(MeshRules.MaxTexturesPerLod, materials.Count - first);
            var groups = new GroupBuilder[count];
            for (int k = 0; k < count; k++) groups[k] = new GroupBuilder();
            for (int i = 0; i < unit.Polygons.Count; i++)
            {
                int m = polygonMaterial[i] - first;
                if (m < 0 || m >= count) continue;
                var p = unit.Polygons[i];
                var tris = LvlGeometryHelpers.Triangulate(p);
                if (p.Corners.Length > 3) _triangulated++;
                var g = groups[m];
                foreach (var (a, b, c) in tris)
                {
                    // RF faces and V3M triangles share the engine's winding, so the corner order is kept.
                    g.Triangles.Add(new V3dMeshTriangle(g.Vertex(p.Corners[a], unit.Offset), g.Vertex(p.Corners[b], unit.Offset), g.Vertex(p.Corners[c], unit.Offset)));
                }
                report.Faces++;
                report.Triangles += tris.Count;
            }

            var mats = ImmutableArray.CreateBuilder<V3dMaterial>();
            var lodGroups = ImmutableArray.CreateBuilder<V3dMaterialGroup>();
            for (int k = 0; k < count; k++)
            {
                var (texture, fullBright) = materials[first + k];
                mats.Add(new V3dMaterial(FixedString.FromText(TextureName(texture), V3dMaterial.NameSize), fullBright ? 1f : 0f, 0f, 0f, 0f,
                    FixedString.FromText("", V3dMaterial.NameSize), 1u));
                var g = groups[k];
                if (g.Triangles.Count == 0) continue;
                if (g.Vertices.Count > V3dBuilder.MaxBatchVertices || g.Triangles.Count > V3dBuilder.MaxBatchTriangles)
                    _splitBatches += Math.Max((g.Vertices.Count + V3dBuilder.MaxBatchVertices - 1) / V3dBuilder.MaxBatchVertices,
                        (g.Triangles.Count + V3dBuilder.MaxBatchTriangles - 1) / V3dBuilder.MaxBatchTriangles) - 1;
                lodGroups.Add(new V3dMaterialGroup { Material = k, Vertices = [.. g.Vertices], Triangles = [.. g.Triangles] });
                report.Vertices += g.Vertices.Count;
            }
            report.Materials += count;
            string name = SubmeshName(chunk == 0 ? unit.Name : $"{unit.Name}_{chunk + 1}");
            submeshes.Add(new V3dSubmeshDescription
            {
                Name = FixedString.FromText(name, V3dSubmesh.NameSize),
                Offset = unit.Offset,
                Lods = [new V3dLodDescription { Distance = 0f, Groups = lodGroups.ToImmutable() }],
                Materials = mats.ToImmutable(),
            });
        }
    }

    private sealed class GroupBuilder
    {
        public readonly List<V3dMeshVertex> Vertices = [];
        public readonly List<V3dMeshTriangle> Triangles = [];
        private readonly Dictionary<(Vector3, Vector3, Vector2), int> _map = [];

        public int Vertex(LvlCorner c, Vector3 offset)
        {
            var key = (c.Position - offset, c.Normal, c.Uv);
            if (_map.TryGetValue(key, out int index)) return index;
            index = Vertices.Count;
            Vertices.Add(new V3dMeshVertex(key.Item1, c.Normal, c.Uv));
            _map[key] = index;
            return index;
        }
    }

    /// <summary>A submesh name that fits its 24-byte field (Latin-1, at most 23 characters), unique in the mesh.</summary>
    private string SubmeshName(string wanted)
    {
        string clean = Latin1(wanted);
        if (clean.Length == 0) clean = "mesh";
        string name = clean.Length > V3dSubmesh.NameSize - 1 ? clean[..(V3dSubmesh.NameSize - 1)] : clean;
        if (name != wanted) _shortenedNames++;
        for (int i = 2; !_submeshNames.Add(name); i++)
        {
            string suffix = "_" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            name = (clean.Length + suffix.Length > V3dSubmesh.NameSize - 1 ? clean[..(V3dSubmesh.NameSize - 1 - suffix.Length)] : clean) + suffix;
        }
        return name;
    }

    /// <summary>A texture name that fits the 32-byte material field; a longer one is shortened and reported as an error.</summary>
    private string TextureName(string texture)
    {
        string clean = Latin1(texture);
        if (clean.Length <= V3dMaterial.NameSize - 1 && clean == texture) return clean;
        _badTextures.Add(texture);
        return clean.Length > V3dMaterial.NameSize - 1 ? clean[..(V3dMaterial.NameSize - 1)] : clean;
    }

    private static string Latin1(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(c is > (char)0 and <= (char)0xFF ? c : '_');
        return sb.ToString();
    }

    private void Summarise()
    {
        if (_triangulated > 0)
            report.Info(LvlConvertCodes.Triangulated, $"{LvlReportBuilder.Count(_triangulated, "polygon")} with more than three corners were triangulated.", LvlIssueKind.Split);
        if (report.Vertices > 0)
            report.Info(LvlConvertCodes.VertexSplit, $"{LvlReportBuilder.Count(report.SourceVertices, "source vertex", "source vertices")} became {LvlReportBuilder.Count(report.Vertices, "mesh vertex", "mesh vertices")}: a vertex is split wherever its corners differ in texture coordinates or normal.", LvlIssueKind.Split);
        if (_splitSubmeshes > 0)
            report.Warning(LvlConvertCodes.SubmeshSplit, $"{LvlReportBuilder.Count(_splitSubmeshes, "extra submesh", "extra submeshes")} were made because the engine draws at most {MeshRules.MaxTexturesPerLod} textures per submesh.", LvlIssueKind.Split);
        if (_splitBatches > 0)
            report.Info(LvlConvertCodes.BatchSplit, $"{LvlReportBuilder.Count(_splitBatches, "extra batch", "extra batches")} were made: one batch holds at most {V3dBuilder.MaxBatchVertices} vertices and {V3dBuilder.MaxBatchTriangles} triangles.", LvlIssueKind.Split);
        if (_shortenedNames > 0)
            report.Warning(LvlConvertCodes.NameTooLong, $"{LvlReportBuilder.Count(_shortenedNames, "submesh name was", "submesh names were")} shortened to the {V3dSubmesh.NameSize - 1} characters a mesh holds.", LvlIssueKind.Approximated);
        foreach (string t in _badTextures)
            report.Error(LvlConvertCodes.NameTooLong, $"Texture '{t}' does not fit a mesh's {V3dMaterial.NameSize - 1}-character texture name; the mesh names a shortened copy that the game will not find.");
        if (_fullBright > 0)
            report.Info(LvlConvertCodes.FullBright, $"{LvlReportBuilder.Count(_fullBright, "full-bright face")} use materials with emissive 1; they stay one-sided.", LvlIssueKind.Approximated);
        if (_scrolling > 0)
            report.Warning(LvlConvertCodes.Scroll, $"{LvlReportBuilder.Count(_scrolling, "face")} scroll their texture; a static mesh cannot, so the scrolling is dropped.", LvlIssueKind.Dropped);
        if (_lightmapped > 0)
            report.Info(LvlConvertCodes.Lightmaps, "Lightmaps are not part of a mesh; the game lights the mesh itself.", LvlIssueKind.Dropped);
        if (_special > 0)
            report.Info(LvlConvertCodes.FaceFlags, $"{LvlReportBuilder.Count(_special, "face")} are sky, invisible, liquid-surface, portal, alpha or hole faces; a mesh has no such flags, so they are drawn as ordinary faces.", LvlIssueKind.Dropped);
        if (scene.Parts.Any(p => p.Brush is not null))
            report.Info(LvlConvertCodes.BrushData, "Brush data (solid flags, life, state) has no place in a mesh and is dropped; the transforms are applied.", LvlIssueKind.Dropped);
        int objects = scene.Objects.Count + scene.ObjectsFiltered;
        if (objects > 0)
            report.Info(LvlConvertCodes.ObjectsDropped, $"{LvlReportBuilder.Count(objects, "object")} not written: a mesh holds geometry only.", LvlIssueKind.Dropped);
    }
}
