using System.Collections.Immutable;

namespace Cairn.Rfa.Linting;

/// <summary>
/// Every mesh rule id. Severity policy as <see cref="ClipRules"/>; the limits are DESIGN.md section 4
/// (and Alpine Faction's <c>docs/mesh_lod_system.md</c>: a mesh holds up to 3 detail levels). Each rule
/// was checked against all 95 stock <c>.v3c</c> and 427 stock <c>.v3m</c>: none reports an Error on
/// stock data.
/// <code>
/// Code    Severity  Rule                                                     Context
/// V3C001  Error     Version other than 0x40000                               -
/// V3C002  Error     More than 50 bones                                       -
/// V3C003  Error     Bone parent index out of range or the bone itself        -
/// V3C004  Error     Bone parents form a cycle                                -
/// V3C005  Error     Collision sphere bone index out of range                 -
/// V3C006  Error     Prop point bone index out of range                       -
/// V3C007  Error     Vertex weighted to a bone that does not exist            -
/// V3C008  Error     LOD count outside 1..3                                   -
/// V3C009  Error     More than 7 textures in one LOD                          -
/// V3C010  Error     Triangle uses a vertex the batch does not have           -
/// V3C011  Error     Batch texture index outside the LOD's texture list       -
/// V3C012  Error     Header counts disagree with the sections                 -
/// V3C013  Error     Non-finite number (NaN or infinity)                      -
/// V3C014  Error     Cannot be written (a count or size the format cannot hold)  -
/// V3C015  Error     Morph map entry outside the batch                        -
/// V3C020  Warning   Character vertex weights sum more than 3 away from 255   -
/// V3C021  Warning   Texture not found                                        resolver
/// V3C022  Warning   Name too long for its field (no room for the terminator) -
/// V3C023  Warning   Two bones share a name                                   -
/// V3C024  Warning   Collision sphere radius zero or negative                 -
/// V3C025  Warning   LOD distances not increasing                             -
/// V3C026  Warning   File name longer than 59 characters                      file name
/// </code>
/// </summary>
public static class MeshRules
{
    /// <summary>V3C001.</summary>
    public const string UnsupportedVersion = "V3C001";
    /// <summary>V3C002.</summary>
    public const string TooManyBones = "V3C002";
    /// <summary>V3C003.</summary>
    public const string BadParent = "V3C003";
    /// <summary>V3C004.</summary>
    public const string ParentCycle = "V3C004";
    /// <summary>V3C005.</summary>
    public const string SphereBone = "V3C005";
    /// <summary>V3C006.</summary>
    public const string PropPointBone = "V3C006";
    /// <summary>V3C007.</summary>
    public const string VertexBone = "V3C007";
    /// <summary>V3C008.</summary>
    public const string LodCount = "V3C008";
    /// <summary>V3C009.</summary>
    public const string TooManyTextures = "V3C009";
    /// <summary>V3C010.</summary>
    public const string TriangleIndex = "V3C010";
    /// <summary>V3C011.</summary>
    public const string BatchTexture = "V3C011";
    /// <summary>V3C012.</summary>
    public const string HeaderCounts = "V3C012";
    /// <summary>V3C013.</summary>
    public const string NonFinite = "V3C013";
    /// <summary>V3C014.</summary>
    public const string NotWritable = "V3C014";
    /// <summary>V3C015.</summary>
    public const string MorphMapIndex = "V3C015";
    /// <summary>V3C020.</summary>
    public const string WeightSum = "V3C020";
    /// <summary>V3C021.</summary>
    public const string MissingTexture = "V3C021";
    /// <summary>V3C022.</summary>
    public const string NameTooLong = "V3C022";
    /// <summary>V3C023.</summary>
    public const string DuplicateBoneName = "V3C023";
    /// <summary>V3C024.</summary>
    public const string SphereRadius = "V3C024";
    /// <summary>V3C025.</summary>
    public const string LodDistances = "V3C025";
    /// <summary>V3C026.</summary>
    public const string FileNameTooLong = "V3C026";

    /// <summary>Most detail levels a mesh may have (VifLodMesh holds 3).</summary>
    public const int MaxLods = 3;

    /// <summary>Most textures one LOD may use.</summary>
    public const int MaxTexturesPerLod = 7;

    /// <summary>The sum of a skinned vertex's four weights.</summary>
    public const int WeightTotal = 255;

    /// <summary>
    /// How far a character vertex's weights may sum from 255 before V3C020 fires. The stock exporter
    /// rounds each weight separately: 22% of stock vertices sum to 254 and a few hundred to 252-253, so
    /// only a larger difference is worth reporting.
    /// </summary>
    public const int WeightSumTolerance = 3;

    /// <summary>Every mesh rule with its policy.</summary>
    public static ImmutableArray<RuleInfo> All { get; } =
    [
        new(UnsupportedVersion, DiagnosticSeverity.Error, "Unsupported version", RuleContext.None),
        new(TooManyBones, DiagnosticSeverity.Error, "More than 50 bones", RuleContext.None),
        new(BadParent, DiagnosticSeverity.Error, "Invalid bone parent", RuleContext.None),
        new(ParentCycle, DiagnosticSeverity.Error, "Bone parent cycle", RuleContext.None),
        new(SphereBone, DiagnosticSeverity.Error, "Collision sphere on a missing bone", RuleContext.None),
        new(PropPointBone, DiagnosticSeverity.Error, "Prop point on a missing bone", RuleContext.None),
        new(VertexBone, DiagnosticSeverity.Error, "Vertex weighted to a missing bone", RuleContext.None),
        new(LodCount, DiagnosticSeverity.Error, "Too many or no LODs", RuleContext.None),
        new(TooManyTextures, DiagnosticSeverity.Error, "Too many textures in a LOD", RuleContext.None),
        new(TriangleIndex, DiagnosticSeverity.Error, "Triangle uses a missing vertex", RuleContext.None),
        new(BatchTexture, DiagnosticSeverity.Error, "Batch uses a missing texture slot", RuleContext.None),
        new(HeaderCounts, DiagnosticSeverity.Error, "Header counts disagree", RuleContext.None),
        new(NonFinite, DiagnosticSeverity.Error, "Not a number", RuleContext.None),
        new(NotWritable, DiagnosticSeverity.Error, "Mesh cannot be written", RuleContext.None),
        new(MorphMapIndex, DiagnosticSeverity.Error, "Morph map points outside the batch", RuleContext.None),
        new(WeightSum, DiagnosticSeverity.Warning, "Vertex weights do not sum to 255", RuleContext.None),
        new(MissingTexture, DiagnosticSeverity.Warning, "Texture not found", RuleContext.Resolver),
        new(NameTooLong, DiagnosticSeverity.Warning, "Name too long", RuleContext.None),
        new(DuplicateBoneName, DiagnosticSeverity.Warning, "Duplicate bone name", RuleContext.None),
        new(SphereRadius, DiagnosticSeverity.Warning, "Collision sphere without size", RuleContext.None),
        new(LodDistances, DiagnosticSeverity.Warning, "LOD distances out of order", RuleContext.None),
        new(FileNameTooLong, DiagnosticSeverity.Warning, "File name too long", RuleContext.FileName),
    ];

    /// <summary>The policy of one rule, or null for an unknown code.</summary>
    public static RuleInfo? Find(string code) => All.FirstOrDefault(r => r.Code == code);
}
