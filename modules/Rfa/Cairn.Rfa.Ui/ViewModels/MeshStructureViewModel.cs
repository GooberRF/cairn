using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Ui.ViewModels.MeshEditing;
using Cairn.Rfa.Docs;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Linting;

namespace Cairn.Rfa.Ui.ViewModels;

/// <summary>One node of the mesh structure tree.</summary>
public sealed class MeshNodeViewModel : ObservableObject
{
    private bool _isExpanded;
    private bool _isSelected;

    public MeshNodeViewModel(string header, MeshNodeRef? node, string glyph, string toolTip)
    {
        Header = header;
        Node = node;
        Glyph = glyph;
        ToolTip = toolTip;
    }

    public string Header { get; }

    /// <summary>The address of the node (null for grouping nodes such as "Materials").</summary>
    public MeshNodeRef? Node { get; }

    /// <summary>Segoe MDL2 glyph.</summary>
    public string Glyph { get; }

    public string ToolTip { get; }

    public ObservableCollection<MeshNodeViewModel> Children { get; } = [];

    public List<FactRow> Facts { get; } = [];

    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    /// <summary>The parent node (for revealing a node: its ancestors are expanded).</summary>
    public MeshNodeViewModel? Parent { get; set; }

    public MeshNodeViewModel Add(MeshNodeViewModel child)
    {
        child.Parent = this;
        Children.Add(child);
        return child;
    }

    public IEnumerable<MeshNodeViewModel> Descendants() => Children.SelectMany(c => c.Descendants().Prepend(c));
}

/// <summary>
/// The mesh document's structure tree (submeshes, LODs, batches, textures, materials, prop points,
/// bones, collision spheres) with the selected node's editor (phase 6: bone, collision sphere, prop
/// point, material, LOD and submesh nodes; <see cref="MeshNodeEditor"/>) and its facts below the tree;
/// selecting a node highlights it in the viewport. The tree is rebuilt after every snapshot change and
/// the selection survives it: the same node is selected again (or the node an edit asked for, such as
/// an added sphere), and its editor is kept, so a number box keeps its focus through a spinner run.
/// </summary>
public sealed class MeshStructureViewModel : ObservableObject
{
    private readonly MeshDocumentViewModel _document;
    private MeshNodeViewModel? _selected;
    private MeshNodeEditor? _editor;
    private MeshNodeRef? _pendingSelect;
    private bool _rebuilding;

    internal MeshStructureViewModel(MeshDocumentViewModel document)
    {
        _document = document;
    }

    public ObservableCollection<MeshNodeViewModel> Roots { get; } = [];

    /// <summary>The selected node; its editor and facts show below the tree and the viewport highlights it.</summary>
    public MeshNodeViewModel? Selected
    {
        get => _selected;
        set
        {
            // While the tree is rebuilt the TreeView reports its old item going away: not a user choice.
            if (_rebuilding || ReferenceEquals(value, _selected)) return;
            SetSelected(value);
        }
    }

    private void SetSelected(MeshNodeViewModel? value)
    {
        // The previous node lets go of its flag too: a virtualised container realised later must not come
        // back selected beside the new one.
        if (_selected is not null && !ReferenceEquals(_selected, value)) _selected.IsSelected = false;
        _selected = value;
        RaiseAll(nameof(Selected), nameof(SelectedFacts), nameof(SelectedHeader), nameof(HasFacts));
        UpdateEditor(value);
        Raise(nameof(ShowFactsHeading));
        _document.OnStructureSelectionChanged(value);
    }

    /// <summary>The selected node's editor, or null for a node with facts only (header, batch, texture, groups).</summary>
    public MeshNodeEditor? Editor
    {
        get => _editor;
        private set
        {
            if (ReferenceEquals(_editor, value)) return;
            _editor = value;
            RaiseAll(nameof(Editor), nameof(HasEditor), nameof(ShowFactsHeading));
        }
    }

    public bool HasEditor => _editor is not null;

    /// <summary>True when an editor and facts both show (the facts get a heading).</summary>
    public bool ShowFactsHeading => HasEditor && HasFacts;

    /// <summary>The Mesh menu commands (the tree's Del and Alt+Up/Down run them).</summary>
    public MeshCommands Tools => _document.Shell.MeshTools;

    /// <summary>The document the tree belongs to.</summary>
    public MeshDocumentViewModel Document => _document;

    /// <summary>True for a document that opens read-only (a .v3m): the editors show disabled.</summary>
    public bool IsReadOnly => _document.IsReadOnly;

