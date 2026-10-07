using System.Collections.Immutable;

namespace Cairn.Rfa.Docs;

/// <summary>One stored (or derived) field of a clip or mesh file, documented for tooltips and Help.</summary>
/// <param name="Id">
/// Stable key, <c>format.field</c> for header fields (<c>rfa.start_time</c>) and
/// <c>format.record.field</c> for fields of a repeated record (<c>rfa.bone.weight</c>,
/// <c>v3c.csphere.radius</c>). Lower case, words joined by underscores.
/// </param>
/// <param name="Format">"RFA" or "V3C".</param>
/// <param name="Section">Id of the <see cref="FormatSection"/> the field belongs to.</param>
/// <param name="Name">Display name, e.g. "Ramp in".</param>
/// <param name="Type">Stored type, e.g. "int32", "float[3]", "char[24]", or "derived" for a value computed from others.</param>
/// <param name="Units">Units, e.g. "ticks (1/4800 s)", "metres", or null.</param>
/// <param name="Summary">What the field holds, in one or two sentences.</param>
/// <param name="EngineUse">What the game does with it (from the decompilation and Alpine Faction), or that it ignores it.</param>
/// <param name="Limits">Valid range or hard limit, when there is one.</param>
/// <param name="ReadByGame">True when RF.exe reads it, false when it is known not to, null when unknown or not applicable.</param>
public sealed record FormatField(
    string Id, string Format, string Section, string Name, string Type, string? Units,
    string Summary, string EngineUse, string? Limits, bool? ReadByGame)
{
    /// <summary>Byte offset within the header or record (e.g. "0x24"), when fixed.</summary>
    public string? Offset { get; init; }

    /// <summary>The text a tooltip shows: summary, engine use and limits.</summary>
    public string Tooltip =>
        Summary + "\n\n" + EngineUse + (Limits is null ? "" : "\n\nLimits: " + Limits)
        + (ReadByGame == false ? "\n\nNot read by the game." : "");
}

/// <summary>A part of a file (header, a record type, a data block) and its fields in stored order.</summary>
/// <param name="Id">Stable key, e.g. "rfa.header", "v3c.batch".</param>
/// <param name="Format">"RFA" or "V3C".</param>
/// <param name="Title">Heading, e.g. "Rotation key".</param>
/// <param name="Summary">What the part is and where it sits in the file.</param>
/// <param name="Fields">The fields, in stored order (derived fields last).</param>
public sealed record FormatSection(string Id, string Format, string Title, string Summary, ImmutableArray<FormatField> Fields);

/// <summary>One format's reference: an introduction and its sections.</summary>
/// <param name="Format">"RFA" or "V3C".</param>
/// <param name="Title">Heading for the Help page.</param>
/// <param name="Summary">Introduction.</param>
/// <param name="Sections">The sections, in file order.</param>
public sealed record FormatReference(string Format, string Title, string Summary, ImmutableArray<FormatSection> Sections)
{
    /// <summary>Every field of every section, in order.</summary>
    public IEnumerable<FormatField> Fields => Sections.SelectMany(s => s.Fields);
}

/// <summary>A Help topic: plain text about how the engine behaves.</summary>
/// <param name="Id">Stable key, e.g. "engine.weights".</param>
/// <param name="Title">Heading.</param>
/// <param name="Text">Paragraphs separated by blank lines.</param>
public sealed record FormatTopic(string Id, string Title, string Text);

/// <summary>
/// The field-by-field reference for RFA clips and V3C meshes: the single source for inspector
/// tooltips and the Help → Format Reference page. Facts come from research/anim_retarget
/// (rfa_format.md, v3c_skeleton.md), the format headers in research/rf_decomp/file-formats, RF.exe's
/// disassembly and Alpine Faction's source; DESIGN.md sections 4 and 9 record how they were settled.
/// </summary>
public static partial class FormatDocs
{
    /// <summary>The RFA (animation clip) reference.</summary>
    public static FormatReference Rfa { get; } = BuildRfa();

