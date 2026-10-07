using Cairn.Assets;
using Cairn.Rfa.Linting;
using Cairn.Vfx.Formats;

namespace Cairn.Vfx.Linting;

/// <summary>
/// Where a VFX problem is. Unused coordinates are null. <see cref="Section"/> is the index into
/// <see cref="VfxFile.Sections"/>; <see cref="Material"/> is a material slot of a mesh (or a material
/// section index for particle systems and unused materials); <see cref="Face"/> a face of a mesh;
/// <see cref="Key"/> names the field or value involved (a docs id such as <c>vfx.header.end_frame</c>,
/// or a texture name).
/// </summary>
public sealed record VfxLocation(string? File = null, int? Section = null, int? Material = null, int? Face = null, string? Key = null)
{
    /// <summary>The file as a whole.</summary>
    public static VfxLocation Document { get; } = new();

    /// <summary>A header field by its docs id.</summary>
    public static VfxLocation Header(string field) => new(Key: field);

    /// <summary>Short text for the Problems list, e.g. "section 3, face 12".</summary>
    public string Display
    {
        get
        {
            var parts = new List<string>();
            if (File is not null) parts.Add(File);
            if (Section is { } s) parts.Add($"section {s}");
            if (Material is { } m) parts.Add($"material {m}");
            if (Face is { } f) parts.Add($"face {f}");
            if (Key is not null) parts.Add(Key);
            return parts.Count == 0 ? "file" : string.Join(", ", parts);
        }
    }

    /// <inheritdoc />
    public override string ToString() => Display;
}

/// <summary>One offered repair: a pure edit of the whole file.</summary>
/// <param name="Label">Button text.</param>
/// <param name="Apply">The edit.</param>
public sealed record VfxQuickFix(string Label, Func<VfxFile, VfxFile> Apply);

/// <summary>One problem found in a VFX file.</summary>
/// <param name="Code">Stable rule id from <see cref="VfxRules"/>, e.g. <c>VFX004</c>.</param>
/// <param name="Severity">Error, warning or info (shared with the RFA linter).</param>
/// <param name="Message">What is wrong, specifically.</param>
/// <param name="Help">How to fix it.</param>
/// <param name="Location">Where it is.</param>
/// <param name="QuickFixes">Offered repairs.</param>
public sealed record VfxDiagnostic(string Code, DiagnosticSeverity Severity, string Message, string Help,
    VfxLocation Location, IReadOnlyList<VfxQuickFix>? QuickFixes = null)
{
    /// <summary>Offered repairs; never null.</summary>
    public IReadOnlyList<VfxQuickFix> QuickFixes { get; init; } = QuickFixes ?? [];

    /// <inheritdoc />
    public override string ToString() => $"{Code} {Severity}: {Message} ({Location.Display})";
}

/// <summary>Inputs to the linter besides the file.</summary>
/// <param name="FileName">The file's name, shown in locations.</param>
/// <param name="Resolver">When set, texture names are looked up through it.</param>
public sealed record VfxLintContext(string? FileName = null, AssetResolver? Resolver = null)
{
    /// <summary>No file name and no texture lookup.</summary>
    public static VfxLintContext Default { get; } = new();
}

/// <summary>A catalogue entry for one rule.</summary>
public sealed record VfxRule(string Code, string Title, DiagnosticSeverity Severity, string Help);

/// <summary>The rule catalogue. Codes are stable; text is written for modders.</summary>
public static class VfxRules
{
    public const string BadVersion = "VFX001", EngineFatalVersion = "VFX002", OldVersion = "VFX003",
        MissingFaceVertex = "VFX004", VertexOutOfRange = "VFX005", FaceMaterialOutOfRange = "VFX006",
        MaterialSlotOutOfRange = "VFX007", ParticleMaterialOutOfRange = "VFX008", TextureNotFound = "VFX009",
        PlaceholderTexture = "VFX010", TextureExtension = "VFX011", ParentNotInFile = "VFX012",
        FrameCountMismatch = "VFX013", MeshOutsideEndFrame = "VFX014", EndFrameMismatch = "VFX015",
        SectionName = "VFX016", MissingSpacewarp = "VFX017", ParticleRange = "VFX018", UnusedMaterial = "VFX019",
        UnexercisedFeature = "VFX020", NonFinite = "VFX021", ZeroAreaFace = "VFX022", EmptyMesh = "VFX023";