    public IReadOnlyList<FactRow> SelectedFacts => _selected is null ? [] : [.. _selected.Facts];

    public bool HasFacts => _selected is { Facts.Count: > 0 };

    public string SelectedHeader => _selected?.Header ?? "Select a node to see and edit it";

    /// <summary>Raised when the selected node's name box should take the keyboard (Mesh › Rename Selected, F2).</summary>
    public event EventHandler? RenameRequested;

    /// <summary>Asks the view to focus the selected node's name box.</summary>
    public void RequestRename() => RenameRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>The node the next rebuild selects instead of the current one (an added, moved or neighbouring node).</summary>
    public void SelectAfterRebuild(MeshNodeRef node) => _pendingSelect = node;

    /// <summary>Selects (and reveals) the node addressed by <paramref name="node"/>.</summary>
    public void Reveal(MeshNodeRef node)
    {
        var match = Find(node);
        if (match is null) return;
        Expand(match);
        if (!ReferenceEquals(match, _selected)) SetSelected(match);
    }

    /// <summary>
    /// Keeps the tree on the document's bone selection (the viewport, the Problems panel and Select All
    /// write it): the active bone's node is selected, expanded into view, and its editor shown; when no
    /// bone is selected any more a selected bone node lets go. Other nodes (a sphere, a material) stay
    /// selected while the bone selection is merely cleared.
    /// </summary>
    public void FollowBoneSelection(int activeBone)
    {
        if (_rebuilding) return;
        if (activeBone < 0)
        {
            if (_selected?.Node is { Kind: MeshNodeKind.Bone }) SetSelected(null);
            return;
        }
        if (_selected?.Node is { Kind: MeshNodeKind.Bone } r && r.Index == activeBone) return;
        Reveal(new MeshNodeRef(MeshNodeKind.Bone, -1, -1, activeBone));
    }

    private MeshNodeViewModel? Find(MeshNodeRef node) =>
        Roots.SelectMany(r => r.Descendants().Prepend(r)).FirstOrDefault(n => n.Node is { } r && Same(r, node));

    private static void Expand(MeshNodeViewModel match)
    {
        for (var p = match.Parent; p is not null; p = p.Parent) p.IsExpanded = true;
        match.IsSelected = true;
    }

    private static bool Same(MeshNodeRef a, MeshNodeRef b) =>
        a.Kind == b.Kind && a.Submesh == b.Submesh && a.Lod == b.Lod && a.Index == b.Index;

    private void UpdateEditor(MeshNodeViewModel? node)
    {
        var mesh = _document.Current;
        if (node?.Node is not { } r)
        {
            Editor = null;
            return;
        }
        if (_editor is { } e && e.Edits(r) && e.Exists(mesh))
        {
            e.Refresh();
            return;
        }
        Editor = MeshNodeEditors.Create(_document, r);
    }

    /// <summary>Rebuilds the tree from <paramref name="mesh"/>, keeping (or moving) the selection.</summary>
    public void Rebuild(V3dFile mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var wanted = _pendingSelect ?? _selected?.Node;
        bool asked = _pendingSelect is not null;
        _pendingSelect = null;
        var pendingTextures = new List<(MeshNodeViewModel Node, int Fact, string Name)>();
        _rebuilding = true;
        try
        {
            Roots.Clear();
            Build(mesh, pendingTextures);
        }
        finally { _rebuilding = false; }

        MeshNodeViewModel? match = null;
        if (wanted is { } w)
        {
            match = Find(w);
            // The node is gone (undo of an add, a removal): its predecessor of the same kind, if any.
            for (int i = w.Index - 1; match is null && !asked && i >= 0 && w.Kind is MeshNodeKind.CollisionSphere or MeshNodeKind.PropPoint or MeshNodeKind.Bone; i--)
                match = Find(w with { Index = i });
        }
        if (match is not null) Expand(match);
        SetSelected(match);
        if (pendingTextures.Count > 0) _ = ResolveTexturesAsync(pendingTextures, ++_generation);
    }