    /// <summary>The V3C (character mesh) reference; V3M static meshes share everything but the bones and spheres.</summary>
    public static FormatReference V3c { get; } = BuildV3c();

    private static readonly ImmutableDictionary<string, FormatField> ById =
        Rfa.Fields.Concat(V3c.Fields).ToImmutableDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);

    private static readonly ImmutableDictionary<string, FormatSection> SectionsById =
        Rfa.Sections.Concat(V3c.Sections).ToImmutableDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>"How the engine plays clips": Help topics on states, actions, weights, ramps, sampling and morphs.</summary>
    public static ImmutableArray<FormatTopic> EngineTopics { get; } = BuildTopics();

    /// <summary>Every documented field, RFA first.</summary>
    public static IEnumerable<FormatField> AllFields => Rfa.Fields.Concat(V3c.Fields);

    /// <summary>
    /// The field ids the inspectors attach tooltips to (clip, bone and key inspectors; mesh structure
    /// editors). Every one is guaranteed to exist.
    /// </summary>
    public static ImmutableArray<string> InspectorIds { get; } =
    [
        // Clip inspector
        "rfa.version", "rfa.start_time", "rfa.end_time", "rfa.duration", "rfa.ramp_in", "rfa.ramp_out",
        "rfa.pos_reduction", "rfa.rot_reduction", "rfa.total_rotation", "rfa.total_translation",
        "rfa.num_bones", "rfa.num_morph_vertices", "rfa.num_morph_keyframes", "rfa.file_size",
        // Bone inspector
        "rfa.bone.index", "rfa.bone.weight", "rfa.bone.num_rot_keys", "rfa.bone.num_pos_keys",
        // Key inspector
        "rfa.rotkey.time", "rfa.rotkey.rotation", "rfa.rotkey.ease_in", "rfa.rotkey.ease_out",
        "rfa.poskey.time", "rfa.poskey.position", "rfa.poskey.in_ctrl", "rfa.poskey.out_ctrl",
        // Mesh document
        "v3c.signature", "v3c.version", "v3c.submesh.name", "v3c.submesh.num_lods", "v3c.submesh.lod_distances",
        "v3c.submesh.radius", "v3c.lod.flags", "v3c.lod.num_vertices", "v3c.lod.textures", "v3c.batch.num_vertices",
        "v3c.batch.num_triangles", "v3c.batch.bone_links", "v3c.material.diffuse_map", "v3c.material.emissive",
        "v3c.prop.name", "v3c.prop.parent", "v3c.prop.position", "v3c.prop.rotation",
        "v3c.csphere.name", "v3c.csphere.bone", "v3c.csphere.position", "v3c.csphere.radius",
        "v3c.bone.name", "v3c.bone.parent", "v3c.bone.rotation", "v3c.bone.position", "v3c.bone.num_bones",
    ];

    /// <summary>The field with this id (ignoring case), or null.</summary>
    public static FormatField? Find(string id) =>
        id is not null && ById.TryGetValue(id, out var field) ? field : null;

    /// <summary>The section with this id (ignoring case), or null.</summary>
    public static FormatSection? FindSection(string id) =>
        id is not null && SectionsById.TryGetValue(id, out var section) ? section : null;

    /// <summary>The engine topic with this id (ignoring case), or null.</summary>
    public static FormatTopic? FindTopic(string id) =>
        EngineTopics.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    // ── Builders ─────────────────────────────────────────────────────────────

    private sealed class SectionBuilder(string format, string id, string title, string summary)
    {
        private readonly ImmutableArray<FormatField>.Builder _fields = ImmutableArray.CreateBuilder<FormatField>();

        public SectionBuilder Field(
            string id, string name, string type, string? units, string summary, string engineUse,
            string? limits = null, bool? read = null, string? offset = null)
        {
            _fields.Add(new FormatField(id, format, Id, name, type, units, summary, engineUse, limits, read) { Offset = offset });
            return this;
        }

        public string Id => id;

        public FormatSection Build() => new(id, format, title, summary, _fields.ToImmutable());
    }
}
