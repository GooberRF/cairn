using System.Numerics;
using Cairn.Rfa.Animation;
using Cairn.Assets;
using Cairn.Formats;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Linting;

/// <summary>Everything the mesh rules can use besides the mesh. Every member is optional.</summary>
public sealed record MeshLintContext
{
    /// <summary>No context: structural rules only.</summary>
    public static MeshLintContext None { get; } = new();

    /// <summary>The mesh's file name, for V3C026.</summary>
    public string? FileName { get; init; }

    /// <summary>Resolves texture names for V3C021 (through the engine's supersede chain).</summary>
    public AssetResolver? Resolver { get; init; }

    /// <summary>Alternative to <see cref="Resolver"/>: answers whether a texture name resolves.</summary>
    public Func<string, bool>? TextureExists { get; init; }
}

/// <summary>
/// Lints a <c>.v3c</c> or <c>.v3m</c> (<see cref="MeshRules"/> lists every rule). Skinning rules apply
/// to character meshes only: stock static meshes carry bone links although they never skin.
/// </summary>
public static class MeshLinter
{
    /// <summary>At most this many diagnostics of one code are reported per batch.</summary>
    public const int MaxPerBatch = 3;

    /// <summary>Runs every rule whose context is available. Errors first.</summary>
    public static IReadOnlyList<Diagnostic> Analyze(V3dFile mesh, MeshLintContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        context ??= MeshLintContext.None;
        var results = new List<Diagnostic>();
        CheckHeader(results, mesh);
        int boneCount = CheckBones(results, mesh);
        CheckSpheres(results, mesh, boneCount);
        CheckSubmeshes(results, mesh, boneCount, context);
        CheckWritable(results, mesh);
        CheckFileName(results, context);
        return [.. results.OrderByDescending(d => d.Severity).ThenBy(d => d.Code, StringComparer.Ordinal)];
    }

    private static void CheckHeader(List<Diagnostic> results, V3dFile mesh)
    {
        var at = DiagnosticLocation.ForMesh(new MeshNodeRef(MeshNodeKind.Header));
        if (mesh.Header.Version != V3dHeader.CurrentVersion)
        {
            results.Add(Make(MeshRules.UnsupportedVersion,
                $"The mesh says it is format version 0x{mesh.Header.Version:X}; the game only loads 0x40000.",
                "Re-export the mesh with a current exporter (or let Cairn rebuild it from glTF).", at));
        }
        int submeshes = mesh.Submeshes.Count(), spheres = mesh.CollisionSpheres.Count();
        if (submeshes == 0)
        {
            results.Add(Make(MeshRules.NoSubmeshes,
                "The mesh has no submeshes: it holds no geometry, so the game draws nothing for it.",
                "Re-export the mesh from its source scene (or rebuild it through glTF import).", at));
        }
        if (mesh.Header.SubmeshCount != submeshes || mesh.Header.CollisionSphereCount != spheres)
        {
            results.Add(Make(MeshRules.HeaderCounts,
                $"The header declares {mesh.Header.SubmeshCount} submeshes and {mesh.Header.CollisionSphereCount} collision spheres, "
                + $"but the file holds {submeshes} and {spheres}. The engine sizes its arrays from the header.",
                "Rebuild the mesh's derived fields (re-export, or rebuild through glTF import).", at, MeshFixes.HeaderCounts()));
        }
    }