    private void Build(V3dFile mesh, List<(MeshNodeViewModel Node, int Fact, string Name)> pendingTextures)
    {
        var ci = CultureInfo.CurrentCulture;
        string Tip(string id) => FormatDocs.Find(id)?.Tooltip ?? string.Empty;

        var header = new MeshNodeViewModel("Header", new MeshNodeRef(MeshNodeKind.Header), "", Tip("v3c.signature"));
        header.Facts.Add(new FactRow("Kind", mesh.Kind == V3dKind.Character ? "Character (RFCM, .v3c)" : "Static (RF3D, .v3m)", Tip("v3c.signature")));
        header.Facts.Add(new FactRow("Version", "0x" + mesh.Header.Version.ToString("X", CultureInfo.InvariantCulture), Tip("v3c.version")));
        header.Facts.Add(new FactRow("Submeshes", mesh.Submeshes.Count().ToString(ci), ""));
        header.Facts.Add(new FactRow("Materials (header)", mesh.Header.TotalMaterials.ToString(ci), ""));
        header.Facts.Add(new FactRow("Bones", mesh.Bones.Length.ToString(ci), Tip("v3c.bone.num_bones")));
        header.Facts.Add(new FactRow("Collision spheres", mesh.CollisionSpheres.Count().ToString(ci), ""));
        header.Facts.Add(new FactRow("Origin", _document.OriginText, "Where this mesh was opened from."));
        Roots.Add(header);

        int s = 0;
        foreach (var sub in mesh.Submeshes)
        {
            int si = s;
            var subNode = new MeshNodeViewModel($"Submesh {si}: {sub.Name.Text}", new MeshNodeRef(MeshNodeKind.Submesh, si), "", Tip("v3c.submesh.name"))
            {
                IsExpanded = true,
            };
            // The name is in the editor above the facts.
            subNode.Facts.Add(new FactRow("Parent", sub.ParentName.Text, Tip("v3c.submesh.parent_name")));
            subNode.Facts.Add(new FactRow("LODs", sub.Lods.Length.ToString(ci), Tip("v3c.submesh.num_lods")));
            subNode.Facts.Add(new FactRow("LOD distances", string.Join(", ", sub.LodDistances.Select(d => d.ToString("0.##", ci))), Tip("v3c.submesh.lod_distances")));
            subNode.Facts.Add(new FactRow("Offset", Vec(sub.Offset), Tip("v3c.submesh.offset")));
            subNode.Facts.Add(new FactRow("Radius", sub.Radius.ToString("0.###", ci) + " m", Tip("v3c.submesh.radius")));
            subNode.Facts.Add(new FactRow("Bounds", $"{Vec(sub.AabbMin)} to {Vec(sub.AabbMax)}", ""));
            Roots.Add(subNode);

            for (int l = 0; l < sub.Lods.Length; l++)
            {
                var lod = sub.Lods[l];
                var lodNode = subNode.Add(new MeshNodeViewModel($"LOD {l}", new MeshNodeRef(MeshNodeKind.Lod, si, l), "", Tip("v3c.lod.flags"))
                {
                    IsExpanded = l == 0,
                });
                int tris = lod.Batches.Sum(b => b.TriangleCount), verts = lod.Batches.Sum(b => b.VertexCount);
                lodNode.Facts.Add(new FactRow("Flags", "0x" + lod.Flags.ToString("X2", CultureInfo.InvariantCulture) + LodFlags(lod.Flags), Tip("v3c.lod.flags")));
                lodNode.Facts.Add(new FactRow("Vertices (declared)", lod.VertexCount.ToString("N0", ci), Tip("v3c.lod.num_vertices")));
                lodNode.Facts.Add(new FactRow("Vertices (batches)", verts.ToString("N0", ci), ""));
                lodNode.Facts.Add(new FactRow("Triangles", tris.ToString("N0", ci), ""));
                lodNode.Facts.Add(new FactRow("Batches", lod.Batches.Length.ToString(ci), Tip("v3c.lod.num_batches")));
                lodNode.Facts.Add(new FactRow("Prop points", lod.PropPoints.Length.ToString(ci), Tip("v3c.lod.num_prop_points")));

                for (int b = 0; b < lod.Batches.Length; b++)
                {
                    var batch = lod.Batches[b];
                    string texture = batch.TextureIndex >= 0 && batch.TextureIndex < lod.Textures.Length ? lod.Textures[batch.TextureIndex].FileName : "(none)";
                    var batchNode = lodNode.Add(new MeshNodeViewModel($"Batch {b}: {texture}", new MeshNodeRef(MeshNodeKind.Batch, si, l, b), "", Tip("v3c.batch.num_vertices")));
                    int doubleSided = batch.Triangles.Count(t => (t.Flags & V3dTriangle.DoubleSided) != 0);
                    batchNode.Facts.Add(new FactRow("Texture", $"{texture} (slot {batch.TextureIndex})", Tip("v3c.lod.textures")));
                    batchNode.Facts.Add(new FactRow("Vertices", batch.VertexCount.ToString("N0", ci), Tip("v3c.batch.num_vertices")));
                    batchNode.Facts.Add(new FactRow("Triangles", batch.TriangleCount.ToString("N0", ci) + (doubleSided > 0 ? $" ({doubleSided:N0} double-sided)" : ""), Tip("v3c.batch.num_triangles")));
                    batchNode.Facts.Add(new FactRow("Render flags", "0x" + batch.RenderFlags.ToString("X6", CultureInfo.InvariantCulture), Tip("v3c.batch.render_flags")));
                    batchNode.Facts.Add(new FactRow("Bone links", batch.BoneLinks.Length > 0 ? "yes" : "none", Tip("v3c.batch.bone_links")));
                    batchNode.Facts.Add(new FactRow("Morph map", batch.MorphMap.Length > 0 ? $"{batch.MorphMap.Length:N0} entries" : "none", Tip("v3c.batch.morph_map")));
                }

                if (lod.Textures.Length > 0)
                {
                    var texturesNode = lodNode.Add(new MeshNodeViewModel($"Textures ({lod.Textures.Length})", null, "", Tip("v3c.lod.textures")));
                    for (int t = 0; t < lod.Textures.Length; t++)
                    {
                        var tex = lod.Textures[t];
                        var texNode = texturesNode.Add(new MeshNodeViewModel($"{t}: {tex.FileName}", new MeshNodeRef(MeshNodeKind.Texture, si, l, t), "", Tip("v3c.lod.textures")));
                        texNode.Facts.Add(new FactRow("File", tex.FileName, Tip("v3c.lod.textures")));
                        texNode.Facts.Add(new FactRow("Material", tex.MaterialIndex.ToString(ci), "Edit the texture name on the material node (the LOD entries follow)."));
                        texNode.Facts.Add(new FactRow("Found", "looking…", "Where the resolver finds this texture."));
                        pendingTextures.Add((texNode, texNode.Facts.Count - 1, tex.FileName));
                    }
                }
            }

            if (sub.Materials.Length > 0)
            {
                var materials = subNode.Add(new MeshNodeViewModel($"Materials ({sub.Materials.Length})", null, "", Tip("v3c.material.diffuse_map")));
                for (int m = 0; m < sub.Materials.Length; m++)
                {
                    var mat = sub.Materials[m];
                    var matNode = materials.Add(new MeshNodeViewModel($"{m}: {mat.DiffuseMap.Text}", new MeshNodeRef(MeshNodeKind.Material, si, -1, m), "", Tip("v3c.material.diffuse_map")));
                    // Every stored field is in the editor; the facts say where the material is used.
                    var uses = new List<string>();
                    for (int l = 0; l < sub.Lods.Length; l++)
                    {
                        for (int t = 0; t < sub.Lods[l].Textures.Length; t++)
                        {
                            if (sub.Lods[l].Textures[t].MaterialIndex == m) uses.Add($"LOD {l} slot {t} ({sub.Lods[l].Textures[t].FileName})");
                        }
                    }
                    matNode.Facts.Add(new FactRow("LOD textures", uses.Count > 0 ? string.Join(", ", uses) : "none", Tip("v3c.lod.textures")));
                }
            }
            s++;
        }

        var props = _document.Scene.PropPoints;
        if (props.Count > 0)
        {
            var propsNode = new MeshNodeViewModel($"Prop points ({props.Count})", null, "", Tip("v3c.prop.name"));
            int lodsTotal = mesh.Submeshes.Sum(x => x.Lods.Length);
            for (int p = 0; p < props.Count; p++)
            {
                var prop = props[p];
                int carrying = mesh.Submeshes.SelectMany(x => x.Lods).Count(l => l.PropPoints.Length > p);
                var node = propsNode.Add(new MeshNodeViewModel(prop.Name.Text, new MeshNodeRef(MeshNodeKind.PropPoint, 0, 0, p), "", Tip("v3c.prop.name")));
                node.Facts.Add(new FactRow("Rotation (stored)", Quat(prop.Rotation), Tip("v3c.prop.rotation")));
                node.Facts.Add(new FactRow("Carried by", $"{carrying} of {lodsTotal} LODs", "Prop points are stored per LOD; edits apply to every LOD that has this one."));
            }
            Roots.Add(propsNode);
        }

        if (mesh.Bones.Length > 0)
        {
            var bones = mesh.Bones;
            var bonesNode = new MeshNodeViewModel($"Bones ({bones.Length})", null, "", Tip("v3c.bone.num_bones")) { IsExpanded = true };
            var nodes = new MeshNodeViewModel[bones.Length];
            var skeleton = _document.Scene.Skeleton;
            var parents = skeleton.Count == bones.Length ? skeleton.EffectiveParents : [.. bones.Select(_ => -1)];
            for (int i = 0; i < bones.Length; i++)
            {
                nodes[i] = new MeshNodeViewModel($"{i}: {bones[i].Name.Text}", new MeshNodeRef(MeshNodeKind.Bone, -1, -1, i), "", Tip("v3c.bone.name"))
                {
                    IsExpanded = true,
                };
                // Name, parent and the bind pose are in the editor; the facts keep the raw stored values.
                nodes[i].Facts.Add(new FactRow("Index", i.ToString(ci), "Clips address bones by this index."));
                nodes[i].Facts.Add(new FactRow("Children", parents.Count(x => x == i).ToString(ci), ""));
                nodes[i].Facts.Add(new FactRow("Stored rotation", Quat(bones[i].Rotation), Tip("v3c.bone.rotation")));
                nodes[i].Facts.Add(new FactRow("Stored position", Vec(bones[i].Position), Tip("v3c.bone.position")));
            }
            for (int i = 0; i < bones.Length; i++)
            {
                int parent = parents[i];
                if (parent >= 0) nodes[parent].Add(nodes[i]);
                else bonesNode.Add(nodes[i]);
            }
            Roots.Add(bonesNode);
        }

        var spheres = mesh.CollisionSpheres.ToList();
        if (spheres.Count > 0)
        {
            var spheresNode = new MeshNodeViewModel($"Collision spheres ({spheres.Count})", null, "", Tip("v3c.csphere.name"));
            for (int i = 0; i < spheres.Count; i++)
            {
                var sp = spheres[i];
                var node = spheresNode.Add(new MeshNodeViewModel(sp.Name.Text, new MeshNodeRef(MeshNodeKind.CollisionSphere, -1, -1, i), "", Tip("v3c.csphere.name")));
                node.Facts.Add(new FactRow("Centre at rest (model)", Vec(MeshEditorFacts.RestCentre(mesh, sp)), "Where the sphere's centre is in model space in the bind pose."));
            }
            Roots.Add(spheresNode);
        }
    }

