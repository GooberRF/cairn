using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Linting;

/// <summary>
/// How much a problem matters (DESIGN.md "Problems"). Errors mean the engine will misbehave (or the
/// file cannot be saved); warnings mean it loads but not the way it was meant; info is tidy-up only.
/// </summary>
public enum DiagnosticSeverity
{
    /// <summary>A tidy-up; nothing is wrong in game.</summary>
    Info,
    /// <summary>Loads, but probably not as intended.</summary>
    Warning,
    /// <summary>The engine will misbehave, crash, or the file cannot be written.</summary>
    Error,
}

/// <summary>What a quick fix does. <see cref="Edit"/> fixes are pure functions; the others need the UI.</summary>
public enum QuickFixKind
{
    /// <summary>Applies <see cref="QuickFix.ClipEdit"/> or <see cref="QuickFix.MeshEdit"/> as one undo step.</summary>
    Edit,
    /// <summary>Opens the preview-mesh picker (a bone-count mismatch may be the wrong mesh, not a broken clip).</summary>
    PickPreviewMesh,
    /// <summary>Opens the Conform to Skeleton tool (reorder/add/drop tracks by bone name).</summary>
    ConformToSkeleton,
    /// <summary>Opens Save As so the clip can get another name.</summary>
    SaveAs,
    /// <summary>Opens Settings (game directory, search folders), for "not found" problems.</summary>
    OpenSearchSettings,
    /// <summary>Lets the user pick the file to use instead of a missing one (the App's texture browser; <see cref="QuickFix.Payload"/> is the missing name).</summary>
    LocateFile,
}

/// <summary>One offered repair for a diagnostic.</summary>
/// <param name="Title">Button text, e.g. "Add position keys from the rest pose".</param>
/// <param name="Kind">How the UI carries the fix out.</param>
/// <param name="ClipEdit">For an <see cref="QuickFixKind.Edit"/> fix on a clip: the pure edit.</param>
/// <param name="MeshEdit">For an <see cref="QuickFixKind.Edit"/> fix on a mesh: the pure edit.</param>
/// <param name="Payload">Extra context for UI-driven fixes, such as a file name.</param>
public sealed record QuickFix(
    string Title,
    QuickFixKind Kind,
    Func<RfaClip, RfaClip>? ClipEdit = null,
    Func<V3dFile, V3dFile>? MeshEdit = null,
    string? Payload = null)
{
    /// <summary>Applies a clip edit fix; any other fix returns the clip unchanged.</summary>
    public RfaClip Apply(RfaClip clip) => ClipEdit is null ? clip : ClipEdit(clip);

    /// <summary>Applies a mesh edit fix; any other fix returns the mesh unchanged.</summary>
    public V3dFile Apply(V3dFile mesh) => MeshEdit is null ? mesh : MeshEdit(mesh);
}

/// <summary>What part of a document a diagnostic points at.</summary>
public enum DiagnosticTarget
{
    /// <summary>The document as a whole (or its file name).</summary>
    Document,
    /// <summary>A header field (<see cref="DiagnosticLocation.Field"/>, a <c>Docs.FormatDocs</c> id).</summary>
    HeaderField,
    /// <summary>A bone (track) of a clip.</summary>
    Bone,
    /// <summary>One key of a clip.</summary>
    Key,
    /// <summary>The clip's morph data.</summary>
    Morph,
    /// <summary>A node of a mesh document (<see cref="DiagnosticLocation.MeshNode"/>).</summary>
    MeshNode,
}

/// <summary>The kind of mesh node a mesh diagnostic points at.</summary>
public enum MeshNodeKind
{
    /// <summary>The file header.</summary>
    Header,
    /// <summary>A submesh.</summary>
    Submesh,
    /// <summary>A level of detail.</summary>
    Lod,
    /// <summary>A geometry batch.</summary>
    Batch,
    /// <summary>A material of a submesh.</summary>
    Material,
    /// <summary>An entry of a LOD's texture list.</summary>
    Texture,
    /// <summary>A prop point of a LOD.</summary>
    PropPoint,
    /// <summary>A bone.</summary>
    Bone,
    /// <summary>A collision sphere.</summary>
    CollisionSphere,
}

