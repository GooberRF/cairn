using Cairn.Formats.Gltf;
using Cairn.Formats.Obj;
using Cairn.Lvl.Model;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Lvl.Conversion;

public static partial class LvlConverter
{
    /// <summary>
    /// A static or character mesh (.v3m / .v3c) to a group file: one brush per submesh, from the chosen level of
    /// detail, placed at the submesh's offset. Each triangle becomes a face (double-sided triangles twice, once each
    /// way); full-bright materials give full-bright faces; the pre-flight check decides solid or detail.
    /// </summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="sourceName">The source file name.</param>
    /// <param name="options">The LOD and what meshes become; null uses the defaults (LOD 0).</param>
    /// <param name="cancellationToken">Stops the conversion.</param>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static LvlConversion MeshToRfg(V3dMeshDescription mesh, string sourceName, RfgImportOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        options ??= new RfgImportOptions();
        var report = new LvlReportBuilder();
        var asm = new RfgFileAssembler(options.GroupName ?? Path.GetFileNameWithoutExtension(sourceName), report);
        var builder = new RfgBrushBuilder(options.BrushKind, report);
        int doubleSided = 0, fullBright = 0, clamped = 0, triangles = 0;
        foreach (var sub in mesh.Submeshes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sub.Lods.Length == 0) continue;
            int lod = Math.Clamp(options.Lod, 0, sub.Lods.Length - 1);
            if (lod != options.Lod) clamped++;
            string name = sub.Name.Text;
            var source = new RfgSourceBrush { Name = name.Length > 0 ? name : "submesh", Position = sub.Offset };
            foreach (var group in sub.Lods[lod].Groups)
            {
                var material = group.Material >= 0 && group.Material < sub.Materials.Length ? sub.Materials[group.Material] : null;
                string texture = material?.DiffuseMap.Text ?? "";
                bool bright = material is { Emissive: >= 0.999f };
                var flags = bright ? RfFaceFlags.FullBright : RfFaceFlags.None;
                var v = group.Vertices;
                RfgSourceCorner Corner(int i) => new(v[i].Position, v[i].TexCoord, v[i].Normal);
                foreach (var t in group.Triangles)
                {
                    if ((uint)t.A >= (uint)v.Length || (uint)t.B >= (uint)v.Length || (uint)t.C >= (uint)v.Length) continue;
                    // V3M triangles wind like RF faces (their normals agree), so the order is kept.
                    source.Faces.Add(new RfgSourceFace([Corner(t.A), Corner(t.B), Corner(t.C)], texture) { Flags = flags });
                    triangles++;
                    if (bright) fullBright++;
                    if (t.IsDoubleSided)
                    {
                        source.Faces.Add(new RfgSourceFace([Corner(t.A), Corner(t.C), Corner(t.B)], texture) { Flags = flags });
                        doubleSided++;
                    }
                }
            }
            if (source.Faces.Count > 0 && builder.Build(source, asm.Uid(null)) is { } brush) asm.AddBrush(0, brush);
        }
        builder.Summarise();
        report.SourceFaces = triangles;
        report.Info(LvlRfgCodes.FaceData, $"Each of the mesh's {LvlReportBuilder.Count(triangles, "triangle")} became a face (meshes keep no polygons).", LvlIssueKind.Approximated);
        if (mesh.Kind == V3dKind.Character)
            report.Info(LvlRfgCodes.FaceData, "A character mesh is converted in its bind pose; bones, weights and prop points are not part of a brush.", LvlIssueKind.Dropped);
        if (clamped > 0)
            report.Info(LvlRfgCodes.FaceData, $"{LvlReportBuilder.Count(clamped, "submesh", "submeshes")} have fewer levels of detail than asked for; their last one was used.", LvlIssueKind.Approximated);
        if (fullBright > 0)
            report.Info(LvlConvertCodes.FullBright, $"{LvlReportBuilder.Count(fullBright, "face")} use a full-bright material and became full-bright faces.");
        if (doubleSided > 0)
            report.Info(LvlRfgCodes.DoubleSided, $"{LvlReportBuilder.Count(doubleSided, "double-sided triangle")} were written twice, once facing each way (brush faces are one-sided).", LvlIssueKind.Split);

        var file = asm.Build(Path.GetFileNameWithoutExtension(sourceName) + ".rfg", options.Version);
        if (file.Groups.All(g => g.Brushes.Length == 0))
        {
            report.Error(LvlConvertCodes.Empty, "The mesh has no triangle a brush can use.", LvlIssueKind.Dropped);
            return new LvlConversion(LvlConvertTarget.Rfg, report.Build(), null, null, null);
        }
        return RfgOutput.Finish(file, options.Version, report);
    }

    /// <summary>A mesh file read with <see cref="V3dReader"/> to a group file (see the description overload).</summary>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static LvlConversion MeshToRfg(V3dFile mesh, string sourceName, RfgImportOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        return MeshToRfg(V3dBuilder.Decompose(mesh), sourceName, options, cancellationToken);
    }

    /// <summary>
    /// Reads a mesh or scene file (.gltf, .glb, .obj with its .mtl, .v3m, .v3c) and converts it to a group file.
    /// Levels and group files go through <see cref="Convert"/> with the .rfg target instead.
    /// </summary>
    /// <exception cref="Formats.AssetFormatException">The file is not readable.</exception>
    /// <exception cref="IOException">The file could not be opened.</exception>
    /// <exception cref="NotSupportedException">The extension is not one this converter reads.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static LvlConversion FileToRfg(string path, RfgImportOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string name = Path.GetFileName(path);
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".gltf":
            case ".glb":
                return GltfToRfg(GltfReader.ReadFile(path), name, options, cancellationToken);
            case ".obj":
            {
                var obj = ObjReader.ReadFile(path);
                var (materials, _) = ObjReader.ReadMaterialLibraries(obj, Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
                return ObjToRfg(obj, materials, name, options, cancellationToken);
            }
            case ".v3m":
            case ".v3c":
                return MeshToRfg(V3dReader.ReadFile(path), name, options, cancellationToken);
            default:
                throw new NotSupportedException($"'{name}' is not a glTF, OBJ or mesh file.");
        }
    }
}