    private static int CheckBones(List<Diagnostic> results, V3dFile mesh)
    {
        var bones = mesh.Bones;
        int n = bones.Length;
        if (n > V3dBoneSection.MaxBones)
        {
            results.Add(Make(MeshRules.TooManyBones,
                $"The mesh has {n} bones; the engine supports at most {V3dBoneSection.MaxBones}.",
                "Merge or remove bones in the source scene and re-export (every clip must then be conformed to the new skeleton).",
                DiagnosticLocation.ForMesh(new MeshNodeRef(MeshNodeKind.Bone, Index: V3dBoneSection.MaxBones))));
        }

        var raw = bones.Select(b => b.ParentIndex).ToArray();
        var effective = ForwardKinematics.EffectiveParents(raw);
        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < n; i++)
        {
            var b = bones[i];
            var at = DiagnosticLocation.ForMesh(new MeshNodeRef(MeshNodeKind.Bone, Index: i));
            string name = UserText.Printable(b.Name.Text);
            int p = b.ParentIndex;
            if (p != -1 && (p < 0 || p >= n || p == i))
            {
                results.Add(Make(MeshRules.BadParent,
                    $"Bone {i} ('{name}') has parent {p}, which is {(p == i ? "the bone itself" : "not a bone of this mesh")}.",
                    "Set the parent to another bone, or to -1 for the root.", at, MeshFixes.MakeRoot(i)));
            }
            else if (p >= 0 && effective[i] < 0)
            {
                results.Add(Make(MeshRules.ParentCycle,
                    $"Bone {i} ('{name}') is part of a parent cycle: following its parents comes back to it, so the engine never finds a root.",
                    "Reparent one bone of the loop to a bone outside it (or make it the root).", at));
            }
            if (!Finite(b.Position) || !float.IsFinite(b.Rotation.X + b.Rotation.Y + b.Rotation.Z + b.Rotation.W))
            {
                results.Add(Make(MeshRules.NonFinite, $"Bone {i} ('{name}')'s bind transform holds a value that is not a number.",
                    "Re-export the mesh, or edit the bind transform in the Structure tab (select the bone).", at));
            }
            CheckName(results, b.Name, $"Bone {i}'s name", at, MeshFixes.TruncateBoneName(i));
            if (b.Name.Text.Length > 0 && !names.TryAdd(b.Name.Text, i))
            {
                results.Add(Make(MeshRules.DuplicateBoneName,
                    $"Bones {names[b.Name.Text]} and {i} are both called '{name}'. Clips address bones by index, but tools that map by name "
                    + "(retarget, conform, glTF) cannot tell them apart.",
                    "Rename one of them.", at, MeshFixes.UniqueBoneName(mesh, i)));
            }
        }
        return n;
    }

    private static void CheckSpheres(List<Diagnostic> results, V3dFile mesh, int boneCount)
    {
        int index = 0;
        foreach (var s in mesh.CollisionSpheres)
        {
            var at = DiagnosticLocation.ForMesh(new MeshNodeRef(MeshNodeKind.CollisionSphere, Index: index));
            string name = UserText.Printable(s.Name.Text);
            if (s.BoneIndex < -1 || s.BoneIndex >= Math.Max(boneCount, 0) && s.BoneIndex != -1)
            {
                results.Add(Make(MeshRules.SphereBone,
                    $"Collision sphere {index} ('{name}') follows bone {s.BoneIndex}, but the mesh has {boneCount} bones.",
                    "Attach the sphere to an existing bone (or -1 for none).", at, MeshFixes.SphereToRoot(mesh, index)));
            }
            if (!Finite(s.Position) || !float.IsFinite(s.Radius))
            {
                results.Add(Make(MeshRules.NonFinite, $"Collision sphere {index} ('{name}') holds a value that is not a number.",
                    "Retype its position and radius.", at));
            }
            else if (s.Radius <= 0f)
            {
                results.Add(Make(MeshRules.SphereRadius,
                    $"Collision sphere {index} ('{name}') has radius {s.Radius}, so nothing can ever hit it.",
                    "Give it a positive radius, or remove it.", at, MeshFixes.SphereRadius(index)));
            }
            CheckName(results, s.Name, $"Collision sphere {index}'s name", at, MeshFixes.TruncateSphereName(index));
            index++;
        }
    }

    private static void CheckSubmeshes(List<Diagnostic> results, V3dFile mesh, int boneCount, MeshLintContext context)
    {
        bool character = mesh.Kind == V3dKind.Character;
        int si = 0;
        foreach (var sub in mesh.Submeshes)
        {
            var subAt = DiagnosticLocation.ForMesh(new MeshNodeRef(MeshNodeKind.Submesh, si));
            CheckName(results, sub.Name, $"Submesh {si}'s name", subAt, MeshFixes.TruncateSubmeshName(si));
            if (sub.Lods.Length < 1 || sub.Lods.Length > MeshRules.MaxLods)
            {
                results.Add(Make(MeshRules.LodCount,
                    $"Submesh {si} ('{UserText.Printable(sub.Name.Text)}') has {sub.Lods.Length} levels of detail; the engine holds 1 to {MeshRules.MaxLods}.",
                    sub.Lods.Length < 1 ? "Add the geometry back (re-export)." : $"Remove detail levels beyond the {MeshRules.MaxLods} most detailed.", subAt));
            }
            for (int d = 1; d < sub.LodDistances.Length; d++)
            {
                if (!(sub.LodDistances[d] > sub.LodDistances[d - 1]))
                {
                    results.Add(Make(MeshRules.LodDistances,
                        $"Submesh {si}'s LOD {d} starts at distance {sub.LodDistances[d]}, not beyond LOD {d - 1} ({sub.LodDistances[d - 1]}), so it is never (or always) used.",
                        "Make every LOD's distance larger than the previous one's.", subAt,
                        MeshFixes.CanSort(sub) ? [MeshFixes.SortLodDistances(si)] : []));
                    break;
                }
            }
            for (int mi = 0; mi < sub.Materials.Length; mi++)
            {
                CheckName(results, sub.Materials[mi].DiffuseMap, $"Submesh {si} material {mi}'s texture name",
                    DiagnosticLocation.ForMesh(new MeshNodeRef(MeshNodeKind.Material, si, Index: mi)), MeshFixes.TruncateTextureName(si, mi));
            }

            for (int li = 0; li < sub.Lods.Length; li++)
            {
                var lod = sub.Lods[li];
                var lodAt = DiagnosticLocation.ForMesh(new MeshNodeRef(MeshNodeKind.Lod, si, li));
                if (lod.Textures.Length > MeshRules.MaxTexturesPerLod)
                {
                    results.Add(Make(MeshRules.TooManyTextures,
                        $"Submesh {si} LOD {li} uses {lod.Textures.Length} textures; the engine allows {MeshRules.MaxTexturesPerLod} per LOD.",
                        "Combine textures (an atlas) or split the object into several submeshes.", lodAt));
                }
                CheckTextures(results, lod, si, li, context);
                CheckPropPoints(results, mesh, lod, si, li, boneCount);
                for (int bi = 0; bi < lod.Batches.Length; bi++)
                    CheckBatch(results, lod, lod.Batches[bi], si, li, bi, boneCount, character);
            }
            si++;
        }
    }

    private static void CheckTextures(List<Diagnostic> results, V3dLod lod, int si, int li, MeshLintContext context)
    {
        for (int ti = 0; ti < lod.Textures.Length; ti++)
        {
            string name = lod.Textures[ti].FileName;
            var at = DiagnosticLocation.ForMesh(new MeshNodeRef(MeshNodeKind.Texture, si, li, ti));
            if (name.Length > V3dMaterial.NameSize - 1)
            {
                results.Add(Make(MeshRules.NameTooLong,
                    $"Texture '{UserText.Printable(name)}' (submesh {si} LOD {li}) is {name.Length} characters; texture names hold {V3dMaterial.NameSize - 1}.",
                    "Rename the texture file and the material to a shorter name.", at));
            }
            if (string.IsNullOrWhiteSpace(name)) continue;
            bool? exists = context.TextureExists?.Invoke(name)
                ?? (context.Resolver is { } r ? r.Resolve(name) is not null : null);
            if (exists == false)
            {
                results.Add(Make(MeshRules.MissingTexture,
                    $"Texture '{UserText.Printable(name)}' (submesh {si} LOD {li}) was not found in the document's folder, the search folders or the game.",
                    "Put the texture next to the mesh or in a search folder, or check the game directory in Settings. The preview draws a neutral material meanwhile.",
                    at,
                    new QuickFix("Open search settings…", QuickFixKind.OpenSearchSettings),
                    new QuickFix("Locate the file…", QuickFixKind.LocateFile, Payload: name)));
            }
        }
    }

    private static void CheckPropPoints(List<Diagnostic> results, V3dFile mesh, V3dLod lod, int si, int li, int boneCount)
    {
        for (int pi = 0; pi < lod.PropPoints.Length; pi++)
        {
            var p = lod.PropPoints[pi];
            var at = DiagnosticLocation.ForMesh(new MeshNodeRef(MeshNodeKind.PropPoint, si, li, pi));
            if (p.ParentIndex < -1 || (p.ParentIndex >= boneCount && p.ParentIndex != -1))
            {
                results.Add(Make(MeshRules.PropPointBone,
                    $"Prop point '{UserText.Printable(p.Name.Text)}' (submesh {si} LOD {li}) is attached to bone {p.ParentIndex}, but the mesh has {boneCount} bones.",
                    "Attach the prop point to an existing bone, or -1 for the mesh itself.", at, MeshFixes.PropToRoot(mesh, si, li, pi)));
            }
            if (!Finite(p.Position)) results.Add(Make(MeshRules.NonFinite, $"Prop point '{UserText.Printable(p.Name.Text)}' has a position that is not a number.", "Retype its position.", at));
            CheckName(results, p.Name, $"Prop point {pi}'s name", at, MeshFixes.TruncatePropName(si, li, pi));
        }
    }

    private static void CheckBatch(List<Diagnostic> results, V3dLod lod, V3dBatch batch, int si, int li, int bi, int boneCount, bool character)
    {
        string where = $"submesh {si} LOD {li} batch {bi}";
        DiagnosticLocation At(int element = -1) => DiagnosticLocation.ForMesh(new MeshNodeRef(MeshNodeKind.Batch, si, li, bi, element));
        int nv = batch.VertexCount;

        if (lod.Textures.Length > 0 && (batch.TextureIndex < 0 || batch.TextureIndex >= lod.Textures.Length))
        {
            results.Add(Make(MeshRules.BatchTexture,
                $"The {where} uses texture slot {batch.TextureIndex}, but the LOD has {lod.Textures.Length} textures.",
                "Re-export the mesh, or pick an existing material for the batch.", At()));
        }

        int bad = 0;
        for (int t = 0; t < batch.Triangles.Length; t++)
        {
            var tri = batch.Triangles[t];
            if ((tri.A >= nv || tri.B >= nv || tri.C >= nv) && bad++ < MaxPerBatch)
            {
                results.Add(Make(MeshRules.TriangleIndex,
                    $"Triangle {t} of the {where} uses vertex {Math.Max(tri.A, Math.Max(tri.B, tri.C))}, but the batch has {nv} vertices.",
                    "Re-export the mesh: its geometry is damaged.", At(t)));
            }
        }

        int nonFinite = 0;
        for (int v = 0; v < nv; v++)
        {
            if (!Finite(batch.Positions[v]) && nonFinite++ < 1)
                results.Add(Make(MeshRules.NonFinite, $"Vertex {v} of the {where} has a position that is not a number.", "Re-export the mesh.", At(v)));
        }

        if ((lod.Flags & V3dLod.FlagMorphVerticesMap) != 0 && !batch.MorphMap.IsDefaultOrEmpty)
        {
            int badMap = 0;
            for (int i = 0; i < batch.MorphMap.Length; i++)
            {
                int target = batch.MorphMap[i];
                if (target >= nv && badMap++ < 1)
                {
                    results.Add(Make(MeshRules.MorphMapIndex,
                        $"Morph map entry {i} of the {where} points at vertex {target}, but the batch has {nv} vertices; vertex animation would write outside the batch.",
                        "Re-export the mesh so the morph map matches its geometry.", At(i)));
                }
            }
        }

        if (!character || batch.BoneLinks.IsDefaultOrEmpty) return;
        int badBone = 0, badSum = 0;
        for (int v = 0; v < batch.BoneLinks.Length; v++)
        {
            var link = batch.BoneLinks[v];
            int sum = 0;
            for (int s = 0; s < 4; s++)
            {
                int w = link.GetWeight(s), b = link.GetBone(s);
                if (w == 0 || b == V3dBoneLink.NoBone) continue;
                sum += w;
                if (b >= boneCount && badBone++ < MaxPerBatch)
                {
                    results.Add(Make(MeshRules.VertexBone,
                        $"Vertex {v} of the {where} is weighted to bone {b}, but the mesh has {boneCount} bones.",
                        "Re-skin the vertex to existing bones (re-export, or rebuild through glTF import).", At(v)));
                }
            }
            if (Math.Abs(sum - MeshRules.WeightTotal) > MeshRules.WeightSumTolerance && badSum++ < 1)
            {
                results.Add(Make(MeshRules.WeightSum,
                    $"Vertex {v} of the {where} has bone weights summing to {sum} instead of {MeshRules.WeightTotal}, so the engine skins it "
                    + (sum < MeshRules.WeightTotal ? "partly towards the model origin." : "too far."),
                    "Normalise the vertex weights in the source scene and re-export (glTF import normalises them).", At(v)));
            }
        }
    }

    private static void CheckWritable(List<Diagnostic> results, V3dFile mesh)
    {
        try
        {
            V3dWriter.Write(mesh);
        }
        catch (ArgumentException ex)
        {
            results.Add(Make(MeshRules.NotWritable,
                "The mesh cannot be saved as it is: " + ex.Message.Split(" (Parameter")[0],
                "A count or size exceeds what the format can store (16-bit vertex and triangle counts per batch). Split the object into more batches or submeshes.",
                DiagnosticLocation.ForMesh(new MeshNodeRef(MeshNodeKind.Header))));
        }
    }

    private static void CheckFileName(List<Diagnostic> results, MeshLintContext context)
    {
        if (context.FileName is not { Length: > 0 } path) return;
        string name = Path.GetFileName(path);
        if (name.Length > ClipRules.MaxFileNameLength)
        {
            results.Add(Make(MeshRules.FileNameTooLong,
                $"The file name '{UserText.Printable(name)}' is {name.Length} characters; keep mesh names to {ClipRules.MaxFileNameLength} or fewer, as the engine's name buffers are small.",
                "Rename the mesh and update the tables that refer to it.",
                DiagnosticLocation.Document, new QuickFix("Save as…", QuickFixKind.SaveAs)));
        }
    }

    private static void CheckName(List<Diagnostic> results, FixedString name, string what, DiagnosticLocation at, QuickFix? fix = null)
    {
        if (name.Length == 0) return;
        if (name.Bytes.AsSpan().IndexOf((byte)0) < 0)
        {
            results.Add(Make(MeshRules.NameTooLong,
                $"{what} '{UserText.Printable(name.Text)}' fills its {name.Length}-byte field with no room for the terminator, so the engine reads past it.",
                $"Shorten the name to at most {name.Length - 1} characters.", at, fix is null ? [] : [fix]));
        }
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static Diagnostic Make(string code, string message, string help, DiagnosticLocation location, params QuickFix[] fixes)
    {
        var info = MeshRules.Find(code) ?? throw new InvalidOperationException($"Unknown rule {code}.");
        return new Diagnostic(code, info.Severity, message, help, location, fixes);
    }
}