    private static readonly DiagnosticSeverity E = DiagnosticSeverity.Error, W = DiagnosticSeverity.Warning, I = DiagnosticSeverity.Info;

    /// <summary>Every rule, in code order.</summary>
    public static ImmutableArray<VfxRule> All { get; } =
    [
        new(BadVersion, "Unsupported version", E, "The game only loads versions 0x30000 to 0x40006. Re-export the effect, or open a stock file of the right version and copy the content across."),
        new(EngineFatalVersion, "Version the game refuses", E, "Versions 0x40000 to 0x40004 parse but the game refuses to load them. Save the file as version 0x40006."),
        new(OldVersion, "Older version", I, "Older versions load in game, but Cairn edits and saves the current version (0x40006). Convert the file to edit it."),
        new(MissingFaceVertex, "Face uses a missing face-vertex record", E, "Each face corner must name an existing face-vertex record of its mesh. The game reads past the end of the list and crashes. Re-export the mesh."),
        new(VertexOutOfRange, "Vertex index out of range", E, "Every face corner and face-vertex record must name a vertex below the mesh's vertex count; anything else crashes the game. Re-export the mesh."),
        new(FaceMaterialOutOfRange, "Face material out of range", E, "A face's material index must be below the number of material slots of its mesh."),
        new(MaterialSlotOutOfRange, "Material slot out of range", E, "Each material slot of a mesh must name an existing material section."),
        new(ParticleMaterialOutOfRange, "Particle material out of range", E, "A particle system's material index must name an existing material section."),
        new(TextureNotFound, "Texture not found", W, "The texture is not in the effect's folder, the search folders or the game's archives. Add it, fix the name, or check the search settings."),
        new(PlaceholderTexture, "Runtime texture placeholder", I, "This name is replaced at run time by the texture of the object the effect is attached to, so there is no file to find. The preview shows a stand-in."),
        new(TextureExtension, "Unexpected texture extension", W, "Effect textures are .tga images or .vbm animated bitmaps. The game looks the name up as written, so other extensions are unlikely to load."),
        new(ParentNotInFile, "Parent not in the file", I, "The parent is not an object of this effect. It may name a bone or prop point of the model the effect is attached to. The game does not apply object hierarchy inside an effect, so this name has no effect on playback."),
        new(FrameCountMismatch, "Frame count does not match the time range", W, "A mesh's frame count must match its start, end and frame rate. The game uses the stored count, so the mesh plays longer or shorter than its range says."),
        new(MeshOutsideEndFrame, "Mesh starts after the effect ends", W, "The game stops (or loops) the effect at the header end frame, so a mesh that starts after it is never drawn. Move the mesh earlier or extend the end frame."),
        new(EndFrameMismatch, "End frame differs from the longest animated object", I, "The header end frame decides when the effect stops or loops. It is usually the end of the longest animated mesh, particle system, dummy or light; a shorter value cuts them off, a longer one leaves a gap before a loop."),
        new(SectionName, "Empty or duplicate object name", W, "Objects are found by name (parents, spacewarps, tools). Give every object a unique, non-empty name."),
        new(MissingSpacewarp, "Spacewarp not found", W, "A particle system names a spacewarp that is not in the file, so the game finds nothing to bind and the particles move without it."),
        new(ParticleRange, "Particle value outside the stock range", I, "The stock effects use particle counts of 2-100, lifetimes of 1600-32000 ticks and emitter types 0 and 1. Other values may work but have not been seen in game."),
        new(UnusedMaterial, "Unused material", I, "No mesh slot or particle system uses this material. It is loaded for nothing; remove it."),
        new(UnexercisedFeature, "Feature not used by stock effects", I, "No stock effect uses this feature, so how the game shows it has not been verified and the preview may be approximate."),
        new(NonFinite, "Invalid number", E, "A value is not a finite number (NaN or infinity). The game's maths breaks on it; re-export the effect."),
        new(ZeroAreaFace, "Zero-area face", W, "Faces whose corners coincide or lie on a line draw nothing and can upset lighting. Remove them in the modelling tool."),
        new(EmptyMesh, "Mesh without vertices", W, "A mesh with no vertices or faces draws nothing. Remove it or re-export it."),
    ];

    /// <summary>The rule with this code, or null.</summary>
    public static VfxRule? Find(string code) => All.FirstOrDefault(r => r.Code == code);
}