/// <summary>
/// Addresses a mesh node. Unused coordinates are -1: a batch is (submesh, LOD, batch); a material
/// (submesh, index); a LOD texture or prop point (submesh, LOD, index); a bone or collision sphere
/// (index); a vertex or triangle inside a batch goes in <see cref="Element"/>.
/// </summary>
/// <param name="Kind">The node kind.</param>
/// <param name="Submesh">Submesh index in file order.</param>
/// <param name="Lod">LOD index.</param>
/// <param name="Index">Batch, material, texture, prop point, bone or sphere index.</param>
/// <param name="Element">A vertex or triangle index inside a batch, when the problem is that precise.</param>
public readonly record struct MeshNodeRef(MeshNodeKind Kind, int Submesh = -1, int Lod = -1, int Index = -1, int Element = -1)
{
    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        MeshNodeKind.Header => "header",
        MeshNodeKind.Submesh => $"submesh {Submesh}",
        MeshNodeKind.Lod => $"submesh {Submesh} LOD {Lod}",
        MeshNodeKind.Batch => $"submesh {Submesh} LOD {Lod} batch {Index}" + (Element >= 0 ? $" element {Element}" : ""),
        MeshNodeKind.Material => $"submesh {Submesh} material {Index}",
        MeshNodeKind.Texture => $"submesh {Submesh} LOD {Lod} texture {Index}",
        MeshNodeKind.PropPoint => $"submesh {Submesh} LOD {Lod} prop point {Index}",
        MeshNodeKind.Bone => $"bone {Index}",
        MeshNodeKind.CollisionSphere => $"collision sphere {Index}",
        _ => Kind.ToString(),
    };
}

/// <summary>Where a problem is, for the Problems panel, the timeline markers and the inspector.</summary>
/// <param name="Target">What kind of place this is.</param>
/// <param name="Field">For a header field: its <c>Docs.FormatDocs</c> id (e.g. <c>rfa.ramp_in</c>).</param>
/// <param name="Bone">For a bone or key: the bone index.</param>
/// <param name="Key">For a key: the key.</param>
/// <param name="Time">For a key: its time in ticks (for the timeline marker).</param>
/// <param name="MeshNode">For a mesh node: which one.</param>
public sealed record DiagnosticLocation(
    DiagnosticTarget Target,
    string? Field = null,
    int? Bone = null,
    KeyRef? Key = null,
    int? Time = null,
    MeshNodeRef? MeshNode = null)
{
    /// <summary>The whole document.</summary>
    public static DiagnosticLocation Document { get; } = new(DiagnosticTarget.Document);

    /// <summary>The morph data.</summary>
    public static DiagnosticLocation Morph { get; } = new(DiagnosticTarget.Morph);

    /// <summary>A header field by its <c>FormatDocs</c> id.</summary>
    public static DiagnosticLocation Header(string field) => new(DiagnosticTarget.HeaderField, Field: field);

    /// <summary>A bone.</summary>
    public static DiagnosticLocation ForBone(int bone) => new(DiagnosticTarget.Bone, Bone: bone);

    /// <summary>A key, with its time.</summary>
    public static DiagnosticLocation ForKey(KeyRef key, int time) => new(DiagnosticTarget.Key, Bone: key.Bone, Key: key, Time: time);

    /// <summary>A mesh node.</summary>
    public static DiagnosticLocation ForMesh(MeshNodeRef node) => new(DiagnosticTarget.MeshNode, MeshNode: node);
}

/// <summary>
/// One problem found in a clip or mesh. <see cref="Message"/> says what is wrong and
/// <see cref="Help"/> says exactly how to fix it, both written for modders rather than programmers.
/// </summary>
/// <param name="Code">Stable rule id (<see cref="ClipRules"/>, <see cref="MeshRules"/>), e.g. <c>RFA003</c>.</param>
/// <param name="Severity">Error, warning or info.</param>
/// <param name="Message">What is wrong, in plain language.</param>
/// <param name="Help">How to fix it.</param>
/// <param name="Location">Where it is.</param>
/// <param name="QuickFixes">Offered repairs.</param>
public sealed record Diagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    string Help,
    DiagnosticLocation Location,
    IReadOnlyList<QuickFix>? QuickFixes = null)
{
    /// <summary>Offered repairs; never null.</summary>
    public IReadOnlyList<QuickFix> QuickFixes { get; init; } = QuickFixes ?? [];

    /// <inheritdoc />
    public override string ToString() => $"{Code} {Severity}: {Message}";
}