    private int _generation;

    /// <summary>Looks the textures up off the UI thread (a miss can mean scanning many archives).</summary>
    private async Task ResolveTexturesAsync(List<(MeshNodeViewModel Node, int Fact, string Name)> pending, int generation)
    {
        using var busy = Cairn.Ui.Services.BusyTracker.Begin("texture facts");
        await _document.Shell.Assets.ArchivesIndexed.ConfigureAwait(true);
        if (generation != _generation) return;
        var results = await Task.Run(() => pending.Select(p => _document.DescribeTexture(p.Name)).ToList()).ConfigureAwait(true);
        if (generation != _generation) return;
        for (int i = 0; i < pending.Count; i++)
        {
            var (node, fact, _) = pending[i];
            node.Facts[fact] = node.Facts[fact] with { Value = results[i] };
            if (ReferenceEquals(node, _selected)) Raise(nameof(SelectedFacts));
        }
    }

    private static string LodFlags(uint flags)
    {
        var parts = new List<string>();
        if ((flags & V3dLod.FlagMorphVerticesMap) != 0) parts.Add("morph map");
        if ((flags & V3dLod.FlagCharacter) != 0) parts.Add("character");
        if ((flags & V3dLod.FlagReflection) != 0) parts.Add("reflection");
        if ((flags & V3dLod.FlagTrianglePlanes) != 0) parts.Add("triangle planes");
        return parts.Count == 0 ? string.Empty : " (" + string.Join(", ", parts) + ")";
    }

    private static string Vec(Vector3 v) => string.Format(CultureInfo.CurrentCulture, "({0:0.###}, {1:0.###}, {2:0.###})", Clean(v.X), Clean(v.Y), Clean(v.Z));

    /// <summary>Rounded to 3 digits, without the "-0" a tiny negative would print as.</summary>
    private static double Clean(float value) => Math.Round(value, 3) + 0.0;

    private static string Quat(Quaternion q) => string.Format(CultureInfo.CurrentCulture, "({0:0.####}, {1:0.####}, {2:0.####}, {3:0.####})", q.X, q.Y, q.Z, q.W);
}
